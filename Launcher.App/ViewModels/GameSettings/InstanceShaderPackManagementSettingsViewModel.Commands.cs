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
    [RelayCommand]
    private void InstallOnlineShaderPack()
    {
        if (selectedInstance is null)
            return;

        logger.LogInformation(
            "Online shader pack install requested from instance shader pack management. InstanceId={InstanceId}, MinecraftVersion={MinecraftVersion}",
            selectedInstance.Id,
            selectedInstance.MinecraftVersion);
        OnlineShaderPackInstallRequested?.Invoke(selectedInstance);
    }

    [RelayCommand]
    private void OpenResourceDetails(ShaderPackManagementItemViewModel? shaderPack)
    {
        if (shaderPack?.ProjectReference is { } reference)
            ResourceDetailsRequested?.Invoke(reference);
    }

[RelayCommand]
    private void OpenShaderPackFolder()
    {
        if (selectedInstance is null)
            return;

        try
        {
            var shaderPacksDirectory = instanceFolderService.EnsureDirectoryExists(
                Path.Combine(selectedInstance.InstanceDirectory, "shaderpacks"));
            logger.LogDebug(
                "Opening shader pack folder. InstanceId={InstanceId} ShaderPacksDirectory={ShaderPacksDirectory}",
                selectedInstance.Id,
                shaderPacksDirectory);

            if (!instanceFolderService.TryOpen(shaderPacksDirectory))
            {
                logger.LogWarning(
                    "Failed to open shader pack folder. InstanceId={InstanceId} ShaderPacksDirectory={ShaderPacksDirectory}",
                    selectedInstance.Id,
                    shaderPacksDirectory);
                statusService.Report(Strings.Status_OpenLocalShaderPackFolderFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to prepare shader pack folder for opening. InstanceId={InstanceId}",
                selectedInstance.Id);
            statusService.Report(Strings.Status_OpenLocalShaderPackFolderFailed);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportLocalShaderPack))]
    private async Task ImportLocalShaderPackAsync()
    {
        if (selectedInstance is null)
            return;

        var archivePath = filePickerService.PickShaderPackArchive();
        if (string.IsNullOrWhiteSpace(archivePath))
            return;

        await ImportShaderPackArchivesAsync([archivePath], ImportTriggerSource.FilePicker);
    }

    public GameSettingsFileDropEvaluation EvaluateDroppedFiles(IReadOnlyList<string> paths)
    {
        return TryValidateImportPaths(paths, Strings.GameSettings_DropShaderPackArchivesOnlyMessage, out var failureMessage)
            ? GameSettingsFileDropEvaluation.Accept(Strings.GameSettings_DropImportShaderPacksMessage)
            : GameSettingsFileDropEvaluation.Reject(failureMessage);
    }

    public Task ImportDroppedShaderPackArchivesAsync(IReadOnlyList<string> paths)
    {
        return ImportShaderPackArchivesAsync(paths, ImportTriggerSource.DragDrop);
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

    [RelayCommand(CanExecute = nameof(CanToggleSelectAllShaderPacks))]
    private void SelectAllShaderPacks()
    {
        if (AreAllVisibleShaderPacksSelected)
        {
            selectionState.ClearVisibleSelections(ShaderPacks);
            selectionState.ClearSelectedPaths();
            SelectedShaderPack = null;
            UpdateSelectedShaderPackState();
            return;
        }

        selectionState.SelectAll(ShaderPacks);
        SelectedShaderPack = null;
        UpdateSelectedShaderPackState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedShaderPacks))]
    private void RequestDeleteSelectedShaderPacks()
    {
        var selectedShaderPacks = GetSelectedVisibleShaderPacks();
        if (selectedShaderPacks.Count == 0)
            return;

        DeleteShaderPacksRequested?.Invoke(new ShaderPackDeleteRequest(
            selectedShaderPacks.Select(shaderPack => shaderPack.FullPath).ToArray(),
            selectedShaderPacks.Select(shaderPack => shaderPack.Title).ToArray()));
    }

    [RelayCommand]
    private void OpenShaderPackLocation(ShaderPackManagementItemViewModel? shaderPack)
    {
        if (shaderPack is null)
            return;

        try
        {
            if (!instanceFolderService.TryRevealFile(shaderPack.FullPath))
            {
                logger.LogWarning(
                    "Failed to reveal local shader pack file. InstanceId={InstanceId} Path={Path}",
                    selectedInstance?.Id ?? "<none>",
                    shaderPack.FullPath);
                statusService.Report(Strings.Status_OpenLocalShaderPackLocationFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to reveal local shader pack file. InstanceId={InstanceId} Path={Path}",
                selectedInstance?.Id ?? "<none>",
                shaderPack.FullPath);
            statusService.Report(Strings.Status_OpenLocalShaderPackLocationFailed);
        }
    }

    [RelayCommand]
    private void RequestDeleteShaderPack(ShaderPackManagementItemViewModel? shaderPack)
    {
        if (shaderPack is null)
            return;

        DeleteShaderPacksRequested?.Invoke(new ShaderPackDeleteRequest(
            [shaderPack.FullPath],
            [shaderPack.Title]));
    }

    [RelayCommand]
    private void SelectShaderPack(ShaderPackManagementItemViewModel? shaderPack)
    {
        if (shaderPack is null)
        {
            SelectedShaderPack = null;
            if (IsMultiSelectMode)
                selectionState.ClearSelectedPaths();
            selectionState.ClearVisibleSelections(ShaderPacks);
            UpdateSelectedShaderPackState();
            return;
        }

        if (IsMultiSelectMode)
        {
            selectionState.ToggleSelection(shaderPack);
            SelectedShaderPack = null;
            UpdateSelectedShaderPackState();
            return;
        }

        SelectedShaderPack = shaderPack;
        selectionState.SelectSingle(shaderPack, ShaderPacks);
    }

    /// <summary>
    /// 批量删除光影包，允许部分失败并在完成后统一同步本地快照。
    /// </summary>
    public async Task DeleteShaderPacksAsync(IReadOnlyList<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);

        // 路径必须重新匹配当前快照，刷新后已不存在的光影包不会进入删除服务。
        var shaderPacksToDelete = ResolveLocalShaderPacks(fullPaths);
        if (shaderPacksToDelete.Count == 0)
        {
            ExitMultiSelectMode();
            return;
        }

        logger.LogInformation(
            "Deleting selected shader packs. InstanceId={InstanceId} Count={Count}",
            selectedInstance?.Id ?? "<none>",
            shaderPacksToDelete.Count);
        try
        {
            // 底层逐项处理并返回失败数，使批量删除能够保留成功项而不是整体回滚。
            var failedCount = await localShaderPacksViewModel.DeleteShaderPacksAsync(shaderPacksToDelete);
            ExitMultiSelectMode();
            ReportBatchOperationResult(
                shaderPacksToDelete.Count,
                failedCount,
                Strings.Status_SelectedShaderPacksDeletedFormat,
                Strings.Status_SelectedShaderPacksDeletePartialFailedFormat);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete selected shader packs. InstanceId={InstanceId}",
                selectedInstance?.Id ?? "<none>");
            statusService.Report(Strings.Status_SelectedShaderPacksDeleteFailed);
        }
    }

    partial void OnInstalledShaderPackCountChanged(int value)
    {
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ShaderPackEmptyMessage));
    }

    partial void OnShaderPackSearchQueryChanged(string value)
    {
        RefreshFromLocalShaderPacks();
        OnPropertyChanged(nameof(ShaderPackEmptyMessage));
    }

    partial void OnSelectedShaderPackCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedShaderPacks));
        OnPropertyChanged(nameof(AreAllVisibleShaderPacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllShaderPacksCommand.NotifyCanExecuteChanged();
        RequestDeleteSelectedShaderPacksCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AreAllVisibleShaderPacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllShaderPacksCommand.NotifyCanExecuteChanged();
    }
}
