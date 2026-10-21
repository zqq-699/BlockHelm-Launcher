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

public sealed class ResourceProject
{
    public ResourceProjectKind Kind { get; init; } = ResourceProjectKind.Mod;

    public ResourceProjectSource Source { get; init; }

    public string ProjectId { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 本地映射命中的显示名称。原始平台标题始终保留在 <see cref="Title"/> 中。
    /// </summary>
    public string? LocalizedTitle { get; set; }

    public string Description { get; init; } = string.Empty;

    public string? IconUrl { get; init; }

    public long Downloads { get; init; }

    public IReadOnlyList<ResourceProjectCategory> Categories { get; init; } = [];

    public IReadOnlyList<string> SupportedMinecraftVersions { get; init; } = [];

    public IReadOnlyList<string> SupportedLoaders { get; init; } = [];

    public string ProjectUrl { get; init; } = string.Empty;
}
