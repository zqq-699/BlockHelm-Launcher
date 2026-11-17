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
using Launcher.Application.Repositories;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Modpacks;

internal sealed class ModpackInstanceStagingService : IModpackInstanceStagingService
{
    private readonly ISettingsService settingsService;
    private readonly IGameInstanceRepository repository;
    private readonly IGameInstanceService instanceService;
    private readonly IInstanceInstallTransactionService installTransactionService;
    private readonly ILogger logger;
    private readonly SemaphoreSlim stagingLock = new(1, 1);

    public ModpackInstanceStagingService(
        ISettingsService settingsService,
        IGameInstanceRepository repository,
        IGameInstanceService instanceService,
        IInstanceInstallTransactionService installTransactionService,
        ILogger<ModpackInstanceStagingService>? logger = null)
    {
        this.settingsService = settingsService;
        this.repository = repository;
        this.instanceService = instanceService;
        this.installTransactionService = installTransactionService;
        this.logger = logger ?? NullLogger<ModpackInstanceStagingService>.Instance;
    }

    public async Task<StagedModpackInstance> StageAsync(
        PreparedModpack preparedModpack,
        string preferredInstanceName,
        CancellationToken cancellationToken = default)
    {
        await stagingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var rejectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolvedInstanceName = await ResolveUniqueInstanceNameAsync(
                    preferredInstanceName,
                    settings.MinecraftDirectory,
                    rejectedNames,
                    cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var instance = new GameInstance
                {
                    Name = resolvedInstanceName,
                    MinecraftVersion = preparedModpack.MinecraftVersion,
                    Loader = preparedModpack.Loader,
                    LoaderVersion = preparedModpack.Loader == LoaderKind.Vanilla ? null : preparedModpack.LoaderVersion,
                    VersionName = resolvedInstanceName,
                    VersionType = string.Empty,
                    MemoryMb = settings.DefaultMemoryMb,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                try
                {
                    var transaction = await installTransactionService.BeginAsync(
                        settings.MinecraftDirectory,
                        resolvedInstanceName,
                        "modpack",
                        string.IsNullOrWhiteSpace(settings.DefaultInstanceDirectory),
                        progress: null,
                        cancellationToken).ConfigureAwait(false);
                    instance.InstanceDirectory = transaction.PendingDirectory;
                    repository.CreateInstanceDirectories(instance.InstanceDirectory);
                    return new StagedModpackInstance
                    {
                        ResolvedInstanceName = resolvedInstanceName,
                        MinecraftDirectory = settings.MinecraftDirectory,
                        InstanceDirectory = transaction.PendingDirectory,
                        Instance = instance,
                        InstallTransaction = transaction
                    };
                }
                catch (InstanceInstallNameConflictException exception)
                {
                    rejectedNames.Add(resolvedInstanceName);
                    rejectedNames.Add(exception.LogicalVersionName);
                    logger.LogInformation(
                        "Modpack instance name became unavailable while staging; selecting a suffixed name. InstanceName={InstanceName}",
                        resolvedInstanceName);
                }
            }
        }
        finally
        {
            stagingLock.Release();
        }
    }

