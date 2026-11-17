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
    /// 顺序导入多个存档压缩包，逐项记录结果并在批次结束后只刷新一次列表。
    /// </summary>
    private async Task ImportSaveArchivesAsync(IReadOnlyList<string> archivePaths, ImportTriggerSource source)
    {
        if (selectedInstance is null)
            return;
        var instance = selectedInstance;

        // 文件选择器和拖放共享校验，但错误呈现方式不同：拖放用状态栏，选择器用对话框。
        if (!TryValidateImportPaths(archivePaths, Strings.GameSettings_DropSaveArchivesOnlyMessage, out var validationMessage))
        {
            if (source is ImportTriggerSource.DragDrop)
            {
                statusService.Report(validationMessage);
            }
            else
            {
                SaveImportFailedRequested?.Invoke(new SaveImportFailureRequest(Strings.Dialog_UnsupportedSaveArchiveMessage));
            }

            return;
        }

        logger.LogInformation(
            "Starting local save import batch. InstanceDirectory={InstanceDirectory} Source={Source} FileCount={FileCount}",
            selectedInstance.InstanceDirectory,
            source,
            archivePaths.Count);

        // 协调器顺序执行并在首个业务失败时停止，防止连续弹出多个相同错误。
        var batch = await LocalContentImportBatchCoordinator.ExecuteAsync(
            archivePaths,
            async archivePath =>
            {
                if (!ReferenceEquals(instance, selectedInstance))
                    return LocalSaveImportResult.Failure(LocalSaveImportFailureReason.UnexpectedError);
                logger.LogDebug(
                    "Importing local save archive. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                    selectedInstance.InstanceDirectory,
                    archivePath);
                return await localSavesViewModel.ImportSaveFromArchiveAsync(archivePath, reportStatus: false);
            },
            result => result.IsSuccess);
        if (!ReferenceEquals(instance, selectedInstance)) return;

        if (batch.Failure is not null)
        {
            // 保留领域失败原因，在 App 层映射为资源化且可操作的提示。
            switch (batch.Failure.FailureReason)
            {
                case LocalSaveImportFailureReason.InvalidMinecraftSaveArchive:
                    SaveImportFailedRequested?.Invoke(new SaveImportFailureRequest(Strings.Dialog_InvalidSaveArchiveMessage));
                    break;
                case LocalSaveImportFailureReason.UnsupportedArchive:
                    SaveImportFailedRequested?.Invoke(new SaveImportFailureRequest(Strings.Dialog_UnsupportedSaveArchiveMessage));
                    break;
                case LocalSaveImportFailureReason.FileNotFound:
                    statusService.Report(Strings.Status_LocalSaveImportFileNotFound);
                    break;
                case LocalSaveImportFailureReason.UnexpectedError:
                    logger.LogWarning(
                        "Local save import failed unexpectedly after service call. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                        selectedInstance.InstanceDirectory,
                        batch.FailedPath);
                    statusService.Report(Strings.Status_LocalSaveImportFailed);
                    break;
            }
            return;
        }

        // 批次内集合变化由共享 ViewModel 合并处理，这里只负责最终用户反馈。
        if (batch.SuccessCount > 0)
        {
            statusService.Report(batch.SuccessCount == 1
                ? Strings.Status_LocalSaveImported
                : string.Format(Strings.Status_LocalSavesImportedFormat, batch.SuccessCount));
        }
        logger.LogInformation(
            "Local save import batch completed. InstanceDirectory={InstanceDirectory} RequestedCount={RequestedCount} ImportedCount={ImportedCount}",
            selectedInstance.InstanceDirectory,
            archivePaths.Count,
            batch.SuccessCount);
    }

    private bool TryValidateImportPaths(
        IReadOnlyList<string> paths,
        string invalidTypeMessage,
        out string failureMessage)
    {
        return LocalContentImportPathEvaluator.TryValidate(
            importPathValidator,
            paths,
            InstanceContentImportKind.SaveArchive,
            invalidTypeMessage,
            out failureMessage);
    }
}
