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

public sealed partial class InstanceSaveManagementSettingsViewModel
{
/// <summary>
    /// 首次加载当前实例存档，并合并页面激活期间的重复加载请求。
    /// </summary>
    private async Task LoadSavesAsync(long generation)
    {
        if (selectedInstance is null)
            return;
        var expectedInstance = selectedInstance;

        // 先发布 Loading 状态，让空列表能够显示骨架/加载提示而不是误报“没有存档”。
        SetInitialProjectionReady(false);
        IsLoadingSaves = true;
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();

        try
        {
            if (!await localSavesViewModel.RefreshSavesAsync())
                return;
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;
            HasLoadedSaves = true;
            // 隐藏页面不播放动画，只记住下次激活需要一次完整视觉刷新。
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
                "Failed to load saves for section activation. InstanceDirectory={InstanceDirectory}",
                expectedInstance.InstanceDirectory);
            HasLoadedSaves = false;
            ClearDisplayedSaves();
            hasPendingVisualRefresh = false;
            SetInitialProjectionReady(true);
            statusService.Report(Strings.Status_LoadLocalSavesFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
            {
                IsLoadingSaves = false;
                loadTask = null;
                OnPropertyChanged(nameof(InstalledSummaryText));
                RaiseAvailabilityPropertyChanges();
                OnPropertyChanged(nameof(SaveEmptyMessage));
            }
        }
    }

