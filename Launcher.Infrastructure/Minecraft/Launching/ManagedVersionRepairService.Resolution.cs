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

using Launcher.Infrastructure.Persistence;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed partial class ManagedVersionRepairService
{
private const int MaximumInheritanceDepth = 10;

internal async Task<ResolvedVersionMetadata> FinalizePreparedVersionAsync(
        string minecraftDirectory,
        string versionName,
        string versionDirectory,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        // This method is used only while a new loader version is still being
        // constructed in its private sandbox, before it becomes an instance.
        var result = await ResolveCurrentVersionAsync(
            minecraftDirectory,
            versionName,
            versionDirectory,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond,
            allowRemoteParentResolution: true);

        if (!string.IsNullOrWhiteSpace(GetStringProperty(result.VersionJson, "inheritsFrom")))
            throw new InstanceRepairException($"Version {versionName} still depends on another version after repair.");

        // 修复结果统一改写为当前版本身份并移除 inheritsFrom，后续启动不再依赖父目录存在。
        var normalized = NormalizeVersionJson(result.VersionJson, versionName);
        if (result.WasModified || !ReferenceEquals(normalized, result.VersionJson))
        {
            await WriteVersionJsonAsync(versionDirectory, versionName, normalized, cancellationToken);
            result = result with { VersionJson = normalized, WasModified = true };
        }

        return result;
    }

    /// <summary>
    /// Resolves an existing instance and its inheritance chain entirely in
    /// memory. A valid instance JSON is never normalized or written back.
    /// </summary>
    internal Task<ResolvedVersionMetadata> ResolveVersionForRepairAsync(
        string minecraftDirectory,
        string versionName,
        string versionDirectory,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        bool allowRemoteParentResolution,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        return ResolveCurrentVersionAsync(
            minecraftDirectory,
            versionName,
            versionDirectory,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond,
            allowRemoteParentResolution);
    }

    /// <summary>
    /// 读取当前版本；存在父版本时递归解析并合并为可独立使用的元数据。
    /// </summary>
    private async Task<ResolvedVersionMetadata> ResolveCurrentVersionAsync(
        string minecraftDirectory,
        string versionName,
        string versionDirectory,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        bool allowRemoteParentResolution,
        HashSet<string>? visitedVersionNames = null)
    {
        visitedVersionNames ??= new HashSet<string>(PathComparer);
        if (visitedVersionNames.Count >= MaximumInheritanceDepth)
        {
            throw new InstanceRepairException(
                $"Version inheritance exceeds the supported depth of {MaximumInheritanceDepth} at {versionName}.");
        }
        if (!visitedVersionNames.Add(versionName))
            throw new InstanceRepairException($"Version inheritance cycle detected at {versionName}.");

        var versionJson = await ReadVersionJsonAsync(versionDirectory, versionName, cancellationToken);
        var currentJsonPath = (InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) ?? Path.Combine(versionDirectory, $"{versionName}.json"));
        var currentJarUrl = VanillaVersionMetadataClient.GetClientJarUrl(versionJson);
        var jarReference = GetStringProperty(versionJson, "jar");
        ResolvedVersionMetadata? referencedClient = null;
        if (!string.IsNullOrWhiteSpace(jarReference)
            && !string.Equals(jarReference, GetStringProperty(versionJson, "id"), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(jarReference, versionName, StringComparison.OrdinalIgnoreCase))
        {
            referencedClient = await ResolveParentVersionAsync(minecraftDirectory, jarReference,
                downloadSourcePreference, cancellationToken, downloadSpeedLimitMbPerSecond,
                allowRemoteParentResolution, new HashSet<string>(visitedVersionNames, PathComparer)).ConfigureAwait(false);
        }
        var inheritsFrom = GetStringProperty(versionJson, "inheritsFrom");
        if (string.IsNullOrWhiteSpace(inheritsFrom))
        {
            var currentJarPath = InstanceVersionFiles.ResolveJarPath(versionDirectory, versionName);
            return new ResolvedVersionMetadata(
                versionName,
                versionJson,
                File.Exists(currentJarPath) ? currentJarPath : null,
                referencedClient?.ClientJarUrl ?? currentJarUrl,
                WasModified: false,
                LocalMetadataPaths: [currentJsonPath, .. (referencedClient?.LocalMetadataPaths ?? [])],
                ClientJarSha1: referencedClient?.ClientJarSha1 ?? VanillaVersionMetadataClient.GetClientJarSha1(versionJson),
                ClientJarSize: referencedClient?.ClientJarSize ?? VanillaVersionMetadataClient.GetClientJarSize(versionJson));
        }

        var parent = await ResolveParentVersionAsync(
            minecraftDirectory,
            inheritsFrom,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond,
            allowRemoteParentResolution,
            visitedVersionNames);
        var mergedVersion = VersionJsonMergeHelper.MergeFlattenedVersion(parent.VersionJson, versionJson, versionName);

        return new ResolvedVersionMetadata(
            versionName,
            mergedVersion,
            referencedClient?.LocalJarPath ?? parent.LocalJarPath,
            referencedClient?.ClientJarUrl ?? VanillaVersionMetadataClient.GetClientJarUrl(mergedVersion) ?? parent.ClientJarUrl ?? currentJarUrl,
            WasModified: true,
            LocalMetadataPaths: [currentJsonPath, .. parent.LocalMetadataPaths, .. (referencedClient?.LocalMetadataPaths ?? [])],
            ClientJarSha1: referencedClient?.ClientJarSha1 ?? VanillaVersionMetadataClient.GetClientJarSha1(mergedVersion) ?? parent.ClientJarSha1,
            ClientJarSize: referencedClient?.ClientJarSize ?? VanillaVersionMetadataClient.GetClientJarSize(mergedVersion) ?? parent.ClientJarSize);
    }

    /// <summary>
    /// 优先读取本地父版本，缺失时从官方元数据获取父版本定义和客户端来源。
    /// </summary>
    private async Task<ResolvedVersionMetadata> ResolveParentVersionAsync(
        string minecraftDirectory,
        string parentVersionName,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        bool allowRemoteParentResolution,
        HashSet<string> visitedVersionNames)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        var parentJsonPath = Path.Combine(
            versionsDirectory,
            parentVersionName,
            $"{parentVersionName}.json");
        try
        {
            parentJsonPath = MinecraftPathGuard.EnsureSafeFileDestination(
                parentJsonPath,
                versionsDirectory,
                "Inherited version metadata");
        }
        catch (InvalidDataException exception)
        {
            throw new InstanceRepairException(
                $"Inherited version metadata path is invalid for {parentVersionName}.",
                exception);
        }
        var parentDirectory = Path.GetDirectoryName(parentJsonPath)
            ?? throw new InstanceRepairException($"Inherited version metadata directory is invalid for {parentVersionName}.");
        parentJsonPath = InstanceVersionFiles.TryResolveVersionJsonPath(parentDirectory, parentVersionName) ?? parentJsonPath;
        if (Directory.Exists(parentDirectory)
            && File.Exists(parentJsonPath))
        {
            return await ResolveCurrentVersionAsync(
                minecraftDirectory,
                parentVersionName,
                parentDirectory,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond,
                allowRemoteParentResolution,
                visitedVersionNames);
        }

        if (!allowRemoteParentResolution)
        {
            throw new InstanceRepairException(
                $"Inherited version metadata is missing for {parentVersionName} and automatic repair is disabled.");
        }

        if (!remoteVersionMetadataCache.TryGetValue(
                (parentVersionName, downloadSourcePreference),
                out var remoteVersionJson))
        {
            try
            {
                remoteVersionJson = await VanillaVersionMetadataClient.DownloadVersionJsonAsync(
                    httpClient,
                    parentVersionName,
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond,
                    downloadSpeedLimitState,
                    logger,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InstanceRepairException(
                    $"Version {parentVersionName} metadata could not be resolved in memory.",
                    exception);
            }

            if (!string.Equals(
                    GetStringProperty(remoteVersionJson, "id"),
                    parentVersionName,
                    StringComparison.Ordinal))
            {
                throw new InstanceRepairException(
                    $"Resolved parent version id does not match the requested version: {parentVersionName}.");
            }
            remoteVersionMetadataCache.TryAdd(
                (parentVersionName, downloadSourcePreference),
                (JsonObject)remoteVersionJson.DeepClone());
        }
        else
        {
            remoteVersionJson = (JsonObject)remoteVersionJson.DeepClone();
        }

        return new ResolvedVersionMetadata(
            parentVersionName,
            NormalizeVersionJson(remoteVersionJson, parentVersionName),
            LocalJarPath: null,
            VanillaVersionMetadataClient.GetClientJarUrl(remoteVersionJson),
            WasModified: false,
            LocalMetadataPaths: [],
            ClientJarSha1: VanillaVersionMetadataClient.GetClientJarSha1(remoteVersionJson),
            ClientJarSize: VanillaVersionMetadataClient.GetClientJarSize(remoteVersionJson));
    }

    private static JsonObject NormalizeVersionJson(JsonObject versionJson, string versionName)
    {
        var normalized = (JsonObject)versionJson.DeepClone();
        normalized["id"] = versionName;
        normalized["jar"] = versionName;
        normalized.Remove("inheritsFrom");

        if (normalized["minecraftArguments"] is JsonValue minecraftArgumentsValue
            && minecraftArgumentsValue.TryGetValue<string>(out var minecraftArguments))
        {
            normalized["minecraftArguments"] = VersionJsonMergeHelper.NormalizeMinecraftArguments(minecraftArguments);
        }

        return normalized;
    }

    /// <summary>
    /// 确保隔离版本拥有同名客户端 JAR，优先复制本地来源并最后尝试下载。
    /// </summary>
}
