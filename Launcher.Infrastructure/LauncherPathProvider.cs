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
using Launcher.Application;

namespace Launcher.Infrastructure;

public sealed class LauncherPathProvider
{
    private readonly string applicationBaseDirectory;
    private readonly string roamingApplicationDataDirectory;

    public LauncherPathProvider(string? applicationBaseDirectory = null, string? applicationDataDirectory = null)
    {
        this.applicationBaseDirectory = string.IsNullOrWhiteSpace(applicationBaseDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(applicationBaseDirectory);
        roamingApplicationDataDirectory = string.IsNullOrWhiteSpace(applicationDataDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.GetFullPath(applicationDataDirectory);
    }

    public string ApplicationId => LauncherApplicationIdentity.StorageDirectoryName;

    public string ApplicationBaseDirectory => applicationBaseDirectory;

    public string DefaultDataDirectory =>
        Path.Combine(applicationBaseDirectory, ApplicationId);

    public string MinecraftMetadataCacheDirectory =>
        Path.Combine(DefaultDataDirectory, "cache", "minecraft-metadata");

    public string DefaultAccountDataDirectory =>
        Path.Combine(roamingApplicationDataDirectory, ApplicationId, "accounts");

    public string DefaultMinecraftDirectory =>
        Path.Combine(applicationBaseDirectory, ".minecraft");

    public string OfficialMinecraftDirectory =>
        Path.Combine(roamingApplicationDataDirectory, ".minecraft");
}
