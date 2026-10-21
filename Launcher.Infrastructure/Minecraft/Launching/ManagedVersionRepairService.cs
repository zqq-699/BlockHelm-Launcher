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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal interface IManagedVersionRepairService
{
    Task RepairAsync(
        string minecraftDirectory,
        string versionName,
        string instanceDirectory,
        IProgress<LauncherProgress>? progress,
        bool allowRepair,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0);
}

/// <summary>
/// 检查或修复版本 JSON、客户端 JAR、库、资源和日志配置，并将继承版本规范化为自包含版本。
/// </summary>
internal sealed partial class ManagedVersionRepairService : IManagedVersionRepairService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;
    private readonly LoaderArtifactRepairCoordinator loaderArtifactRepairCoordinator;
    private readonly ConcurrentDictionary<(string VersionName, DownloadSourcePreference Source), JsonObject>
        remoteVersionMetadataCache = new();

    public ManagedVersionRepairService(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        IForgeInstallerRunner? forgeInstallerRunner = null,
        IFinalVersionInstaller? finalVersionInstaller = null,
        string? tempRootDirectory = null,
        IEnumerable<ILoaderProvider>? loaderProviders = null,
        IGameInstallCoordinator? gameInstallCoordinator = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
        var providers = loaderProviders?.ToArray()
            ?? CreateDefaultLoaderProviders(
                this.httpClient,
                downloadSpeedLimitState,
                forgeInstallerRunner,
                finalVersionInstaller,
                tempRootDirectory);
        loaderArtifactRepairCoordinator = new LoaderArtifactRepairCoordinator(
            providers,
            gameInstallCoordinator ?? new GameInstallCoordinator(),
            this.logger);
    }

    /// <summary>
    /// 按元数据、JAR、库、资源和日志配置的顺序检查或修复启动所需文件。
    /// </summary>
    public Task RepairAsync(
        string minecraftDirectory,
        string versionName,
        string instanceDirectory,
        IProgress<LauncherProgress>? progress,
        bool allowRepair,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        return RepairWithOperationAsync(
            minecraftDirectory,
            versionName,
            instanceDirectory,
            progress,
            allowRepair,
            operationContext: null,
            cancellationToken: cancellationToken,
            downloadSourcePreference: downloadSourcePreference,
            downloadSpeedLimitMbPerSecond: downloadSpeedLimitMbPerSecond,
            loaderIdentity: null);
    }

    internal Task RepairWithIdentityAsync(
        string minecraftDirectory,
        string versionName,
        string instanceDirectory,
        IProgress<LauncherProgress>? progress,
        bool allowRepair,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        GameFileLoaderIdentity? loaderIdentity)
    {
        return RepairWithOperationAsync(
            minecraftDirectory,
            versionName,
            instanceDirectory,
            progress,
            allowRepair,
            operationContext: null,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            loaderIdentity);
    }

    public async Task RepairWithOperationAsync(
        string minecraftDirectory,
        string versionName,
        string instanceDirectory,
        IProgress<LauncherProgress>? progress,
        bool allowRepair,
        MinecraftDownloadOperationContext? operationContext,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        GameFileLoaderIdentity? loaderIdentity = null)
    {
        var versionDirectory = ResolveVersionDirectory(minecraftDirectory, versionName, instanceDirectory);
        if (!Directory.Exists(versionDirectory))
            throw new InstanceRepairException($"Version directory is missing for {versionName}.");

        // Existing instance metadata is authoritative. Validate it before any
        // loader sandbox is allowed to publish a replacement for an invalid
        // target JSON.
        _ = await ReadVersionJsonAsync(versionDirectory, versionName, cancellationToken)
            .ConfigureAwait(false);

        if (loaderIdentity is not null
            && await loaderArtifactRepairCoordinator.RequiresRepairAsync(
                    minecraftDirectory,
                    versionName,
                    versionDirectory,
                    loaderIdentity,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            if (!allowRepair)
            {
                throw new InstanceRepairException(
                    $"{loaderIdentity.LoaderKind} managed files require repair and automatic repair is disabled.");
            }
            await loaderArtifactRepairCoordinator.RepairAsync(
                    minecraftDirectory,
                    versionName,
                    versionDirectory,
                    loaderIdentity,
                    progress,
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var downloadBatch = new ManagedVersionRepairDownloadBatch(
            httpClient,
            downloadSpeedLimitState,
            logger,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            progress,
            operationContext);
        ReportProgress(progress, LaunchProgressStages.CheckingInstance, "Checking instance files", 6);
        ReportProgress(
            progress,
            allowRepair ? LaunchProgressStages.RepairingMetadata : LaunchProgressStages.CheckingFiles,
            allowRepair ? "Repairing version metadata" : "Checking launch files",
            18);
        var resolvedVersion = await ResolveVersionForRepairAsync(
            minecraftDirectory,
            versionName,
            versionDirectory,
            downloadSourcePreference,
            cancellationToken,
            allowRemoteParentResolution: allowRepair,
            downloadSpeedLimitMbPerSecond);

        ReportProgress(
            progress,
            allowRepair ? LaunchProgressStages.RepairingJar : LaunchProgressStages.CheckingFiles,
            allowRepair ? "Repairing instance jar" : "Checking launch files",
            32);
        await EnsureVersionJarAsync(
            versionDirectory,
            versionName,
            resolvedVersion,
            allowRepair,
            downloadBatch,
            cancellationToken);

        ReportProgress(
            progress,
            allowRepair ? LaunchProgressStages.RepairingLibraries : LaunchProgressStages.CheckingFiles,
            allowRepair ? "Repairing shared libraries" : "Checking launch files",
            48);
        await EnsureLibrariesAsync(
            minecraftDirectory,
            resolvedVersion.VersionJson,
            allowRepair,
            downloadBatch,
            cancellationToken);

        ReportProgress(
            progress,
            allowRepair ? LaunchProgressStages.RepairingAssets : LaunchProgressStages.CheckingFiles,
            allowRepair ? "Repairing shared assets" : "Checking launch files",
            64);
        await EnsureAssetsAsync(
            minecraftDirectory,
            resolvedVersion.VersionJson,
            allowRepair,
            downloadBatch,
            cancellationToken);

        ReportProgress(
            progress,
            allowRepair ? LaunchProgressStages.RepairingLogging : LaunchProgressStages.CheckingFiles,
            allowRepair ? "Repairing logging configuration" : "Checking launch files",
            80);
        await EnsureLoggingAsync(
            minecraftDirectory,
            resolvedVersion.VersionJson,
            allowRepair,
            downloadBatch,
            cancellationToken);

        ReportProgress(progress, LaunchProgressStages.CheckingFiles, "Game file repair completed", 90);
    }

    private static ILoaderProvider[] CreateDefaultLoaderProviders(
        HttpClient httpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        IForgeInstallerRunner? forgeInstallerRunner,
        IFinalVersionInstaller? finalVersionInstaller,
        string? tempRootDirectory)
    {
        return
        [
            new VanillaLoaderProvider(httpClient, downloadSpeedLimitState),
            new FabricLoaderProvider(httpClient, downloadSpeedLimitState),
            new ForgeLoaderProvider(
                httpClient,
                forgeInstallerRunner,
                finalVersionInstaller,
                tempRootDirectory,
                downloadSpeedLimitState),
            new NeoForgeLoaderProvider(
                httpClient,
                forgeInstallerRunner,
                finalVersionInstaller,
                tempRootDirectory,
                downloadSpeedLimitState),
            new QuiltLoaderProvider(httpClient, downloadSpeedLimitState)
        ];
    }

    /// <summary>
    /// 验证或消除 inheritsFrom 依赖，并返回最终版本 JSON 与客户端 JAR 来源。
    /// </summary>
}
