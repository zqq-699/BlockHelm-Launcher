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
using Launcher.App.ViewModels.Shared;

namespace Launcher.App.Utilities;

internal static class MinecraftVersionTypeDisplayProvider
{
    public static string GetLabel(string? versionType, string fallback = "")
    {
        return MinecraftVersionIconResolver.NormalizeVersionType(versionType) switch
        {
            "release" => Strings.Download_ReleaseCategory,
            "snapshot" => Strings.Download_SnapshotCategory,
            "april_fools" => Strings.Download_AprilFoolsCategory,
            "old_beta" => Strings.Download_BetaCategory,
            "old_alpha" => Strings.Download_AlphaCategory,
            _ => fallback
        };
    }
}
