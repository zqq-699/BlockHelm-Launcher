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

namespace Launcher.App.ViewModels.Shared;

internal static class MinecraftVersionIconResolver
{
    public const string DefaultReleaseIconSource = "/Assets/Icons/block/grass_block.png";
    public const string DefaultSnapshotIconSource = "/Assets/Icons/block/dirt_block.png";
    public const string DefaultAprilFoolsIconSource = "/Assets/Icons/block/TNT_block.png";
    public const string DefaultBetaIconSource = "/Assets/Icons/block/craftingtable_block.png";
    public const string DefaultAlphaIconSource = "/Assets/Icons/block/stone_block.png";
    public const string DefaultFabricIconSource = "/Assets/Icons/block/fabric.png";
    public const string DefaultForgeIconSource = "/Assets/Icons/block/Anvil.png";
    public const string DefaultNeoForgeIconSource = "/Assets/Icons/block/neo_logo.png";
    public const string DefaultQuiltIconSource = "/Assets/Icons/block/quilt_x16.png";

    public static string Resolve(GameInstance instance, string? versionType = null, string? minecraftVersion = null)
    {
        if (!string.IsNullOrWhiteSpace(instance.IconSource))
            return instance.IconSource;

        if (instance.Loader is LoaderKind.Fabric)
            return DefaultFabricIconSource;

        if (instance.Loader is LoaderKind.Forge)
            return DefaultForgeIconSource;

        if (instance.Loader is LoaderKind.NeoForge)
            return DefaultNeoForgeIconSource;

        if (instance.Loader is LoaderKind.Quilt)
            return DefaultQuiltIconSource;

        return Resolve(versionType, minecraftVersion, instance.MinecraftVersion, instance.VersionName);
    }

    public static string Resolve(string? versionType = null, string? minecraftVersion = null)
    {
        return Resolve(versionType, minecraftVersion, null, null);
    }

    private static string Resolve(
        string? versionType,
        string? minecraftVersion,
        string? instanceMinecraftVersion,
        string? instanceVersionName)
    {
        var normalizedType = NormalizeVersionType(versionType);
        if (normalizedType == "april_fools"
            || MinecraftAprilFoolsVersionClassifier.IsAprilFoolsVersion(minecraftVersion)
            || MinecraftAprilFoolsVersionClassifier.IsAprilFoolsVersion(instanceMinecraftVersion)
            || MinecraftAprilFoolsVersionClassifier.IsAprilFoolsVersion(instanceVersionName))
            return DefaultAprilFoolsIconSource;

        if (normalizedType.Equals("old_beta", StringComparison.OrdinalIgnoreCase))
            return DefaultBetaIconSource;

        if (normalizedType.Equals("old_alpha", StringComparison.OrdinalIgnoreCase))
            return DefaultAlphaIconSource;

        if (normalizedType.Equals("snapshot", StringComparison.OrdinalIgnoreCase)
            || IsSnapshotVersionName(minecraftVersion)
            || IsSnapshotVersionName(instanceMinecraftVersion)
            || IsSnapshotVersionName(instanceVersionName))
            return DefaultSnapshotIconSource;

        return DefaultReleaseIconSource;
    }

    public static string NormalizeVersionType(string? type)
    {
        return type?.Trim().ToLowerInvariant().Replace("-", "_") switch
        {
            "release" => "release",
            "snapshot" => "snapshot",
            "april_fools" or "aprilfools" => "april_fools",
            "ancient" => "ancient",
            "old_beta" or "oldbeta" or "beta" => "old_beta",
            "old_alpha" or "oldalpha" or "alpha" => "old_alpha",
            _ => string.Empty
        };
    }

    private static bool IsSnapshotVersionName(string? version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && version.Length >= 5
            && char.IsDigit(version[0])
            && char.IsDigit(version[1])
            && version[2] == 'w'
            && char.IsDigit(version[3])
            && char.IsDigit(version[4]);
    }
}

