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

using Launcher.Application.Repositories;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Application.Services;

/// <summary>
/// 串联安装租约、游戏与 Loader 安装、可选内容安装、实例持久化和失败清理。
/// </summary>
internal sealed class GameInstanceCreationCoordinator
{
    private readonly ISettingsService settingsService;
    private readonly IGameInstanceRepository repository;
    private readonly IReadOnlyDictionary<LoaderKind, ILoaderProvider> providers;
    private readonly IModrinthService? modrinthService;
    private readonly IModpackGameInstaller gameInstaller;
    private readonly IInstanceInstallTransactionService? installTransactionService;
    private readonly IGameInstallCoordinator installCoordinator;
    private readonly Func<LauncherSettings, CancellationToken, Task<IReadOnlyList<GameInstance>>> loadInstancesAsync;
    private readonly ILogger logger;

    public GameInstanceCreationCoordinator(
        ISettingsService settingsService,
        IGameInstanceRepository repository,
        IReadOnlyDictionary<LoaderKind, ILoaderProvider> providers,
        IModrinthService? modrinthService,
        IModpackGameInstaller? gameInstaller,
        IInstanceInstallTransactionService? installTransactionService,
        IGameInstallCoordinator installCoordinator,
        Func<LauncherSettings, CancellationToken, Task<IReadOnlyList<GameInstance>>> loadInstancesAsync,
        ILogger logger)
    {
        this.settingsService = settingsService;
        this.repository = repository;
        this.providers = providers;
        this.modrinthService = modrinthService;
        this.gameInstaller = gameInstaller ?? new LoaderProviderGameInstaller(providers);
        this.installTransactionService = installTransactionService;
        this.installCoordinator = installCoordinator;
        this.loadInstancesAsync = loadInstancesAsync;
        this.logger = logger;
    }

