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

public sealed partial class InstanceShaderPackManagementSettingsViewModel
{
/// <summary>
    /// 加载当前实例光影包，并复用尚未结束的同一加载任务。
    /// </summary>
    private async Task LoadShaderPacksAsync(long generation)
    {
        if (selectedInstance is null)
            return;
        var expectedInstance = selectedInstance;

        // Loading 与 HasLoaded 分离，首次进入和刷新失败可以呈现不同的空状态。
        SetInitialProjectionReady(false);
        IsLoadingShaderPacks = true;
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();

        try
        {
            if (!await localShaderPacksViewModel.RefreshShaderPacksAsync())
                return;
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;
            HasLoadedShaderPacks = true;
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
                "Failed to load shader packs for section activation. InstanceDirectory={InstanceDirectory}",
                expectedInstance.InstanceDirectory);
            HasLoadedShaderPacks = false;
            ClearDisplayedShaderPacks();
            hasPendingVisualRefresh = false;
            SetInitialProjectionReady(true);
            statusService.Report(Strings.Status_LoadLocalShaderPacksFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
            {
                IsLoadingShaderPacks = false;
                loadTask = null;
                OnPropertyChanged(nameof(InstalledSummaryText));
                RaiseAvailabilityPropertyChanges();
                OnPropertyChanged(nameof(ShaderPackEmptyMessage));
            }
        }
    }

    private async Task RefreshCachedShaderPacksAsync(long generation)
    {
        if (selectedInstance is null)
            return;
        var expectedInstance = selectedInstance;
        var previousRevision = localShaderPacksViewModel.Revision;

        try
        {
            if (!await localShaderPacksViewModel.RefreshIfInvalidatedAsync()
                || !IsCurrentLifecycle(generation, expectedInstance))
            {
                return;
            }

            if (localShaderPacksViewModel.Revision == previousRevision)
                return;

            hasPendingVisualRefresh = false;
            RefreshFromLocalShaderPacks();
        }
        catch (Exception exception)
        {
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;

            logger.LogError(
                exception,
                "Failed to silently refresh cached shader packs. InstanceDirectory={InstanceDirectory}",
                expectedInstance.InstanceDirectory);
            statusService.Report(Strings.Status_LoadLocalShaderPacksFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
                loadTask = null;
        }
    }

    private void RefreshSummary()
    {
        InstalledShaderPackCount = localShaderPacksViewModel.CurrentShaderPacks.Count;
    }

    private void LocalShaderPacksViewModel_ShaderPacksChanged(object? sender, EventArgs e)
    {
        if (suppressLocalCollectionEvents)
            return;

        if (!HasLoadedShaderPacks)
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

    private void LocalShaderPacksViewModel_IconChanged(object? sender, LocalContentIconChangedEventArgs e)
    {
        if (!HasLoadedShaderPacks || !isSectionActive)
            return;

        if (selectionState.ItemsByPath.TryGetValue(e.FullPath, out var item)
            && !string.Equals(item.IconSource, e.IconSource, StringComparison.Ordinal))
        {
            item.IconSource = e.IconSource;
        }
    }

    /// <summary>
    /// 增量更新筛选后的光影包列表，并保持单选或多选状态稳定。
    /// </summary>
    private void RefreshFromLocalShaderPacks()
    {
        // 用完整路径作为稳定身份复用 Item ViewModel，保持选择和图片等 UI 状态。
        var selectedFullPath = selectionState.LastSingleSelectedPath ?? SelectedShaderPack?.FullPath;
        var filteredShaderPacks = StableFilteredItemProjection.Synchronize(
            localShaderPacksViewModel.CurrentShaderPacks,
            selectionState.ItemsByPath,
            shaderPack => shaderPack.FullPath,
            shaderPack => new ShaderPackManagementItemViewModel(shaderPack),
            static (item, shaderPack) => item.SyncFrom(shaderPack),
            MatchesSearch);

        // 多选仅覆盖当前可见集合，搜索隐藏的项目不会被批量操作误处理。
        selectionState.SyncSelectionToItems(filteredShaderPacks, IsMultiSelectMode);
        SetVisibleShaderPacks(filteredShaderPacks);

        RefreshSummary();
        OnPropertyChanged(nameof(HasShaderPacks));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ShaderPackEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleShaderPacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllShaderPacksCommand.NotifyCanExecuteChanged();

        if (IsMultiSelectMode)
        {
            SelectedShaderPack = null;
            UpdateSelectedShaderPackState();
            return;
        }

        // 单选刷新后按路径恢复；原项消失时使用第一项保证详情面板有确定状态。
        var restoredSelection = ShaderPacks.FirstOrDefault(shaderPack =>
            string.Equals(shaderPack.FullPath, selectedFullPath, StringComparison.OrdinalIgnoreCase));
        SelectShaderPack(restoredSelection ?? ShaderPacks.FirstOrDefault());
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
            RefreshFromLocalShaderPacks();
        });
    }

