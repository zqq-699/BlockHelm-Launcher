/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 组合实例设置各分区，维护统一实例选择，并路由导入、删除和在线安装等跨分区事件。
/// </summary>
public sealed partial class GameSettingsDetailsViewModel : ObservableObject, IDisposable
{
    // 每个分区拥有独立状态和服务依赖；聚合层只同步实例引用与当前分区生命周期。
    private readonly InstanceSettingsPersistenceCoordinator persistence;
    private readonly ILogger logger;
    private bool isPageActive;

    [ObservableProperty]
    private GameSettingsInstanceItem? selectedInstance;

    [ObservableProperty]
    private GameSettingsDetailSectionItem? selectedSection;

    [ObservableProperty]
    private GameSettingsDetailsSectionViewModelBase? currentSectionViewModel;

    public GameSettingsDetailsViewModel(
        GameSettingsEditDialogViewModel editDialog,
        IGameInstanceService instanceService,
        IStatusService statusService,
        IInstanceFolderService instanceFolderService,
        ISystemMemoryService systemMemoryService,
        IModService modService,
        IInstanceBackupService backupService,
        DownloadTasksPageViewModel downloadTasksPage,
        LocalModsViewModel localModsViewModel,
        LocalSavesViewModel localSavesViewModel,
        LocalResourcePacksViewModel localResourcePacksViewModel,
        LocalShaderPacksViewModel localShaderPacksViewModel,
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        IFilePickerService filePickerService,
        IInstanceContentImportPathValidator importPathValidator,
        IFloatingMessageService floatingMessageService,
        IUiDispatcher uiDispatcher,
        ILogger<GameSettingsDetailsViewModel>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IModpackExportService? modpackExportService = null,
        IModUpdateService? modUpdateService = null)
    {
        var resolvedLogger = logger ?? NullLogger<GameSettingsDetailsViewModel>.Instance;
        this.logger = resolvedLogger;
        persistence = new InstanceSettingsPersistenceCoordinator(
            instanceService,
            statusService,
            uiDispatcher,
            resolvedLogger);
        persistence.InstanceSaved += Persistence_InstanceSaved;

        General = new InstanceGeneralSettingsViewModel(
            editDialog,
            instanceFolderService,
            statusService,
            persistence);
        General.DeleteInstanceRequested += General_DeleteInstanceRequested;
        Launch = new InstanceLaunchSettingsViewModel(systemMemoryService, modService, persistence);
        Java = new InstanceJavaSettingsViewModel(
            persistence,
            javaRuntimeDiscoveryService,
            statusService,
            filePickerService,
            floatingMessageService);
        ModManagement = new InstanceModManagementSettingsViewModel(
            this,
            localModsViewModel,
            statusService,
            instanceFolderService,
            filePickerService,
            importPathValidator,
            floatingMessageService,
            uiDispatcher: uiDispatcher,
            logger: loggerFactory?.CreateLogger<InstanceModManagementSettingsViewModel>(),
            modUpdateService: modUpdateService,
            downloadTasksPage: downloadTasksPage);
        ModManagement.DeleteModsRequested += ModManagement_DeleteModsRequested;
        ModManagement.ImportModConflictRequested += ModManagement_ImportModConflictRequested;
        ModManagement.OnlineModInstallRequested += ModManagement_OnlineModInstallRequested;
        ModManagement.ModUpdateConfirmationRequested += request => ModUpdateConfirmationRequested?.Invoke(request);
        ModManagement.ModBulkUpdateConfirmationRequested += request => ModBulkUpdateConfirmationRequested?.Invoke(request);
        SaveManagement = new InstanceSaveManagementSettingsViewModel(
            this,
            localSavesViewModel,
            statusService,
            instanceFolderService,
            filePickerService,
            importPathValidator,
            logger: loggerFactory?.CreateLogger<InstanceSaveManagementSettingsViewModel>());
        SaveManagement.DeleteSavesRequested += SaveManagement_DeleteSavesRequested;
        SaveManagement.SaveImportFailedRequested += SaveManagement_SaveImportFailedRequested;
        ResourcePackManagement = new InstanceResourcePackManagementSettingsViewModel(
            this,
            localResourcePacksViewModel,
            statusService,
            instanceFolderService,
            filePickerService,
            importPathValidator,
            logger: loggerFactory?.CreateLogger<InstanceResourcePackManagementSettingsViewModel>());
        ResourcePackManagement.DeleteResourcePacksRequested += ResourcePackManagement_DeleteResourcePacksRequested;
        ResourcePackManagement.ResourcePackImportFailedRequested += ResourcePackManagement_ResourcePackImportFailedRequested;
        ShaderPackManagement = new InstanceShaderPackManagementSettingsViewModel(
            this,
            localShaderPacksViewModel,
            statusService,
            instanceFolderService,
            filePickerService,
            importPathValidator,
            logger: loggerFactory?.CreateLogger<InstanceShaderPackManagementSettingsViewModel>());
        ShaderPackManagement.DeleteShaderPacksRequested += ShaderPackManagement_DeleteShaderPacksRequested;
        ShaderPackManagement.ShaderPackImportFailedRequested += ShaderPackManagement_ShaderPackImportFailedRequested;
        Backup = new InstanceBackupSettingsViewModel(
            this,
            instanceService,
            backupService,
            downloadTasksPage,
            statusService,
            instanceFolderService,
            filePickerService,
            floatingMessageService,
            logger: loggerFactory?.CreateLogger<InstanceBackupSettingsViewModel>());
        Export = new InstanceExportSettingsViewModel(
            this,
            filePickerService,
            statusService,
            floatingMessageService,
            modpackExportService);
        Placeholder = new InstancePlaceholderSettingsViewModel(this);
        CurrentSectionViewModel = General;
    }

