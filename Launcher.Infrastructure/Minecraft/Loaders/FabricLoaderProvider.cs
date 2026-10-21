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

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed class FabricLoaderProvider : ILoaderProvider, ISeparatedInstallPathLoaderProvider
{
    private const string NoLoaderMessagePrefix = "Cannot find any loader for";
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;

    public FabricLoaderProvider(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<FabricLoaderProvider>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<FabricLoaderProvider>.Instance;
    }

    public LoaderKind Kind => LoaderKind.Fabric;
    public bool IsImplemented => true;

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        // 响应必须是非空数组且包含有效 loader.version，格式错误与没有结果使用不同诊断。
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Metadata);
        var result = await executor.ExecuteLookupAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{minecraftVersion}",
            downloadSourcePreference,
            categoryHint: "Fabric",
            async (context, token) =>
            {
                await using var stream = await context.Response.Content.ReadAsStreamAsync(token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (json.RootElement.ValueKind is not JsonValueKind.Array)
                {
                    throw new DownloadContentValidationException(
                        "Fabric loader metadata is not a JSON array.");
                }

                var entries = json.RootElement.EnumerateArray().ToList();
                if (entries.Count == 0)
                    throw new DownloadNoResultException("Fabric returned an empty loader list.");

                var versions = entries
                    .Select(ReadLoaderVersion)
                    .Where(version => version is not null)
                    .Select(version => version!)
                    .ToList();
                if (versions.Count == 0)
                {
                    throw new DownloadContentValidationException(
                        "Fabric loader metadata contains no valid loader entries.");
                }

                return (IReadOnlyList<LoaderVersionInfo>)versions;
            },
            statusCode => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            cancellationToken);
        return result.Found ? result.Value! : [];
    }

    public Task<string> InstallAsync(
        string minecraftVersion,
        string gameDirectory,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        // 未指定 Loader 时选择目录首项；确定版本后再组合隔离 profile 并安装依赖。
        return InstallCoreAsync(
            minecraftVersion,
            new MinecraftPath(gameDirectory),
            gameDirectory,
            gameDirectory,
            isolatedVersionName,
            loaderVersion,
            progress,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    Task<string> ISeparatedInstallPathLoaderProvider.InstallWithSeparatedPathsAsync(
        string minecraftVersion,
        MinecraftInstallPathLayout installPathLayout,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond) =>
        InstallCoreAsync(
            minecraftVersion,
            installPathLayout.Path,
            installPathLayout.WorkspaceMinecraftDirectory,
            installPathLayout.SharedMinecraftDirectory,
            isolatedVersionName,
            loaderVersion,
            progress,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);

    private async Task<string> InstallCoreAsync(
        string minecraftVersion,
        MinecraftPath path,
        string versionWorkspaceDirectory,
        string sharedMinecraftDirectory,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond)
    {
        progress?.Report(new LauncherProgress(InstallProgressStages.Preparing, string.Empty));
        var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(path);
        var speedMeter = SpeedMeterProgress.TryGet(progress);
        var operationResourcesDisposed = false;
        try
        {
            var selectedLoaderVersion = loaderVersion;

            if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
            {
                var availableLoaders = await GetLoaderVersionsAsync(
                    minecraftVersion,
                    downloadSourcePreference,
                    cancellationToken,
                    downloadSpeedLimitMbPerSecond);
                selectedLoaderVersion = availableLoaders.FirstOrDefault()?.Version;
            }

            if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
                throw new InvalidOperationException($"No Fabric loader version available for {minecraftVersion}.");

            var launcher = VanillaLoaderProvider.CreateLauncher(
                path,
                progress,
                downloadSourcePreference,
                logger,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                downloadOperation,
                speedMeter);
            VanillaLoaderProvider.AttachProgress(launcher, progress);
            var finalVersionName = await ComposedVersionInstallRunner.RunAsync(
                token => FabricVersionComposer.PrepareFinalVersionAsync(
                    httpClient,
                    minecraftVersion,
                    selectedLoaderVersion,
                    isolatedVersionName,
                    versionWorkspaceDirectory,
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond,
                    downloadSpeedLimitState,
                    logger,
                    token),
                async (versionName, token) => await launcher.InstallAsync(versionName, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new LauncherProgress(InstallProgressStages.FinalizingVersion, string.Empty));
            var validationStopwatch = Stopwatch.StartNew();
            var validation = await new GameFileIntegrityService(httpClient, downloadSpeedLimitState, logger)
                .ValidateInstalledVersionAsync(
                    new GameFileIntegrityRequest(
                        sharedMinecraftDirectory,
                        finalVersionName,
                        Path.Combine(versionWorkspaceDirectory, "versions", finalVersionName),
                        downloadSourcePreference,
                        downloadSpeedLimitMbPerSecond)
                    {
                        LoaderIdentity = new GameFileLoaderIdentity(
                            LoaderKind.Fabric,
                            minecraftVersion,
                            selectedLoaderVersion)
                    },
                    downloadOperation,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            validationStopwatch.Stop();
            logger.LogDebug(
                "Fabric post-install validation completed. VersionName={VersionName} DurationMs={DurationMs}",
                finalVersionName,
                validationStopwatch.ElapsedMilliseconds);
            if (!validation.LaunchAllowed)
                throw new InstanceRepairException($"Installed Fabric version {finalVersionName} failed required-file validation.");

            logger.LogDebug("Fabric installation cleanup started. VersionName={VersionName}", finalVersionName);
            var cleanupStopwatch = Stopwatch.StartNew();
            downloadOperation.Dispose();
            cleanupStopwatch.Stop();
            operationResourcesDisposed = true;
            logger.LogDebug(
                "Fabric installation cleanup completed. VersionName={VersionName} TotalDurationMs={TotalDurationMs}",
                finalVersionName,
                cleanupStopwatch.ElapsedMilliseconds);
            return finalVersionName;
        }
        finally
        {
            if (!operationResourcesDisposed)
                downloadOperation.Dispose();
        }
    }

    internal static bool IsNoAvailableVersionException(Exception exception, string minecraftVersion)
    {
        // 兼容 CmlLib 和 HTTP 层的不同包装，遍历异常链识别明确的“不支持该版本”。
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException httpRequestException
                && httpRequestException.StatusCode is HttpStatusCode.NotFound)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.Message))
                continue;

            var message = current.Message;
            if (message.Contains(NoLoaderMessagePrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            if (message.Contains("404", StringComparison.OrdinalIgnoreCase)
                && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains(minecraftVersion, StringComparison.OrdinalIgnoreCase)
                && message.Contains("no loader", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains(minecraftVersion, StringComparison.OrdinalIgnoreCase)
                && (message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("could not find", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("not support", StringComparison.OrdinalIgnoreCase))
                && (message.Contains("loader", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("version", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("game", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static LoaderVersionInfo? ReadLoaderVersion(JsonElement item)
    {
        // 缺少稳定版本标识的条目直接丢弃，不向 UI 暴露无法安装的空选项。
        if (!item.TryGetProperty("loader", out var loader)
            || loader.ValueKind is not JsonValueKind.Object
            || !loader.TryGetProperty("version", out var versionProperty)
            || versionProperty.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var version = versionProperty.GetString();
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var isStable = loader.TryGetProperty("stable", out var stableProperty)
            && stableProperty.ValueKind is JsonValueKind.True or JsonValueKind.False
            && stableProperty.GetBoolean();

        return new LoaderVersionInfo(version, isStable);
    }
}

/// <summary>
/// 为尚未实现的 Loader 保留显式占位，使调用方得到可识别失败而不是静默回退到 Vanilla。
/// </summary>
