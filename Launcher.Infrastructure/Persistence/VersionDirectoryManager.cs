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
using Launcher.Application.Services;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Persistence;

internal sealed class VersionDirectoryManager(ILogger logger)
{
    public string GetVersionDirectory(string minecraftDirectory, string versionName)
    {
        if (!VersionDirectoryName.IsSafeDirectoryName(versionName))
            throw new ArgumentException($"Version name is not a safe directory name: {versionName}", nameof(versionName));
        var versionsDirectory = Path.GetFullPath(Path.Combine(minecraftDirectory, "versions"));
        var versionDirectory = Path.GetFullPath(Path.Combine(versionsDirectory, versionName));
        if (!string.Equals(Path.GetDirectoryName(versionDirectory), versionsDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Version directory resolved outside the versions directory: {versionName}", nameof(versionName));
        return versionDirectory;
    }

    public void CreateInstanceDirectories(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var child in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks" })
            Directory.CreateDirectory(Path.Combine(directory, child));
        logger.LogDebug("Instance directories ensured. InstanceDirectory={InstanceDirectory}", directory);
    }

}
