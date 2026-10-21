/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net.Http;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class LoaderInstallerJavaRequirementResolver : ILoaderInstallerJavaRequirementResolver
{
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;

    public LoaderInstallerJavaRequirementResolver(
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<LoaderInstallerJavaRequirementResolver>? logger = null)
    {
        httpClient = MinecraftHttpClientFactory.CreateTransportClient();
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<LoaderInstallerJavaRequirementResolver>.Instance;
    }

    internal LoaderInstallerJavaRequirementResolver(
        HttpClient httpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<JavaRuntimeCompatibilityRequirement> ResolveRequirementAsync(
        LoaderInstallerJavaRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        var versionJson = await VanillaVersionMetadataClient.DownloadVersionJsonAsync(
            httpClient,
            request.MinecraftVersion,
            request.DownloadSourcePreference,
            request.DownloadSpeedLimitMbPerSecond,
            downloadSpeedLimitState,
            logger,
            cancellationToken).ConfigureAwait(false);
        var metadataMajorVersion = versionJson["javaVersion"]?["majorVersion"]?.GetValue<int?>();
        var requirement = JavaRuntimeCompatibilityResolver.Resolve(
            request.MinecraftVersion,
            request.Loader,
            request.LoaderVersion,
            metadataMajorVersion);

        logger.LogInformation(
            "Resolved Java requirement for loader installer. MinecraftVersion={MinecraftVersion} Loader={Loader} LoaderVersion={LoaderVersion} JavaRequirement={JavaRequirement} UsedMetadata={UsedMetadata}",
            request.MinecraftVersion,
            request.Loader,
            request.LoaderVersion,
            requirement,
            metadataMajorVersion is not null);
        return requirement;
    }
}
