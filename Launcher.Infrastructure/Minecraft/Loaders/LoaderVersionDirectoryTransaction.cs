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
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 记录 Loader 安装前的版本目录快照，并只提交最终版本、清理本次安装产生的中间目录。
/// </summary>
internal static class LoaderVersionDirectoryTransaction
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 捕获安装前版本目录名称，作为后续清理不得越过的安全边界。
    /// </summary>
    public static HashSet<string> CaptureExistingVersions(string gameDirectory)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
            return [];

        return Directory.GetDirectories(versionsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void EnsureLauncherProfileExists(string gameDirectory)
    {
        Directory.CreateDirectory(gameDirectory);

        var launcherProfilesPath = Path.Combine(gameDirectory, "launcher_profiles.json");
        var microsoftStoreProfilesPath = Path.Combine(gameDirectory, "launcher_profiles_microsoft_store.json");
        if (File.Exists(launcherProfilesPath) || File.Exists(microsoftStoreProfilesPath))
            return;

        File.WriteAllText(
            launcherProfilesPath,
            """
            {
              "profiles": {}
            }
            """);
    }

    /// <summary>
    /// 在最终版本 JSON 中写入 Launcher 可稳定识别的原始 Minecraft 版本元数据。
    /// </summary>
    public static async Task WriteLauncherMetadataAsync(
        string gameDirectory,
        string versionName,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var versionJsonPath = Path.Combine(gameDirectory, "versions", versionName, $"{versionName}.json");
        JsonNode versionJson;
        await using (var jsonStream = File.OpenRead(versionJsonPath))
        {
            versionJson = await JsonNode.ParseAsync(jsonStream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException($"Version JSON is empty: {versionJsonPath}");
        }

        var versionObject = versionJson.AsObject();
        LauncherVersionMetadata.Apply(versionObject, minecraftVersion);
        await File.WriteAllTextAsync(
            versionJsonPath,
            versionObject.ToJsonString(JsonOptions),
            cancellationToken);
    }

    public static void CopyFinalVersionDirectory(
        string sourceGameDirectory,
        string destinationGameDirectory,
        string versionName,
        CancellationToken cancellationToken = default)
    {
        MinecraftVersionDirectoryCopier.CopyVersionDirectory(
            sourceGameDirectory,
            destinationGameDirectory,
            versionName,
            allowExistingDestination: true,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 删除快照后新出现的中间版本目录，同时保留指定最终版本。
    /// </summary>
    public static void CleanupCreatedVersionDirectories(
        string gameDirectory,
        HashSet<string> existingVersionNames,
        string? preserveVersionName, ILogger? logger = null)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
            return;

        // 安装前已存在的版本以及明确保留的最终版本永远不会被清理。
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(versionsDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Loader workspace cleanup enumeration failed. Directory={Directory} Stage=Cleanup", versionsDirectory);
            return;
        }
        foreach (var directory in directories)
        {
            var versionName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(versionName)
                || existingVersionNames.Contains(versionName)
                || string.Equals(versionName, preserveVersionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteDirectory(directory, logger);
        }
    }

    public static void TryDeleteDirectory(string directory, ILogger? logger = null)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException exception)
        {
            logger?.LogWarning(exception, "Loader workspace cleanup failed. Directory={Directory} Stage=Cleanup", directory);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger?.LogWarning(exception, "Loader workspace cleanup denied. Directory={Directory} Stage=Cleanup", directory);
        }
    }
}
