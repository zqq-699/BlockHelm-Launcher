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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed class QuiltLoaderProvider : ILoaderProvider, ISeparatedInstallPathLoaderProvider
{
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;

    public QuiltLoaderProvider(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<QuiltLoaderProvider>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<QuiltLoaderProvider>.Instance;
    }

    public LoaderKind Kind => LoaderKind.Quilt;

    public bool IsImplemented => true;

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        logger.LogDebug(
            "Loading Quilt versions. MinecraftVersion={MinecraftVersion}",
            minecraftVersion);

        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Metadata);
        var result = await executor.ExecuteLookupAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{minecraftVersion}",
            downloadSourcePreference,
            categoryHint: "Quilt",
            async (context, token) =>
            {
                await using var stream = await context.Response.Content.ReadAsStreamAsync(token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (json.RootElement.ValueKind is not JsonValueKind.Array)
                {
                    throw new DownloadContentValidationException(
                        "Quilt loader metadata is not a JSON array.");
                }

                var entries = json.RootElement.EnumerateArray().ToList();
                if (entries.Count == 0)
                    throw new DownloadNoResultException("Quilt returned an empty loader list.");

                var versions = entries
                    .Select(ReadLoaderVersion)
                    .Where(version => version is not null)
                    .Select(version => version!)
                    .GroupBy(version => version.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(version => version.IsStable)
                    .ThenByDescending(version => ParseVersionKey(version.Version), QuiltVersionKeyComparer.Instance)
                    .ThenByDescending(version => version.Version, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (versions.Count == 0)
                {
                    throw new DownloadContentValidationException(
                        "Quilt loader metadata contains no valid loader entries.");
                }

                return (IReadOnlyList<LoaderVersionInfo>)versions;
            },
            statusCode => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            cancellationToken);

        var resolvedVersions = result.Found ? result.Value! : [];
        logger.LogDebug(
            "Loaded Quilt versions. MinecraftVersion={MinecraftVersion} Count={Count}",
            minecraftVersion,
            resolvedVersions.Count);
        return resolvedVersions;
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
            throw new InvalidOperationException($"No Quilt loader version available for {minecraftVersion}.");

        logger.LogInformation(
            "Installing Quilt. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion} TargetVersionName={TargetVersionName}",
            minecraftVersion,
            selectedLoaderVersion,
            isolatedVersionName);

        try
        {
            using var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(path);
            var speedMeter = SpeedMeterProgress.TryGet(progress);
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
                token => QuiltVersionComposer.PrepareFinalVersionAsync(
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
                            LoaderKind.Quilt,
                            minecraftVersion,
                            selectedLoaderVersion)
                    },
                    downloadOperation,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            if (!validation.LaunchAllowed)
                throw new InstanceRepairException($"Installed Quilt version {finalVersionName} failed required-file validation.");

            logger.LogInformation(
                "Quilt installation completed. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion} FinalVersionName={FinalVersionName}",
                minecraftVersion,
                selectedLoaderVersion,
                finalVersionName);
            return finalVersionName;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Quilt installation failed. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion} TargetVersionName={TargetVersionName}",
                minecraftVersion,
                selectedLoaderVersion,
                isolatedVersionName);
            throw;
        }
    }

    private static LoaderVersionInfo? ReadLoaderVersion(JsonElement item)
    {
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

        return new LoaderVersionInfo(version, IsStableVersion(version));
    }

    private static bool IsStableVersion(string version)
    {
        return version.IndexOf("-beta", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("-alpha", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("-rc", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("+snapshot", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("+pre", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static QuiltVersionKey ParseVersionKey(string version)
    {
        var numericPart = version;
        var suffixIndex = version.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            numericPart = version[..suffixIndex];

        var values = numericPart
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();
        return new QuiltVersionKey(values);
    }

    private readonly record struct QuiltVersionKey(int[] Parts);

    private sealed class QuiltVersionKeyComparer : IComparer<QuiltVersionKey>
    {
        public static QuiltVersionKeyComparer Instance { get; } = new();

        public int Compare(QuiltVersionKey left, QuiltVersionKey right)
        {
            var length = Math.Max(left.Parts.Length, right.Parts.Length);
            for (var index = 0; index < length; index++)
            {
                var leftValue = index < left.Parts.Length ? left.Parts[index] : 0;
                var rightValue = index < right.Parts.Length ? right.Parts[index] : 0;
                var comparison = leftValue.CompareTo(rightValue);
                if (comparison != 0)
                    return comparison;
            }

            return 0;
        }
    }
}
