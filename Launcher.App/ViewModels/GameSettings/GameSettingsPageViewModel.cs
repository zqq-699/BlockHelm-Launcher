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

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 作为游戏设置页面壳层协调实例列表、详情分区、编辑对话框和跨分区文件拖放。
/// </summary>
public sealed partial class GameSettingsPageViewModel : ObservableObject
{
    // 页面壳层只依赖 UI 服务；实例业务和各详情功能分别委托给组合的子 ViewModel。
    private readonly IStatusService statusService;
    private readonly IInstanceFolderService instanceFolderService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly ILogger<GameSettingsPageViewModel> logger;
    // 选中项对象会被列表刷新替换，单独保存订阅目标以便安全解除旧对象事件。
    private INotifyPropertyChanged? selectedInstanceNotifier;
    private bool isShellPageActive;

    [ObservableProperty]
    private GameSettingsPageStep currentStep = GameSettingsPageStep.List;

    public GameSettingsPageViewModel(
        GameSettingsInstanceListViewModel instanceList,
        GameSettingsDetailsViewModel details,
        GameSettingsEditDialogViewModel editDialog,
        GameSettingsDialogsViewModel dialogs,
        IStatusService statusService,
        IInstanceFolderService instanceFolderService,
        IFloatingMessageService floatingMessageService,
        ILogger<GameSettingsPageViewModel>? logger = null)
    {
        InstanceList = instanceList;
        Details = details;
        EditDialog = editDialog;
        Dialogs = dialogs;
        this.statusService = statusService;
        this.instanceFolderService = instanceFolderService;
        this.floatingMessageService = floatingMessageService;
        this.logger = logger ?? NullLogger<GameSettingsPageViewModel>.Instance;

        // 分区对象只创建一次，切换时更新 IsSelected，保持 XAML 对象身份和动画状态稳定。
        foreach (var section in GameSettingsDetailSectionFactory.Create())
            DetailSections.Add(section);
        SelectDetailsSectionCore(DetailSections.FirstOrDefault());

        // 壳层集中桥接子 ViewModel 事件，避免各子页面互相直接依赖。
        InstanceList.PropertyChanged += InstanceList_PropertyChanged;
        InstanceList.LocalImportRequested += InstanceList_LocalImportRequested;
        EditDialog.InstanceUpdated += EditDialog_InstanceUpdated;
        EditDialog.InstanceRenameStarting += EditDialog_InstanceRenameStarting;
        EditDialog.InstanceRenameFinished += EditDialog_InstanceRenameFinished;
        Dialogs.InstanceDeleted += Dialogs_InstanceDeleted;
        Details.InstanceSettingsSaved += Details_InstanceSettingsSaved;
        Details.DeleteInstanceRequested += Dialogs.OpenDeleteInstance;
        Details.DeleteModsRequested += Dialogs.OpenDeleteMods;
        Details.DeleteSavesRequested += Dialogs.OpenDeleteSaves;
        Details.DeleteResourcePacksRequested += Dialogs.OpenDeleteResourcePacks;
        Details.DeleteShaderPacksRequested += Dialogs.OpenDeleteShaderPacks;
        Details.ImportModConflictRequested += Dialogs.OpenModImportConflict;
        Details.SaveImportFailedRequested += Dialogs.OpenSaveImportFailure;
        Details.ResourcePackImportFailedRequested += Dialogs.OpenResourcePackImportFailure;
        Details.ShaderPackImportFailedRequested += Dialogs.OpenShaderPackImportFailure;
        Details.OnlineModInstallRequested += Details_OnlineModInstallRequested;
        Details.ModUpdateConfirmationRequested += Dialogs.OpenModUpdate;
        Details.ModBulkUpdateConfirmationRequested += Dialogs.OpenModBulkUpdate;
        Details.ModManagement.ResourceDetailsRequested += reference => ResourceProjectDetailsRequested?.Invoke(reference);
        Details.ResourcePackManagement.ResourceDetailsRequested += reference => ResourceProjectDetailsRequested?.Invoke(reference);
        Details.ShaderPackManagement.ResourceDetailsRequested += reference => ResourceProjectDetailsRequested?.Invoke(reference);
        Details.PropertyChanged += Details_PropertyChanged;
        Details.ModManagement.PropertyChanged += ModManagement_PropertyChanged;
        Details.SaveManagement.PropertyChanged += SaveManagement_PropertyChanged;
        Details.ResourcePackManagement.PropertyChanged += ResourcePackManagement_PropertyChanged;
        Details.ShaderPackManagement.PropertyChanged += ShaderPackManagement_PropertyChanged;
        Details.Backup.PropertyChanged += Backup_PropertyChanged;

        HandleSelectedInstanceChanged();
    }

