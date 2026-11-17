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

public sealed partial class InstanceResourcePackManagementSettingsViewModel
{
/// <summary>
    /// 加载当前实例资源包，并复用尚未结束的同一加载任务。
    /// </summary>
    private async Task LoadResourcePacksAsync(long generation)
    {
        if (selectedInstance is null)
            return;
        var expectedInstance = selectedInstance;

        // Loading 与 HasLoaded 分离，首次进入和刷新失败可以呈现不同的空状态。
        SetInitialProjectionReady(false);
        IsLoadingResourcePacks = true;
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();

        try
        {
            if (!await localResourcePacksViewModel.RefreshResourcePacksAsync())
                return;
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;
            HasLoadedResourcePacks = true;
            // 只有可见页面立即播放列表动画，隐藏页面仅设置待刷新标志。
            if (isSectionActive)
                PublishReadyProjection();
            else
                hasPendingVisualRefresh = true;
        }
        catch (Exception exception)
        {
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;

            logger.LogError(
                exception,
                "Failed to load resource packs for section activation. InstanceDirectory={InstanceDirectory}",
                expectedInstance.InstanceDirectory);
            HasLoadedResourcePacks = false;
            ClearDisplayedResourcePacks();
            hasPendingVisualRefresh = false;
            SetInitialProjectionReady(true);
            statusService.Report(Strings.Status_LoadLocalResourcePacksFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
            {
                IsLoadingResourcePacks = false;
                loadTask = null;
                OnPropertyChanged(nameof(InstalledSummaryText));
                RaiseAvailabilityPropertyChanges();
                OnPropertyChanged(nameof(ResourcePackEmptyMessage));
            }
        }
    }

    private async Task RefreshCachedResourcePacksAsync(long generation)
    {
        if (selectedInstance is null)
            return;
        var expectedInstance = selectedInstance;
        var previousRevision = localResourcePacksViewModel.Revision;

        try
        {
            if (!await localResourcePacksViewModel.RefreshIfInvalidatedAsync()
                || !IsCurrentLifecycle(generation, expectedInstance))
            {
                return;
            }

            if (localResourcePacksViewModel.Revision == previousRevision)
                return;

            hasPendingVisualRefresh = false;
            RefreshFromLocalResourcePacks();
        }
        catch (Exception exception)
        {
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;

            logger.LogError(
                exception,
                "Failed to silently refresh cached resource packs. InstanceDirectory={InstanceDirectory}",
                expectedInstance.InstanceDirectory);
            statusService.Report(Strings.Status_LoadLocalResourcePacksFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
                loadTask = null;
        }
    }

    private void RefreshSummary()
    {
        InstalledResourcePackCount = localResourcePacksViewModel.CurrentResourcePacks.Count;
    }

    private void LocalResourcePacksViewModel_ResourcePacksChanged(object? sender, EventArgs e)
    {
        if (suppressLocalCollectionEvents)
            return;

        if (!HasLoadedResourcePacks)
        {
            hasPendingVisualRefresh = true;
            return;
        }

        if (!isSectionActive)
        {
            hasPendingVisualRefresh = true;
            return;
        }

        QueueVisibleRefresh();
    }

    private void LocalResourcePacksViewModel_IconChanged(object? sender, LocalContentIconChangedEventArgs e)
    {
        if (!HasLoadedResourcePacks || !isSectionActive)
            return;

        if (selectionState.ItemsByPath.TryGetValue(e.FullPath, out var item)
            && !string.Equals(item.IconSource, e.IconSource, StringComparison.Ordinal))
        {
            item.IconSource = e.IconSource;
        }
    }

    /// <summary>
    /// 增量更新筛选后的资源包列表，并保持单选或多选状态稳定。
    /// </summary>
    private void RefreshFromLocalResourcePacks()
    {
        // 用完整路径作为稳定身份复用 Item ViewModel，保持选择和图片等 UI 状态。
        var selectedFullPath = selectionState.LastSingleSelectedPath ?? SelectedResourcePack?.FullPath;
        var filteredResourcePacks = StableFilteredItemProjection.Synchronize(
            localResourcePacksViewModel.CurrentResourcePacks,
            selectionState.ItemsByPath,
            resourcePack => resourcePack.FullPath,
            resourcePack => new ResourcePackManagementItemViewModel(resourcePack),
            static (item, resourcePack) => item.SyncFrom(resourcePack),
            MatchesSearch);

        // 多选仅覆盖当前可见集合，搜索隐藏的项目不会被批量操作误处理。
        selectionState.SyncSelectionToItems(filteredResourcePacks, IsMultiSelectMode);
        SetVisibleResourcePacks(filteredResourcePacks);

        RefreshSummary();
        OnPropertyChanged(nameof(HasResourcePacks));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ResourcePackEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleResourcePacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllResourcePacksCommand.NotifyCanExecuteChanged();

        if (IsMultiSelectMode)
        {
            SelectedResourcePack = null;
            UpdateSelectedResourcePackState();
            return;
        }

        // 单选刷新后按路径恢复；原项消失时使用第一项保证详情面板有确定状态。
        var restoredSelection = ResourcePacks.FirstOrDefault(resourcePack =>
            string.Equals(resourcePack.FullPath, selectedFullPath, StringComparison.OrdinalIgnoreCase));
        SelectResourcePack(restoredSelection ?? ResourcePacks.FirstOrDefault());
    }

    private void QueueVisibleRefresh()
    {
        if (isVisibleRefreshQueued)
            return;

        isVisibleRefreshQueued = true;
        uiDispatcher.Post(() =>
        {
            isVisibleRefreshQueued = false;
            // UI 回调执行时重新判断激活状态，防止页面离开后的排队任务修改隐藏页面。
            if (!isSectionActive)
            {
                hasPendingVisualRefresh = true;
                return;
            }

            hasPendingVisualRefresh = false;
            RefreshFromLocalResourcePacks();
        });
    }

    private void PublishReadyProjection()
    {
        hasPendingVisualRefresh = false;
        RefreshFromLocalResourcePacks();
        SetInitialProjectionReady(true);
        ListEntranceAnimationToken++;
    }

    private void SetInitialProjectionReady(bool value)
    {
        if (isInitialProjectionReady == value)
            return;

        isInitialProjectionReady = value;
        OnPropertyChanged(nameof(CanShowResourcePackScrollableContent));
    }

    private bool MatchesSearch(LocalResourcePack resourcePack)
    {
        if (string.IsNullOrWhiteSpace(ResourcePackSearchQuery))
            return true;

        var query = ResourcePackSearchQuery.Trim();
        return Contains(resourcePack.Name, query)
            || Contains(resourcePack.FileName, query);
    }

    private static bool Contains(string? source, string query)
    {
        return !string.IsNullOrWhiteSpace(source)
            && source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldResetForInstanceReference(GameInstance? instance)
    {
        if (selectedInstance is null || instance is null)
            return selectedInstance is not null || instance is not null;

        return !string.Equals(selectedInstance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(selectedInstance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanToggleSelectAllResourcePacks()
    {
        return IsMultiSelectMode && HasResourcePacks;
    }

    private void RaiseAvailabilityPropertyChanges()
    {
        OnPropertyChanged(nameof(CanShowResourcePackInfoSection));
        OnPropertyChanged(nameof(CanShowResourcePackScrollableContent));
        OnPropertyChanged(nameof(HasInstalledResourcePacks));
        OnPropertyChanged(nameof(CanShowResourcePackListSection));
        OnPropertyChanged(nameof(CanShowNoResourcePacksEmptyState));
        OnPropertyChanged(nameof(CanShowResourcePackEmptyState));
        OnPropertyChanged(nameof(CanShowResourcePackLoadingState));
    }

    private void EnterMultiSelectMode()
    {
        var selectedResourcePack = SelectedResourcePack;
        IsMultiSelectMode = true;
        SelectedResourcePack = null;
        selectionState.BeginMultiSelect(selectedResourcePack, ResourcePacks);
        UpdateSelectedResourcePackState();
    }

    private void ExitMultiSelectMode()
    {
        IsMultiSelectMode = false;
        selectionState.ClearVisibleSelections(ResourcePacks);
        selectionState.ClearSelectedPaths();
        UpdateSelectedResourcePackState();

        var restoredSelection = ResourcePacks.FirstOrDefault(resourcePack =>
            string.Equals(resourcePack.FullPath, selectionState.LastSingleSelectedPath, StringComparison.OrdinalIgnoreCase));
        SelectResourcePack(restoredSelection ?? ResourcePacks.FirstOrDefault());
    }

    private void ResetSelectionState()
    {
        selectionState.Reset();
        IsMultiSelectMode = false;
        SelectedResourcePack = null;
        SelectedResourcePackCount = 0;
    }

    private void ClearDisplayedResourcePacks()
    {
        selectionState.ClearCache();
        SetVisibleResourcePacks(Array.Empty<ResourcePackManagementItemViewModel>());
        RefreshVisibleResourcePackListItems();
        SelectedResourcePack = null;
        RefreshSummary();
        OnPropertyChanged(nameof(HasResourcePacks));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ResourcePackEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleResourcePacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllResourcePacksCommand.NotifyCanExecuteChanged();
        UpdateSelectedResourcePackState();
    }

    private void ClearVisibleSelections()
    {
        selectionState.ClearVisibleSelections(ResourcePacks);
    }

    private IReadOnlyList<ResourcePackManagementItemViewModel> GetSelectedVisibleResourcePacks()
    {
        return selectionState.GetSelectedVisibleItems(ResourcePacks);
    }

    private IReadOnlyList<LocalResourcePack> ResolveLocalResourcePacks(IEnumerable<string> fullPaths)
    {
        var pathSet = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        return localResourcePacksViewModel.CurrentResourcePacks
            .Where(resourcePack => pathSet.Contains(resourcePack.FullPath))
            .ToArray();
    }

    private void ReportBatchOperationResult(
        int totalCount,
        int failedCount,
        string successFormat,
        string partialFailureFormat)
    {
        if (failedCount <= 0)
        {
            statusService.Report(string.Format(successFormat, totalCount));
            return;
        }

        statusService.Report(string.Format(partialFailureFormat, totalCount - failedCount, failedCount));
    }

    private void UpdateSelectedResourcePackState()
    {
        SelectedResourcePackCount = selectionState.CountSelectedVisibleItems(ResourcePacks);
    }

    partial void OnVisibleResourcePacksChanged(IReadOnlyList<ResourcePackManagementItemViewModel> value)
    {
        OnPropertyChanged(nameof(ResourcePacks));
        RefreshVisibleResourcePackListItems();
    }

    private void SetVisibleResourcePacks(IReadOnlyList<ResourcePackManagementItemViewModel> resourcePacks)
    {
        if (LocalContentListPresentation.HasSameReferences(VisibleResourcePacks, resourcePacks))
            return;

        VisibleResourcePacks = resourcePacks;
    }

    private void RefreshVisibleResourcePackListItems()
    {
        var items = LocalContentListPresentation.CreateSectionedItems(
            VisibleResourcePacks,
            ResourcePackManagementInfoPanelItem.Instance,
            ResourcePackManagementListSectionItem.Instance,
            CanShowResourcePackInfoSection);
        if (!LocalContentListPresentation.HasSameReferences(VisibleResourcePackListItems, items))
            VisibleResourcePackListItems = items;
    }
}
