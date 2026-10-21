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
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// 在临时 Minecraft 沙箱中组合整合包所需版本，再把最终版本和共享运行时内容提交到目标目录。
/// </summary>
internal sealed class ModpackGameInstaller : IModpackGameInstaller
{
    private readonly IReadOnlyDictionary<LoaderKind, ILoaderProvider> providers;
    private readonly IFinalVersionInstaller finalVersionInstaller;
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly IModpackSandboxCleanupService sandboxCleanupService;
    private readonly ILogger logger;

    public ModpackGameInstaller(
        IEnumerable<ILoaderProvider> providers,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<ModpackGameInstaller>? logger = null,
        IModpackSandboxCleanupService? sandboxCleanupService = null)
        : this(
            providers,
            new FinalVersionInstaller(),
            httpClient: null,
            downloadSpeedLimitState,
            tempRootDirectory: null,
            logger,
            sandboxCleanupService)
    {
    }

    internal ModpackGameInstaller(
        IEnumerable<ILoaderProvider> providers,
        IFinalVersionInstaller finalVersionInstaller,
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        string? tempRootDirectory = null,
        ILogger? logger = null,
        IModpackSandboxCleanupService? sandboxCleanupService = null)
    {
        this.providers = providers.ToDictionary(provider => provider.Kind);
        this.finalVersionInstaller = finalVersionInstaller;
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
        this.sandboxCleanupService = sandboxCleanupService ?? new ModpackSandboxCleanupService(
            string.IsNullOrWhiteSpace(tempRootDirectory) ? Path.GetTempPath() : tempRootDirectory,
            logger: this.logger);
    }

