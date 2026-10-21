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
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 管理实例资源包的目录监听、导入、筛选、多选与批量删除状态。
/// </summary>
public sealed partial class InstanceResourcePackManagementSettingsViewModel : GameSettingsDetailsSectionViewModelBase
{
    // LocalResourcePacksViewModel 是目录内容的唯一来源；本类只保存筛选、选择和页面生命周期状态。
    private readonly LocalResourcePacksViewModel localResourcePacksViewModel;
    private readonly IStatusService statusService;
    private readonly IInstanceFolderService instanceFolderService;
    private readonly IFilePickerService filePickerService;
    private readonly IInstanceContentImportPathValidator importPathValidator;
    private readonly IUiDispatcher uiDispatcher;
    private readonly ILogger<InstanceResourcePackManagementSettingsViewModel> logger;
    private readonly LocalContentSelectionState<ResourcePackManagementItemViewModel> selectionState;
    // 加载任务和刷新标记共同保证激活幂等，并把隐藏期间的多次目录事件压缩成一次刷新。
    private Task? loadTask;
    private GameInstance? selectedInstance;
    private bool hasPendingVisualRefresh;
    private bool isVisibleRefreshQueued;
    private bool isSectionActive;
    private bool isInitialProjectionReady;
    private bool suppressLocalCollectionEvents;
    private long lifecycleGeneration;

    // 可观察属性均为界面投影，任何磁盘变化都必须先进入共享本地内容 ViewModel。
    [ObservableProperty]
    private int installedResourcePackCount;

    [ObservableProperty]
    private ResourcePackManagementItemViewModel? selectedResourcePack;

    [ObservableProperty]
    private string resourcePackSearchQuery = string.Empty;

    [ObservableProperty]
    private bool isMultiSelectMode;

    [ObservableProperty]
    private int selectedResourcePackCount;

    [ObservableProperty]
    private bool isLoadingResourcePacks;

    [ObservableProperty]
    private bool hasLoadedResourcePacks;

    [ObservableProperty]
    private IReadOnlyList<ResourcePackManagementItemViewModel> visibleResourcePacks = Array.Empty<ResourcePackManagementItemViewModel>();

    [ObservableProperty]
    private IReadOnlyList<object> visibleResourcePackListItems = Array.Empty<object>();

    [ObservableProperty]
    private int listEntranceAnimationToken;

    public InstanceResourcePackManagementSettingsViewModel(
        GameSettingsDetailsViewModel parent,
        LocalResourcePacksViewModel localResourcePacksViewModel,
        IStatusService statusService,
        IInstanceFolderService instanceFolderService,
        IFilePickerService filePickerService,
        IInstanceContentImportPathValidator importPathValidator,
        IUiDispatcher? uiDispatcher = null,
        ILogger<InstanceResourcePackManagementSettingsViewModel>? logger = null)
        : base(parent)
    {
        this.localResourcePacksViewModel = localResourcePacksViewModel;
        this.statusService = statusService;
        this.instanceFolderService = instanceFolderService;
        this.filePickerService = filePickerService;
        this.importPathValidator = importPathValidator;
        this.uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
        this.logger = logger ?? NullLogger<InstanceResourcePackManagementSettingsViewModel>.Instance;
        selectionState = new LocalContentSelectionState<ResourcePackManagementItemViewModel>(
            resourcePack => resourcePack.FullPath,
            resourcePack => resourcePack.IsSelected,
            static (resourcePack, isSelected) => resourcePack.IsSelected = isSelected);
        this.localResourcePacksViewModel.ResourcePacksChanged += LocalResourcePacksViewModel_ResourcePacksChanged;
        this.localResourcePacksViewModel.IconChanged += LocalResourcePacksViewModel_IconChanged;
    }

    public event Action<ResourcePackDeleteRequest>? DeleteResourcePacksRequested;
    public event Action<ResourcePackImportFailureRequest>? ResourcePackImportFailedRequested;
    public event Action<ResourceProjectReference>? ResourceDetailsRequested;
    public event Action<GameInstance>? OnlineResourcePackInstallRequested;

    public override bool UsesFullViewportLayout => true;

    public IReadOnlyList<ResourcePackManagementItemViewModel> ResourcePacks => VisibleResourcePacks;

    public bool CanShowResourcePackInfoSection => selectedInstance is not null;

    public bool HasResourcePacks => ResourcePacks.Count > 0;

    public bool CanShowResourcePackScrollableContent => selectedInstance is not null && isInitialProjectionReady;

    public bool HasInstalledResourcePacks => InstalledResourcePackCount > 0;

    public bool CanShowResourcePackListSection => selectedInstance is not null && (IsLoadingResourcePacks || HasInstalledResourcePacks);

    public bool CanShowNoResourcePacksEmptyState => selectedInstance is not null && HasLoadedResourcePacks && !IsLoadingResourcePacks && !HasInstalledResourcePacks;

    public bool CanShowResourcePackEmptyState => selectedInstance is not null && HasLoadedResourcePacks && !IsLoadingResourcePacks && HasInstalledResourcePacks && !HasResourcePacks;

    public bool CanShowResourcePackLoadingState => selectedInstance is not null && IsLoadingResourcePacks && !HasLoadedResourcePacks;

