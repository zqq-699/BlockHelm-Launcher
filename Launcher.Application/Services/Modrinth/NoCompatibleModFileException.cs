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

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public sealed class NoCompatibleModFileException : Exception, ISafeDiagnosticException
{
    public NoCompatibleModFileException(string projectId, string minecraftVersion, LoaderKind loader)
        : base($"No compatible mod file was found for project '{projectId}' on Minecraft {minecraftVersion} with loader {loader}.")
    {
        ProjectId = projectId;
        MinecraftVersion = minecraftVersion;
        Loader = loader;
    }

    public string ProjectId { get; }
    public string MinecraftVersion { get; }
    public LoaderKind Loader { get; }
    public string DiagnosticCode => "NoCompatibleModFile";
    public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>
    {
        ["ProjectId"] = ProjectId, ["MinecraftVersion"] = MinecraftVersion, ["Loader"] = Loader.ToString()
    };
}
