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
/// 管理实例存档的监听、压缩包导入、筛选、多选和批量删除页面状态。
/// </summary>
public sealed partial class InstanceSaveManagementSettingsViewModel : GameSettingsDetailsSectionViewModelBase
{
    // 本地数据由 LocalSavesViewModel 负责；本类只维护页面投影、交互选择和 UI 服务协调。
    private readonly LocalSavesViewModel localSavesViewModel;
    private readonly IStatusService statusService;
    private readonly IInstanceFolderService instanceFolderService;
    private readonly IFilePickerService filePickerService;
    private readonly IInstanceContentImportPathValidator importPathValidator;
    private readonly IUiDispatcher uiDispatcher;
    private readonly ILogger<InstanceSaveManagementSettingsViewModel> logger;
    private readonly LocalContentSelectionState<SaveManagementSaveItemViewModel> selectionState;
    // 这组字段描述页面生命周期。loadTask 合并激活请求，两个刷新标记合并隐藏期间的文件事件。
    private Task? loadTask;
    private GameInstance? selectedInstance;
    private bool hasPendingVisualRefresh;
    private bool isVisibleRefreshQueued;
    private bool isSectionActive;
    private bool isInitialProjectionReady;
    private bool suppressLocalCollectionEvents;
    private long lifecycleGeneration;

    // 以下属性是 XAML 所需的派生页面状态，不是第二份业务数据源。
    [ObservableProperty]
    private int installedSaveCount;

    [ObservableProperty]
    private SaveManagementSaveItemViewModel? selectedSave;

    [ObservableProperty]
    private string saveSearchQuery = string.Empty;

    [ObservableProperty]
    private bool isMultiSelectMode;

    [ObservableProperty]
    private int selectedSaveCount;

    [ObservableProperty]
    private bool isLoadingSaves;

    [ObservableProperty]
    private bool hasLoadedSaves;

    [ObservableProperty]
    private IReadOnlyList<SaveManagementSaveItemViewModel> visibleSaves = Array.Empty<SaveManagementSaveItemViewModel>();

    [ObservableProperty]
    private IReadOnlyList<object> visibleSaveListItems = Array.Empty<object>();

    [ObservableProperty]
    private int listEntranceAnimationToken;

    public InstanceSaveManagementSettingsViewModel(
        GameSettingsDetailsViewModel parent,
        LocalSavesViewModel localSavesViewModel,
        IStatusService statusService,
        IInstanceFolderService instanceFolderService,
        IFilePickerService filePickerService,
        IInstanceContentImportPathValidator importPathValidator,
        IUiDispatcher? uiDispatcher = null,
        ILogger<InstanceSaveManagementSettingsViewModel>? logger = null)
        : base(parent)
    {
        this.localSavesViewModel = localSavesViewModel;
        this.statusService = statusService;
        this.instanceFolderService = instanceFolderService;
        this.filePickerService = filePickerService;
        this.importPathValidator = importPathValidator;
        this.uiDispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
        this.logger = logger ?? NullLogger<InstanceSaveManagementSettingsViewModel>.Instance;
        selectionState = new LocalContentSelectionState<SaveManagementSaveItemViewModel>(
            save => save.FullPath,
            save => save.IsSelected,
            static (save, isSelected) => save.IsSelected = isSelected);
        this.localSavesViewModel.SavesChanged += LocalSavesViewModel_SavesChanged;
    }

    public event Action<SaveDeleteRequest>? DeleteSavesRequested;
    public event Action<SaveImportFailureRequest>? SaveImportFailedRequested;
    public event Action<GameInstance>? OnlineSaveInstallRequested;

    public override bool UsesFullViewportLayout => true;

    public IReadOnlyList<SaveManagementSaveItemViewModel> Saves => VisibleSaves;

    public bool CanShowSaveInfoSection => selectedInstance is not null;

    public bool HasSaves => Saves.Count > 0;

    public bool CanShowSaveScrollableContent => selectedInstance is not null && isInitialProjectionReady;

    public bool HasInstalledSaves => InstalledSaveCount > 0;

    public bool CanShowSaveListSection => selectedInstance is not null && (IsLoadingSaves || HasInstalledSaves);

    public bool CanShowNoSavesEmptyState => selectedInstance is not null && HasLoadedSaves && !IsLoadingSaves && !HasInstalledSaves;

    public bool CanShowSaveEmptyState => selectedInstance is not null && HasLoadedSaves && !IsLoadingSaves && HasInstalledSaves && !HasSaves;

    public bool CanShowSaveLoadingState => selectedInstance is not null && IsLoadingSaves && !HasLoadedSaves;