    public event Action<GameInstance>? LaunchInstanceRequested;

    public event Action<GameSettingsInstancesChangedEventArgs>? InstancesChanged;

    public event Action<GameInstance>? OnlineModInstallRequested;

    public event Action? LocalImportRequested;

    public event Action? InstanceListActivated;

    public event Action? MinecraftDirectorySwitchRequested;

    public event Action<ResourceProjectReference>? ResourceProjectDetailsRequested;

    public GameSettingsInstanceListViewModel InstanceList { get; }

    public GameSettingsDetailsViewModel Details { get; }

    public GameSettingsEditDialogViewModel EditDialog { get; }

    public GameSettingsDialogsViewModel Dialogs { get; }

    public ObservableCollection<GameSettingsDetailSectionItem> DetailSections { get; } = [];

    public bool IsListStep => CurrentStep is GameSettingsPageStep.List;

    public bool IsDetailsStep => CurrentStep is GameSettingsPageStep.Details;

    public void SetShellPageActive(bool active)
    {
        if (isShellPageActive == active)
            return;

        isShellPageActive = active;
        UpdateDetailsActivation();
    }

    public bool IsGameSettingsContentVisible => IsDetailsStep || InstanceList.HasVisibleInstances;

    public System.Collections.IEnumerable CurrentSecondaryMenuItems => IsDetailsStep
        ? DetailSections
        : InstanceList.Categories;

    public string PageTitle => IsDetailsStep && InstanceList.SelectedInstance is not null
        ? InstanceList.SelectedInstance.Name
        : InstanceList.SelectedCategory?.Title ?? Strings.GameSettings_AllCategory;

    public string? PageTitleIconSource => IsDetailsStep
        ? InstanceList.SelectedInstance?.IconSource
        : null;

    public bool IsModManagementDetailsStep => IsDetailsSection("mod_management");

    public bool IsSaveManagementDetailsStep => IsDetailsSection("saves");

    public bool IsResourcePackManagementDetailsStep => IsDetailsSection("resource_packs");

    public bool IsShaderPackManagementDetailsStep => IsDetailsSection("shaders");

    public bool IsBackupManagementDetailsStep => IsDetailsSection("backup");

    public bool IsExportDetailsStep => IsDetailsSection("export");

    public bool IsTopResourceManagementDetailsStep => IsModManagementDetailsStep
        || IsSaveManagementDetailsStep
        || IsResourcePackManagementDetailsStep
        || IsShaderPackManagementDetailsStep
        || IsBackupManagementDetailsStep;

    public bool IsTopSearchVisible => IsListStep || IsTopResourceManagementDetailsStep;

    public string TopSearchQuery
    {
        get
        {
            if (IsModManagementDetailsStep)
                return Details.ModManagement.ModSearchQuery;
            if (IsSaveManagementDetailsStep)
                return Details.SaveManagement.SaveSearchQuery;
            if (IsResourcePackManagementDetailsStep)
                return Details.ResourcePackManagement.ResourcePackSearchQuery;
            if (IsShaderPackManagementDetailsStep)
                return Details.ShaderPackManagement.ShaderPackSearchQuery;
            if (IsBackupManagementDetailsStep)
                return Details.Backup.BackupSearchQuery;
            return InstanceList.SearchQuery;
        }
        set
        {
            if (IsModManagementDetailsStep)
                Details.ModManagement.ModSearchQuery = value;
            else if (IsSaveManagementDetailsStep)
                Details.SaveManagement.SaveSearchQuery = value;
            else if (IsResourcePackManagementDetailsStep)
                Details.ResourcePackManagement.ResourcePackSearchQuery = value;
            else if (IsShaderPackManagementDetailsStep)
                Details.ShaderPackManagement.ShaderPackSearchQuery = value;
            else if (IsBackupManagementDetailsStep)
                Details.Backup.BackupSearchQuery = value;
            else if (IsListStep)
                InstanceList.SearchQuery = value;
            OnPropertyChanged();
        }
    }

