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

public sealed partial class InstanceModManagementSettingsViewModel
{
    /// <summary>
    /// 首次加载当前实例 Mod，并把并发调用合并到同一个加载任务。
    /// </summary>
    private async Task LoadModsAsync(long generation)
    {
        if (selectedInstance is null || !IsModManagementSupported)
            return;
        var expectedInstance = selectedInstance;

        // Loading 和 HasLoaded 分开表示“首次加载中”“已有结果”和“加载失败”三种状态。
        SetInitialProjectionReady(false);
        IsLoadingMods = true;
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();

        try
        {
            if (!await localModsViewModel.RefreshModsAsync())
                return;
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;
            HasLoadedMods = true;
            // 隐藏页面不消费入场动画；下次激活时才触发一次完整视觉更新。
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
                "Failed to load mods for section activation. InstanceId={InstanceId}",
                expectedInstance.Id);
            HasLoadedMods = false;
            ClearDisplayedMods();
            hasPendingVisualRefresh = false;
            SetInitialProjectionReady(true);
            statusService.Report(Strings.Status_LoadLocalModsFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
            {
                IsLoadingMods = false;
                loadTask = null;
                OnPropertyChanged(nameof(InstalledSummaryText));
                RaiseAvailabilityPropertyChanges();
                OnPropertyChanged(nameof(ModEmptyMessage));
            }
        }
    }

    private async Task RefreshCachedModsAsync(long generation)
    {
        if (selectedInstance is null || !IsModManagementSupported)
            return;
        var expectedInstance = selectedInstance;
        var previousRevision = localModsViewModel.Revision;

        try
        {
            if (!await localModsViewModel.RefreshIfInvalidatedAsync()
                || !IsCurrentLifecycle(generation, expectedInstance))
            {
                return;
            }

            if (localModsViewModel.Revision == previousRevision)
                return;

            hasPendingVisualRefresh = false;
            RefreshFromLocalMods();
        }
        catch (Exception exception)
        {
            if (!IsCurrentLifecycle(generation, expectedInstance))
                return;

            logger.LogError(
                exception,
                "Failed to silently refresh cached mods. InstanceId={InstanceId}",
                expectedInstance.Id);
            statusService.Report(Strings.Status_LoadLocalModsFailed);
        }
        finally
        {
            if (IsCurrentLifecycle(generation, expectedInstance))
                loadTask = null;
        }
    }

    private void RefreshSummary()
    {
        InstalledModCount = localModsViewModel.CurrentMods.Count;
        EnabledModCount = localModsViewModel.CurrentMods.Count(mod => mod.IsEnabled);
    }