    public async Task<GameInstance> FinalizeAsync(
        StagedModpackInstance stagedInstance,
        string finalVersionName,
        CancellationToken cancellationToken = default)
    {
        var instance = stagedInstance.Instance;
        var transaction = stagedInstance.InstallTransaction
            ?? throw SafeExceptionLog.Describe(new InvalidOperationException("Modpack install transaction is missing."), "DownloadValidation", ("Reason", "Modpack install transaction is missing."));
        instance.VersionName = stagedInstance.ResolvedInstanceName;
        instance.InstanceDirectory = stagedInstance.InstanceDirectory;
        instance.UpdatedAt = DateTimeOffset.UtcNow;
        repository.CreateInstanceDirectories(instance.InstanceDirectory);

        await transaction.CommitAsync(instance, cancellationToken).ConfigureAwait(false);
        instance.InstanceDirectory = transaction.FinalDirectory;
        var logicalCommitCompleted = true;
        try
        {
            var rootMatches = false;
            var currentMinecraftDirectory = string.Empty;
            await settingsService.UpdateAsync(
                    latestSettings =>
                    {
                        currentMinecraftDirectory = latestSettings.MinecraftDirectory;
                        rootMatches = PathsEqual(
                            latestSettings.MinecraftDirectory,
                            stagedInstance.MinecraftDirectory);
                        if (rootMatches && string.IsNullOrWhiteSpace(latestSettings.DefaultInstanceDirectory))
                            latestSettings.DefaultInstanceDirectory = instance.InstanceDirectory;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!rootMatches)
            {
                logicalCommitCompleted = false;
                logger.LogWarning(
                    "Modpack installation committed after the Minecraft directory changed; leaving recovery marker for the original directory. InstanceDirectory={InstanceDirectory} InstallMinecraftDirectory={InstallMinecraftDirectory} CurrentMinecraftDirectory={CurrentMinecraftDirectory}",
                    instance.InstanceDirectory,
                    stagedInstance.MinecraftDirectory,
                    currentMinecraftDirectory);
            }
        }
        catch (Exception exception)
        {
            logicalCommitCompleted = false;
            logger.LogError(exception, "Modpack installation committed but logical persistence needs reconciliation. InstanceDirectory={InstanceDirectory}", instance.InstanceDirectory);
        }
        if (logicalCommitCompleted)
            await transaction.CompleteLogicalCommitAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        return instance;
    }

    private static bool PathsEqual(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    public async Task CleanupFailedImportAsync(
        StagedModpackInstance stagedInstance,
        string? finalVersionName,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (stagedInstance.InstallTransaction is not null)
        {
            await stagedInstance.InstallTransaction.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            await stagedInstance.InstallTransaction.DisposeAsync().ConfigureAwait(false);
            return;
        }

        logger.LogWarning(
            "Skipped non-transactional modpack cleanup because directory ownership cannot be proven. InstanceDirectory={InstanceDirectory} ResolvedInstanceName={ResolvedInstanceName} FinalVersionName={FinalVersionName} MinecraftDirectory={MinecraftDirectory}",
            stagedInstance.Instance.InstanceDirectory,
            stagedInstance.ResolvedInstanceName,
            finalVersionName,
            settings.MinecraftDirectory);
    }

    private async Task<string> ResolveUniqueInstanceNameAsync(
        string preferredInstanceName,
        string minecraftDirectory,
        IReadOnlySet<string> rejectedNames,
        CancellationToken cancellationToken)
    {
        var baseName = preferredInstanceName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseName))
            throw new ModpackImportException(ModpackImportFailureReason.InvalidManifest, "Prepared modpack package name is missing.");

        var unavailableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = await instanceService.GetInstancesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var instance in instances)
        {
            AddUnavailableName(instance.Name);
            AddUnavailableName(instance.VersionName);
        }

        foreach (var rejectedName in rejectedNames)
            AddUnavailableName(rejectedName);

        AddExistingVersionEntryNames(minecraftDirectory, unavailableNames);

        if (!unavailableNames.Contains(baseName))
            return baseName;

        var suffix = 1;
        while (true)
        {
            var candidate = $"{baseName} ({suffix})";
            if (!unavailableNames.Contains(candidate))
                return candidate;

            suffix++;
        }

        void AddUnavailableName(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                unavailableNames.Add(name);
        }
    }

    private static void AddExistingVersionEntryNames(
        string minecraftDirectory,
        HashSet<string> unavailableNames)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
            return;

        foreach (var versionEntry in Directory.EnumerateFileSystemEntries(versionsDirectory))
        {
            if (Directory.Exists(versionEntry)
                && PendingInstanceInstallDirectory.IsPending(versionEntry)
                && PendingInstanceInstallDirectory.TryGetLogicalName(versionEntry, out var logicalVersionName))
            {
                unavailableNames.Add(logicalVersionName);
                continue;
            }
            var versionName = Path.GetFileName(versionEntry);
            if (!string.IsNullOrWhiteSpace(versionName))
                unavailableNames.Add(versionName);
        }
    }
}