    public bool HasSelectedSaves => SelectedSaveCount > 0;

    public bool AreAllVisibleSavesSelected => HasSaves && SelectedSaveCount == Saves.Count;

    public bool CanImportLocalSave => selectedInstance is not null;

    public string SelectAllButtonText => AreAllVisibleSavesSelected
        ? Strings.GameSettings_SaveManagementCancelSelectAllButton
        : Strings.GameSettings_SaveManagementSelectAllButton;

    public string InstalledSummaryText => IsLoadingSaves && !HasLoadedSaves
        ? Strings.GameSettings_SaveManagementLoading
        : string.Format(
            Strings.GameSettings_SaveManagementInstalledSummaryFormat,
            InstalledSaveCount);

    public string SaveEmptyMessage => !HasInstalledSaves || string.IsNullOrWhiteSpace(SaveSearchQuery)
        ? Strings.GameSettings_SaveManagementEmptyMessage
        : Strings.GameSettings_SaveManagementSearchEmptyMessage;

    public Task SetSelectedInstanceAsync(GameInstance? instance)
    {
        OnSelectedInstanceChanged(instance);
        return EnsureLoadedForSelectedInstanceAsync();
    }

    /// <summary>
    /// 切换存档实例上下文，并整体重置监听、选择和延迟刷新状态。
    /// </summary>
    public override void OnSelectedInstanceChanged(GameInstance? instance)
    {
        // 切换监听目标时会触发集合清空事件；暂时抑制回调，最后由本方法一次性重置页面。
        Interlocked.Increment(ref lifecycleGeneration);
        selectedInstance = instance;
        loadTask = null;
        hasPendingVisualRefresh = false;
        isVisibleRefreshQueued = false;
        suppressLocalCollectionEvents = true;
        try
        {
            localSavesViewModel.SetSelectedInstance(instance);
            localSavesViewModel.SetSectionActive(isSectionActive && selectedInstance is not null);
        }
        finally
        {
            suppressLocalCollectionEvents = false;
        }

        IsLoadingSaves = false;
        HasLoadedSaves = false;
        selectionState.ClearCache();
        SetInitialProjectionReady(false);
        ResetSelectionState();
        ClearDisplayedSaves();
        ImportLocalSaveCommand.NotifyCanExecuteChanged();
    }

    public bool RefreshSelectedInstanceReference(GameInstance? instance)
    {
        if (ShouldResetForInstanceReference(instance))
        {
            OnSelectedInstanceChanged(instance);
            return true;
        }

        selectedInstance = instance;
        ImportLocalSaveCommand.NotifyCanExecuteChanged();
        return false;
    }

    public override void OnSectionDeactivated()
    {
        if (!isSectionActive)
            return;

        isSectionActive = false;
        Interlocked.Increment(ref lifecycleGeneration);
        loadTask = null;
        IsLoadingSaves = false;
        localSavesViewModel.SetSectionActive(false);
    }

    public void ReleaseLocalObservation()
    {
        isSectionActive = false;
        Interlocked.Increment(ref lifecycleGeneration);
        loadTask = null;
        IsLoadingSaves = false;
        localSavesViewModel.ReleaseObservation();
    }

    public void SuspendLocalWatchersForInstanceRename()
    {
        localSavesViewModel.SuspendWatcherForInstanceRename();
    }

    public void ResumeLocalWatchersAfterInstanceRename(bool restart = true)
    {
        if (restart && isSectionActive)
        {
            Interlocked.Increment(ref lifecycleGeneration);
            loadTask = null;
            localSavesViewModel.InvalidateSnapshot();
        }
        localSavesViewModel.ResumeWatcherAfterInstanceRename(restart);
    }

    public override Task OnSectionActivatedAsync()
    {
        if (!isSectionActive)
        {
            // 首次进入后保留轻量监听；隐藏期间 watcher 只标记失效，不扫描目录或重建页面。
            isSectionActive = true;
            Interlocked.Increment(ref lifecycleGeneration);
            loadTask = null;
            localSavesViewModel.SetSectionActive(selectedInstance is not null);
        }

        if (selectedInstance is null)
            return Task.CompletedTask;

        if (HasLoadedSaves)
        {
            if (hasPendingVisualRefresh)
            {
                hasPendingVisualRefresh = false;
                RefreshFromLocalSaves();
            }

            loadTask = RefreshCachedSavesAsync(Volatile.Read(ref lifecycleGeneration));
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

        if (HasLoadedSaves)
            return Task.CompletedTask;

        loadTask = LoadSavesAsync(Volatile.Read(ref lifecycleGeneration));
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
