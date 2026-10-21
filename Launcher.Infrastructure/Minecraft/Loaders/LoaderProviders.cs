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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal enum CmlLibJavaFileMode
{
    Exclude,
    Only
}

/// <summary>
/// 安装原版 Minecraft，并提供各 Loader 共用的 CmlLib 下载进度与启动器配置。
/// </summary>
public sealed class VanillaLoaderProvider : ILoaderProvider, ISeparatedInstallPathLoaderProvider
{
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;

    public VanillaLoaderProvider(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<VanillaLoaderProvider>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<VanillaLoaderProvider>.Instance;
    }

    public LoaderKind Kind => LoaderKind.Vanilla;
    public bool IsImplemented => true;

    public Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        IReadOnlyList<LoaderVersionInfo> versions = [new LoaderVersionInfo(nameof(LoaderKind.Vanilla))];
        return Task.FromResult(versions);
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
        // 先生成隔离版本元数据，再交给 CmlLib 补齐文件；返回值必须是实际可启动版本名。
        _ = loaderVersion;
        return InstallCoreAsync(
            minecraftVersion,
            new MinecraftPath(gameDirectory),
            gameDirectory,
            gameDirectory,
            isolatedVersionName,
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
        int downloadSpeedLimitMbPerSecond)
    {
        _ = loaderVersion;
        return InstallCoreAsync(
            minecraftVersion,
            installPathLayout.Path,
            installPathLayout.WorkspaceMinecraftDirectory,
            installPathLayout.SharedMinecraftDirectory,
            isolatedVersionName,
            progress,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    private async Task<string> InstallCoreAsync(
        string minecraftVersion,
        MinecraftPath minecraftPath,
        string versionWorkspaceDirectory,
        string sharedMinecraftDirectory,
        string isolatedVersionName,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond)
    {
        progress?.Report(new LauncherProgress(InstallProgressStages.Preparing, string.Empty));
        using var downloadOperation = CreateDownloadOperationContext(minecraftPath);
        var speedMeter = SpeedMeterProgress.TryGet(progress);

        var launcher = CreateLauncher(
            minecraftPath,
            progress,
            downloadSourcePreference,
            logger,
            downloadSpeedLimitMbPerSecond,
            downloadSpeedLimitState,
            downloadOperation,
            speedMeter);
        AttachProgress(launcher, progress);
        var finalVersionName = await ComposedVersionInstallRunner.RunAsync(
            token => VanillaVersionComposer.PrepareFinalVersionAsync(
                httpClient,
                minecraftVersion,
                isolatedVersionName,
                versionWorkspaceDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                token),
            async (versionName, token) => await launcher.InstallAsync(versionName, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        await EnsureInstalledVersionIsValidAsync(
            finalVersionName,
            minecraftVersion,
            sharedMinecraftDirectory,
            versionWorkspaceDirectory,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            progress,
            cancellationToken,
            downloadOperation).ConfigureAwait(false);
        return finalVersionName;
    }

    private async Task EnsureInstalledVersionIsValidAsync(
        string versionName,
        string minecraftVersion,
        string sharedMinecraftDirectory,
        string versionWorkspaceDirectory,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext downloadOperation)
    {
        var result = await new GameFileIntegrityService(httpClient, downloadSpeedLimitState, logger)
            .ValidateInstalledVersionAsync(
                new GameFileIntegrityRequest(
                    sharedMinecraftDirectory,
                    versionName,
                    Path.Combine(versionWorkspaceDirectory, "versions", versionName),
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond)
                {
                    LoaderIdentity = new GameFileLoaderIdentity(
                        LoaderKind.Vanilla,
                        minecraftVersion,
                        LoaderVersion: null)
                },
                downloadOperation,
                progress,
                cancellationToken).ConfigureAwait(false);
        if (!result.LaunchAllowed)
            throw new InstanceRepairException($"Installed vanilla version {versionName} failed required-file validation.");
    }

    internal static void AttachProgress(MinecraftLauncher launcher, IProgress<LauncherProgress>? progress)
    {
        if (progress is null)
            return;

        // 文件数和当前文件字节进度组合成总体百分比，并强制单调，避免 UI 进度条倒退。
        // 高频事件可能来自多个下载线程，因此统计与节流状态统一在此锁内更新。
        var syncRoot = new object();
        var totalTasks = 0;
        var progressedTasks = 0;
        var currentTaskFraction = 0d;
        var lastPercent = 0d;
        var lastReportedPercent = 0d;
        var lastReportedAt = DateTimeOffset.MinValue;
        var lastReportedMessage = string.Empty;
        launcher.FileProgressChanged += (_, args) =>
        {
            lock (syncRoot)
            {
                if (args.TotalTasks > 0)
                    totalTasks = args.TotalTasks;

                progressedTasks = totalTasks <= 0
                    ? Math.Max(args.ProgressedTasks, 0)
                    : Math.Clamp(args.ProgressedTasks, 0, totalTasks);
                currentTaskFraction = 0;

                // CML only distinguishes queued/done file events. They describe
                // this file's verification or completion, even while another
                // parallel file is reading a response body. Keep that stage
                // truthful; ByteProgressChanged remains the download signal and
                // the separate speed telemetry is not cleared here.
                ReportProgress(
                    LaunchProgressStages.CheckingFiles,
                    $"{args.EventType}: {args.Name}",
                    CalculateTotalPercent());
            }
        };

        launcher.ByteProgressChanged += (_, args) =>
        {
            lock (syncRoot)
            {
                currentTaskFraction = args.TotalBytes <= 0
                    ? 0
                    : Math.Clamp(args.ProgressedBytes * 1d / args.TotalBytes, 0, 1);

                ReportProgress(LaunchProgressStages.DownloadingFiles, string.Empty, CalculateTotalPercent());
            }
        };

        double? CalculateTotalPercent()
        {
            if (totalTasks <= 0)
                return null;

            return (progressedTasks + currentTaskFraction) * 100d / totalTasks;
        }

        void ReportProgress(string stage, string message, double? percent)
        {
            var now = DateTimeOffset.UtcNow;
            if (percent is null)
            {
                if (now - lastReportedAt < TimeSpan.FromMilliseconds(250)
                    && string.Equals(lastReportedMessage, message, StringComparison.Ordinal))
                {
                    return;
                }

                lastReportedAt = now;
                lastReportedMessage = message;
                progress.Report(new LauncherProgress(stage, message));
                return;
            }

            // CML may finish its file-check pass before the remaining download,
            // publishing and installer work. Keep 100% exclusively for the
            // caller's successful completion signal.
            var nextPercent = Math.Clamp(percent.Value, 0, 99);
            if (nextPercent < lastPercent)
                nextPercent = lastPercent;

            lastPercent = nextPercent;
            if (nextPercent < 100
                && nextPercent - lastReportedPercent < 0.35
                && now - lastReportedAt < TimeSpan.FromMilliseconds(120))
            {
                return;
            }

            lastReportedPercent = nextPercent;
            lastReportedAt = now;
            lastReportedMessage = message;
            progress.Report(new LauncherProgress(stage, message, nextPercent));
        }
    }

    internal static MinecraftDownloadOperationContext CreateDownloadOperationContext(MinecraftPath path) =>
        new([path.Versions, path.Library, path.Assets, path.Resource, path.Runtime]);

    internal static MinecraftLauncher CreateLauncher(
        string gameDirectory,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        ILogger? logger = null,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? sharedSpeedMeter = null,
        CmlLibJavaFileMode javaFileMode = CmlLibJavaFileMode.Exclude)
    {
        return CreateLauncher(
            new MinecraftPath(gameDirectory),
            progress,
            downloadSourcePreference,
            logger,
            downloadSpeedLimitMbPerSecond,
            downloadSpeedLimitState,
            operationContext,
            sharedSpeedMeter,
            javaFileMode);
    }

    internal static MinecraftLauncher CreateLauncher(
        MinecraftPath path,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        ILogger? logger = null,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? sharedSpeedMeter = null,
        CmlLibJavaFileMode javaFileMode = CmlLibJavaFileMode.Exclude)
    {
        // 统一注入镜像路由、限速和运行库下载器，保证 Vanilla/Fabric 使用相同下载策略。
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        var bandwidthLimiter = DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState);
        parameters.HttpClient = LauncherHttpUserAgent.Apply(new HttpClient(new DownloadSourceRoutingHttpMessageHandler(
            downloadSourcePreference,
            DownloadConcurrencyCategory.Metadata,
            MinecraftHttpClientFactory.CreateTransportHandler(),
            logger,
            bandwidthLimiter))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        parameters.VersionLoader = new InstanceAuthoritativeVersionLoader(
            path,
            (versionName, cancellationToken) => VanillaVersionMetadataClient.DownloadVersionJsonAsync(
                parameters.HttpClient,
                versionName,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                cancellationToken));
        var runtimeHttpClient = MinecraftHttpClientFactory.CreateTransportClient();
        var runtimeExecutor = new MinecraftDownloadRequestExecutor(
            runtimeHttpClient,
            logger,
            bandwidthLimiter,
            category: DownloadConcurrencyCategory.Runtime);
        var assetIndexExecutor = new MinecraftDownloadRequestExecutor(
            runtimeHttpClient,
            logger,
            bandwidthLimiter,
            category: DownloadConcurrencyCategory.Metadata);
        var gameInstaller = DownloadSpeedTrackingGameInstaller.CreateAsCoreCount(
            parameters.HttpClient,
            runtimeExecutor,
            downloadSourcePreference,
            progress,
            path,
            operationContext,
            sharedSpeedMeter,
            logger);
        parameters.GameInstaller = gameInstaller;
        var globalDownloadSnapshot = ImportConcurrencyLimiter.Shared.DownloadSnapshot;
        logger?.LogDebug(
            "Minecraft game installer concurrency configured. CheckerConcurrency={CheckerConcurrency} DownloaderWorkerCapacity={DownloaderWorkerCapacity} GlobalTarget={GlobalTarget}",
            gameInstaller.ConfiguredMaxChecker,
            gameInstaller.ConfiguredMaxDownloader,
            globalDownloadSnapshot.CurrentTarget);
        var fileExtractors = parameters.FileExtractors
            ?? throw new InvalidOperationException("CmlLib did not configure its default file extractors.");
        if (javaFileMode is CmlLibJavaFileMode.Only)
        {
            for (var index = fileExtractors.Count() - 1; index >= 0; index--)
            {
                if (fileExtractors[index] is not CmlLib.Core.FileExtractors.JavaFileExtractor
                    and not CmlLib.Core.FileExtractors.LegacyJavaFileExtractor)
                {
                    fileExtractors.RemoveAt(index);
                }
            }
            return new MinecraftLauncher(parameters);
        }

        for (var index = fileExtractors.Count() - 1; index >= 0; index--)
        {
            if (fileExtractors[index] is CmlLib.Core.FileExtractors.JavaFileExtractor
                or CmlLib.Core.FileExtractors.LegacyJavaFileExtractor)
            {
                fileExtractors.RemoveAt(index);
            }
        }
        var defaultAssetExtractor = fileExtractors
            .Select((extractor, index) => (extractor, index))
            .First(entry => entry.extractor is CmlLib.Core.FileExtractors.AssetFileExtractor);
        fileExtractors.RemoveAt(defaultAssetExtractor.index);
        fileExtractors.Insert(
            defaultAssetExtractor.index,
            new SafeAssetFileExtractor(assetIndexExecutor, downloadSourcePreference, operationContext, sharedSpeedMeter, logger));

        var clientExtractor = fileExtractors
            .Select((extractor, index) => (extractor, index))
            .First(entry => entry.extractor is CmlLib.Core.FileExtractors.ClientFileExtractor);
        if (clientExtractor.index > 0)
        {
            fileExtractors.RemoveAt(clientExtractor.index);
            fileExtractors.Insert(0, clientExtractor.extractor);
        }
        return new MinecraftLauncher(parameters);
    }
}

/// <summary>
/// 查询并安装 Fabric Loader，并把官方元数据差异映射为稳定的可用版本语义。
/// </summary>
