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
    [RelayCommand]
    private void InstallOnlineSave()
    {
        if (selectedInstance is null)
            return;

        logger.LogInformation(
            "Online save install requested from instance save management. InstanceDirectory={InstanceDirectory}, MinecraftVersion={MinecraftVersion}",
            selectedInstance.InstanceDirectory,
            selectedInstance.MinecraftVersion);
        OnlineSaveInstallRequested?.Invoke(selectedInstance);
    }

[RelayCommand]
    private async Task OpenSaveFolder()
    {
        if (selectedInstance is null)
            return;

        try
        {
            var savesDirectory = await instanceFolderService.EnsureInstanceContentDirectoryAsync(selectedInstance, ResourceProjectKind.World);
            logger.LogDebug(
                "Opening save folder. InstanceDirectory={InstanceDirectory} SavesDirectory={SavesDirectory}",
                selectedInstance.InstanceDirectory,
                savesDirectory);

            if (!instanceFolderService.TryOpen(savesDirectory))
            {
                logger.LogWarning(
                    "Failed to open save folder. InstanceDirectory={InstanceDirectory} SavesDirectory={SavesDirectory}",
                    selectedInstance.InstanceDirectory,
                    savesDirectory);
                statusService.Report(Strings.Status_OpenLocalSaveFolderFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to prepare save folder for opening. InstanceDirectory={InstanceDirectory}",
                selectedInstance.InstanceDirectory);
            statusService.Report(Strings.Status_OpenLocalSaveFolderFailed);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportLocalSave))]
    private async Task ImportLocalSaveAsync()
    {
        if (selectedInstance is null)
            return;

        var archivePath = filePickerService.PickSaveArchive();
        if (string.IsNullOrWhiteSpace(archivePath))
            return;

        await ImportSaveArchivesAsync([archivePath], ImportTriggerSource.FilePicker);
    }

    public GameSettingsFileDropEvaluation EvaluateDroppedFiles(IReadOnlyList<string> paths)
    {
        return TryValidateImportPaths(paths, Strings.GameSettings_DropSaveArchivesOnlyMessage, out var failureMessage)
            ? GameSettingsFileDropEvaluation.Accept(Strings.GameSettings_DropImportSavesMessage)
            : GameSettingsFileDropEvaluation.Reject(failureMessage);
    }

    public Task ImportDroppedSaveArchivesAsync(IReadOnlyList<string> paths)
    {
        return ImportSaveArchivesAsync(paths, ImportTriggerSource.DragDrop);
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

    [RelayCommand(CanExecute = nameof(CanToggleSelectAllSaves))]
    private void SelectAllSaves()
    {
        if (AreAllVisibleSavesSelected)
        {
            selectionState.ClearVisibleSelections(Saves);
            selectionState.ClearSelectedPaths();
            SelectedSave = null;
            UpdateSelectedSaveState();
            return;
        }

        selectionState.SelectAll(Saves);
        SelectedSave = null;
        UpdateSelectedSaveState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSaves))]
    private void RequestDeleteSelectedSaves()
    {
        var selectedSaves = GetSelectedVisibleSaves();
        if (selectedSaves.Count == 0)
            return;

        DeleteSavesRequested?.Invoke(new SaveDeleteRequest(
            selectedSaves.Select(save => save.FullPath).ToArray(),
            selectedSaves.Select(save => save.Title).ToArray()));
    }

    [RelayCommand]
    private void OpenSaveLocation(SaveManagementSaveItemViewModel? save)
    {
        if (save is null)
            return;

        try
        {
            if (!instanceFolderService.TryOpen(save.FullPath))
            {
                logger.LogWarning(
                    "Failed to open local save directory. InstanceDirectory={InstanceDirectory} Path={Path}",
                    selectedInstance?.InstanceDirectory ?? "<none>",
                    save.FullPath);
                statusService.Report(Strings.Status_OpenLocalSaveFolderFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to open local save directory. InstanceDirectory={InstanceDirectory} Path={Path}",
                selectedInstance?.InstanceDirectory ?? "<none>",
                save.FullPath);
            statusService.Report(Strings.Status_OpenLocalSaveFolderFailed);
        }
    }

    [RelayCommand]
    private void RequestDeleteSave(SaveManagementSaveItemViewModel? save)
    {
        if (save is null)
            return;

        DeleteSavesRequested?.Invoke(new SaveDeleteRequest(
            [save.FullPath],
            [save.Title]));
    }

    [RelayCommand]
    private void SelectSave(SaveManagementSaveItemViewModel? save)
    {
        if (save is null)
        {
            SelectedSave = null;
            if (IsMultiSelectMode)
                selectionState.ClearSelectedPaths();
            selectionState.ClearVisibleSelections(Saves);
            UpdateSelectedSaveState();
            return;
        }

        if (IsMultiSelectMode)
        {
            selectionState.ToggleSelection(save);
            SelectedSave = null;
            UpdateSelectedSaveState();
            return;
        }

        SelectedSave = save;
        selectionState.SelectSingle(save, Saves);
    }

    /// <summary>
    /// 批量删除解析出的本地存档，汇总部分失败并统一刷新页面快照。
    /// </summary>
    public async Task DeleteSavesAsync(IReadOnlyList<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);

        // 将 UI 路径重新解析为当前快照对象，自动忽略刷新后已不存在的选择。
        var savesToDelete = ResolveLocalSaves(fullPaths);
        if (savesToDelete.Count == 0)
        {
            ExitMultiSelectMode();
            return;
        }

        logger.LogInformation(
            "Deleting selected saves. InstanceDirectory={InstanceDirectory} Count={Count}",
            selectedInstance?.InstanceDirectory ?? "<none>",
            savesToDelete.Count);
        try
        {
            // 底层批处理会继续处理剩余项并返回失败数，因此页面可以给出准确的部分成功提示。
            var failedCount = await localSavesViewModel.DeleteSavesAsync(savesToDelete);
            ExitMultiSelectMode();
            ReportBatchOperationResult(
                savesToDelete.Count,
                failedCount,
                Strings.Status_SelectedSavesDeletedFormat,
                Strings.Status_SelectedSavesDeletePartialFailedFormat);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete selected saves. InstanceDirectory={InstanceDirectory}",
                selectedInstance?.InstanceDirectory ?? "<none>");
            statusService.Report(Strings.Status_SelectedSavesDeleteFailed);
        }
    }

    partial void OnInstalledSaveCountChanged(int value)
    {
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(SaveEmptyMessage));
    }

    partial void OnSaveSearchQueryChanged(string value)
    {
        RefreshFromLocalSaves();
        OnPropertyChanged(nameof(SaveEmptyMessage));
    }

    partial void OnSelectedSaveCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedSaves));
        OnPropertyChanged(nameof(AreAllVisibleSavesSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllSavesCommand.NotifyCanExecuteChanged();
        RequestDeleteSelectedSavesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AreAllVisibleSavesSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllSavesCommand.NotifyCanExecuteChanged();
    }
}
