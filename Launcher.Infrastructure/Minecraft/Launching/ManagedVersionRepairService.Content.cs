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
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed partial class ManagedVersionRepairService
{
private async Task EnsureVersionJarAsync(
        string versionDirectory,
        string versionName,
        ResolvedVersionMetadata resolvedVersion,
        bool allowRepair,
        ManagedVersionRepairDownloadBatch downloadBatch,
        CancellationToken cancellationToken)
    {
        var jarPath = InstanceVersionFiles.ResolveJarPath(versionDirectory, versionName);
        var jarDirectory = Path.GetDirectoryName(jarPath)!;
        MinecraftPathGuard.EnsureSafeFileDestination(jarPath, Path.GetDirectoryName(versionDirectory)!, "Managed version client");
        var jarStatus = await MinecraftFileIntegrity.EvaluateAsync(
            jarPath,
            resolvedVersion.ClientJarSha1,
            resolvedVersion.ClientJarSize,
            MinecraftFileVerification.Full,
            cancellationToken).ConfigureAwait(false);
        if (jarStatus == MinecraftFileIntegrityStatus.Valid)
            return;

        if (!allowRepair)
        {
            throw new InstanceRepairException(
                $"Version {versionName} client jar is missing or failed integrity validation and automatic repair is disabled.");
        }

        LogInvalidFile(jarPath, jarStatus);
        if (!string.IsNullOrWhiteSpace(resolvedVersion.LocalJarPath)
            && !PathComparer.Equals(Path.GetFullPath(resolvedVersion.LocalJarPath), Path.GetFullPath(jarPath))
            && await MinecraftFileIntegrity.IsValidAsync(
                resolvedVersion.LocalJarPath,
                resolvedVersion.ClientJarSha1,
                resolvedVersion.ClientJarSize,
                MinecraftFileVerification.Full,
                cancellationToken).ConfigureAwait(false))
        {
            // 优先复用父版本或本地已有 JAR，只有无可用本地来源时才下载。
            if (!string.IsNullOrWhiteSpace(resolvedVersion.ClientJarSha1))
            {
                await AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
                    resolvedVersion.LocalJarPath,
                    jarPath,
                    resolvedVersion.ClientJarSha1,
                    cancellationToken,
                    managedRoot: jarDirectory, logger: logger).ConfigureAwait(false);
                return;
            }

            if (jarStatus == MinecraftFileIntegrityStatus.Missing)
            {
                await AtomicSharedFilePublisher.PublishCopyAsync(
                    resolvedVersion.LocalJarPath,
                    jarPath,
                    expectedSha1: null,
                    cancellationToken,
                    managedRoot: jarDirectory, logger: logger).ConfigureAwait(false);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(resolvedVersion.ClientJarUrl))
        {
            await downloadBatch.DownloadAsync(
                new RepairDownloadRequest(
                    OriginalUrl: resolvedVersion.ClientJarUrl,
                    jarPath,
                    ResourceCategory: "Mojang",
                    LibraryName: null,
                    ArtifactPath: Path.GetFileName(jarPath),
                    ExpectedSha1: resolvedVersion.ClientJarSha1,
                    ExpectedSize: resolvedVersion.ClientJarSize,
                    ManagedRoot: jarDirectory),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InstanceRepairException($"Version {versionName} client jar is missing or invalid and no repair source is available.");
    }

    /// <summary>
    /// 解析受当前平台规则允许的库构件，收集所有缺失项后批量下载。
    /// </summary>
    private async Task EnsureLibrariesAsync(
        string minecraftDirectory,
        JsonObject versionJson,
        bool allowRepair,
        ManagedVersionRepairDownloadBatch downloadBatch,
        CancellationToken cancellationToken)
    {
        if (versionJson["libraries"] is not JsonArray libraries)
            return;

        var downloads = new List<RepairDownloadRequest>();
        foreach (var libraryNode in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (libraryNode is not JsonObject library || !ManagedLibraryArtifactResolver.IsAllowed(library))
                continue;

            foreach (var artifact in ManagedLibraryArtifactResolver.EnumerateDownloads(library))
            {
                var librariesRoot = Path.Combine(minecraftDirectory, "libraries");
                var destinationPath = MinecraftPathGuard.EnsureWithin(
                    Path.Combine(librariesRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    librariesRoot,
                    "Managed library");
                MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, librariesRoot, "Managed library");
                var status = await MinecraftFileIntegrity.EvaluateAsync(
                    destinationPath,
                    artifact.Sha1,
                    artifact.Size,
                    MinecraftFileVerification.Full,
                    cancellationToken).ConfigureAwait(false);
                if (status == MinecraftFileIntegrityStatus.Valid)
                    continue;

                if (!allowRepair)
                {
                    throw new InstanceRepairException(
                        $"Required library {artifact.RelativePath} is missing or failed integrity validation and automatic repair is disabled.");
                }

                LogInvalidFile(artifact.RelativePath, status);
                downloads.Add(new RepairDownloadRequest(
                    OriginalUrl: artifact.Url,
                    destinationPath,
                    artifact.ResourceCategory,
                    artifact.LibraryName,
                    artifact.RelativePath,
                    artifact.Sha1,
                    artifact.Size,
                    ManagedRoot: librariesRoot));
            }
        }

        // 先收集缺失项再交给批次统一并发、限速和汇报进度。
        await downloadBatch.DownloadAllAsync(downloads, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保资源索引存在，并根据索引内容补齐缺失的对象哈希文件。
    /// </summary>
    private async Task EnsureAssetsAsync(
        string minecraftDirectory,
        JsonObject versionJson,
        bool allowRepair,
        ManagedVersionRepairDownloadBatch downloadBatch,
        CancellationToken cancellationToken)
    {
        if (versionJson["assetIndex"] is not JsonObject assetIndex)
            return;

        var assetIndexId = GetStringProperty(assetIndex, "id");
        var assetIndexUrl = GetStringProperty(assetIndex, "url");
        if (string.IsNullOrWhiteSpace(assetIndexId) || string.IsNullOrWhiteSpace(assetIndexUrl))
            return;

        var assetIndexesRoot = Path.Combine(minecraftDirectory, "assets", "indexes");
        var indexPath = MinecraftPathGuard.EnsureWithin(
            Path.Combine(assetIndexesRoot, $"{assetIndexId}.json"),
            assetIndexesRoot,
            "Asset index");
        MinecraftPathGuard.EnsureSafeFileDestination(indexPath, assetIndexesRoot, "Asset index");
        var indexStatus = await MinecraftFileIntegrity.EvaluateAsync(
            indexPath,
            GetStringProperty(assetIndex, "sha1"),
            GetLongProperty(assetIndex, "size"),
            MinecraftFileVerification.Full,
            cancellationToken).ConfigureAwait(false);
        if (indexStatus != MinecraftFileIntegrityStatus.Valid)
        {
            if (!allowRepair)
                throw new InstanceRepairException($"Asset index {assetIndexId} is missing or failed integrity validation and automatic repair is disabled.");

            LogInvalidFile($"assets/indexes/{assetIndexId}.json", indexStatus);
            await downloadBatch.DownloadAsync(
                new RepairDownloadRequest(
                    OriginalUrl: assetIndexUrl,
                    indexPath,
                    ResourceCategory: "Mojang",
                    LibraryName: null,
                    ArtifactPath: $"assets/indexes/{assetIndexId}.json",
                    ExpectedSha1: GetStringProperty(assetIndex, "sha1"),
                    ExpectedSize: GetLongProperty(assetIndex, "size"),
                    ManagedRoot: assetIndexesRoot),
                cancellationToken).ConfigureAwait(false);
        }

        MinecraftPathGuard.EnsureSafeFileDestination(indexPath, assetIndexesRoot, "Asset index");
        await using var indexStream = File.OpenRead(indexPath);
        var indexNode = await JsonNode.ParseAsync(indexStream, cancellationToken: cancellationToken)
            ?? throw new InstanceRepairException($"Asset index {assetIndexId} is empty.");
        if (indexNode["objects"] is not JsonObject objects)
            return;

        var requirements = objects
            .Where(asset => asset.Value is JsonObject)
            .Select(asset => (Asset: (JsonObject)asset.Value!, Hash: GetStringProperty((JsonObject)asset.Value!, "hash")))
            .Where(requirement => MinecraftFileIntegrity.IsSha1(requirement.Hash))
            .ToArray();
        var downloads = new ConcurrentBag<RepairDownloadRequest>();
        var assetObjectsRoot = Path.Combine(minecraftDirectory, "assets", "objects");
        await Parallel.ForEachAsync(
            requirements,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async (requirement, token) =>
            {
                var objectPath = MinecraftPathGuard.EnsureWithin(
                    Path.Combine(assetObjectsRoot, requirement.Hash[..2], requirement.Hash),
                    assetObjectsRoot,
                    "Asset object");
                MinecraftPathGuard.EnsureSafeFileDestination(objectPath, assetObjectsRoot, "Asset object");
                var expectedSize = GetLongProperty(requirement.Asset, "size");
                var objectStatus = await MinecraftFileIntegrity.EvaluateAsync(
                    objectPath,
                    expectedSha1: requirement.Hash,
                    expectedSize,
                    MinecraftFileVerification.SizeOnly,
                    token).ConfigureAwait(false);
                if (objectStatus == MinecraftFileIntegrityStatus.Valid)
                    return;

                if (!allowRepair)
                {
                    throw new InstanceRepairException(
                        $"Required asset {requirement.Hash} is missing or failed integrity validation and automatic repair is disabled.");
                }

                LogInvalidFile($"assets/objects/{requirement.Hash[..2]}/{requirement.Hash}", objectStatus);
                downloads.Add(new RepairDownloadRequest(
                    OriginalUrl: $"https://resources.download.minecraft.net/{requirement.Hash[..2]}/{requirement.Hash}",
                    objectPath,
                    ResourceCategory: "Mojang",
                    LibraryName: null,
                    ArtifactPath: $"assets/objects/{requirement.Hash[..2]}/{requirement.Hash}",
                    ExpectedSha1: requirement.Hash,
                    ExpectedSize: expectedSize,
                    PersistenceMode: DownloadPersistenceMode.LightweightAtomic,
                    ManagedRoot: assetObjectsRoot));
            }).ConfigureAwait(false);

        await downloadBatch.DownloadAllAsync(downloads, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查版本声明的日志配置文件，并在允许修复时下载缺失配置。
    /// </summary>
    private async Task EnsureLoggingAsync(
        string minecraftDirectory,
        JsonObject versionJson,
        bool allowRepair,
        ManagedVersionRepairDownloadBatch downloadBatch,
        CancellationToken cancellationToken)
    {
        if (versionJson["logging"]?["client"] is not JsonObject clientLogging)
            return;

        if (clientLogging["file"] is not JsonObject loggingFile)
            return;

        var id = GetStringProperty(loggingFile, "id");
        var url = GetStringProperty(loggingFile, "url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
            return;

        var logConfigsRoot = Path.Combine(minecraftDirectory, "assets", "log_configs");
        var logConfigPath = MinecraftPathGuard.EnsureWithin(
            Path.Combine(logConfigsRoot, id),
            logConfigsRoot,
            "Logging configuration");
        MinecraftPathGuard.EnsureSafeFileDestination(
            logConfigPath,
            logConfigsRoot,
            "Logging configuration");
        var loggingStatus = await MinecraftFileIntegrity.EvaluateAsync(
            logConfigPath,
            GetStringProperty(loggingFile, "sha1"),
            GetLongProperty(loggingFile, "size"),
            MinecraftFileVerification.Full,
            cancellationToken).ConfigureAwait(false);
        if (loggingStatus == MinecraftFileIntegrityStatus.Valid)
            return;

        if (!allowRepair)
            throw new InstanceRepairException($"Logging configuration {id} is missing or failed integrity validation and automatic repair is disabled.");

        LogInvalidFile($"assets/log_configs/{id}", loggingStatus);
        await downloadBatch.DownloadAsync(
            new RepairDownloadRequest(
                OriginalUrl: url,
                logConfigPath,
                ResourceCategory: "Mojang",
                LibraryName: null,
                ArtifactPath: id,
                ExpectedSha1: GetStringProperty(loggingFile, "sha1"),
                ExpectedSize: GetLongProperty(loggingFile, "size"),
                ManagedRoot: logConfigsRoot),
            cancellationToken).ConfigureAwait(false);
    }

    private void LogInvalidFile(string artifactPath, MinecraftFileIntegrityStatus status)
    {
        if (status == MinecraftFileIntegrityStatus.Missing)
            return;

        logger.LogWarning(
            "Repairing Minecraft file after integrity validation failed. ArtifactPath={ArtifactPath} IntegrityStatus={IntegrityStatus}",
            artifactPath,
            status);
    }
}
