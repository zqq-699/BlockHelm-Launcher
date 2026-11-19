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
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Repositories;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Application.Services;

/// <summary>
/// 以磁盘中实际安装的版本为准同步实例清单，并协调实例元数据、默认实例与版本目录操作。
/// </summary>
public sealed partial class GameInstanceService : IGameInstanceService
{
    private readonly ISettingsService settingsService;
    private readonly IGameInstanceRepository repository;
    private readonly IGameInstallCoordinator installCoordinator;
    private readonly GameInstanceCreationCoordinator creationCoordinator;
    private readonly ILogger<GameInstanceService> logger;
    private readonly ConcurrentDictionary<string, string> pendingInstanceMutations = new(StringComparer.OrdinalIgnoreCase);

    public GameInstanceService(
        ISettingsService settingsService,
        IGameInstanceRepository repository,
        IEnumerable<ILoaderProvider> providers,
        IModrinthService? modrinthService = null,
        IModpackGameInstaller? modpackGameInstaller = null,
        ILogger<GameInstanceService>? logger = null,
        IGameInstallCoordinator? installCoordinator = null,
        IInstanceInstallTransactionService? installTransactionService = null)
    {
        this.settingsService = settingsService;
        this.repository = repository;
        this.installCoordinator = installCoordinator ?? new GameInstallCoordinator();
        this.logger = logger ?? NullLogger<GameInstanceService>.Instance;
        var providerMap = providers.ToDictionary(provider => provider.Kind);
        creationCoordinator = new GameInstanceCreationCoordinator(
            settingsService,
            repository,
            providerMap,
            modrinthService,
            modpackGameInstaller,
            installTransactionService,
            this.installCoordinator,
            GetInstancesCoreAsync,
            this.logger);
    }

    public bool CanReuseSettingsSnapshot(GameInstance previous, GameInstance current, bool allowOlderRead = false) =>
        repository.CanReuseSettingsSnapshot(previous, current, allowOlderRead);

    public async Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        await repository.RecoverPendingVersionRenamesAsync(settings.MinecraftDirectory, cancellationToken).ConfigureAwait(false);
        var instances = await GetInstancesCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Game instances refreshed. Count={InstanceCount} DefaultInstanceDirectory={DefaultInstanceDirectory}",
            instances.Count,
            settings.DefaultInstanceDirectory);
        return instances;
    }

    public async Task<IReadOnlyList<GameInstance>> GetStoredInstancesAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        await repository.RecoverPendingVersionRenamesAsync(settings.MinecraftDirectory, cancellationToken).ConfigureAwait(false);
        var instances = await repository.GetAllAsync(settings.MinecraftDirectory, cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Stored game instances loaded. Count={InstanceCount} DefaultInstanceDirectory={DefaultInstanceDirectory}",
            instances.Count,
            settings.DefaultInstanceDirectory);
        return instances;
    }

    /// <summary>
    /// 合并持久化实例与磁盘版本发现结果，并在必要时修正实例清单和默认实例。
    /// </summary>
    private async Task<IReadOnlyList<GameInstance>> GetInstancesCoreAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        var catalog = await repository.ReadCatalogAsync(settings.MinecraftDirectory, cancellationToken).ConfigureAwait(false);
        var storedInstances = catalog.Instances.ToList();
        if (catalog.DefaultInstanceDirectory is not null)
            settings.DefaultInstanceDirectory = catalog.DefaultInstanceDirectory;
        // 安装中的目录可能只包含半成品元数据，完成前不能被发现流程持久化为正式实例。
        var installedVersions = catalog.InstalledVersions
            .Where(version => !installCoordinator.IsInstallingVersion(settings.MinecraftDirectory, version.VersionName))
            .ToList();
        var syncedInstances = SynchronizeInstalledInstances(
            storedInstances,
            installedVersions,
            settings,
            out _).OrderBy(instance => instance.VersionName, StringComparer.OrdinalIgnoreCase).ToList();

        var defaultChanged = false;
        var previousDefaultInstanceDirectory = settings.DefaultInstanceDirectory;
        if (!string.IsNullOrWhiteSpace(settings.DefaultInstanceDirectory)
            && catalog.UnavailableDirectories?.Any(directory => MinecraftDirectoryPath.Equals(directory, settings.DefaultInstanceDirectory)) != true
            && syncedInstances.All(instance => !MinecraftDirectoryPath.Equals(instance.InstanceDirectory, settings.DefaultInstanceDirectory)))
        {
            settings.DefaultInstanceDirectory = syncedInstances.FirstOrDefault()?.InstanceDirectory ?? string.Empty;
            defaultChanged = true;
        }

        if (defaultChanged)
        {
            logger.LogDebug("Default game instance reset during synchronization. DefaultInstanceDirectory={DefaultInstanceDirectory}", settings.DefaultInstanceDirectory);
            var replacementDefaultInstanceDirectory = settings.DefaultInstanceDirectory;
            await settingsService.UpdateAsync(
                    latest =>
                    {
                        if (PathsEqual(latest.MinecraftDirectory, settings.MinecraftDirectory)
                            && string.Equals(
                                latest.DefaultInstanceDirectory,
                                previousDefaultInstanceDirectory,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            latest.DefaultInstanceDirectory = replacementDefaultInstanceDirectory;
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return syncedInstances;
    }

    public async Task<GameInstance?> GetDefaultInstanceAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var instances = await GetInstancesCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        return instances.FirstOrDefault(instance => MinecraftDirectoryPath.Equals(instance.InstanceDirectory, settings.DefaultInstanceDirectory))
            ?? instances.FirstOrDefault();
    }
}
