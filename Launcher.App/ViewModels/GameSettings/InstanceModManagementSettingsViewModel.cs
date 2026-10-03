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
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Shared;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 将本地 Mod 快照投影为可筛选、可批量选择的页面状态，并协调页面激活、文件监听和导入冲突。
/// </summary>
public sealed partial class InstanceModManagementSettingsViewModel : GameSettingsDetailsSectionViewModelBase
{
    // LocalModsViewModel 提供真实 Mod 快照和文件操作；本类负责页面筛选、选择及对话框编排。
    private readonly LocalModsViewModel localModsViewModel;
    private readonly IStatusService statusService;
    private readonly IInstanceFolderService instanceFolderService;
    private readonly IFilePickerService filePickerService;
    private readonly IInstanceContentImportPathValidator importPathValidator;
    private readonly IUiDispatcher uiDispatcher;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly IModUpdateService? modUpdateService;
    private readonly DownloadTasksPageViewModel? downloadTasksPage;
    private readonly ILogger<InstanceModManagementSettingsViewModel> logger;
    // 投影路径索引用于跨刷新复用 Item ViewModel；同名启用/禁用文件并存时必须保持两个独立键。
    private readonly Dictionary<string, ModManagementModItemViewModel> allModsByProjectionPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModManagementModItemViewModel> allModsByFullPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedModPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeUpdatePaths = new(StringComparer.OrdinalIgnoreCase);
    // 冲突对话框通过 TaskCompletionSource 将事件驱动 UI 转换为可顺序 await 的导入步骤。
    private TaskCompletionSource<bool>? pendingImportConflictResolutionSource;
    // 生命周期字段用于合并重复加载、隐藏页面刷新和同一 Dispatcher 周期内的集合事件。
    private Task? loadTask;
    private GameInstance? selectedInstance;
    private string? lastSingleSelectedModPath;
    private bool hasPendingVisualRefresh;
    private bool isVisibleRefreshQueued;
    private bool isSectionActive;
    private bool isInitialProjectionReady;
    private bool suppressLocalCollectionEvents;
    private long lifecycleGeneration;

    // 可观察属性是本地快照的 UI 投影，不直接拥有任何文件系统状态。
    [ObservableProperty]
    private int installedModCount;

    [ObservableProperty]
    private int enabledModCount;

    [ObservableProperty]
    private ModManagementModItemViewModel? selectedMod;

    [ObservableProperty]
    private string modSearchQuery = string.Empty;

    [ObservableProperty]
    private ModManagementFilter modFilter = ModManagementFilter.All;

    [ObservableProperty]
    private bool isMultiSelectMode;

    [ObservableProperty]
    private int selectedModCount;