    /// <summary>
    /// 在独占安装租约中完成版本安装、可选内容安装、实例持久化和默认实例初始化。
    /// </summary>
    public async Task<GameInstance> CreateAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        string? name,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        bool installFabricApi,
        string? fabricApiVersionId,
        string? quiltStandardLibraryVersionId)
    {
        using var diagnosticOperation = DownloadDiagnostics.BeginOperation(logger, "InstanceInstall");
        if (!providers.TryGetValue(loader, out var provider) || !provider.IsImplemented)
            throw new NotSupportedException($"{loader} is not implemented yet.");

        logger.LogDebug(
            "Creating game instance. MinecraftVersion={MinecraftVersion} Loader={Loader} LoaderVersion={LoaderVersion} RequestedName={RequestedName}",
            minecraftVersion,
            loader,
            loaderVersion,
            name);

        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var cardProgress = progress is null
            ? null
            : new InstallCardProgressMapper(
                progress,
                loader,
                installFabricApi || !string.IsNullOrWhiteSpace(quiltStandardLibraryVersionId),
                useUnifiedCard: true);
        var versionIdentity = ResolveVersionIdentity(minecraftVersion, loader, loaderVersion, name);
        // 租约同时用于串行化同名安装，并让实例发现流程识别尚未完成的版本目录。
        await using var installLease = await installCoordinator
            .AcquireInstallAsync(settings.MinecraftDirectory, versionIdentity, cardProgress, cancellationToken)
            .ConfigureAwait(false);
        if (progress is not null)
            logger.LogDebug("Game instance install acquired coordinator lease. VersionIdentity={VersionIdentity}", versionIdentity);

        // 安装前记录目录是否已存在，失败时只能删除本次新建的目录，绝不能损坏用户已有版本。
        var instances = (await loadInstancesAsync(settings, cancellationToken).ConfigureAwait(false)).ToList();
        if (instances.Any(instance => IsSameVersionIdentity(instance, versionIdentity)))
            throw new DuplicateGameInstanceNameException(versionIdentity);

        var transactionService = installTransactionService
            ?? throw new InvalidOperationException("Instance install transaction service is required.");
        var instance = CreateInstance(settings, minecraftVersion, loader, loaderVersion, versionIdentity, string.Empty);
        await using var transaction = await transactionService.BeginAsync(
            settings.MinecraftDirectory,
            versionIdentity,
            "game",
            string.IsNullOrWhiteSpace(settings.DefaultInstanceDirectory),
            cardProgress,
            cancellationToken).ConfigureAwait(false);
        instance.VersionName = versionIdentity;
        instance.InstanceDirectory = transaction.PendingDirectory;
        repository.CreateInstanceDirectories(instance.InstanceDirectory);

        try
        {
            if (loader is LoaderKind.Fabric && installFabricApi && !string.IsNullOrWhiteSpace(fabricApiVersionId))
            {
                using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var gameTask = InstallGameAsync(preparation.Token);
                var apiTask = PrepareOptionalContentAsync(preparation.Token);
                var both = Task.WhenAll(gameTask, apiTask);
                try
                {
                    var first = await Task.WhenAny(gameTask, apiTask).ConfigureAwait(false);
                    await first.ConfigureAwait(false);
                    await both.ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        await preparation.CancelAsync().ConfigureAwait(false);
                    }
                    catch (AggregateException exception)
                    {
                        logger.LogWarning(exception, "A game installation cancellation callback failed.");
                    }
                    await both.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    throw;
                }
            }
            else
            {
                await InstallGameAsync(cancellationToken).ConfigureAwait(false);
                await PrepareOptionalContentAsync(cancellationToken).ConfigureAwait(false);
            }

            cardProgress?.ReportCommitStarted();
            await transaction.CommitAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Game instance creation failed. VersionIdentity={VersionIdentity} Loader={Loader}",
                versionIdentity,
                loader);
            throw;
        }

        instance.InstanceDirectory = transaction.FinalDirectory;
        var logicalCommitCompleted = true;
        try
        {
            var rootMatches = false;
            var defaultInitialized = false;
            var currentMinecraftDirectory = string.Empty;
            await settingsService.UpdateAsync(
                    latestSettings =>
                    {
                        currentMinecraftDirectory = latestSettings.MinecraftDirectory;
                        rootMatches = PathsEqual(latestSettings.MinecraftDirectory, settings.MinecraftDirectory);
                        if (!rootMatches || !string.IsNullOrWhiteSpace(latestSettings.DefaultInstanceDirectory))
                            return;
                        latestSettings.DefaultInstanceDirectory = instance.InstanceDirectory;
                        defaultInitialized = true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!rootMatches)
            {
                logicalCommitCompleted = false;
                logger.LogWarning(
                    "Instance install committed after the Minecraft directory changed; leaving recovery marker for the original directory. InstanceDirectory={InstanceDirectory} InstallMinecraftDirectory={InstallMinecraftDirectory} CurrentMinecraftDirectory={CurrentMinecraftDirectory}",
                    instance.InstanceDirectory,
                    settings.MinecraftDirectory,
                    currentMinecraftDirectory);
            }
            else if (defaultInitialized)
            {
                logger.LogDebug("Default game instance initialized. InstanceDirectory={InstanceDirectory}", instance.InstanceDirectory);
            }
        }
        catch (Exception exception)
        {
            logicalCommitCompleted = false;
            logger.LogError(exception, "Instance install committed but logical persistence needs reconciliation. InstanceDirectory={InstanceDirectory}", instance.InstanceDirectory);
        }
        if (logicalCommitCompleted)
            await transaction.CompleteLogicalCommitAsync(CancellationToken.None).ConfigureAwait(false);

        cardProgress?.ReportCommitCompleted();

        logger.LogDebug(
            "Game instance created. InstanceDirectory={InstanceDirectory} VersionName={VersionName}",
            instance.InstanceDirectory,
            instance.VersionName);
        return instance;

        async Task InstallGameAsync(CancellationToken token)
        {
            var versionName = await gameInstaller.InstallInstanceAsync(
                minecraftVersion,
                loader,
                loaderVersion,
                new LoaderInstallTarget(settings.MinecraftDirectory, versionIdentity, transaction.PendingDirectory),
                cardProgress,
                token,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond).ConfigureAwait(false);
            if (!string.Equals(versionName, versionIdentity, StringComparison.Ordinal))
                throw new InvalidDataException("Installed version name does not match the requested logical name.");

            logger.LogDebug("Game version installed. VersionIdentity={VersionIdentity} VersionName={VersionName} Loader={Loader}",
                versionIdentity, versionName, loader);
            cardProgress?.ReportBaseInstallCompleted();
        }

        async Task PrepareOptionalContentAsync(CancellationToken token)
        {
            await InstallOptionalContentAsync(instance, installFabricApi, fabricApiVersionId,
                quiltStandardLibraryVersionId, cardProgress, token).ConfigureAwait(false);
            cardProgress?.ReportOptionalContentCompleted();
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    private GameInstance CreateInstance(
        LauncherSettings settings,
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        string versionIdentity,
        string versionName)
    {
        var instanceDirectory = string.IsNullOrWhiteSpace(versionName)
            ? string.Empty
            : repository.GetVersionDirectory(settings.MinecraftDirectory, versionName);
        var now = DateTimeOffset.UtcNow;
        return new GameInstance
        {
            Name = versionIdentity,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loader == LoaderKind.Vanilla ? null : loaderVersion,
            VersionName = versionName,
            VersionType = string.Empty,
            InstanceDirectory = instanceDirectory,
            MemoryMb = settings.DefaultMemoryMb,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// 根据 Loader 选择安装 Fabric API 或 Quilt 标准库；未选择时保持纯 Loader 实例。
    /// </summary>
    private async Task InstallOptionalContentAsync(
        GameInstance instance,
        bool installFabricApi,
        string? fabricApiVersionId,
        string? quiltStandardLibraryVersionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (instance.Loader is LoaderKind.Fabric
            && installFabricApi
            && !string.IsNullOrWhiteSpace(fabricApiVersionId))
        {
            if (modrinthService is null)
                throw new InvalidOperationException("Modrinth service is required to install Fabric API.");

            logger.LogDebug(
                "Installing Fabric API for game instance. InstanceDirectory={InstanceDirectory} VersionId={VersionId}",
                instance.InstanceDirectory,
                fabricApiVersionId);
            await modrinthService.InstallFabricApiAsync(instance, fabricApiVersionId, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (instance.Loader is not LoaderKind.Quilt || string.IsNullOrWhiteSpace(quiltStandardLibraryVersionId))
            return;

        if (modrinthService is null)
            throw new InvalidOperationException("Modrinth service is required to install QFAPI/QSL.");

        logger.LogDebug(
            "Installing Quilt standard library for game instance. InstanceDirectory={InstanceDirectory} VersionId={VersionId}",
            instance.InstanceDirectory,
            quiltStandardLibraryVersionId);
        await modrinthService.InstallQuiltStandardLibraryAsync(
                instance,
                quiltStandardLibraryVersionId,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveVersionIdentity(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        string? requestedName)
    {
        return string.IsNullOrWhiteSpace(requestedName)
            ? VersionDirectoryName.Sanitize(CreateDefaultInstanceName(minecraftVersion, loader, loaderVersion))
            : VersionDirectoryName.NormalizeUserInput(requestedName);
    }

    private static string CreateDefaultInstanceName(string minecraftVersion, LoaderKind loader, string? loaderVersion)
    {
        return loader switch
        {
            LoaderKind.Fabric when !string.IsNullOrWhiteSpace(loaderVersion) => $"{minecraftVersion}-fabric-{loaderVersion}",
            LoaderKind.Fabric => $"{minecraftVersion}-fabric",
            LoaderKind.Forge when !string.IsNullOrWhiteSpace(loaderVersion) => $"{minecraftVersion}-forge-{loaderVersion}",
            LoaderKind.Forge => $"{minecraftVersion}-forge",
            LoaderKind.NeoForge when !string.IsNullOrWhiteSpace(loaderVersion) => $"{minecraftVersion}-neoforge-{loaderVersion}",
            LoaderKind.NeoForge => $"{minecraftVersion}-neoforge",
            LoaderKind.Quilt when !string.IsNullOrWhiteSpace(loaderVersion) => $"{minecraftVersion}-quilt-{loaderVersion}",
            LoaderKind.Quilt => $"{minecraftVersion}-quilt",
            _ => minecraftVersion
        };
    }

    private static bool IsSameVersionIdentity(GameInstance instance, string versionName)
    {
        return string.Equals(instance.VersionName, versionName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.Name, versionName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LoaderProviderGameInstaller(IReadOnlyDictionary<LoaderKind, ILoaderProvider> providers)
        : IModpackGameInstaller
    {
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
            return InstallInstanceAsync(
                minecraftVersion,
                loader,
                loaderVersion,
                target,
                progress,
                cancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond);
        }

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
            if (!providers.TryGetValue(loader, out var provider) || !provider.IsImplemented)
                throw new NotSupportedException($"{loader} is not implemented yet.");

            var derivedFinalDirectory = Path.GetFullPath(Path.Combine(
                target.MinecraftDirectory,
                "versions",
                target.LogicalVersionName));
            if (!string.Equals(
                    derivedFinalDirectory,
                    Path.GetFullPath(target.PhysicalOutputDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The infrastructure game installer is required for an explicit physical version output directory.");
            }

            return provider.InstallAsync(
                minecraftVersion,
                target.MinecraftDirectory,
                target.LogicalVersionName,
                loaderVersion,
                progress,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond);
        }
    }
}
