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
[RelayCommand(CanExecute = nameof(CanOpenBackupDirectory))]
    private async Task OpenBackupFolderAsync()
    {
        if (selectedInstance is null || string.IsNullOrWhiteSpace(BackupDirectory))
            return;

        try
        {
            var normalizedDirectory = await backupService.EnsureBackupDirectoryAsync(BackupDirectory);
            BackupDirectory = normalizedDirectory;
            selectedInstance.BackupDirectory = normalizedDirectory;

            logger.LogDebug(
                "Opening instance backup folder. InstanceDirectory={InstanceDirectory} BackupDirectory={BackupDirectory}",
                selectedInstance.InstanceDirectory,
                normalizedDirectory);

            if (!instanceFolderService.TryOpen(normalizedDirectory))
            {
                logger.LogWarning(
                    "Failed to open instance backup folder. InstanceDirectory={InstanceDirectory} BackupDirectory={BackupDirectory}",
                    selectedInstance.InstanceDirectory,
                    normalizedDirectory);
                statusService.Report(Strings.Status_OpenBackupDirectoryFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to prepare instance backup folder for opening. InstanceDirectory={InstanceDirectory}",
                selectedInstance.InstanceDirectory);
            statusService.Report(Strings.Status_OpenBackupDirectoryFailed);
        }
    }

    [RelayCommand]
    private void ToggleMultiSelectMode()
    {
        if (IsMultiSelectMode)
        {
            ExitMultiSelectMode();
            return;
        }

        EnterMultiSelectMode();
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectAllBackups))]
    private void SelectAllBackups()
    {
        if (AreAllVisibleBackupsSelected)
        {
            ClearVisibleSelections();
            selectedBackupPaths.Clear();
            UpdateSelectedBackupState();
            return;
        }

        foreach (var backup in VisibleBackups)
        {
            backup.IsSelected = true;
            selectedBackupPaths.Add(backup.FullPath);
        }

        UpdateSelectedBackupState();
    }

    [RelayCommand(CanExecute = nameof(CanCreateBackupNow))]
    private void CreateBackupNow()
    {
        if (!CanCreateBackupNow)
            return;

        NewBackupName = string.Format(
            Strings.GameSettings_BackupDefaultNameFormat,
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH-mm"));
        IsCreateBackupDialogOpen = true;
        logger.LogDebug(
            "Instance backup naming dialog opened. InstanceDirectory={InstanceDirectory}",
            selectedInstance?.InstanceDirectory ?? "<none>");
    }

    /// <summary>
    /// 创建备份并在成功后刷新清单；失败时保留友好错误和诊断摘要。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirmCreateBackupDialog))]
    private Task ConfirmCreateBackupDialogAsync()
    {
        var operation = ConfirmCreateBackupDialogCoreAsync();
        downloadTasksPage.TrackBackgroundTask(operation);
        return operation;
    }

    private async Task ConfirmCreateBackupDialogCoreAsync()
    {
        if (selectedInstance is null || string.IsNullOrWhiteSpace(BackupDirectory) || string.IsNullOrWhiteSpace(NewBackupName))
            return;

        var instance = selectedInstance;
        var backupDirectory = BackupDirectory;
        // 先关闭命名对话框并锁定创建命令，防止用户重复提交同一备份。
        var backupName = NewBackupName.Trim();
        IsCreateBackupDialogOpen = false;
        IsCreatingBackup = true;
        floatingMessageService.Show(Strings.Status_BackupCreating);

        try
        {
            // 只有归档和清单都提交成功后才刷新页面；服务内部会清理临时文件。
            await backupService.CreateBackupAsync(instance, backupDirectory, backupName);
            await RefreshBackupsAsync();
            floatingMessageService.Show(Strings.Status_BackupCreated);
        }
        catch (Exception exception)
        {
            // 用户看到本地化原因和简短诊断，完整异常仍保留在日志中。
            logger.LogError(
                exception,
                "Failed to create instance backup from backup settings page. InstanceDirectory={InstanceDirectory}",
                instance.InstanceDirectory);
            BackupFailureDialogMessage = string.Format(
                Strings.Dialog_BackupCreateFailedMessageFormat,
                GetFriendlyBackupFailureMessage(exception),
                GetExceptionSummary(exception));
            IsBackupFailureDialogOpen = true;
        }
        finally
        {
            IsCreatingBackup = false;
        }
    }

    [RelayCommand]
    private void CancelCreateBackupDialog()
    {
        IsCreateBackupDialogOpen = false;
    }

    [RelayCommand]
    private void CloseBackupFailureDialog()
    {
        IsBackupFailureDialogOpen = false;
    }

    [RelayCommand]
    private void OpenBackupLocation(InstanceBackupItemViewModel? backup)
    {
        if (backup is null)
            return;

        try
        {
            if (!instanceFolderService.TryRevealFile(backup.FullPath))
            {
                logger.LogWarning(
                    "Failed to reveal instance backup file. InstanceDirectory={InstanceDirectory} BackupFile={BackupFile}",
                    selectedInstance?.InstanceDirectory ?? "<none>",
                    backup.FullPath);
                statusService.Report(Strings.Status_OpenBackupLocationFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to reveal instance backup file. InstanceDirectory={InstanceDirectory} BackupFile={BackupFile}",
                selectedInstance?.InstanceDirectory ?? "<none>",
                backup.FullPath);
            statusService.Report(Strings.Status_OpenBackupLocationFailed);
        }
    }

    [RelayCommand]
    private void RequestDeleteBackup(InstanceBackupItemViewModel? backup)
    {
        if (backup is null)
            return;

        pendingDeleteBackups = [backup];
        OnPropertyChanged(nameof(DeleteBackupDialogMessage));
        IsDeleteBackupDialogOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedBackups))]
    private void RequestDeleteSelectedBackups()
    {
        var selectedBackups = GetSelectedVisibleBackups();
        if (selectedBackups.Count == 0)
            return;

        pendingDeleteBackups = selectedBackups;
        OnPropertyChanged(nameof(DeleteBackupDialogMessage));
        IsDeleteBackupDialogOpen = true;
    }

    [RelayCommand]
    private void SelectBackup(InstanceBackupItemViewModel? backup)
    {
        if (backup is null || !IsMultiSelectMode)
            return;

        var isSelected = !backup.IsSelected;
        backup.IsSelected = isSelected;
        if (isSelected)
            selectedBackupPaths.Add(backup.FullPath);
        else
            selectedBackupPaths.Remove(backup.FullPath);

        UpdateSelectedBackupState();
    }

    [RelayCommand(CanExecute = nameof(CanRestoreBackup))]
    private void RequestRestoreBackup(InstanceBackupItemViewModel? backup)
    {
        if (backup is null)
            return;

        pendingRestoreBackup = backup;
        OnPropertyChanged(nameof(RestoreBackupDialogMessage));
        IsRestoreBackupDialogOpen = true;
    }

    [RelayCommand]
    private void CancelRestoreBackup()
    {
        pendingRestoreBackup = null;
        IsRestoreBackupDialogOpen = false;
        OnPropertyChanged(nameof(RestoreBackupDialogMessage));
    }

    /// <summary>
    /// 恢复已确认的备份，并在操作期间锁定其他会改变备份或实例内容的命令。
    /// </summary>
    [RelayCommand]
    private Task ConfirmRestoreBackupAsync()
    {
        var operation = ConfirmRestoreBackupCoreAsync();
        downloadTasksPage.TrackBackgroundTask(operation);
        return operation;
    }

    private async Task ConfirmRestoreBackupCoreAsync()
    {
        if (selectedInstance is null || pendingRestoreBackup is null || string.IsNullOrWhiteSpace(BackupDirectory))
            return;

        var instance = selectedInstance;
        var backupDirectory = BackupDirectory;
        // 快照待恢复项并立即清空对话框状态，后续异步流程不再依赖可变字段。
        var backup = pendingRestoreBackup;
        pendingRestoreBackup = null;
        IsRestoreBackupDialogOpen = false;
        OnPropertyChanged(nameof(RestoreBackupDialogMessage));
        IsRestoringBackup = true;
        floatingMessageService.Show(Strings.Status_BackupRestoring);

        try
        {
            // 恢复前强制创建保护备份，即使目标备份损坏也能保留用户当前实例。
            var protectionBackupName = string.Format(
                Strings.GameSettings_BackupPreRestoreNameFormat,
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH-mm"));
            await backupService.CreateBackupAsync(instance, backupDirectory, protectionBackupName);
            await backupService.RestoreBackupAsync(instance, backupDirectory, backup.FullPath);
            await RefreshRestoredInstanceAsync(instance);
            await RefreshBackupsAsync();
            floatingMessageService.Show(Strings.Status_BackupRestored);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to restore instance backup from backup settings page. InstanceDirectory={InstanceDirectory} BackupFile={BackupFile}",
                instance.InstanceDirectory,
                backup.FullPath);
            statusService.Report(Strings.Status_BackupRestoreFailed);
        }
        finally
        {
            IsRestoringBackup = false;
        }
    }

    private async Task RefreshRestoredInstanceAsync(GameInstance restoredInstance)
    {
        try
        {
            var instances = await instanceService.GetInstancesAsync();
            var current = instances.FirstOrDefault(instance =>
                MinecraftDirectoryPath.Equals(instance.InstanceDirectory, restoredInstance.InstanceDirectory));
            if (current is not null)
            {
                Parent.NotifyInstanceSettingsSaved(current);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to refresh restored instance. InstanceDirectory={InstanceDirectory}", restoredInstance.InstanceDirectory);
            statusService.Report(Strings.Status_LoadInstancesFailed);
        }
        Parent.NotifyInstancesReloadRequired();
    }

    [RelayCommand]
    private void CancelDeleteBackup()
    {
        pendingDeleteBackups = Array.Empty<InstanceBackupItemViewModel>();
        IsDeleteBackupDialogOpen = false;
        OnPropertyChanged(nameof(DeleteBackupDialogMessage));
    }

    /// <summary>
    /// 删除单个或多选备份，允许部分失败并在结束后重建可见选择状态。
    /// </summary>
    [RelayCommand]
    private Task ConfirmDeleteBackupAsync()
    {
        var operation = ConfirmDeleteBackupCoreAsync();
        downloadTasksPage.TrackBackgroundTask(operation);
        return operation;
    }

    private async Task ConfirmDeleteBackupCoreAsync()
    {
        if (pendingDeleteBackups.Count == 0 || string.IsNullOrWhiteSpace(BackupDirectory))
            return;

        var instanceDirectory = selectedInstance?.InstanceDirectory ?? "<none>";
        var backupDirectory = BackupDirectory;
        // 冻结本次删除集合；关闭对话框后新的选择不会混入正在执行的批次。
        var backups = pendingDeleteBackups;
        pendingDeleteBackups = Array.Empty<InstanceBackupItemViewModel>();
        IsDeleteBackupDialogOpen = false;
        OnPropertyChanged(nameof(DeleteBackupDialogMessage));

        try
        {
            // 清单与文件由服务逐项保持一致；全部完成后再重读目录作为最终事实来源。
            foreach (var backup in backups)
                await backupService.DeleteBackupAsync(backupDirectory, backup.FullPath);

            if (backups.Count > 1)
                ExitMultiSelectMode();

            await RefreshBackupsAsync();
            statusService.Report(backups.Count == 1
                ? Strings.Status_BackupDeleted
                : string.Format(Strings.Status_SelectedBackupsDeletedFormat, backups.Count));
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete instance backup from backup settings page. InstanceDirectory={InstanceDirectory} BackupCount={BackupCount}",
                instanceDirectory,
                backups.Count);
            statusService.Report(Strings.Status_BackupDeleteFailed);
        }
    }
}
