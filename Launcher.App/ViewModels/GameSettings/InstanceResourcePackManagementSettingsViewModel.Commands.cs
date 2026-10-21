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
    [RelayCommand]
    private void InstallOnlineResourcePack()
    {
        if (selectedInstance is null)
            return;

        logger.LogInformation(
            "Online resource pack install requested from instance resource pack management. InstanceId={InstanceId}, MinecraftVersion={MinecraftVersion}",
            selectedInstance.Id,
            selectedInstance.MinecraftVersion);
        OnlineResourcePackInstallRequested?.Invoke(selectedInstance);
    }

    [RelayCommand]
    private void OpenResourceDetails(ResourcePackManagementItemViewModel? resourcePack)
    {
        if (resourcePack?.ProjectReference is { } reference)
            ResourceDetailsRequested?.Invoke(reference);
    }

[RelayCommand]
    private void OpenResourcePackFolder()
    {
        if (selectedInstance is null)
            return;

        try
        {
            var resourcePacksDirectory = instanceFolderService.EnsureDirectoryExists(
                Path.Combine(selectedInstance.InstanceDirectory, "resourcepacks"));
            logger.LogDebug(
                "Opening resource pack folder. InstanceId={InstanceId} ResourcePacksDirectory={ResourcePacksDirectory}",
                selectedInstance.Id,
                resourcePacksDirectory);

            if (!instanceFolderService.TryOpen(resourcePacksDirectory))
            {
                logger.LogWarning(
                    "Failed to open resource pack folder. InstanceId={InstanceId} ResourcePacksDirectory={ResourcePacksDirectory}",
                    selectedInstance.Id,
                    resourcePacksDirectory);
                statusService.Report(Strings.Status_OpenLocalResourcePackFolderFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to prepare resource pack folder for opening. InstanceId={InstanceId}",
                selectedInstance.Id);
            statusService.Report(Strings.Status_OpenLocalResourcePackFolderFailed);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportLocalResourcePack))]
    private async Task ImportLocalResourcePackAsync()
    {
        if (selectedInstance is null)
            return;

        var archivePath = filePickerService.PickResourcePackArchive();
        if (string.IsNullOrWhiteSpace(archivePath))
            return;

        await ImportResourcePackArchivesAsync([archivePath], ImportTriggerSource.FilePicker);
    }

    public GameSettingsFileDropEvaluation EvaluateDroppedFiles(IReadOnlyList<string> paths)
    {
        return TryValidateImportPaths(paths, Strings.GameSettings_DropResourcePackArchivesOnlyMessage, out var failureMessage)
            ? GameSettingsFileDropEvaluation.Accept(Strings.GameSettings_DropImportResourcePacksMessage)
            : GameSettingsFileDropEvaluation.Reject(failureMessage);
    }

    public Task ImportDroppedResourcePackArchivesAsync(IReadOnlyList<string> paths)
    {
        return ImportResourcePackArchivesAsync(paths, ImportTriggerSource.DragDrop);
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

    [RelayCommand(CanExecute = nameof(CanToggleSelectAllResourcePacks))]
    private void SelectAllResourcePacks()
    {
        if (AreAllVisibleResourcePacksSelected)
        {
            selectionState.ClearVisibleSelections(ResourcePacks);
            selectionState.ClearSelectedPaths();
            SelectedResourcePack = null;
            UpdateSelectedResourcePackState();
            return;
        }

        selectionState.SelectAll(ResourcePacks);
        SelectedResourcePack = null;
        UpdateSelectedResourcePackState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedResourcePacks))]
    private void RequestDeleteSelectedResourcePacks()
    {
        var selectedResourcePacks = GetSelectedVisibleResourcePacks();
        if (selectedResourcePacks.Count == 0)
            return;

        DeleteResourcePacksRequested?.Invoke(new ResourcePackDeleteRequest(
            selectedResourcePacks.Select(resourcePack => resourcePack.FullPath).ToArray(),
            selectedResourcePacks.Select(resourcePack => resourcePack.Title).ToArray()));
    }

    [RelayCommand]
    private void OpenResourcePackLocation(ResourcePackManagementItemViewModel? resourcePack)
    {
        if (resourcePack is null)
            return;

        try
        {
            if (!instanceFolderService.TryRevealFile(resourcePack.FullPath))
            {
                logger.LogWarning(
                    "Failed to reveal local resource pack file. InstanceId={InstanceId} Path={Path}",
                    selectedInstance?.Id ?? "<none>",
                    resourcePack.FullPath);
                statusService.Report(Strings.Status_OpenLocalResourcePackLocationFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to reveal local resource pack file. InstanceId={InstanceId} Path={Path}",
                selectedInstance?.Id ?? "<none>",
                resourcePack.FullPath);
            statusService.Report(Strings.Status_OpenLocalResourcePackLocationFailed);
        }
    }

    [RelayCommand]
    private void RequestDeleteResourcePack(ResourcePackManagementItemViewModel? resourcePack)
    {
        if (resourcePack is null)
            return;

        DeleteResourcePacksRequested?.Invoke(new ResourcePackDeleteRequest(
            [resourcePack.FullPath],
            [resourcePack.Title]));
    }

    [RelayCommand]
    private void SelectResourcePack(ResourcePackManagementItemViewModel? resourcePack)
    {
        if (resourcePack is null)
        {
            SelectedResourcePack = null;
            if (IsMultiSelectMode)
                selectionState.ClearSelectedPaths();
            selectionState.ClearVisibleSelections(ResourcePacks);
            UpdateSelectedResourcePackState();
            return;
        }

        if (IsMultiSelectMode)
        {
            selectionState.ToggleSelection(resourcePack);
            SelectedResourcePack = null;
            UpdateSelectedResourcePackState();
            return;
        }

        SelectedResourcePack = resourcePack;
        selectionState.SelectSingle(resourcePack, ResourcePacks);
    }

    /// <summary>
    /// 批量删除资源包，允许部分失败并在完成后统一同步本地快照。
    /// </summary>
    public async Task DeleteResourcePacksAsync(IReadOnlyList<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);

        // 路径必须重新匹配当前快照，刷新后已不存在的资源包不会进入删除服务。
        var resourcePacksToDelete = ResolveLocalResourcePacks(fullPaths);
        if (resourcePacksToDelete.Count == 0)
        {
            ExitMultiSelectMode();
            return;
        }

        logger.LogInformation(
            "Deleting selected resource packs. InstanceId={InstanceId} Count={Count}",
            selectedInstance?.Id ?? "<none>",
            resourcePacksToDelete.Count);
        try
        {
            // 底层逐项处理并返回失败数，使批量删除能够保留成功项而不是整体回滚。
            var failedCount = await localResourcePacksViewModel.DeleteResourcePacksAsync(resourcePacksToDelete);
            ExitMultiSelectMode();
            ReportBatchOperationResult(
                resourcePacksToDelete.Count,
                failedCount,
                Strings.Status_SelectedResourcePacksDeletedFormat,
                Strings.Status_SelectedResourcePacksDeletePartialFailedFormat);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete selected resource packs. InstanceId={InstanceId}",
                selectedInstance?.Id ?? "<none>");
            statusService.Report(Strings.Status_SelectedResourcePacksDeleteFailed);
        }
    }

    partial void OnInstalledResourcePackCountChanged(int value)
    {
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ResourcePackEmptyMessage));
    }

    partial void OnResourcePackSearchQueryChanged(string value)
    {
        RefreshFromLocalResourcePacks();
        OnPropertyChanged(nameof(ResourcePackEmptyMessage));
    }

    partial void OnSelectedResourcePackCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedResourcePacks));
        OnPropertyChanged(nameof(AreAllVisibleResourcePacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllResourcePacksCommand.NotifyCanExecuteChanged();
        RequestDeleteSelectedResourcePacksCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AreAllVisibleResourcePacksSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllResourcePacksCommand.NotifyCanExecuteChanged();
    }
}