    public bool HasSelectedResourcePacks => SelectedResourcePackCount > 0;

    public bool AreAllVisibleResourcePacksSelected => HasResourcePacks && SelectedResourcePackCount == ResourcePacks.Count;

    public bool CanImportLocalResourcePack => selectedInstance is not null;

    public string SelectAllButtonText => AreAllVisibleResourcePacksSelected
        ? Strings.GameSettings_ResourcePackManagementCancelSelectAllButton
        : Strings.GameSettings_ResourcePackManagementSelectAllButton;

    public string InstalledSummaryText => IsLoadingResourcePacks && !HasLoadedResourcePacks
        ? Strings.GameSettings_ResourcePackManagementLoading
        : string.Format(
            Strings.GameSettings_ResourcePackManagementInstalledSummaryFormat,
            InstalledResourcePackCount);

    public string ResourcePackEmptyMessage => !HasInstalledResourcePacks || string.IsNullOrWhiteSpace(ResourcePackSearchQuery)
        ? Strings.GameSettings_ResourcePackManagementEmptyMessage
        : Strings.GameSettings_ResourcePackManagementSearchEmptyMessage;

    public Task SetSelectedInstanceAsync(GameInstance? instance)
    {
        OnSelectedInstanceChanged(instance);
        return EnsureLoadedForSelectedInstanceAsync();
    }

    /// <summary>
    /// 切换资源包实例上下文，并使旧实例的加载、选择和视觉刷新失效。
    /// </summary>
    public override void OnSelectedInstanceChanged(GameInstance? instance)
    {
        // 切换底层集合时会同步触发事件，抑制回调以避免在半重置状态下重建列表。
        Interlocked.Increment(ref lifecycleGeneration);
        selectedInstance = instance;
        loadTask = null;
        hasPendingVisualRefresh = false;
        isVisibleRefreshQueued = false;
        suppressLocalCollectionEvents = true;
        try
        {
            localResourcePacksViewModel.SetSelectedInstance(instance);
            localResourcePacksViewModel.SetSectionActive(isSectionActive && selectedInstance is not null);
        }
        finally
        {
            suppressLocalCollectionEvents = false;
        }

        IsLoadingResourcePacks = false;
        HasLoadedResourcePacks = false;
        selectionState.ClearCache();
        SetInitialProjectionReady(false);
        ResetSelectionState();
        ClearDisplayedResourcePacks();
        ImportLocalResourcePackCommand.NotifyCanExecuteChanged();
    }

    public bool RefreshSelectedInstanceReference(GameInstance? instance)
    {
        if (ShouldResetForInstanceReference(instance))
        {
            OnSelectedInstanceChanged(instance);
            return true;
        }

        selectedInstance = instance;
        ImportLocalResourcePackCommand.NotifyCanExecuteChanged();
        return false;
    }

    public override void OnSectionDeactivated()
    {
        if (!isSectionActive)
            return;

        isSectionActive = false;
        Interlocked.Increment(ref lifecycleGeneration);
        loadTask = null;
        IsLoadingResourcePacks = false;
        localResourcePacksViewModel.SetSectionActive(false);
    }

    public void ReleaseLocalObservation()
    {
        isSectionActive = false;
        Interlocked.Increment(ref lifecycleGeneration);
        loadTask = null;
        IsLoadingResourcePacks = false;
        localResourcePacksViewModel.ReleaseObservation();
    }

    public void SuspendLocalWatchersForInstanceRename()
    {
        localResourcePacksViewModel.SuspendWatcherForInstanceRename();
    }

    public void ResumeLocalWatchersAfterInstanceRename(bool restart = true)
    {
        if (restart && isSectionActive)
        {
            Interlocked.Increment(ref lifecycleGeneration);
            loadTask = null;
            localResourcePacksViewModel.InvalidateSnapshot();
        }
        localResourcePacksViewModel.ResumeWatcherAfterInstanceRename(restart);
    }

    public override Task OnSectionActivatedAsync()
    {
        if (!isSectionActive)
        {
            // 首次进入后保留轻量监听；隐藏期间 watcher 只标记失效，不扫描目录或重建页面。
            isSectionActive = true;
            Interlocked.Increment(ref lifecycleGeneration);
            loadTask = null;
            localResourcePacksViewModel.SetSectionActive(selectedInstance is not null);
        }

        if (selectedInstance is null)
            return Task.CompletedTask;

        if (HasLoadedResourcePacks)
        {
            if (hasPendingVisualRefresh)
            {
                hasPendingVisualRefresh = false;
                RefreshFromLocalResourcePacks();
            }

            loadTask = RefreshCachedResourcePacksAsync(Volatile.Read(ref lifecycleGeneration));
            return loadTask;
        }

        return EnsureLoadedForSelectedInstanceAsync();
    }

    public Task EnsureLoadedForSelectedInstanceAsync()
    {
        if (!isSectionActive || selectedInstance is null)
            return Task.CompletedTask;

        if (loadTask is { IsCompleted: false })
            return loadTask;

        if (HasLoadedResourcePacks)
            return Task.CompletedTask;

        loadTask = LoadResourcePacksAsync(Volatile.Read(ref lifecycleGeneration));
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
