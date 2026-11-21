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

namespace Launcher.Domain.Models;

public static class InstallProgressStages
{
    public const string Queue = "Install.Queue";
    public const string Preparing = "Install.Preparing";
    public const string DownloadingGameFiles = "Install.DownloadingGameFiles";
    public const string InstallingGame = "Install.InstallingGame";
    public const string DownloadingLoaderInstaller = "Install.DownloadingLoaderInstaller";
    public const string CheckingJava = "Install.CheckingJava";
    public const string DownloadingJava = "Install.DownloadingJava";
    public const string RunningLoaderInstaller = "Install.RunningLoaderInstaller";
    public const string FinalizingVersion = "Install.FinalizingVersion";
    public const string CompletingFiles = "Install.CompletingFiles";
}