    [ObservableProperty]
    private bool isLoadingMods;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateAllMods))]
    private bool hasLoadedMods;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateAllMods))]
    private bool isBulkUpdateBusy;

    [ObservableProperty]
    private IReadOnlyList<ModManagementModItemViewModel> visibleMods = Array.Empty<ModManagementModItemViewModel>();

    [ObservableProperty]
    private IReadOnlyList<object> visibleModListItems = Array.Empty<object>();

    [ObservableProperty]
    private int listEntranceAnimationToken;

    public InstanceModManagementSettingsViewModel(
        GameSettingsDetailsViewModel parent,
        LocalModsViewModel localModsViewModel,
        IStatusService statusService,
        IInstanceFolderService instanceFolderService,
        IFilePickerService filePickerService,
        IInstanceContentImportPathValidator importPathValidator,
        IFloatingMessageService floatingMessageService,
        IUiDispatcher? uiDispatcher = null,
        ILogger<InstanceModManagementSettingsViewModel>? logger = null,
        IModUpdateService? modUpdateService = null,
        DownloadTasksPageViewModel? downloadTasksPage = null)
        : base(parent)
    {
        this.localModsViewModel = localModsViewModel;
        this.statusService = statusService;
        this.instanceFolderService = instanceFolderService;
        this.filePickerService = filePickerService;
        this.importPathValidator = importPathValidator;
        this.floatingMessageService = floatingMessageService;
        this.modUpdateService = modUpdateService;
        this.downloadTasksPage = downloadTasksPage;
        this.uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
        this.logger = logger ?? NullLogger<InstanceModManagementSettingsViewModel>.Instance;
        this.localModsViewModel.ModsChanged += LocalModsViewModel_ModsChanged;
        this.localModsViewModel.IconChanged += LocalModsViewModel_IconChanged;
    }

    public event Action<ModDeleteRequest>? DeleteModsRequested;
    public event Action<ModImportConflictRequest>? ImportModConflictRequested;
    public event Action<GameInstance>? OnlineModInstallRequested;
    public event Action<ResourceProjectReference>? ResourceDetailsRequested;
    public event Action<ModUpdateConfirmationRequest>? ModUpdateConfirmationRequested;
    public event Action<ModBulkUpdateConfirmationRequest>? ModBulkUpdateConfirmationRequested;

    public override bool UsesFullViewportLayout => true;

    public IReadOnlyList<ModManagementModItemViewModel> Mods => VisibleMods;

    public bool IsModManagementSupported => selectedInstance?.Loader is not LoaderKind.Vanilla;

    public bool CanShowModInfoSection => IsModManagementSupported;

    public bool HasMods => Mods.Count > 0;

    public bool CanShowModScrollableContent => IsModManagementSupported && isInitialProjectionReady;

    public bool HasInstalledMods => InstalledModCount > 0;

    public bool CanUpdateAllMods =>
        IsModManagementSupported
        && HasLoadedMods
        && HasInstalledMods
        && !IsBulkUpdateBusy
        && activeUpdatePaths.Count == 0
        && modUpdateService is not null
        && downloadTasksPage is not null;

    public bool CanShowModListSection => IsModManagementSupported && (IsLoadingMods || HasInstalledMods);

    public bool CanShowNoModsEmptyState => IsModManagementSupported && HasLoadedMods && !IsLoadingMods && !HasInstalledMods;

    public bool CanShowModEmptyState => IsModManagementSupported && HasLoadedMods && !IsLoadingMods && HasInstalledMods && !HasMods;

    public bool CanShowModUnavailableState => !IsModManagementSupported;

    public bool CanShowModLoadingState => IsModManagementSupported && IsLoadingMods && !HasLoadedMods;

    public bool HasSelectedMods => SelectedModCount > 0;

    public bool AreAllVisibleModsSelected => HasMods && SelectedModCount == Mods.Count;

    public string SelectAllButtonText => AreAllVisibleModsSelected
        ? Strings.GameSettings_ModManagementCancelSelectAllButton
        : Strings.GameSettings_ModManagementSelectAllButton;

    public string InstalledSummaryText => IsLoadingMods && !HasLoadedMods
        ? Strings.GameSettings_ModManagementLoading
        : string.Format(
            Strings.GameSettings_ModManagementInstalledSummaryFormat,
            InstalledModCount,
            EnabledModCount);

    public string ModEmptyMessage => !HasInstalledMods || string.IsNullOrWhiteSpace(ModSearchQuery)
        ? Strings.GameSettings_ModManagementEmptyMessage
        : Strings.GameSettings_ModManagementSearchEmptyMessage;

    public string ModUnavailableMessage => Strings.GameSettings_ModManagementUnavailableMessage;

    public bool IsAllModsFilterSelected => ModFilter is ModManagementFilter.All;

    public bool IsEnabledModsFilterSelected => ModFilter is ModManagementFilter.Enabled;

    public bool IsDisabledModsFilterSelected => ModFilter is ModManagementFilter.Disabled;

    public Task SetSelectedInstanceAsync(GameInstance? instance)
    {
        OnSelectedInstanceChanged(instance);
        return EnsureLoadedForSelectedInstanceAsync();
    }

    /// <summary>
    /// 将页面完整切换到新实例上下文，清理旧选择、冲突请求和可见项状态。
    /// </summary>
    public override void OnSelectedInstanceChanged(GameInstance? instance)
    {
        // 实例引用变化意味着现有选择、冲突对话框和可见项都不再有效，必须作为一个状态边界整体重置。
        Interlocked.Increment(ref lifecycleGeneration);
        selectedInstance = instance;
        ResolvePendingImportConflict(false);
        loadTask = null;
        hasPendingVisualRefresh = false;
        isVisibleRefreshQueued = false;
        suppressLocalCollectionEvents = true;
        try
        {
            localModsViewModel.SetSelectedInstance(instance);
            localModsViewModel.SetSectionActive(isSectionActive && IsModManagementSupported);
        }
        finally
        {
            suppressLocalCollectionEvents = false;
        }

        IsLoadingMods = false;
        HasLoadedMods = false;
        allModsByProjectionPath.Clear();
        SetInitialProjectionReady(false);
        ResetSelectionState();
        ClearDisplayedMods();
    }

    public bool RefreshSelectedInstanceReference(GameInstance? instance)
    {
        if (ShouldResetForInstanceReference(instance))
        {
            OnSelectedInstanceChanged(instance);
            return true;
        }

        selectedInstance = instance;
        return false;
    }

    public override void OnSectionDeactivated()
    {
        if (!isSectionActive)
            return;

        isSectionActive = false;
        Interlocked.Increment(ref lifecycleGeneration);
        loadTask = null;
        IsLoadingMods = false;
        localModsViewModel.SetSectionActive(false);
    }

    public void ReleaseLocalObservation()
    {
        isSectionActive = false;
        Interlocked.Increment(ref lifecycleGeneration);
        loadTask = null;
        IsLoadingMods = false;
        localModsViewModel.ReleaseObservation();
    }

    public void SuspendLocalWatchersForInstanceRename()
    {
        localModsViewModel.SuspendWatcherForInstanceRename();
    }

    public void ResumeLocalWatchersAfterInstanceRename(bool restart = true)
    {
        if (restart && isSectionActive)
        {
            Interlocked.Increment(ref lifecycleGeneration);
            loadTask = null;
            localModsViewModel.InvalidateSnapshot();
        }
        localModsViewModel.ResumeWatcherAfterInstanceRename(restart);
    }

    /// <summary>
    /// 激活页面监听，并补做页面隐藏期间合并掉的视觉刷新。
    /// </summary>
    public override Task OnSectionActivatedAsync()
    {
        if (!isSectionActive)
        {
            isSectionActive = true;
            Interlocked.Increment(ref lifecycleGeneration);
            loadTask = null;
            localModsViewModel.SetSectionActive(IsModManagementSupported);
        }
        if (selectedInstance is null || !IsModManagementSupported)
            return Task.CompletedTask;

        if (HasLoadedMods)
        {
            if (hasPendingVisualRefresh)
            {
                hasPendingVisualRefresh = false;
                RefreshFromLocalMods();
            }

            loadTask = RefreshCachedModsAsync(Volatile.Read(ref lifecycleGeneration));
            return loadTask;
        }

        return EnsureLoadedForSelectedInstanceAsync();
    }

    public Task EnsureLoadedForSelectedInstanceAsync()
    {
        if (!isSectionActive || selectedInstance is null || !IsModManagementSupported)
            return Task.CompletedTask;

        if (loadTask is { IsCompleted: false })
            return loadTask;

        if (HasLoadedMods)
            return Task.CompletedTask;

        loadTask = LoadModsAsync(Volatile.Read(ref lifecycleGeneration));
        return loadTask;
    }

    private bool IsCurrentLifecycle(long generation, GameInstance expectedInstance)
    {
        return isSectionActive
            && generation == Volatile.Read(ref lifecycleGeneration)
            && string.Equals(expectedInstance.Id, selectedInstance?.Id, StringComparison.Ordinal)
            && string.Equals(expectedInstance.InstanceDirectory, selectedInstance?.InstanceDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