    private void PublishReadyProjection()
    {
        hasPendingVisualRefresh = false;
        RefreshFromLocalShaderPacks();
        SetInitialProjectionReady(true);
        ListEntranceAnimationToken++;
    }

    private void SetInitialProjectionReady(bool value)
    {
        if (isInitialProjectionReady == value)
            return;

        isInitialProjectionReady = value;
        OnPropertyChanged(nameof(CanShowShaderPackScrollableContent));
    }

    private bool MatchesSearch(LocalShaderPack shaderPack)
    {
        if (string.IsNullOrWhiteSpace(ShaderPackSearchQuery))
            return true;

        var query = ShaderPackSearchQuery.Trim();
        return Contains(shaderPack.Name, query)
            || Contains(shaderPack.FileName, query);
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

    private bool CanToggleSelectAllShaderPacks()
    {
        return IsMultiSelectMode && HasShaderPacks;
    }

    private void RaiseAvailabilityPropertyChanges()
    {
        OnPropertyChanged(nameof(CanShowShaderPackInfoSection));
        OnPropertyChanged(nameof(CanShowShaderPackScrollableContent));
        OnPropertyChanged(nameof(HasInstalledShaderPacks));
        OnPropertyChanged(nameof(CanShowShaderPackListSection));
        OnPropertyChanged(nameof(CanShowNoShaderPacksEmptyState));
        OnPropertyChanged(nameof(CanShowShaderPackEmptyState));
        OnPropertyChanged(nameof(CanShowShaderPackLoadingState));
    }

    private void EnterMultiSelectMode()
    {
        var selectedShaderPack = SelectedShaderPack;
        IsMultiSelectMode = true;
        SelectedShaderPack = null;
        selectionState.BeginMultiSelect(selectedShaderPack, ShaderPacks);
        UpdateSelectedShaderPackState();
    }

    private void ExitMultiSelectMode()
    {
        IsMultiSelectMode = false;
        selectionState.ClearVisibleSelections(ShaderPacks);
        selectionState.ClearSelectedPaths();
        UpdateSelectedShaderPackState();

        var restoredSelection = ShaderPacks.FirstOrDefault(shaderPack =>
            string.Equals(shaderPack.FullPath, selectionState.LastSingleSelectedPath, StringComparison.OrdinalIgnoreCase));
        SelectShaderPack(restoredSelection ?? ShaderPacks.FirstOrDefault());
    }

    private void ResetSelectionState()
    {
        selectionState.Reset();
        IsMultiSelectMode = false;
        SelectedShaderPack = null;
        SelectedShaderPackCount = 0;
    }

    private void ClearDisplayedShaderPacks()
    {
        selectionState.ClearCache();
        SetVisibleShaderPacks(Array.Empty<ShaderPackManagementItemViewModel>());
        RefreshVisibleShaderPackListItems();
        SelectedShaderPack = null;
        RefreshSummary();
        OnPropertyChanged(nameof(HasShaderPacks));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ShaderPackEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleShaderPacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllShaderPacksCommand.NotifyCanExecuteChanged();
        UpdateSelectedShaderPackState();
    }

    private void ClearVisibleSelections()
    {
        selectionState.ClearVisibleSelections(ShaderPacks);
    }

    private IReadOnlyList<ShaderPackManagementItemViewModel> GetSelectedVisibleShaderPacks()
    {
        return selectionState.GetSelectedVisibleItems(ShaderPacks);
    }

    private IReadOnlyList<LocalShaderPack> ResolveLocalShaderPacks(IEnumerable<string> fullPaths)
    {
        var pathSet = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        return localShaderPacksViewModel.CurrentShaderPacks
            .Where(shaderPack => pathSet.Contains(shaderPack.FullPath))
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

    private void UpdateSelectedShaderPackState()
    {
        SelectedShaderPackCount = selectionState.CountSelectedVisibleItems(ShaderPacks);
    }

    partial void OnVisibleShaderPacksChanged(IReadOnlyList<ShaderPackManagementItemViewModel> value)
    {
        OnPropertyChanged(nameof(ShaderPacks));
        RefreshVisibleShaderPackListItems();
    }

    private void SetVisibleShaderPacks(IReadOnlyList<ShaderPackManagementItemViewModel> shaderPacks)
    {
        if (LocalContentListPresentation.HasSameReferences(VisibleShaderPacks, shaderPacks))
            return;

        VisibleShaderPacks = shaderPacks;
    }

    private void RefreshVisibleShaderPackListItems()
    {
        var items = LocalContentListPresentation.CreateSectionedItems(
            VisibleShaderPacks,
            ShaderPackManagementInfoPanelItem.Instance,
            ShaderPackManagementListSectionItem.Instance,
            CanShowShaderPackInfoSection);
        if (!LocalContentListPresentation.HasSameReferences(VisibleShaderPackListItems, items))
            VisibleShaderPackListItems = items;
    }
}
