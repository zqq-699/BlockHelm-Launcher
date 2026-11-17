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

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.Utilities;
using Launcher.Domain.Models;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Download;

public sealed partial class DownloadLocalImportDialogViewModel
{
private async Task RunImportTaskAsync(
        string importPath,
        string importFileName,
        DownloadTaskItem importTask,
        DownloadSourcePreference taskDownloadSourcePreference,
        int taskDownloadSpeedLimitMbPerSecond)
    {
        using var diagnosticOperation = DownloadDiagnostics.BeginOperation(logger, "ModpackImport", importTask.Id);
        // 后台任务项与对话框共享进度源，结束后无论成败都必须解除 Busy 状态。
        try
        {
            var result = await modpackImportService.ImportFromArchiveAsync(
                importPath,
                CreateProgressReporter(importTask),
                importTask.CancellationToken,
                taskDownloadSourcePreference,
                taskDownloadSpeedLimitMbPerSecond);

            if (result.IsSuccess && result.ImportedInstance is not null)
            {
                if (result.HasManualDownloads)
                {
                    importTask.Complete(string.Format(Strings.Status_ModpackImportedWithManualDownloadsFormat, result.ImportedInstance.Name));
                    ExecuteOnUiThread(() => modpackManualDownloadsDialog.Show(result.ImportedInstance, result.ManualDownloads));
                }
                else
                {
                    importTask.Complete(string.Format(Strings.Status_ModpackImportedFormat, result.ImportedInstance.Name));
                }

                ModpackImported?.Invoke(this, result.ImportedInstance);
                return;
            }

            importTask.Fail(MapFailureMessage(result.FailureReason));
        }
        catch (OperationCanceledException) when (importTask.IsCancellationRequested)
        {
            downloadTasksPage.CancelTask(importTask);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected local modpack import failure. SelectedFileName={SelectedFileName}",
                importFileName);
            importTask.Fail(Strings.Status_ModpackImportFailed);
        }
    }

    [RelayCommand]
    private void ConfirmUnrecognized()
    {
        logger.LogInformation(
            "Acknowledged unrecognized local import file. SelectedFileName={SelectedFileName}",
            string.IsNullOrWhiteSpace(SelectedFileName) ? "<none>" : SelectedFileName);
        Close(resetDialogState: false);
    }
}
