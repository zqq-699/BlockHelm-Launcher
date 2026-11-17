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

using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Download;

public sealed partial class DownloadPageViewModel
{
private bool CanHandleLocalImportDropCore(IReadOnlyList<string> paths)
    {
        if (!LocalImportDialog.CanAcceptDroppedFiles(paths))
            return false;
        var extension = Path.GetExtension(paths[0]);
        return string.Equals(extension, ".mrpack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private void CancelOptionsNavigation()
    {
        // CTS 只归本页面步骤所有；子 ViewModel 仍负责其内部网络请求生命周期。
        var cancellation = Interlocked.Exchange(ref optionsNavigationCancellation, null);
        cancellation?.Cancel();
    }

    private void ShowVersionList()
    {
        // 返回列表时清除安装选项瞬态状态，但保留版本缓存和滚动位置。
        CancelOptionsNavigation();
        CurrentStep = DownloadPageStep.VersionList;
        InstanceOptions.Deactivate();
        VersionList.ClearSelectedVersion();
    }

    private sealed class NullFilePickerService : IFilePickerService
    {
        public static NullFilePickerService Instance { get; } = new();
        private NullFilePickerService() { }
        public string? PickMinecraftSkin() => null;
        public string? PickJavaExecutable() => null;
        public string? PickLocalImportFile() => null;
        public string? PickModFile() => null;
        public string? PickSaveArchive() => null;
        public string? PickResourcePackArchive() => null;
        public string? PickShaderPackArchive() => null;
        public string? PickModpackExportArchive(string defaultFileName, ModpackExportKind kind) => null;
        public string? PickLaunchDiagnosticExportArchive(string instanceName) => null;
        public string? PickCustomDownloadDestination(string defaultFileName) => null;
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }

    private sealed class NullInstanceFolderService : IInstanceFolderService
    {
        public static NullInstanceFolderService Instance { get; } = new();
        private NullInstanceFolderService() { }
        public bool DirectoryExists(string folderPath) => false;
        public string EnsureDirectoryExists(string folderPath) => folderPath;
        public bool TryOpen(string folderPath) => false;
        public bool TryOpenFile(string filePath) => false;
        public bool TryRevealFile(string filePath) => false;
    }

    private sealed class NullLocalModpackImportService : ILocalModpackImportService
    {
        public static NullLocalModpackImportService Instance { get; } = new();
        private NullLocalModpackImportService() { }
        public Task<ModpackRecognitionResult> RecognizeArchiveAsync(
            string archivePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnsupportedArchive));
        }

        public Task<ModpackImportResult> ImportFromArchiveAsync(
            string archivePath,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0,
            Func<StagedModpackInstance, Task>? prepareInstanceAsync = null)
        {
            return Task.FromResult(ModpackImportResult.Failure(ModpackImportFailureReason.UnsupportedArchive));
        }
    }

    private sealed class NullFloatingMessageService : IFloatingMessageService
    {
        public static NullFloatingMessageService Instance { get; } = new();
        public event Action<FloatingMessageRequest>? MessageRequested { add { } remove { } }
        private NullFloatingMessageService() { }
        public void Show(string message) { }
        public void ShowDragHint(object source, string message) { }
        public void ClearDragHint(object source) { }
        public void ClearDragHint() { }
    }

    private sealed class RejectingExistingFilePathValidator : IExistingFilePathValidator
    {
        public static RejectingExistingFilePathValidator Instance { get; } = new();
        public bool TryNormalize(string? path, out string normalizedPath)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }
}