    public event Action<GameInstance>? InstanceSettingsSaved;

    public event Action<GameSettingsInstanceItem>? DeleteInstanceRequested;

    public event Action<ModDeleteRequest>? DeleteModsRequested;
    public event Action<SaveDeleteRequest>? DeleteSavesRequested;
    public event Action<ModImportConflictRequest>? ImportModConflictRequested;
    public event Action<GameInstance>? OnlineModInstallRequested;
    public event Action<SaveImportFailureRequest>? SaveImportFailedRequested;
    public event Action<ResourcePackDeleteRequest>? DeleteResourcePacksRequested;
    public event Action<ResourcePackImportFailureRequest>? ResourcePackImportFailedRequested;
    public event Action<ShaderPackDeleteRequest>? DeleteShaderPacksRequested;
    public event Action<ShaderPackImportFailureRequest>? ShaderPackImportFailedRequested;
    public event Action<ModUpdateConfirmationRequest>? ModUpdateConfirmationRequested;
    public event Action<ModBulkUpdateConfirmationRequest>? ModBulkUpdateConfirmationRequested;

    public bool HasSelectedInstance => SelectedInstance is not null;

    public string SectionTitle => SelectedSection?.Title ?? Strings.GameSettings_DetailGeneral;

    public string SectionPlaceholderBody => string.Format(
        Strings.GameSettings_DetailPlaceholderBodyFormat,
        SectionTitle);

    public GameSettingsDetailsSectionViewModelBase? ScrollSectionViewModel =>
        CurrentSectionViewModel?.UsesFullViewportLayout is true ? null : CurrentSectionViewModel;

    public GameSettingsDetailsSectionViewModelBase? FullViewportSectionViewModel =>
        CurrentSectionViewModel?.UsesFullViewportLayout is true ? CurrentSectionViewModel : null;

    public GameSettingsDetailsSectionViewModelBase? NonRetainedFullViewportSectionViewModel =>
        CurrentSectionViewModel?.UsesFullViewportLayout is true && !IsRetainedLocalContentSection
            ? CurrentSectionViewModel
            : null;

    public bool IsModManagementSection =>
        ReferenceEquals(CurrentSectionViewModel, ModManagement);

    public bool IsSaveManagementSection =>
        ReferenceEquals(CurrentSectionViewModel, SaveManagement);

    public bool IsResourcePackManagementSection =>
        ReferenceEquals(CurrentSectionViewModel, ResourcePackManagement);

    public bool IsShaderPackManagementSection =>
        ReferenceEquals(CurrentSectionViewModel, ShaderPackManagement);

    private bool IsRetainedLocalContentSection =>
        IsModManagementSection
        || IsSaveManagementSection
        || IsResourcePackManagementSection
        || IsShaderPackManagementSection;

    public bool IsGeneralSection => string.Equals(SelectedSection?.Id, "general", StringComparison.OrdinalIgnoreCase);