    private async Task RefreshCachedSavesAsync(long generation)
    {
        if (selectedInstance is null)
            return;
        var expectedInstance = selectedInstance;
        var previousRevision = localSavesViewModel.Revision;

        try
        {
            if (!await localSavesViewModel.RefreshIfInvalidatedAsync()
                || !IsCurrentLifecycle(generation, expectedInstance))
            {
                return;
            }

            if (localSavesViewModel.Revision == previousRevision)
                return;

            hasPendingVisualRefresh = false;
            RefreshFromLocalSaves();
        }
        catch (Exception exception)
        {
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;

            logger.LogError(
                exception,
                "Failed to silently refresh cached saves. InstanceDirectory={InstanceDirectory}",
                expectedInstance.InstanceDirectory);
            statusService.Report(Strings.Status_LoadLocalSavesFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
                loadTask = null;
        }
    }

    private void RefreshSummary()
    {
        InstalledSaveCount = localSavesViewModel.CurrentSaves.Count;
    }

    private void LocalSavesViewModel_SavesChanged(object? sender, EventArgs e)
    {
        if (suppressLocalCollectionEvents)
            return;

        if (!HasLoadedSaves)
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

    /// <summary>
    /// 将共享存档快照增量投影到可见列表，并恢复稳定路径对应的选择。
    /// </summary>
    private void RefreshFromLocalSaves()
    {
        // 先保存稳定路径；投影同步可能更换可见集合，但相同存档项会继续复用原 ViewModel。
        var selectedFullPath = selectionState.LastSingleSelectedPath ?? SelectedSave?.FullPath;
        var filteredSaves = StableFilteredItemProjection.Synchronize(
            localSavesViewModel.CurrentSaves,
            selectionState.ItemsByPath,
            save => save.FullPath,
            save => new SaveManagementSaveItemViewModel(save),
            static (item, save) => item.SyncFrom(save),
            MatchesSearch);

        // 搜索过滤后裁剪不可见选择，避免批量命令作用于用户当前看不到的项目。
        selectionState.SyncSelectionToItems(filteredSaves, IsMultiSelectMode);
        SetVisibleSaves(filteredSaves);

        RefreshSummary();
        OnPropertyChanged(nameof(HasSaves));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(SaveEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleSavesSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllSavesCommand.NotifyCanExecuteChanged();

        if (IsMultiSelectMode)
        {
            SelectedSave = null;
            UpdateSelectedSaveState();
            return;
        }

        // 单选模式优先恢复刷新前项目；项目消失时再选择新的第一项。
        var restoredSelection = Saves.FirstOrDefault(save =>
            string.Equals(save.FullPath, selectedFullPath, StringComparison.OrdinalIgnoreCase));
        SelectSave(restoredSelection ?? Saves.FirstOrDefault());
    }

    private void QueueVisibleRefresh()
    {
        if (isVisibleRefreshQueued)
            return;

        isVisibleRefreshQueued = true;
        uiDispatcher.Post(() =>
        {
            isVisibleRefreshQueued = false;
            // 调度执行时页面可能已经离开，不能在后台重建列表或消费动画令牌。
            if (!isSectionActive)
            {
                hasPendingVisualRefresh = true;
                return;
            }

            hasPendingVisualRefresh = false;
            RefreshFromLocalSaves();
        });
    }

    private void PublishReadyProjection()
    {
        hasPendingVisualRefresh = false;
        RefreshFromLocalSaves();
        SetInitialProjectionReady(true);
        ListEntranceAnimationToken++;
    }

    private void SetInitialProjectionReady(bool value)
    {
        if (isInitialProjectionReady == value)
            return;

        isInitialProjectionReady = value;
        OnPropertyChanged(nameof(CanShowSaveScrollableContent));
    }

    private bool MatchesSearch(LocalSave save)
    {
        if (string.IsNullOrWhiteSpace(SaveSearchQuery))
            return true;

        var query = SaveSearchQuery.Trim();
        return Contains(save.Name, query)
            || Contains(save.DirectoryName, query);
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

    private bool CanToggleSelectAllSaves()
    {
        return IsMultiSelectMode && HasSaves;
    }

    private void RaiseAvailabilityPropertyChanges()
    {
        OnPropertyChanged(nameof(CanShowSaveInfoSection));
        OnPropertyChanged(nameof(CanShowSaveScrollableContent));
        OnPropertyChanged(nameof(HasInstalledSaves));
        OnPropertyChanged(nameof(CanShowSaveListSection));
        OnPropertyChanged(nameof(CanShowNoSavesEmptyState));
        OnPropertyChanged(nameof(CanShowSaveEmptyState));
        OnPropertyChanged(nameof(CanShowSaveLoadingState));
    }

    private void EnterMultiSelectMode()
    {
        var selectedSave = SelectedSave;
        IsMultiSelectMode = true;
        SelectedSave = null;
        selectionState.BeginMultiSelect(selectedSave, Saves);
        UpdateSelectedSaveState();
    }

    private void ExitMultiSelectMode()
    {
        IsMultiSelectMode = false;
        selectionState.ClearVisibleSelections(Saves);
        selectionState.ClearSelectedPaths();
        UpdateSelectedSaveState();

        var restoredSelection = Saves.FirstOrDefault(save =>
            string.Equals(save.FullPath, selectionState.LastSingleSelectedPath, StringComparison.OrdinalIgnoreCase));
        SelectSave(restoredSelection ?? Saves.FirstOrDefault());
    }

    private void ResetSelectionState()
    {
        selectionState.Reset();
        IsMultiSelectMode = false;
        SelectedSave = null;
        SelectedSaveCount = 0;
    }

    private void ClearDisplayedSaves()
    {
        selectionState.ClearCache();
        SetVisibleSaves(Array.Empty<SaveManagementSaveItemViewModel>());
        RefreshVisibleSaveListItems();
        SelectedSave = null;
        RefreshSummary();
        OnPropertyChanged(nameof(HasSaves));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(SaveEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleSavesSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllSavesCommand.NotifyCanExecuteChanged();
        UpdateSelectedSaveState();
    }

    private void ClearVisibleSelections()
    {
        selectionState.ClearVisibleSelections(Saves);
    }

    private IReadOnlyList<SaveManagementSaveItemViewModel> GetSelectedVisibleSaves()
    {
        return selectionState.GetSelectedVisibleItems(Saves);
    }

    private IReadOnlyList<LocalSave> ResolveLocalSaves(IEnumerable<string> fullPaths)
    {
        var pathSet = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        return localSavesViewModel.CurrentSaves
            .Where(save => pathSet.Contains(save.FullPath))
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

    private void UpdateSelectedSaveState()
    {
        SelectedSaveCount = selectionState.CountSelectedVisibleItems(Saves);
    }

    partial void OnVisibleSavesChanged(IReadOnlyList<SaveManagementSaveItemViewModel> value)
    {
        OnPropertyChanged(nameof(Saves));
        RefreshVisibleSaveListItems();
    }

    private void SetVisibleSaves(IReadOnlyList<SaveManagementSaveItemViewModel> saves)
    {
        if (LocalContentListPresentation.HasSameReferences(VisibleSaves, saves))
            return;

        VisibleSaves = saves;
    }

    private void RefreshVisibleSaveListItems()
    {
        var items = LocalContentListPresentation.CreateSectionedItems(
            VisibleSaves,
            SaveManagementInfoPanelItem.Instance,
            SaveManagementListSectionItem.Instance,
            CanShowSaveInfoSection);
        if (!LocalContentListPresentation.HasSameReferences(VisibleSaveListItems, items))
            VisibleSaveListItems = items;
    }
}
