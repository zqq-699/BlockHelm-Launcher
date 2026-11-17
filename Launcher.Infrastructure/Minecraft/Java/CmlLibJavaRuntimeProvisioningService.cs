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

using System.Net.Http;
using CmlLib.Core;
using CmlLib.Core.Version;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed class CmlLibJavaRuntimeProvisioningService
    : IJavaRuntimeProvisioningService, ILoaderInstallerJavaRuntimeProvisioner
{
    private const double ProgressStartPercent = 90;
    private const double ProgressEndPercent = 94;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly HttpClient? metadataHttpClient;
    private readonly ILogger logger;

    public CmlLibJavaRuntimeProvisioningService(
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<CmlLibJavaRuntimeProvisioningService>? logger = null)
    {
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<CmlLibJavaRuntimeProvisioningService>.Instance;
    }

    internal CmlLibJavaRuntimeProvisioningService(
        HttpClient metadataHttpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null)
    {
        this.metadataHttpClient = metadataHttpClient;
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task EnsureForLaunchAsync(
        GameInstance instance,
        LauncherSettings settings,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var versionName = string.IsNullOrWhiteSpace(instance.VersionName)
            ? instance.MinecraftVersion
            : instance.VersionName;
        if (string.IsNullOrWhiteSpace(versionName))
            throw SafeExceptionLog.Describe(new InvalidOperationException("Version name is required to prepare Java runtime."), "DownloadValidation", ("Reason", "Version name is required to prepare Java runtime."));

        if (string.IsNullOrWhiteSpace(settings.MinecraftDirectory))
            throw SafeExceptionLog.Describe(new InvalidOperationException("Minecraft directory is required to prepare Java runtime."), "DownloadValidation", ("Reason", "Minecraft directory is required to prepare Java runtime."));

        logger.LogDebug(
            "Preparing Java runtime for launch. InstanceDirectory={InstanceDirectory} InstanceName={InstanceName} VersionName={VersionName} MinecraftDirectory={MinecraftDirectory} DownloadSourcePreference={DownloadSourcePreference} DownloadSpeedLimitMbPerSecond={DownloadSpeedLimitMbPerSecond}",
            instance.InstanceDirectory,
            instance.Name,
            versionName,
            settings.MinecraftDirectory,
            settings.DownloadSourcePreference,
            settings.DownloadSpeedLimitMbPerSecond);
        progress?.Report(new LauncherProgress(LaunchProgressStages.CheckingJava, string.Empty, ProgressStartPercent));
        var provisioningProgress = progress is null
            ? null
            : new JavaRuntimeProvisioningProgress(progress);

        try
        {
            using var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(
                new MinecraftPath(settings.MinecraftDirectory));
            var launcher = VanillaLoaderProvider.CreateLauncher(
                settings.MinecraftDirectory,
                provisioningProgress,
                settings.DownloadSourcePreference,
                logger,
                settings.DownloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                downloadOperation,
                javaFileMode: CmlLibJavaFileMode.Only);
            VanillaLoaderProvider.AttachProgress(launcher, provisioningProgress);
            await launcher.InstallAsync(versionName, cancellationToken).ConfigureAwait(false);
            progress?.Report(new LauncherProgress(LaunchProgressStages.CheckingJava, string.Empty, ProgressEndPercent));

            logger.LogInformation(
                "Java runtime preparation completed. InstanceDirectory={InstanceDirectory} VersionName={VersionName}",
                instance.InstanceDirectory,
                versionName);
            logger.LogDebug("Java runtime preparation directory. MinecraftDirectory={MinecraftDirectory}", settings.MinecraftDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Java runtime preparation failed. InstanceDirectory={InstanceDirectory} VersionName={VersionName} MinecraftDirectory={MinecraftDirectory}",
                instance.InstanceDirectory,
                versionName,
                settings.MinecraftDirectory);
            throw;
        }
    }

    async Task ILoaderInstallerJavaRuntimeProvisioner.ProvisionAsync(
        LoaderInstallerJavaRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftDirectory);

        logger.LogDebug(
            "Preparing Java runtime for loader installer. VersionName={VersionName} MinecraftVersion={MinecraftVersion} MinecraftDirectory={MinecraftDirectory} DownloadSourcePreference={DownloadSourcePreference} DownloadSpeedLimitMbPerSecond={DownloadSpeedLimitMbPerSecond}",
            request.VersionName,
            request.MinecraftVersion,
            request.MinecraftDirectory,
            request.DownloadSourcePreference,
            request.DownloadSpeedLimitMbPerSecond);
        request.Progress?.Report(new LauncherProgress(InstallProgressStages.DownloadingJava, string.Empty));
        var provisioningProgress = request.Progress is null
            ? null
            : new InstallerJavaRuntimeProvisioningProgress(request.Progress);

        try
        {
            using var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(
                new MinecraftPath(request.MinecraftDirectory));
            var launcher = VanillaLoaderProvider.CreateLauncher(
                request.MinecraftDirectory,
                provisioningProgress,
                request.DownloadSourcePreference,
                logger,
                request.DownloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                downloadOperation,
                javaFileMode: CmlLibJavaFileMode.Only);
            VanillaLoaderProvider.AttachProgress(launcher, provisioningProgress);
            using var ownedMetadataHttpClient = metadataHttpClient is null
                ? MinecraftHttpClientFactory.CreateTransportClient()
                : null;
            var effectiveMetadataHttpClient = metadataHttpClient ?? ownedMetadataHttpClient!;
            var versionJson = await VanillaVersionMetadataClient.DownloadVersionJsonAsync(
                    effectiveMetadataHttpClient,
                    request.MinecraftVersion,
                    request.DownloadSourcePreference,
                    request.DownloadSpeedLimitMbPerSecond,
                    downloadSpeedLimitState,
                    logger,
                    cancellationToken)
                .ConfigureAwait(false);
            var version = JsonVersionParser.ParseFromJsonString(
                versionJson.ToJsonString(),
                new JsonVersionParserOptions());
            await launcher.InstallAsync(version, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Java runtime preparation completed for loader installer. VersionName={VersionName} MinecraftVersion={MinecraftVersion}",
                request.VersionName,
                request.MinecraftVersion);
            logger.LogDebug("Loader installer Java runtime directory. MinecraftDirectory={MinecraftDirectory}", request.MinecraftDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Java runtime preparation failed for loader installer. VersionName={VersionName} MinecraftVersion={MinecraftVersion} MinecraftDirectory={MinecraftDirectory}",
                request.VersionName,
                request.MinecraftVersion,
                request.MinecraftDirectory);
            throw;
        }
    }

    internal sealed class JavaRuntimeProvisioningProgress : IProgress<LauncherProgress>, ISpeedMeterProgress
    {
        private readonly IProgress<LauncherProgress> inner;

        public JavaRuntimeProvisioningProgress(IProgress<LauncherProgress> inner)
        {
            this.inner = inner;
        }

        public SpeedMeter? SpeedMeter => SpeedMeterProgress.TryGet(inner);

        public void Report(LauncherProgress value)
        {
            if (value.DownloadSpeedTelemetry is not null)
            {
                inner.Report(value);
                return;
            }
            var percent = value.Percent is double progressPercent
                ? ProgressStartPercent + Math.Clamp(progressPercent, 0, 100) / 100d * (ProgressEndPercent - ProgressStartPercent)
                : value.Percent;

            inner.Report(value with
            {
                Stage = LaunchProgressStages.CheckingJava,
                Percent = percent
            });
        }
    }

    internal sealed class InstallerJavaRuntimeProvisioningProgress : IProgress<LauncherProgress>, ISpeedMeterProgress
    {
        private readonly IProgress<LauncherProgress> inner;

        public InstallerJavaRuntimeProvisioningProgress(IProgress<LauncherProgress> inner)
        {
            this.inner = inner;
        }

        public SpeedMeter? SpeedMeter => SpeedMeterProgress.TryGet(inner);

        public void Report(LauncherProgress value)
        {
            if (value.DownloadSpeedTelemetry is not null)
            {
                inner.Report(value with { Stage = InstallProgressStages.DownloadingJava });
                return;
            }

            inner.Report(value with { Stage = InstallProgressStages.DownloadingJava });
        }
    }
}
