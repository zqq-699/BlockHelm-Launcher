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
    [RelayCommand]
    private async Task OpenModFolder()
    {
        if (selectedInstance is null)
            return;

        try
        {
            var modsDirectory = await instanceFolderService.EnsureInstanceContentDirectoryAsync(selectedInstance, ResourceProjectKind.Mod);
            logger.LogDebug(
                "Opening mod folder. InstanceDirectory={InstanceDirectory} ModsDirectory={ModsDirectory}",
                selectedInstance.InstanceDirectory,
                modsDirectory);

            if (!instanceFolderService.TryOpen(modsDirectory))
            {
                logger.LogWarning(
                    "Failed to open mod folder. InstanceDirectory={InstanceDirectory} ModsDirectory={ModsDirectory}",
                    selectedInstance.InstanceDirectory,
                    modsDirectory);
                statusService.Report(Strings.Status_OpenInstanceFolderFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to prepare mod folder for opening. InstanceDirectory={InstanceDirectory}",
                selectedInstance.InstanceDirectory);
            statusService.Report(Strings.Status_OpenInstanceFolderFailed);
        }
    }

    [RelayCommand]
    private async Task ImportLocalModAsync()
    {
        if (selectedInstance is null)
            return;

        var modPath = filePickerService.PickModFile();
        if (string.IsNullOrWhiteSpace(modPath))
            return;

        await ImportModFilesAsync([modPath], ImportTriggerSource.FilePicker);
    }

    public Task ReplaceImportedModAsync(string sourcePath)
    {
        ResolvePendingImportConflict(true);
        return Task.CompletedTask;
    }

    public void SkipPendingImportedModReplacement()
    {
        ResolvePendingImportConflict(false);
    }

    public GameSettingsFileDropEvaluation EvaluateDroppedFiles(IReadOnlyList<string> paths)
    {
        if (!IsModManagementSupported)
            return GameSettingsFileDropEvaluation.Reject(ModUnavailableMessage);

        return TryValidateImportPaths(paths, Strings.GameSettings_DropModsOnlyMessage, out var failureMessage)
            ? GameSettingsFileDropEvaluation.Accept(Strings.GameSettings_DropImportModsMessage)
            : GameSettingsFileDropEvaluation.Reject(failureMessage);
    }

    public Task ImportDroppedModFilesAsync(IReadOnlyList<string> paths)
    {
        return ImportModFilesAsync(paths, ImportTriggerSource.DragDrop);
    }

    private async Task ImportLocalModCoreAsync(string modPath, bool overwriteExisting)
    {
        if (selectedInstance is null || string.IsNullOrWhiteSpace(modPath))
            return;

        logger.LogDebug(
            "Importing local mod from file picker. InstanceDirectory={InstanceDirectory} SourcePath={SourcePath} OverwriteExisting={OverwriteExisting}",
            selectedInstance.InstanceDirectory,
            modPath,
            overwriteExisting);

        try
        {
            await localModsViewModel.ImportModFromPathAsync(modPath, overwriteExisting);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to import local mod. InstanceDirectory={InstanceDirectory} SourcePath={SourcePath} OverwriteExisting={OverwriteExisting}",
                selectedInstance.InstanceDirectory,
                modPath,
                overwriteExisting);
            statusService.Report(Strings.Status_LocalModImportFailed);
        }
    }

    /// <summary>
    /// 校验并顺序导入一组 Mod 文件，逐项处理重名冲突并汇总部分失败结果。
    /// </summary>
    private async Task ImportModFilesAsync(IReadOnlyList<string> paths, ImportTriggerSource source)
    {
        if (selectedInstance is null)
            return;
        var instance = selectedInstance;

        // 先对整个批次做类型/目录校验，避免导入一半后才发现输入中混有非法路径。
        if (!TryValidateImportPaths(paths, Strings.GameSettings_DropModsOnlyMessage, out var validationMessage))
        {
            statusService.Report(source is ImportTriggerSource.DragDrop
                ? validationMessage
                : Strings.Status_LocalModImportFailed);
            return;
        }

        logger.LogInformation(
            "Starting local mod import batch. InstanceDirectory={InstanceDirectory} Source={Source} FileCount={FileCount}",
            selectedInstance.InstanceDirectory,
            source,
            paths.Count);

        // 顺序处理是有意的：每个重名文件都可能需要等待独立的用户确认。
        var successCount = 0;
        foreach (var modPath in paths)
        {
            if (!ReferenceEquals(instance, selectedInstance)) return;
            var fileName = Path.GetFileName(modPath);
            var overwriteExisting = false;
            if (localModsViewModel.Mods.Any(mod => string.Equals(mod.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                // 冲突判断基于当前快照；确认替换后由服务再次负责安全覆盖。
                var replace = await RequestModImportConflictResolutionAsync(modPath, fileName);
                if (!ReferenceEquals(instance, selectedInstance)) return;
                if (!replace)
                {
                    logger.LogDebug(
                        "Skipping local mod replacement after user canceled conflict dialog. InstanceDirectory={InstanceDirectory} SourcePath={SourcePath}",
                        selectedInstance.InstanceDirectory,
                        modPath);
                    continue;
                }

                overwriteExisting = true;
            }

            try
            {
                var imported = await localModsViewModel.ImportModFromPathAsync(modPath, overwriteExisting, reportStatus: false);
                // 返回 false 是可预期业务失败；停止批次以免连续产生相同失败和提示。
                if (!imported)
                {
                    statusService.Report(Strings.Status_LocalModImportFailed);
                    return;
                }

                successCount++;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to import local mod during batch import. InstanceDirectory={InstanceDirectory} SourcePath={SourcePath} OverwriteExisting={OverwriteExisting}",
                    selectedInstance.InstanceDirectory,
                    modPath,
                    overwriteExisting);
                statusService.Report(Strings.Status_LocalModImportFailed);
                return;
            }
        }

        // 单个文件状态在导入期间不重复上报，只在批次结束时给出汇总。
        if (successCount > 0)
        {
            statusService.Report(successCount == 1
                ? Strings.Status_LocalModImported
                : string.Format(Strings.Status_LocalModsImportedFormat, successCount));
        }
        logger.LogInformation(
            "Local mod import batch completed. InstanceDirectory={InstanceDirectory} RequestedCount={RequestedCount} ImportedCount={ImportedCount} SkippedCount={SkippedCount}",
            selectedInstance.InstanceDirectory,
            paths.Count,
            successCount,
            paths.Count - successCount);
    }
}
