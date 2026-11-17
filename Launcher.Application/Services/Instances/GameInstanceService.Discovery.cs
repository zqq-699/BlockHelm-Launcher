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

using System.Collections.Concurrent;
using Launcher.Application.Repositories;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Application.Services;

public sealed partial class GameInstanceService
{
private List<GameInstance> SynchronizeInstalledInstances(
        List<GameInstance> instances,
        IReadOnlyList<InstalledGameVersion> installedVersions,
        LauncherSettings settings,
        out bool changed)
    {
        // 已存实例保留用户配置，但必须能匹配实际版本目录；未登记目录按实际位置建立发现实例。
        changed = false;
        var syncedInstances = new List<GameInstance>();
        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installedByVersion = installedVersions
            .Where(version => !string.IsNullOrWhiteSpace(version.VersionName))
            .GroupBy(version => version.VersionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var instance in instances)
        {
            var versionName = GetVersionName(instance);
            if (!string.IsNullOrWhiteSpace(versionName)
                && IsInstallingVersion(settings.MinecraftDirectory, versionName))
            {
                // 安装租约存在时保留原记录，即使磁盘元数据暂时不可读，也不能把实例当作已删除。
                if (seenVersions.Add(versionName))
                    syncedInstances.Add(instance);

                continue;
            }

            if (string.IsNullOrWhiteSpace(versionName)
                || !installedByVersion.TryGetValue(versionName, out var installedVersion)
                || !seenVersions.Add(installedVersion.VersionName))
            {
                changed = true;
                continue;
            }

            changed |= ApplyInstalledVersion(instance, installedVersion);
            syncedInstances.Add(instance);
        }

        foreach (var installedVersion in installedVersions)
        {
            if (string.IsNullOrWhiteSpace(installedVersion.VersionName)
                || !seenVersions.Add(installedVersion.VersionName))
            {
                continue;
            }

            syncedInstances.Add(CreateDiscoveredInstance(installedVersion, settings));
            changed = true;
        }

        return syncedInstances;
    }

    private bool IsInstallingVersion(string minecraftDirectory, string versionName)
    {
        return installCoordinator.IsInstallingVersion(minecraftDirectory, versionName);
    }

    private GameInstance CreateDiscoveredInstance(
        InstalledGameVersion installedVersion,
        LauncherSettings settings)
    {
        return new GameInstance
        {

            Name = installedVersion.VersionName,
            MinecraftVersion = installedVersion.MinecraftVersion,
            Loader = installedVersion.Loader,
            LoaderVersion = installedVersion.LoaderVersion,
            VersionName = installedVersion.VersionName,
            VersionType = installedVersion.VersionType,
            InstanceDirectory = installedVersion.Directory,
            MemoryMb = settings.DefaultMemoryMb,
            CreatedAt = installedVersion.DiscoveredAt,
            UpdatedAt = installedVersion.DiscoveredAt
        };
    }

    /// <summary>
    /// 用磁盘发现的权威版本信息更新实例，同时保留名称、图标等用户配置。
    /// </summary>
    private static bool ApplyInstalledVersion(GameInstance instance, InstalledGameVersion installedVersion)
    {
        var changed = false;
        changed |= SetIfChanged(instance.Name, installedVersion.VersionName, value => instance.Name = value ?? string.Empty);
        changed |= SetIfChanged(
            instance.MinecraftVersion,
            ResolvePreferredMinecraftVersion(instance.MinecraftVersion, installedVersion.MinecraftVersion, installedVersion.VersionName),
            value => instance.MinecraftVersion = value ?? string.Empty);
        changed |= SetIfChanged(instance.VersionName, installedVersion.VersionName, value => instance.VersionName = value ?? string.Empty);
        changed |= SetIfChanged(instance.VersionType, installedVersion.VersionType, value => instance.VersionType = value ?? string.Empty);
        changed |= SetIfChanged(instance.LoaderVersion, installedVersion.LoaderVersion, value => instance.LoaderVersion = value);
        changed |= SetIfChanged(instance.InstanceDirectory, installedVersion.Directory, value => instance.InstanceDirectory = value ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (instance.Loader != installedVersion.Loader)
        {
            instance.Loader = installedVersion.Loader;
            changed = true;
        }

        return changed;
    }

    private static bool SetIfChanged(
        string? currentValue,
        string? nextValue,
        Action<string?> setValue,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.Equals(currentValue, nextValue, comparison))
            return false;

        setValue(nextValue);
        return true;
    }

    private static string ResolvePreferredMinecraftVersion(
        string? currentMinecraftVersion,
        string? discoveredMinecraftVersion,
        string versionName)
    {
        if (LooksLikeMinecraftVersion(discoveredMinecraftVersion))
            return discoveredMinecraftVersion ?? string.Empty;

        if (LooksLikeMinecraftVersion(currentMinecraftVersion)
            && !string.Equals(currentMinecraftVersion, versionName, StringComparison.OrdinalIgnoreCase))
        {
            return currentMinecraftVersion ?? string.Empty;
        }

        return discoveredMinecraftVersion ?? string.Empty;
    }

    private static bool LooksLikeMinecraftVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Version.TryParse(value, out _))
            return true;

        if (value.Length >= 6
            && char.IsDigit(value[0])
            && char.IsDigit(value[1])
            && value[2] == 'w'
            && char.IsDigit(value[3])
            && char.IsDigit(value[4]))
        {
            return true;
        }

        return value.StartsWith("a", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("b", StringComparison.OrdinalIgnoreCase);
    }


    private static string GetVersionName(GameInstance instance)
    {
        return string.IsNullOrWhiteSpace(instance.VersionName)
            ? instance.MinecraftVersion
            : instance.VersionName;
    }

    private static string NormalizeUserInstanceName(string? value)
    {
        return VersionDirectoryName.NormalizeUserInput(value);
    }
}