    public bool UpdateImportDropState(IReadOnlyList<string> paths)
    {
        var evaluation = EvaluateImportDrop(paths);
        ApplyImportDropHint(evaluation);
        return evaluation.ShouldHandle && evaluation.CanAccept;
    }

    public void ClearImportDropState()
    {
        floatingMessageService.ClearDragHint(this);
    }

    /// <summary>
    /// 按当前详情分区把拖入文件路由到 Mod、存档、资源包或光影包导入流程。
    /// </summary>
    public async Task HandleImportDropAsync(IReadOnlyList<string> paths)
    {
        // 拖放移动期间和真正释放时使用同一评估器，确保提示与最终处理规则完全一致。
        // 此处不再重新显示提示：松手后它必然被立刻清除，重显只会让浮层闪一下。
        var evaluation = EvaluateImportDrop(paths);
        ClearImportDropState();
        if (!evaluation.ShouldHandle || !evaluation.CanAccept)
            return;
        try
        {
            // 具体导入类型由当前详情分区决定，壳层只负责日志和清理全局浮层提示。
            logger.LogInformation(
                "Handling game settings import drop. Section={SectionId} FileCount={FileCount} InstanceId={InstanceId}",
                Details.SelectedSection?.Id ?? "<none>",
                paths.Count,
                InstanceList.SelectedInstance?.Instance.Id ?? "<none>");
            await Details.HandleImportDropAsync(paths);
        }
        finally
        {
            ClearImportDropState();
        }
    }

    public void PrimeFromSettings(LauncherSettings settings) => Details.PrimeFromSettings(settings);

    public bool ApplyInstanceCatalog(
        IReadOnlyList<GameInstance> instances,
        long catalogRevision,
        bool playEntranceAnimation = true)
    {
        return InstanceList.ApplyInstanceCatalog(instances, catalogRevision, playEntranceAnimation);
    }

    public async Task OpenInstanceDetailsAsync(GameInstance? instance, CancellationToken cancellationToken = default)
    {
        await OpenInstanceDetailsAsync(instance, null, cancellationToken);
    }

    public async Task OpenInstanceJavaSettingsAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        await OpenInstanceDetailsAsync(instance, "java", cancellationToken);
    }

    /// <summary>
    /// 切换详情上下文和目标分区，并同步所有依赖所选实例的子 ViewModel。
    /// </summary>
    public void ShowInstanceDetails(GameInstance? instance, string? sectionId = null)
    {
        if (instance is null)
        {
            CurrentStep = GameSettingsPageStep.List;
            return;
        }
        // 外部传入实例可能尚未进入当前筛选结果，先合并到列表再通过统一选择路径传播状态。
        var item = InstanceList.GetOrAdd(instance);
        InstanceList.SelectInstance(item);
        SelectDetailsSectionCore(ResolveDetailSection(sectionId));
        CurrentStep = GameSettingsPageStep.Details;
    }

    [RelayCommand]
    private void SelectInstance(GameSettingsInstanceItem instance)
    {
        InstanceList.SelectInstance(instance);
        SelectDetailsSectionCore(DetailSections.FirstOrDefault());
        CurrentStep = GameSettingsPageStep.Details;
    }

    [RelayCommand]
    private void SelectSecondaryMenuItem(object? item)
    {
        switch (item)
        {
            case GameSettingsInstanceCategory category:
                CurrentStep = GameSettingsPageStep.List;
                InstanceList.SelectCategory(category);
                break;
            case GameSettingsDetailSectionItem section:
                SelectDetailsSectionCore(section);
                break;
        }
    }

    [RelayCommand]
    private void BackToInstanceList()
    {
        CurrentStep = GameSettingsPageStep.List;
        InstanceList.SetPreserveFilteredSelection(false);
    }

    private bool CanRequestMinecraftDirectorySwitch() => IsListStep;

    [RelayCommand(CanExecute = nameof(CanRequestMinecraftDirectorySwitch))]
    private void RequestMinecraftDirectorySwitch() =>
        MinecraftDirectorySwitchRequested?.Invoke();

    [RelayCommand]
    private void OpenInstanceFolder(GameSettingsInstanceItem instance)
    {
        var folderPath = instance.Instance.InstanceDirectory;
        if (!instanceFolderService.DirectoryExists(folderPath))
        {
            statusService.Report(Strings.Status_InstanceFolderNotFound);
            return;
        }
        if (!instanceFolderService.TryOpen(folderPath))
            statusService.Report(Strings.Status_OpenInstanceFolderFailed);
    }

    [RelayCommand]
    private void SelectInstanceAndGoHome(GameSettingsInstanceItem instance) =>
        LaunchInstanceRequested?.Invoke(instance.Instance);

    partial void OnCurrentStepChanged(GameSettingsPageStep value)
    {
        // 详情页保留被筛选掉的当前实例选择，返回列表时恢复普通筛选语义。
        var isDetailsActive = value is GameSettingsPageStep.Details;
        InstanceList.SetPreserveFilteredSelection(isDetailsActive);
        UpdateDetailsActivation();
        OnPropertyChanged(nameof(IsListStep));
        OnPropertyChanged(nameof(IsDetailsStep));
        OnPropertyChanged(nameof(IsGameSettingsContentVisible));
        OnPropertyChanged(nameof(CurrentSecondaryMenuItems));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageTitleIconSource));
        RequestMinecraftDirectorySwitchCommand.NotifyCanExecuteChanged();
        RaiseTopSearchPropertyChanges();
        if (value is GameSettingsPageStep.List)
            InstanceListActivated?.Invoke();
    }

    private void UpdateDetailsActivation()
    {
        Details.SetPageActive(
            isShellPageActive && IsDetailsStep,
            releaseLocalContentObservation: IsListStep);
    }

    private Task OpenInstanceDetailsAsync(
        GameInstance? instance,
        string? sectionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShowInstanceDetails(instance, sectionId);
        return Task.CompletedTask;
    }

    private bool IsDetailsSection(string sectionId) => IsDetailsStep
        && string.Equals(Details.SelectedSection?.Id, sectionId, StringComparison.OrdinalIgnoreCase);

    private void SelectDetailsSectionCore(GameSettingsDetailSectionItem? section)
    {
        // 导航项选择和详情激活在同一方法中提交，避免菜单高亮与内容分区短暂不一致。
        foreach (var item in DetailSections)
            item.IsSelected = ReferenceEquals(item, section);
        Details.SetSelectedSection(section);
    }

    private GameSettingsDetailSectionItem? ResolveDetailSection(string? sectionId)
    {
        if (!string.IsNullOrWhiteSpace(sectionId))
        {
            var match = DetailSections.FirstOrDefault(item =>
                string.Equals(item.Id, sectionId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return DetailSections.FirstOrDefault();
    }

    private GameSettingsFileDropEvaluation EvaluateImportDrop(IReadOnlyList<string> paths) =>
        IsDetailsStep ? Details.EvaluateImportDrop(paths) : GameSettingsFileDropEvaluation.Hidden;

    private void ApplyImportDropHint(GameSettingsFileDropEvaluation evaluation)
    {
        var message = evaluation.ShouldHandle
            ? evaluation.CanAccept
                ? Strings.GameSettings_DropReleaseToImportMessage
                : Strings.GameSettings_DropUnsupportedFileMessage
            : string.Empty;
        // DragOver 高频触发；去重由浮层服务统一负责，避免动画不断重新开始。
        floatingMessageService.ShowDragHint(this, message);
    }

    /// <summary>
    /// 将实例列表选择传播到详情、对话框和顶部搜索状态，避免子页面各自监听列表。
    /// </summary>
    private void HandleSelectedInstanceChanged()
    {
        // 刷新列表可能产生新的包装对象，先解除旧订阅再把选择传播给详情和标题。
        if (selectedInstanceNotifier is not null)
            selectedInstanceNotifier.PropertyChanged -= SelectedInstance_PropertyChanged;
        selectedInstanceNotifier = InstanceList.SelectedInstance;
        if (selectedInstanceNotifier is not null)
            selectedInstanceNotifier.PropertyChanged += SelectedInstance_PropertyChanged;
        Details.SetSelectedInstance(InstanceList.SelectedInstance);
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageTitleIconSource));
        if (InstanceList.SelectedInstance is null && IsDetailsStep)
            CurrentStep = GameSettingsPageStep.List;
    }

    private void InstanceList_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GameSettingsInstanceListViewModel.SelectedInstance):
                HandleSelectedInstanceChanged();
                break;
            case nameof(GameSettingsInstanceListViewModel.SelectedCategory):
                OnPropertyChanged(nameof(PageTitle));
                break;
            case nameof(GameSettingsInstanceListViewModel.SearchQuery):
                OnPropertyChanged(nameof(TopSearchQuery));
                break;
            case nameof(GameSettingsInstanceListViewModel.HasVisibleInstances):
                OnPropertyChanged(nameof(IsGameSettingsContentVisible));
                break;
        }
    }

    private void InstanceList_LocalImportRequested() => LocalImportRequested?.Invoke();

    private void SelectedInstance_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageTitleIconSource));
    }

    private void EditDialog_InstanceRenameStarting() => Details.SuspendLocalWatchersForInstanceMove();

    private void EditDialog_InstanceRenameFinished() => Details.ResumeLocalWatchersAfterInstanceMove();

    private void EditDialog_InstanceUpdated(GameInstance instance)
    {
        InstanceList.AddOrUpdate(instance);
        var item = InstanceList.Find(instance.Id);
        if (item is not null)
        {
            InstanceList.SelectInstance(item);
            CurrentStep = GameSettingsPageStep.Details;
        }
        InstancesChanged?.Invoke(GameSettingsInstancesChangedEventArgs.Updated(instance));
    }

    private void Dialogs_InstanceDeleted(GameSettingsInstanceItem item)
    {
        InstanceList.Remove(item.Instance.Id);
        InstancesChanged?.Invoke(GameSettingsInstancesChangedEventArgs.Deleted(item.Instance.Id));
    }

    private void Details_InstanceSettingsSaved(GameInstance instance)
    {
        InstanceList.AddOrUpdate(instance);
        InstancesChanged?.Invoke(GameSettingsInstancesChangedEventArgs.Updated(instance));
    }

    private void Details_OnlineModInstallRequested(GameInstance instance) => OnlineModInstallRequested?.Invoke(instance);

    private void Details_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameSettingsDetailsViewModel.SelectedSection)
            or nameof(GameSettingsDetailsViewModel.CurrentSectionViewModel))
        {
            RaiseTopSearchPropertyChanges();
        }
    }

    private void ModManagement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceModManagementSettingsViewModel.ModSearchQuery))
            OnPropertyChanged(nameof(TopSearchQuery));
    }

    private void SaveManagement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceSaveManagementSettingsViewModel.SaveSearchQuery))
            OnPropertyChanged(nameof(TopSearchQuery));
    }

    private void ResourcePackManagement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceResourcePackManagementSettingsViewModel.ResourcePackSearchQuery))
            OnPropertyChanged(nameof(TopSearchQuery));
    }

    private void ShaderPackManagement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceShaderPackManagementSettingsViewModel.ShaderPackSearchQuery))
            OnPropertyChanged(nameof(TopSearchQuery));
    }

    private void Backup_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceBackupSettingsViewModel.BackupSearchQuery))
            OnPropertyChanged(nameof(TopSearchQuery));
    }

    private void RaiseTopSearchPropertyChanges()
    {
        OnPropertyChanged(nameof(IsModManagementDetailsStep));
        OnPropertyChanged(nameof(IsSaveManagementDetailsStep));
        OnPropertyChanged(nameof(IsResourcePackManagementDetailsStep));
        OnPropertyChanged(nameof(IsShaderPackManagementDetailsStep));
        OnPropertyChanged(nameof(IsBackupManagementDetailsStep));
        OnPropertyChanged(nameof(IsExportDetailsStep));
        OnPropertyChanged(nameof(IsTopResourceManagementDetailsStep));
        OnPropertyChanged(nameof(IsTopSearchVisible));
        OnPropertyChanged(nameof(TopSearchQuery));
    }
}
