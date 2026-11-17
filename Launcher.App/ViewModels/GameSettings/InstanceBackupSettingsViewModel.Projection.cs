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
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class InstanceBackupSettingsViewModel
{
/// <summary>
    /// 从当前备份目录重新读取有效记录，并忽略已切换实例产生的陈旧结果。
    /// </summary>
    private async Task RefreshBackupsAsync()
    {
        // token 与目录字符串双重校验：即使多个目录恰好使用同一 token 流程，也不能跨目录发布。
        var token = ++refreshToken;
        var directory = BackupDirectory;
        var isInitialProjection = !HasLoadedBackups;
        if (string.IsNullOrWhiteSpace(directory))
        {
            // 空目录是合法的“尚未配置”状态，清空投影但标记加载完成，避免无限 Loading。
            allBackups = Array.Empty<InstanceBackupItemViewModel>();
            VisibleBackups = Array.Empty<InstanceBackupItemViewModel>();
            selectedBackupPaths.Clear();
            SelectedBackupCount = 0;
            BackupCount = 0;
            IsLoadingBackups = false;
            RefreshVisibleBackupItems(playEntranceAnimation: !isInitialProjection);
            if (isInitialProjection)
                PublishInitialProjectionReady();
            NotifyBackupListStateChanged();
            return;
        }

        IsLoadingBackups = true;
        try
        {
            var backups = await backupService.GetBackupsAsync(directory);
            // 等待 I/O 期间用户可能更换目录或实例，过期结果必须静默丢弃。
            if (token != refreshToken || !string.Equals(directory, BackupDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            allBackups = backups.Select(backup => new InstanceBackupItemViewModel(backup)).ToArray();
            BackupCount = allBackups.Count;
            RefreshVisibleBackupItems(playEntranceAnimation: !isInitialProjection);
        }
        catch (Exception exception)
        {
            // 只有当前请求可以展示错误；过期请求失败不应污染新目录页面。
            if (token != refreshToken)
                return;

            logger.LogWarning(
                exception,
                "Failed to refresh instance backups. InstanceDirectory={InstanceDirectory} BackupDirectory={BackupDirectory}",
                selectedInstance?.InstanceDirectory ?? "<none>",
                directory);
            allBackups = Array.Empty<InstanceBackupItemViewModel>();
            VisibleBackups = Array.Empty<InstanceBackupItemViewModel>();
            selectedBackupPaths.Clear();
            SelectedBackupCount = 0;
            BackupCount = 0;
            RefreshVisibleBackupItems(playEntranceAnimation: !isInitialProjection);
            statusService.Report(Strings.Status_LoadBackupsFailed);
        }
        finally
        {
            if (token == refreshToken)
            {
                IsLoadingBackups = false;
                if (isInitialProjection)
                    PublishInitialProjectionReady();
                NotifyBackupListStateChanged();
            }
        }
    }

    /// <summary>
    /// 应用搜索过滤并复用备份项状态，同时维护信息面板和列表分区项。
    /// </summary>
    private void RefreshVisibleBackupItems(bool playEntranceAnimation = true)
    {
        if (selectedInstance is null)
        {
            VisibleBackups = Array.Empty<InstanceBackupItemViewModel>();
            VisibleBackupListItems = Array.Empty<object>();
            selectedBackupPaths.Clear();
            UpdateSelectedBackupState();
            NotifyBackupListStateChanged();
            return;
        }

        // 搜索只作用于内存快照，不重新读取清单，因此输入响应保持即时。
        var query = BackupSearchQuery.Trim();
        VisibleBackups = string.IsNullOrWhiteSpace(query)
            ? allBackups
            : allBackups
                .Where(backup => backup.Matches(query))
                .ToArray();

        if (IsMultiSelectMode)
            // 不可见项从选择集合移除，确保“删除所选”与当前屏幕一致。
            selectedBackupPaths.IntersectWith(VisibleBackups.Select(backup => backup.FullPath));

        foreach (var backup in allBackups)
            backup.IsSelected = IsMultiSelectMode && selectedBackupPaths.Contains(backup.FullPath);

        UpdateSelectedBackupState();
        // 列表以信息面板、可选分区标题、数据项的固定结构提供给单一 ItemsControl。
        var hasListSection = VisibleBackups.Count > 0;
        var listItems = new object[VisibleBackups.Count + (hasListSection ? 2 : 1)];
        listItems[0] = BackupManagementInfoPanelItem.Instance;
        if (hasListSection)
            listItems[1] = BackupManagementListSectionItem.Instance;

        for (var index = 0; index < VisibleBackups.Count; index++)
            listItems[index + (hasListSection ? 2 : 1)] = VisibleBackups[index];

        VisibleBackupListItems = listItems;
        if (playEntranceAnimation && HasLoadedBackups && selectedInstance is not null)
            ListEntranceAnimationToken++;
        NotifyBackupListStateChanged();
    }

    private void PublishInitialProjectionReady()
    {
        HasLoadedBackups = true;
        if (selectedInstance is not null)
            ListEntranceAnimationToken++;
    }

    private void NotifyBackupListStateChanged()
    {
        OnPropertyChanged(nameof(HasVisibleBackups));
        OnPropertyChanged(nameof(AreAllVisibleBackupsSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        OnPropertyChanged(nameof(CanShowBackupLoadingState));
        OnPropertyChanged(nameof(CanShowBackupEmptyState));
        OnPropertyChanged(nameof(BackupEmptyMessage));
        SelectAllBackupsCommand.NotifyCanExecuteChanged();
    }

    private bool CanToggleSelectAllBackups()
    {
        return IsMultiSelectMode && HasVisibleBackups;
    }

    private void EnterMultiSelectMode()
    {
        IsMultiSelectMode = true;
        selectedBackupPaths.Clear();
        ClearVisibleSelections();
        UpdateSelectedBackupState();
    }

    private void ExitMultiSelectMode()
    {
        IsMultiSelectMode = false;
        selectedBackupPaths.Clear();
        ClearVisibleSelections();
        UpdateSelectedBackupState();
    }

    private void ClearVisibleSelections()
    {
        foreach (var backup in VisibleBackups)
            backup.IsSelected = false;
    }

    private IReadOnlyList<InstanceBackupItemViewModel> GetSelectedVisibleBackups()
    {
        return VisibleBackups.Where(backup => selectedBackupPaths.Contains(backup.FullPath)).ToArray();
    }

    private void UpdateSelectedBackupState()
    {
        SelectedBackupCount = VisibleBackups.Count(backup => backup.IsSelected);
    }
}