    private void LocalModsViewModel_ModsChanged(object? sender, EventArgs e)
    {
        if (suppressLocalCollectionEvents)
            return;

        if (!HasLoadedMods)
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

    private void LocalModsViewModel_IconChanged(object? sender, LocalContentIconChangedEventArgs e)
    {
        if (!HasLoadedMods || !isSectionActive)
            return;

        if (allModsByFullPath.TryGetValue(e.FullPath, out var item)
            && !string.Equals(item.IconSource, e.IconSource, StringComparison.Ordinal))
        {
            item.IconSource = e.IconSource;
        }
    }

    /// <summary>
    /// 增量同步本地 Mod 快照到筛选列表，同时恢复单选或裁剪多选集合。
    /// </summary>
    private void RefreshFromLocalMods()
    {
        // 单个状态文件继续使用稳定路径复用；启用和禁用文件并存时改用精确路径，避免两个条目共享同一个 Item。
        var selectedPath = lastSingleSelectedModPath ?? SelectedMod?.FullPath;
        var stablePathCounts = localModsViewModel.CurrentMods
            .GroupBy(mod => GetStableModPath(mod.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var filteredMods = StableFilteredItemProjection.Synchronize(
            localModsViewModel.CurrentMods,
            allModsByProjectionPath,
            mod =>
            {
                var stablePath = GetStableModPath(mod.FullPath);
                return stablePathCounts[stablePath] > 1
                    ? mod.FullPath
                    : stablePath;
            },
            mod => new ModManagementModItemViewModel(mod),
            static (item, mod) => item.SyncFrom(mod),
            MatchesSearch);
        allModsByFullPath.Clear();
        foreach (var item in allModsByProjectionPath.Values)
            allModsByFullPath[item.FullPath] = item;
        SynchronizeModUpdateBusyStates();

        // 搜索或筛选隐藏的项目不应继续留在批量选择中。
        if (IsMultiSelectMode)
            selectedModPaths.IntersectWith(filteredMods.Select(mod => mod.FullPath));

        // 同步所有缓存项的选择标志，防止离开筛选后看到过期勾选状态。
        foreach (var item in allModsByProjectionPath.Values)
            item.IsSelected = IsMultiSelectMode && selectedModPaths.Contains(item.FullPath);

        SetVisibleMods(filteredMods);

        RefreshSummary();
        OnPropertyChanged(nameof(HasMods));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ModEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleModsSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllModsCommand.NotifyCanExecuteChanged();

        if (IsMultiSelectMode)
        {
            SelectedMod = null;
            UpdateSelectedModState();
            return;
        }

        // 同名双文件存在时优先恢复精确路径；正常启停重命名后再回退稳定路径。
        var restoredSelection = FindModByPreferredPath(selectedPath);
        SelectMod(restoredSelection ?? Mods.FirstOrDefault());
    }

    /// <summary>
    /// 将密集集合事件合并为一次 UI 调度，并按页面可见性决定立即刷新或延后。
    /// </summary>
    private void QueueVisibleRefresh()
    {
        // 合并同一 UI 循环内的多次集合事件；页面不可见时只记录一次待刷新标记。
        if (isVisibleRefreshQueued)
            return;

        isVisibleRefreshQueued = true;
        uiDispatcher.Post(() =>
        {
            isVisibleRefreshQueued = false;
            // 回调执行前页面可能已离开，隐藏页面只保留待刷新标志。
            if (!isSectionActive)
            {
                hasPendingVisualRefresh = true;
                return;
            }

            hasPendingVisualRefresh = false;
            RefreshFromLocalMods();
        });
    }

    private void PublishReadyProjection()
    {
        hasPendingVisualRefresh = false;
        RefreshFromLocalMods();
        SetInitialProjectionReady(true);
        ListEntranceAnimationToken++;
    }

    private void SetInitialProjectionReady(bool value)
    {
        if (isInitialProjectionReady == value)
            return;

        isInitialProjectionReady = value;
        OnPropertyChanged(nameof(CanShowModScrollableContent));
    }

    private bool MatchesSearch(LocalMod mod)
    {
        if (ModFilter is ModManagementFilter.Enabled && !mod.IsEnabled)
            return false;

        if (ModFilter is ModManagementFilter.Disabled && mod.IsEnabled)
            return false;

        if (string.IsNullOrWhiteSpace(ModSearchQuery))
            return true;

        var query = ModSearchQuery.Trim();
        return Contains(mod.Name, query)
            || Contains(mod.Loader, query)
            || Contains(mod.ModId, query)
            || Contains(mod.Version, query)
            || Contains(mod.FileName, query);
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

        return !string.Equals(selectedInstance.Id, instance.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(selectedInstance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanToggleSelectAllMods()
    {
        return IsMultiSelectMode && HasMods;
    }

    private void RaiseAvailabilityPropertyChanges()
    {
        OnPropertyChanged(nameof(IsModManagementSupported));
        OnPropertyChanged(nameof(CanShowModInfoSection));
        OnPropertyChanged(nameof(CanShowModScrollableContent));
        OnPropertyChanged(nameof(HasInstalledMods));
        OnPropertyChanged(nameof(CanShowModListSection));
        OnPropertyChanged(nameof(CanShowNoModsEmptyState));
        OnPropertyChanged(nameof(CanShowModEmptyState));
        OnPropertyChanged(nameof(CanShowModUnavailableState));
        OnPropertyChanged(nameof(CanShowModLoadingState));
        OnPropertyChanged(nameof(ModUnavailableMessage));
        OnPropertyChanged(nameof(CanUpdateAllMods));
        UpdateAllModsCommand.NotifyCanExecuteChanged();
    }

    private void SynchronizeModUpdateBusyStates()
    {
        foreach (var item in allModsByProjectionPath.Values)
        {
            var path = Path.GetFullPath(item.FullPath);
            item.IsUpdateBusy = IsBulkUpdateBusy || activeUpdatePaths.Contains(path);
        }
    }

    private void EnterMultiSelectMode()
    {
        lastSingleSelectedModPath = SelectedMod?.FullPath ?? lastSingleSelectedModPath;
        IsMultiSelectMode = true;
        SelectedMod = null;
        selectedModPaths.Clear();
        ClearVisibleSelections();
        UpdateSelectedModState();
    }

    private void ExitMultiSelectMode()
    {
        IsMultiSelectMode = false;
        ClearVisibleSelections();
        selectedModPaths.Clear();
        UpdateSelectedModState();

        var restoredSelection = FindModByPreferredPath(lastSingleSelectedModPath);
        SelectMod(restoredSelection ?? Mods.FirstOrDefault());
    }

    private void ResetSelectionState()
    {
        lastSingleSelectedModPath = null;
        IsMultiSelectMode = false;
        SelectedMod = null;
        selectedModPaths.Clear();
        SelectedModCount = 0;
    }

    private void ClearDisplayedMods()
    {
        allModsByProjectionPath.Clear();
        allModsByFullPath.Clear();
        SetVisibleMods(Array.Empty<ModManagementModItemViewModel>());
        RefreshVisibleModListItems();
        SelectedMod = null;
        RefreshSummary();
        OnPropertyChanged(nameof(HasMods));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ModEmptyMessage));
        OnPropertyChanged(nameof(AreAllVisibleModsSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllModsCommand.NotifyCanExecuteChanged();
        UpdateSelectedModState();
    }

    private void ClearVisibleSelections()
    {
        foreach (var mod in Mods)
            mod.IsSelected = false;
    }

    private IReadOnlyList<ModManagementModItemViewModel> GetSelectedVisibleMods()
    {
        return Mods.Where(mod => selectedModPaths.Contains(mod.FullPath)).ToArray();
    }

    private IReadOnlyList<LocalMod> ResolveLocalMods(IEnumerable<string> fullPaths)
    {
        var pathSet = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        return localModsViewModel.CurrentMods
            .Where(mod => pathSet.Contains(mod.FullPath))
            .ToArray();
    }

    private LocalMod? ResolveLocalMod(string fullPath)
    {
        var stablePath = GetStableModPath(fullPath);
        return localModsViewModel.CurrentMods.FirstOrDefault(mod =>
            string.Equals(mod.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            ?? localModsViewModel.CurrentMods.FirstOrDefault(mod =>
                string.Equals(GetStableModPath(mod.FullPath), stablePath, StringComparison.OrdinalIgnoreCase));
    }

    private ModManagementModItemViewModel? FindModByPreferredPath(string? preferredPath)
    {
        if (string.IsNullOrWhiteSpace(preferredPath))
            return null;

        return Mods.FirstOrDefault(mod =>
                   string.Equals(mod.FullPath, preferredPath, StringComparison.OrdinalIgnoreCase))
               ?? Mods.FirstOrDefault(mod =>
                   string.Equals(
                       GetStableModPath(mod.FullPath),
                       GetStableModPath(preferredPath),
                       StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 执行多选批量启停并保持选择集合与重命名后的稳定路径一致。
    /// </summary>
    private async Task SetSelectedModsEnabledAsync(bool enabled)
    {
        var selectedMods = ResolveLocalMods(selectedModPaths);
        if (selectedMods.Count == 0)
        {
            UpdateSelectedModState();
            return;
        }

        logger.LogInformation(
            "Changing selected mods enabled state. InstanceId={InstanceId} Count={Count} Enabled={Enabled}",
            selectedInstance?.Id ?? "<none>",
            selectedMods.Count,
            enabled);
        try
        {
            // 批量重命名会产生 watcher 事件；暂时抑制页面回调，最后主动同步一次。
            suppressLocalCollectionEvents = true;
            LocalModEnabledStateBatchResult result;
            try
            {
                result = await localModsViewModel.SetModsEnabledAsync(selectedMods, enabled);
            }
            finally
            {
                suppressLocalCollectionEvents = false;
            }

            // 文件启停会改变 .disabled 后缀，因此用服务返回后的新路径重建选择集合。
            selectedModPaths.Clear();
            selectedModPaths.UnionWith(selectedMods.Select(mod => mod.FullPath));

            RefreshFromLocalMods();
            ReportBatchOperationResult(
                selectedMods.Count,
                result.FailedCount,
                enabled
                    ? Strings.Status_SelectedModsEnabledFormat
                    : Strings.Status_SelectedModsDisabledFormat,
                enabled
                    ? Strings.Status_SelectedModsEnablePartialFailedFormat
                    : Strings.Status_SelectedModsDisablePartialFailedFormat);
            if (!string.IsNullOrWhiteSpace(result.FirstConflictTargetPath))
            {
                floatingMessageService.Show(
                    FormatModEnabledStateTargetExists(result.FirstConflictTargetPath));
            }
        }
        catch (Exception exception)
        {
            suppressLocalCollectionEvents = false;
            logger.LogError(
                exception,
                "Failed to change selected mods enabled state. InstanceId={InstanceId} Enabled={Enabled}",
                selectedInstance?.Id ?? "<none>",
                enabled);
            statusService.Report(enabled
                ? Strings.Status_SelectedModsEnableFailed
                : Strings.Status_SelectedModsDisableFailed);
            RefreshFromLocalMods();
        }
    }

    private static string FormatModEnabledStateTargetExists(string targetPath) =>
        string.Format(
            Strings.Status_ModEnabledStateTargetExistsFormat,
            Path.GetFileName(targetPath));

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

    private void UpdateSelectedModState()
    {
        SelectedModCount = Mods.Count(mod => mod.IsSelected);
    }

    partial void OnVisibleModsChanged(IReadOnlyList<ModManagementModItemViewModel> value)
    {
        OnPropertyChanged(nameof(Mods));
        RefreshVisibleModListItems();
    }

    private void SetVisibleMods(IReadOnlyList<ModManagementModItemViewModel> mods)
    {
        if (IsSameVisibleMods(mods))
            return;

        VisibleMods = mods;
    }

    private bool IsSameVisibleMods(IReadOnlyList<ModManagementModItemViewModel> mods)
    {
        if (VisibleMods.Count != mods.Count)
            return false;

        for (var index = 0; index < mods.Count; index++)
        {
            if (!ReferenceEquals(VisibleMods[index], mods[index]))
                return false;
        }

        return true;
    }

    private void RefreshVisibleModListItems()
    {
        if (!CanShowModInfoSection)
        {
            if (VisibleModListItems.Count > 0)
                VisibleModListItems = Array.Empty<object>();
            return;
        }

        if (IsSameVisibleModListItems())
            return;

        // ItemsControl 使用“信息面板 + 可选分区标题 + 数据项”的稳定异构结构。
        var hasListSection = localModsViewModel.CurrentMods.Count > 0;
        var items = new object[VisibleMods.Count + (hasListSection ? 2 : 1)];
        items[0] = ModManagementInfoPanelItem.Instance;
        if (hasListSection)
            items[1] = ModManagementListSectionItem.Instance;

        for (var index = 0; index < VisibleMods.Count; index++)
            items[index + (hasListSection ? 2 : 1)] = VisibleMods[index];

        VisibleModListItems = items;
    }

    private bool IsSameVisibleModListItems()
    {
        var hasListSection = localModsViewModel.CurrentMods.Count > 0;
        if (VisibleModListItems.Count != VisibleMods.Count + (hasListSection ? 2 : 1))
            return false;

        if (!ReferenceEquals(VisibleModListItems[0], ModManagementInfoPanelItem.Instance))
            return false;

        if (!hasListSection)
            return true;

        if (!ReferenceEquals(VisibleModListItems[1], ModManagementListSectionItem.Instance))
            return false;

        for (var index = 0; index < VisibleMods.Count; index++)
        {
            if (!ReferenceEquals(VisibleModListItems[index + 2], VisibleMods[index]))
                return false;
        }

        return true;
    }
}
