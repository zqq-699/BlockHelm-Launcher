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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class ForgeLoaderProvider
{
private async Task DownloadInstallerAsync(
        Uri installerUrl,
        string destinationPath,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0,
        SpeedMeter? speedMeter = null)
    {
        var expectedSha1 = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(
            httpClient,
            downloadSpeedLimitState,
            logger,
            installerUrl.AbsoluteUri,
            downloadSourcePreference,
            "Forge",
            downloadSpeedLimitMbPerSecond,
            cancellationToken).ConfigureAwait(false);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Runtime);
        var logScope = new ForegroundDownloadLogScope(
            logger,
            "ForgeInstall",
            Path.GetFileName(destinationPath),
            destinationPath,
            installerUrl.AbsoluteUri);
        try
        {
            var resolution = await executor.DownloadFileAsync(
            installerUrl.AbsoluteUri,
            downloadSourcePreference,
            categoryHint: "Forge",
            destinationPath,
            expectedSha1,
            expectedSize: null,
            cancellationToken,
            reportAttemptProgress: logScope.BeginSource(),
            options: new DownloadFileOptions(ValidateUnverifiedFile: (path, token) => LoaderInstallerFallbackValidator.ValidateAsync(path, minecraftVersion, token)),
            speedMeter: speedMeter,
            reportTransferredBytes: logScope.ReportTransferredBytes);
            logScope.Complete(resolution);
        }
        catch (OperationCanceledException)
        {
            logScope.CompleteWithoutDownload("Canceled", installerUrl.AbsoluteUri);
            throw;
        }
        catch (Exception exception)
        {
            logScope.Fail(exception, installerUrl.AbsoluteUri);
            throw;
        }
    }

    /// <summary>
    /// 从安装器输出目录中选择元数据确实匹配目标 Minecraft 与 Forge 版本的源版本。
    /// </summary>
    private static string FindInstalledSourceVersionName(
        string gameDirectory,
        string minecraftVersion,
        string forgeVersion,
        HashSet<string> existingVersionNames)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
            throw new InvalidOperationException("Forge installation did not create a version directory.");

        var candidates = Directory.GetDirectories(versionsDirectory)
            .Select(directory => new DirectoryInfo(directory))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();

        // 优先匹配本次新建目录；若安装器复用了目录，再以 Maven 坐标和版本文本进行受控兜底。
        var sourceVersion = candidates
            .Where(directory => !existingVersionNames.Contains(directory.Name))
            .Select(directory => TryCreateSourceMatch(directory.FullName, directory.Name, minecraftVersion, forgeVersion))
            .FirstOrDefault(match => match is not null)
            ?? candidates
                .Select(directory => TryCreateSourceMatch(directory.FullName, directory.Name, minecraftVersion, forgeVersion))
                .FirstOrDefault(match => match is not null);

        return sourceVersion?.VersionName
            ?? throw new InvalidOperationException($"Forge installer did not produce a usable version for {minecraftVersion}-{forgeVersion}.");
    }

    private static ForgeSourceMatch? TryCreateSourceMatch(
        string versionDirectory,
        string versionName,
        string minecraftVersion,
        string forgeVersion)
    {
        var metadata = TryReadVersionMetadata(versionDirectory, versionName);
        if (metadata is null)
            return null;

        var combinedVersion = $"{minecraftVersion}-{forgeVersion}";
        var hasExactForgeLibrary = metadata.LibraryNames.Any(library =>
            library.Contains($"net.minecraftforge:forge:{combinedVersion}", StringComparison.OrdinalIgnoreCase));
        var normalizedMetadata = $"{metadata.Id} {metadata.InheritsFrom} {metadata.Jar} {versionName}";
        var hasLooseForgeMatch = normalizedMetadata.Contains("forge", StringComparison.OrdinalIgnoreCase)
            && normalizedMetadata.Contains(minecraftVersion, StringComparison.OrdinalIgnoreCase)
            && normalizedMetadata.Contains(forgeVersion, StringComparison.OrdinalIgnoreCase);

        if (!hasExactForgeLibrary && !hasLooseForgeMatch)
            return null;

        return new ForgeSourceMatch(versionName, metadata);
    }

    /// <summary>
    /// 优先扁平化 Forge 派生版本；无法读取父版本时退化为源版本隔离复制。
    /// </summary>
    private static async Task<string> CreateFinalVersionAsync(
        string gameDirectory,
        string sourceVersionName,
        string isolatedVersionName,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.Combine(gameDirectory, "versions", sourceVersionName);
        var metadata = TryReadVersionMetadata(sourceDirectory, sourceVersionName)
            ?? throw new InvalidOperationException($"Forge version metadata is missing for {sourceVersionName}.");

        string finalVersionName;
        if (!string.IsNullOrWhiteSpace(metadata.InheritsFrom))
        {
            try
            {
                // 能读取父版本时优先扁平化；父元数据缺失的旧安装结果再退化为直接复制源版本。
                finalVersionName = await VanillaVersionIsolator.CreateFlattenedDerivedVersionAsync(
                    metadata.InheritsFrom,
                    sourceVersionName,
                    isolatedVersionName,
                    gameDirectory,
                    cancellationToken);
                await LoaderVersionDirectoryTransaction.WriteLauncherMetadataAsync(
                    gameDirectory,
                    finalVersionName,
                    minecraftVersion,
                    cancellationToken);
                return finalVersionName;
            }
            catch (FileNotFoundException)
            {
            }
        }

        finalVersionName = await VanillaVersionIsolator.CreateIsolatedVersionFromSourceAsync(
            sourceVersionName,
            isolatedVersionName,
            gameDirectory,
            cancellationToken: cancellationToken);
        await LoaderVersionDirectoryTransaction.WriteLauncherMetadataAsync(
            gameDirectory,
            finalVersionName,
            minecraftVersion,
            cancellationToken);
        return finalVersionName;
    }

    private async Task EnsureFinalVersionIsSelfContainedAsync(
        string gameDirectory,
        string finalVersionName,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", finalVersionName);
        var repairService = new ManagedVersionRepairService(httpClient, downloadSpeedLimitState, logger);
        await repairService.FinalizePreparedVersionAsync(
            gameDirectory,
            finalVersionName,
            versionDirectory,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond: downloadSpeedLimitMbPerSecond);
    }

    private static IReadOnlyDictionary<string, ForgeCatalogEntry> ParseBmclCatalogEntries(
        string minecraftVersion,
        JsonElement root)
    {
        var entries = new Dictionary<string, ForgeCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("version", out var versionProperty)
                || versionProperty.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            var forgeVersion = versionProperty.GetString();
            if (string.IsNullOrWhiteSpace(forgeVersion) || entries.ContainsKey(forgeVersion))
                continue;

            var installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{minecraftVersion}-{forgeVersion}/forge-{minecraftVersion}-{forgeVersion}-installer.jar";
            entries[forgeVersion] = new ForgeCatalogEntry(
                minecraftVersion,
                forgeVersion,
                new Uri(installerUrl, UriKind.Absolute));
        }

        return entries;
    }

    private static bool IsLegacyForgeInstallClientFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(ForgeInstallerRunner.InstallClientUnrecognizedOptionMessage, StringComparison.OrdinalIgnoreCase)
                || (current.Message.Contains("installClient", StringComparison.OrdinalIgnoreCase)
                    && current.Message.Contains("UnrecognizedOptionException", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