    public bool IsLaunchSection => string.Equals(SelectedSection?.Id, "launch", StringComparison.OrdinalIgnoreCase);

    public bool IsJavaSection => string.Equals(SelectedSection?.Id, "java", StringComparison.OrdinalIgnoreCase);

    public InstanceGeneralSettingsViewModel General { get; }

    public InstanceLaunchSettingsViewModel Launch { get; }

    public InstanceJavaSettingsViewModel Java { get; }

    public InstanceModManagementSettingsViewModel ModManagement { get; }

    public InstanceSaveManagementSettingsViewModel SaveManagement { get; }

    public InstanceResourcePackManagementSettingsViewModel ResourcePackManagement { get; }

    public InstanceShaderPackManagementSettingsViewModel ShaderPackManagement { get; }

    public InstanceBackupSettingsViewModel Backup { get; }

    public InstanceExportSettingsViewModel Export { get; }

    public InstancePlaceholderSettingsViewModel Placeholder { get; }

    public void PrimeFromSettings(LauncherSettings launcherSettings)
    {
        Launch.PrimeFromSettings(launcherSettings);
        Java.PrimeFromSettings(launcherSettings);
    }

    public void SetSelectedInstance(GameSettingsInstanceItem? instance)
    {
        // 选择变更集中应用给所有分区，避免隐藏分区仍观察上一实例目录。
        var previousInstance = SelectedInstance;
        SelectedInstance = instance;
        if (ReferenceEquals(previousInstance, instance))
            RefreshSelectedInstanceReference(instance);
    }

    public void SetSelectedSection(GameSettingsDetailSectionItem? section)
    {
        SelectedSection = section;
    }

    public void SetPageActive(bool value, bool releaseLocalContentObservation = false)
    {
        if (isPageActive == value)
        {
            if (!value && releaseLocalContentObservation)
                ReleaseLocalContentObservation();
            return;
        }

        isPageActive = value;
        if (isPageActive)
            ActivateCurrentSection();
        else
        {
            CurrentSectionViewModel?.OnSectionDeactivated();
            if (releaseLocalContentObservation)
                ReleaseLocalContentObservation();
        }
    }

    private void ReleaseLocalContentObservation()
    {
        ModManagement.ReleaseLocalObservation();
        SaveManagement.ReleaseLocalObservation();
        ResourcePackManagement.ReleaseLocalObservation();
        ShaderPackManagement.ReleaseLocalObservation();
    }

    public void NotifyInstanceSettingsSaved(GameInstance instance)
    {
        InstanceSettingsSaved?.Invoke(instance);
    }

    public void SuspendLocalWatchersForInstanceMove()
    {
        // 重命名和删除都会移动实例目录；先释放 watcher 和刷新任务持有的目录句柄。
        ModManagement.SuspendLocalWatchersForInstanceRename();
        SaveManagement.SuspendLocalWatchersForInstanceRename();
        ResourcePackManagement.SuspendLocalWatchersForInstanceRename();
        ShaderPackManagement.SuspendLocalWatchersForInstanceRename();
    }

    public void ResumeLocalWatchersAfterInstanceMove(bool restart = true)
    {
        ModManagement.ResumeLocalWatchersAfterInstanceRename(restart);
        SaveManagement.ResumeLocalWatchersAfterInstanceRename(restart);
        ResourcePackManagement.ResumeLocalWatchersAfterInstanceRename(restart);
        ShaderPackManagement.ResumeLocalWatchersAfterInstanceRename(restart);
        if (restart)
            ActivateCurrentSection();
    }

    public void ClearSelectedInstanceIf(string instanceId)
    {
        if (string.Equals(SelectedInstance?.Instance.Id, instanceId, StringComparison.Ordinal))
            SetSelectedInstance(null);
    }

    public Task DeleteModsAsync(IReadOnlyList<string> fullPaths) => ModManagement.DeleteModsAsync(fullPaths);

    public Task DeleteSavesAsync(IReadOnlyList<string> fullPaths) => SaveManagement.DeleteSavesAsync(fullPaths);

    public Task DeleteResourcePacksAsync(IReadOnlyList<string> fullPaths) => ResourcePackManagement.DeleteResourcePacksAsync(fullPaths);

    public Task DeleteShaderPacksAsync(IReadOnlyList<string> fullPaths) => ShaderPackManagement.DeleteShaderPacksAsync(fullPaths);
}
