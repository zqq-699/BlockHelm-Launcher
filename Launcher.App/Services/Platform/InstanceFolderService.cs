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

using System.Diagnostics;
using System.IO;

namespace Launcher.App.Services;

public sealed class InstanceFolderService(Launcher.Application.Services.IResourceProjectInstallationService installationService) : IInstanceFolderService
{
    public Task<string> EnsureInstanceContentDirectoryAsync(Launcher.Domain.Models.GameInstance instance,
        Launcher.Domain.Models.ResourceProjectKind kind)
        => installationService.EnsureInstanceContentDirectoryAsync(kind, instance);

    public bool DirectoryExists(string folderPath)
    {
        return !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath);
    }

    public string EnsureDirectoryExists(string folderPath)
    {
        var normalizedFolderPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(normalizedFolderPath);
        return normalizedFolderPath;
    }

    public bool TryOpen(string folderPath)
    {
        if (!DirectoryExists(folderPath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryOpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryRevealFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
