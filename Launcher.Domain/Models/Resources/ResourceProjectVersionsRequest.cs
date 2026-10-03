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

public sealed class ResourceProjectVersionsRequest
{
    public ResourceProjectKind Kind { get; init; } = ResourceProjectKind.Mod;

    public ResourceProjectSource Source { get; init; }

    public string ProjectId { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string MinecraftVersion { get; init; } = string.Empty;

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    public bool IncludeAllVersions { get; init; }

    public bool ForServerInstallation { get; init; }

    public bool IncludeDependencies { get; init; } = true;

    public int Offset { get; init; }

    public int PageSize { get; init; } = 50;
}
