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

namespace Launcher.App.Services;

public interface IInstanceFolderService
{
    bool DirectoryExists(string folderPath);

    string EnsureDirectoryExists(string folderPath);

    Task<string> EnsureInstanceContentDirectoryAsync(Launcher.Domain.Models.GameInstance instance,
        Launcher.Domain.Models.ResourceProjectKind kind)
        => throw new NotSupportedException("Instance content directory creation is unavailable.");

    bool TryOpen(string folderPath);

    bool TryOpenFile(string filePath);

    bool TryRevealFile(string filePath);
}
