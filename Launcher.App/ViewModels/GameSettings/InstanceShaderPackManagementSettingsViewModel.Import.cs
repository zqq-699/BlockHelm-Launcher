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
    /// 执行光影包批量导入并汇总结果，避免每个文件完成时重复刷新整列表。
    /// </summary>
    private async Task ImportShaderPackArchivesAsync(IReadOnlyList<string> archivePaths, ImportTriggerSource source)
    {
        if (selectedInstance is null)
            return;
        var instance = selectedInstance;

        // 拖放和文件选择器复用同一校验规则，仅错误反馈载体不同。
        if (!TryValidateImportPaths(archivePaths, Strings.GameSettings_DropShaderPackArchivesOnlyMessage, out var validationMessage))
        {
            if (source is ImportTriggerSource.DragDrop)
            {
                statusService.Report(validationMessage);
            }
            else
            {
                ShaderPackImportFailedRequested?.Invoke(
                    new ShaderPackImportFailureRequest(Strings.Dialog_UnsupportedShaderPackArchiveMessage));
            }

            return;
        }

        logger.LogInformation(
            "Starting local shader pack import batch. InstanceDirectory={InstanceDirectory} Source={Source} FileCount={FileCount}",
            selectedInstance.InstanceDirectory,
            source,
            archivePaths.Count);

        // 顺序执行避免同名目标和目录 watcher 互相竞争，并统一汇总成功数量。
        var batch = await LocalContentImportBatchCoordinator.ExecuteAsync(
            archivePaths,
            async archivePath =>
            {
                if (!ReferenceEquals(instance, selectedInstance))
                    return LocalShaderPackImportResult.Failure(LocalShaderPackImportFailureReason.UnexpectedError);
                logger.LogDebug(
                    "Importing local shader pack archive. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                    selectedInstance.InstanceDirectory,
                    archivePath);
                return await localShaderPacksViewModel.ImportShaderPackAsync(archivePath, reportStatus: false);
            },
            result => result.IsSuccess);
        if (!ReferenceEquals(instance, selectedInstance)) return;

        if (batch.Failure is not null)
        {
            // 保留服务返回的失败类型，在页面边界映射为资源化且可操作的提示。
            switch (batch.Failure.FailureReason)
            {
                case LocalShaderPackImportFailureReason.UnsupportedArchive:
                    ShaderPackImportFailedRequested?.Invoke(
                        new ShaderPackImportFailureRequest(Strings.Dialog_UnsupportedShaderPackArchiveMessage));
                    break;
                case LocalShaderPackImportFailureReason.FileNotFound:
                    statusService.Report(Strings.Status_LocalShaderPackImportFileNotFound);
                    break;
                case LocalShaderPackImportFailureReason.UnexpectedError:
                    logger.LogWarning(
                        "Local shader pack import failed unexpectedly after service call. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                        selectedInstance.InstanceDirectory,
                        batch.FailedPath);
                    statusService.Report(Strings.Status_LocalShaderPackImportFailed);
                    break;
            }
            return;
        }

        // 导入过程的集合变化由 watcher/共享 ViewModel 刷新，这里不重复操作集合。
        if (batch.SuccessCount > 0)
        {
            statusService.Report(batch.SuccessCount == 1
                ? Strings.Status_LocalShaderPackImported
                : string.Format(Strings.Status_LocalShaderPacksImportedFormat, batch.SuccessCount));
        }
        logger.LogInformation(
            "Local shader pack import batch completed. InstanceDirectory={InstanceDirectory} RequestedCount={RequestedCount} ImportedCount={ImportedCount}",
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
            InstanceContentImportKind.ShaderPack,
            invalidTypeMessage,
            out failureMessage);
    }
}
