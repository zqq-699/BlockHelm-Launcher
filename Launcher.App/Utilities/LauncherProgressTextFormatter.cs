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

using Launcher.App.Resources;
using Launcher.Domain.Models;

namespace Launcher.App.Utilities;

internal static class LauncherProgressTextFormatter
{
    public static string FormatDownloadSpeed(DownloadSpeedTelemetry telemetry)
    {
        if (telemetry.BytesPerSecond is not { } bytesPerSecond || bytesPerSecond <= 0)
            return string.Empty;

        return bytesPerSecond switch
        {
            >= 1024 * 1024 => string.Format(Strings.DownloadSpeed_MegabytesPerSecondFormat,
                bytesPerSecond / 1024d / 1024d),
            >= 1024 => string.Format(Strings.DownloadSpeed_KilobytesPerSecondFormat,
                bytesPerSecond / 1024d),
            _ => string.Format(Strings.DownloadSpeed_BytesPerSecondFormat, bytesPerSecond)
        };
    }

    public static string Format(LauncherProgress progress)
    {
        return progress.Stage switch
        {
            InstallProgressStages.Queue => Strings.Status_InstallQueued,
            InstallProgressStages.Preparing => Strings.Status_InstallPreparing,
            InstallProgressStages.DownloadingGameFiles => Strings.Status_InstallDownloadingFiles,
            InstallProgressStages.InstallingGame => Strings.Status_InstallInstallingGame,
            InstallProgressStages.DownloadingLoaderInstaller => Strings.Status_InstallDownloadingLoaderInstaller,
            InstallProgressStages.CheckingJava => Strings.Status_LaunchCheckingJava,
            InstallProgressStages.DownloadingJava => Strings.Status_InstallDownloadingJava,
            InstallProgressStages.RunningLoaderInstaller => Strings.Status_InstallRunningLoaderInstaller,
            InstallProgressStages.FinalizingVersion => Strings.Status_InstallFinalizingVersion,
            InstallProgressStages.CompletingFiles => Strings.Status_InstallCompletingFiles,
            ImportProgressStages.PreparingArchive => Strings.Status_ModpackPreparingArchive,
            ImportProgressStages.ParsingManifest => Strings.Status_ModpackParsingManifest,
            ImportProgressStages.ResolvingPackFiles when !string.IsNullOrWhiteSpace(progress.Message)
                => string.Format(Strings.Status_ModpackResolvingFilesFormat, progress.Message),
            ImportProgressStages.ResolvingPackFiles => Strings.Status_ModpackResolvingFiles,
            ImportProgressStages.CreatingInstance => Strings.Status_ModpackCreatingInstance,
            ImportProgressStages.InstallingMinecraftBase => Strings.Status_ModpackInstallingMinecraftBase,
            ImportProgressStages.InstallingLoader => Strings.Status_ModpackInstallingLoader,
            ImportProgressStages.DownloadingPackFiles when !string.IsNullOrWhiteSpace(progress.Message)
                => string.Format(Strings.Status_ModpackDownloadingFileFormat, progress.Message),
            ImportProgressStages.DownloadingPackFiles => Strings.Status_ModpackDownloadingFiles,
            ImportProgressStages.ProcessingPackFiles => Strings.Status_ModpackProcessingFiles,
            ImportProgressStages.CopyingOverrides => Strings.Status_ModpackCopyingOverrides,
            ImportProgressStages.CleaningUp => Strings.Status_ModpackCleaningUp,
            LaunchProgressStages.CheckingJava => Strings.Status_LaunchCheckingJava,
            LaunchProgressStages.DownloadingJava => Strings.Status_InstallDownloadingJava,
            LaunchProgressStages.CheckingFiles => Strings.Status_InstallCheckingFiles,
            LaunchProgressStages.DownloadingFiles => Strings.Status_InstallDownloadingFiles,
            ModProgressStages.DownloadingFile when !string.IsNullOrWhiteSpace(progress.Message)
                => string.Format(Strings.Status_ModDownloadingFormat, progress.Message),
            ModProgressStages.DownloadingFile => Strings.Status_ModDownloading,
            _ when !string.IsNullOrWhiteSpace(progress.Message) => progress.Message,
            _ => Strings.DownloadTask_Preparing
        };
    }
}
