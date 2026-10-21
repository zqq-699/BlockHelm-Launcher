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

public sealed class LocalMod
{
    public string Name { get; set; } = string.Empty;
    public string? LocalizedName { get; set; }
    public string? Loader { get; set; }
    public string? ModId { get; set; }
    public string? Version { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string? IconSource { get; set; }
    public IReadOnlyList<ResourceProjectCategory> Categories { get; set; } = [];
    public ResourceProjectReference? ProjectReference { get; set; }
    public bool IsEnabled { get; set; }
    public long SizeBytes { get; set; }
    public string Source { get; set; } = "Local";
}