    /// <summary>
    /// 根据 Loader 类型选择沙箱组合流程；Forge 系列委托其专用安装提供方。
    /// </summary>
    public Task<string> InstallLoaderAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        ValidateTarget(target);
        return loader switch
        {
            LoaderKind.Vanilla => InstallVanillaInSandboxAsync(
                minecraftVersion,
                target,
                progress,
                cancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond),
            LoaderKind.Fabric => InstallFabricInSandboxAsync(
                minecraftVersion,
                loaderVersion,
                target,
                progress,
                cancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond),
            LoaderKind.Quilt => InstallQuiltInSandboxAsync(
                minecraftVersion,
                loaderVersion,
                target,
                progress,
                cancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond),
            _ => InstallInstanceAsync(
                minecraftVersion,
                loader,
                loaderVersion,
                target,
                progress,
                cancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond)
        };
    }

    /// <summary>
    /// 调用已注册 Loader 提供方在指定目标目录安装隔离实例版本。
    /// </summary>
    public Task<string> InstallInstanceAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        ValidateTarget(target);
        if (!providers.TryGetValue(loader, out var provider) || !provider.IsImplemented)
            throw new NotSupportedException($"{loader} is not implemented yet.");

        return InstallProviderInSandboxAsync(
            provider,
            minecraftVersion,
            loaderVersion,
            target,
            progress,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond);
    }

    private Task<string> InstallVanillaInSandboxAsync(
        string minecraftVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond)
    {
        return InstallComposedVersionInSandboxAsync(
            "Vanilla",
            minecraftVersion,
            target,
            progress,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            (sandboxMinecraftDirectory, token) => VanillaVersionComposer.PrepareFinalVersionAsync(
                httpClient,
                minecraftVersion,
                target.LogicalVersionName,
                sandboxMinecraftDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                token));
    }

    private async Task<string> InstallFabricInSandboxAsync(
        string minecraftVersion,
        string? loaderVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond)
    {
        var selectedLoaderVersion = loaderVersion;
        if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
        {
            if (!providers.TryGetValue(LoaderKind.Fabric, out var provider))
                throw new InvalidOperationException("Fabric loader provider is not available.");

            selectedLoaderVersion = (await provider.GetLoaderVersionsAsync(
                minecraftVersion,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond).ConfigureAwait(false))
                .FirstOrDefault()?
                .Version;
        }

        if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
            throw new InvalidOperationException($"No Fabric loader version available for {minecraftVersion}.");

        return await InstallComposedVersionInSandboxAsync(
            "Fabric",
            minecraftVersion,
            target,
            progress,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            (sandboxMinecraftDirectory, token) => FabricVersionComposer.PrepareFinalVersionAsync(
                httpClient,
                minecraftVersion,
                selectedLoaderVersion,
                target.LogicalVersionName,
                sandboxMinecraftDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                token)).ConfigureAwait(false);
    }

    private async Task<string> InstallQuiltInSandboxAsync(
        string minecraftVersion,
        string? loaderVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond)
    {
        var selectedLoaderVersion = loaderVersion;
        if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
        {
            if (!providers.TryGetValue(LoaderKind.Quilt, out var provider))
                throw new InvalidOperationException("Quilt loader provider is not available.");

            selectedLoaderVersion = (await provider.GetLoaderVersionsAsync(
                minecraftVersion,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond).ConfigureAwait(false))
                .FirstOrDefault()?
                .Version;
        }

        if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
            throw new InvalidOperationException($"No Quilt loader version available for {minecraftVersion}.");

        return await InstallComposedVersionInSandboxAsync(
            "Quilt",
            minecraftVersion,
            target,
            progress,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            (sandboxMinecraftDirectory, token) => QuiltVersionComposer.PrepareFinalVersionAsync(
                httpClient,
                minecraftVersion,
                selectedLoaderVersion,
                target.LogicalVersionName,
                sandboxMinecraftDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                token)).ConfigureAwait(false);
    }

    /// <summary>
    /// 播种沙箱、组合并补齐版本、提交最终目录与共享内容，最后无条件清理沙箱。
    /// </summary>
    private async Task<string> InstallComposedVersionInSandboxAsync(
        string loaderName,
        string minecraftVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        Func<string, CancellationToken, Task<PreparedVersionInstall>> composeAsync)
    {
        // Loader 安装器只能面向完整 .minecraft 工作；沙箱隔离其中间文件，避免污染真实实例。
        await using var sandboxSession = sandboxCleanupService.CreateSession(ModpackSandboxKind.ModpackVersion);
        var sessionDirectory = sandboxSession.DirectoryPath;
        var sandboxMinecraftDirectory = Path.Combine(sessionDirectory, ".minecraft");
        var wasCanceled = false;

        logger.LogDebug(
            "Installing modpack loader version through sandbox. Loader={Loader} MinecraftVersion={MinecraftVersion} TargetMinecraftDirectory={TargetMinecraftDirectory} TargetVersionName={TargetVersionName} SessionDirectory={SessionDirectory}",
            loaderName,
            minecraftVersion,
            target.MinecraftDirectory,
            target.LogicalVersionName,
            sessionDirectory);

        try
        {
            // 版本文件留在私有沙箱；共享运行库通过分离路径直接复用真实 Minecraft 缓存。
            var installLayout = MinecraftInstallPathLayout.Create(
                sandboxMinecraftDirectory,
                target.MinecraftDirectory);
            using var composeDownloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(installLayout.Path);
            var finalVersionName = await ComposedVersionInstallRunner.RunAsync(
                token => composeAsync(sandboxMinecraftDirectory, token),
                (versionName, token) => finalVersionInstaller.InstallAsync(
                    installLayout.Path,
                    versionName,
                    composeDownloadOperation,
                    downloadSourcePreference,
                    progress,
                    token,
                    downloadSpeedLimitMbPerSecond),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new LauncherProgress(InstallProgressStages.FinalizingVersion, string.Empty));
            var validation = await new GameFileIntegrityService(httpClient, downloadSpeedLimitState, logger)
                .ValidateInstalledVersionAsync(
                    new GameFileIntegrityRequest(
                        target.MinecraftDirectory,
                        finalVersionName,
                        Path.Combine(sandboxMinecraftDirectory, "versions", finalVersionName),
                        downloadSourcePreference,
                        downloadSpeedLimitMbPerSecond),
                    composeDownloadOperation,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            if (!validation.LaunchAllowed)
            {
                throw new InstanceRepairException(
                    $"Installed {loaderName} version {finalVersionName} failed required-file validation.");
            }

            MinecraftVersionDirectoryCopier.CopyVersionDirectoryTo(
                sandboxMinecraftDirectory,
                finalVersionName,
                target.PhysicalOutputDirectory,
                allowExistingDestination: true,
                cancellationToken: cancellationToken);
            logger.LogDebug(
                "Installed modpack loader version through split sandbox paths. Loader={Loader} MinecraftVersion={MinecraftVersion} FinalVersionName={FinalVersionName} SessionDirectory={SessionDirectory} SharedMinecraftDirectory={SharedMinecraftDirectory}",
                loaderName,
                minecraftVersion,
                finalVersionName,
                sessionDirectory,
                target.MinecraftDirectory);
            return finalVersionName;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Installing modpack loader version through sandbox failed. Loader={Loader} MinecraftVersion={MinecraftVersion} TargetVersionName={TargetVersionName} SessionDirectory={SessionDirectory}",
                loaderName,
                minecraftVersion,
                target.LogicalVersionName,
                sessionDirectory);
            throw;
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
            throw;
        }
        finally
        {
            await sandboxSession
                .CleanupAsync(wasCanceled || cancellationToken.IsCancellationRequested)
                .ConfigureAwait(false);
        }
    }

    private async Task<string> InstallProviderInSandboxAsync(
        ILoaderProvider provider,
        string minecraftVersion,
        string? loaderVersion,
        LoaderInstallTarget target,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond)
    {
        await using var sandboxSession = sandboxCleanupService.CreateSession(ModpackSandboxKind.InstanceVersion);
        var sessionDirectory = sandboxSession.DirectoryPath;
        var sandboxMinecraftDirectory = Path.Combine(sessionDirectory, ".minecraft");
        var wasCanceled = false;
        var finalVersionName = string.Empty;
        try
        {
            var separatedPathProvider = provider as ISeparatedInstallPathLoaderProvider;
            finalVersionName = provider is IStagedLoaderProvider stagedProvider
                ? await stagedProvider.InstallStagedAsync(
                    minecraftVersion,
                    sandboxMinecraftDirectory,
                    target.MinecraftDirectory,
                    target.LogicalVersionName,
                    loaderVersion,
                    progress,
                    downloadSourcePreference,
                    cancellationToken,
                    downloadSpeedLimitMbPerSecond).ConfigureAwait(false)
                : separatedPathProvider is not null
                    ? await separatedPathProvider.InstallWithSeparatedPathsAsync(
                        minecraftVersion,
                        MinecraftInstallPathLayout.Create(sandboxMinecraftDirectory, target.MinecraftDirectory),
                        target.LogicalVersionName,
                        loaderVersion,
                        progress,
                        downloadSourcePreference,
                        cancellationToken,
                        downloadSpeedLimitMbPerSecond).ConfigureAwait(false)
                    : await provider.InstallAsync(
                        minecraftVersion,
                        sandboxMinecraftDirectory,
                        target.LogicalVersionName,
                        loaderVersion,
                        progress,
                        downloadSourcePreference,
                        cancellationToken,
                        downloadSpeedLimitMbPerSecond).ConfigureAwait(false);
            if (!string.Equals(finalVersionName, target.LogicalVersionName, StringComparison.Ordinal))
                throw new InvalidDataException("Loader returned a version identity different from the logical install name.");

            logger.LogDebug(
                "Instance loader sandbox publication started. Loader={Loader} VersionName={VersionName} SessionDirectory={SessionDirectory}",
                provider.Kind,
                finalVersionName,
                sessionDirectory);
            var publicationStopwatch = Stopwatch.StartNew();
            var versionCopyStopwatch = Stopwatch.StartNew();
            MinecraftVersionDirectoryCopier.CopyVersionDirectoryTo(
                sandboxMinecraftDirectory,
                finalVersionName,
                target.PhysicalOutputDirectory,
                allowExistingDestination: true,
                cancellationToken);
            versionCopyStopwatch.Stop();

            var sharedFilePublishDuration = 0L;
            if (separatedPathProvider is null
                && provider is not IDirectSharedContentStagedLoaderProvider)
            {
                var sharedFilePublishStopwatch = Stopwatch.StartNew();
                await new LoaderInstallerPrerequisiteSeeder(logger).PublishDeltaAsync(
                    new LoaderInstallerWorkspaceSnapshot(
                        sandboxMinecraftDirectory,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                    target.MinecraftDirectory,
                    cancellationToken).ConfigureAwait(false);
                sharedFilePublishStopwatch.Stop();
                sharedFilePublishDuration = sharedFilePublishStopwatch.ElapsedMilliseconds;
            }
            publicationStopwatch.Stop();
            logger.LogDebug(
                "Instance loader sandbox publication completed. Loader={Loader} VersionName={VersionName} TotalDurationMs={TotalDurationMs} VersionCopyDurationMs={VersionCopyDurationMs} SharedFilePublishDurationMs={SharedFilePublishDurationMs}",
                provider.Kind,
                finalVersionName,
                publicationStopwatch.ElapsedMilliseconds,
                versionCopyStopwatch.ElapsedMilliseconds,
                sharedFilePublishDuration);
            return finalVersionName;
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
            throw;
        }
        finally
        {
            logger.LogDebug(
                "Instance loader sandbox cleanup started. Loader={Loader} VersionName={VersionName} SessionDirectory={SessionDirectory}",
                provider.Kind,
                finalVersionName,
                sessionDirectory);
            var cleanupStopwatch = Stopwatch.StartNew();
            await sandboxSession
                .CleanupAsync(wasCanceled || cancellationToken.IsCancellationRequested)
                .ConfigureAwait(false);
            cleanupStopwatch.Stop();
            logger.LogDebug(
                "Instance loader sandbox cleanup completed. Loader={Loader} VersionName={VersionName} DurationMs={DurationMs} SessionDirectory={SessionDirectory}",
                provider.Kind,
                finalVersionName,
                cleanupStopwatch.ElapsedMilliseconds,
                sessionDirectory);
        }
    }

    private static void ValidateTarget(LoaderInstallTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.LogicalVersionName))
            throw new ArgumentException("Logical version name is required.", nameof(target));
        var versionsDirectory = Path.GetFullPath(Path.Combine(target.MinecraftDirectory, "versions"));
        var outputDirectory = Path.GetFullPath(target.PhysicalOutputDirectory);
        if (!string.Equals(Path.GetDirectoryName(outputDirectory), versionsDirectory, StringComparison.OrdinalIgnoreCase)
            || !PendingInstanceInstallDirectory.IsPending(outputDirectory))
        {
            throw new ArgumentException("Physical version output must be a pending install directory.", nameof(target));
        }
    }
}
