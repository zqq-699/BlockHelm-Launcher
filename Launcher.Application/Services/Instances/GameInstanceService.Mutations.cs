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
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Repositories;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Application.Services;

public sealed partial class GameInstanceService
{

public async Task<GameInstance> CreateInstanceAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        string? name,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        bool installFabricApi = true,
        string? fabricApiVersionId = null,
        string? quiltStandardLibraryVersionId = null)
    {
        return await creationCoordinator.CreateAsync(
                minecraftVersion,
                loader,
                loaderVersion,
                name,
                progress,
                cancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                installFabricApi,
                fabricApiVersionId,
                quiltStandardLibraryVersionId)
            .ConfigureAwait(false);
    }

    public async Task SaveInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        instance.UpdatedAt = DateTimeOffset.UtcNow;

        await repository.UpdateInstanceAsync(settings.MinecraftDirectory, instance, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("Game instance saved. InstanceDirectory={InstanceDirectory} VersionName={VersionName}", instance.InstanceDirectory, instance.VersionName);
    }

    /// <summary>
    /// 校验实例新名称，事务性重命名版本目录，再提交实例元数据和图标变化。
    /// </summary>
    public Task<GameInstance> RenameInstanceAsync(
        string instanceDirectory,
        string? newName,
        string? newIconSource,
        CancellationToken cancellationToken = default)
        => RenameInstanceCoreAsync(instanceDirectory, newName, newIconSource, null, cancellationToken);

    public Task<GameInstance> RenameInstanceAsync(GameInstance instance, string? newName, string? newIconSource, CancellationToken cancellationToken = default)
        => RenameInstanceCoreAsync(instance.InstanceDirectory, newName, newIconSource, instance, cancellationToken);

    private async Task<GameInstance> RenameInstanceCoreAsync(string instanceDirectory, string? newName, string? newIconSource,
        GameInstance? expectedInstance, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            throw new InvalidOperationException("Instance directory is required.");

        var mutationKey = MinecraftDirectoryPath.Normalize(instanceDirectory);
        if (!pendingInstanceMutations.TryAdd(mutationKey, "rename"))
            throw new InvalidOperationException("Instance is already being modified.");

        try
        {
        var instances = (await GetInstancesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var currentInstance = instances.FirstOrDefault(existing =>
            string.Equals(existing.InstanceDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Instance was not found.");
        var instance = expectedInstance ?? currentInstance;

        var sanitizedName = NormalizeUserInstanceName(newName);
        var normalizedIconSource = string.IsNullOrWhiteSpace(newIconSource) ? null : newIconSource.Trim();
        var persistedIconSource = normalizedIconSource;
        var currentVersionName = GetVersionName(instance);

        if (instances.Any(existing =>
                !string.Equals(existing.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(existing.Name, sanitizedName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existing.VersionName, sanitizedName, StringComparison.OrdinalIgnoreCase))))
        {
            throw new DuplicateGameInstanceNameException(sanitizedName);
        }

        var nameChanged = !string.Equals(currentVersionName, sanitizedName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(instance.Name, sanitizedName, StringComparison.Ordinal);
        var iconChanged = !string.Equals(instance.IconSource, normalizedIconSource, StringComparison.Ordinal);

        if (!nameChanged && !iconChanged)
            return instance;

        var directoryRenamed = !string.Equals(currentVersionName, sanitizedName, StringComparison.Ordinal);
        var updatedAt = DateTimeOffset.UtcNow;
        if (directoryRenamed)
        {
            // 先完成目录及版本 JSON 的事务性重命名，再更新内存模型，失败时不会保存失配路径。
            var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sourceDirectory = repository.GetVersionDirectory(settings.MinecraftDirectory, currentVersionName);
            var destinationDirectory = repository.GetVersionDirectory(settings.MinecraftDirectory, sanitizedName);
            persistedIconSource = RebaseManagedResourceProjectIcon(
                instance.IconSource,
                normalizedIconSource,
                sourceDirectory,
                destinationDirectory);
            var originalDirectory = instance.InstanceDirectory;
            try
            {
                await repository.RenameVersionAsync(
                    settings.MinecraftDirectory,
                    instance,
                    sanitizedName,
                    persistedIconSource,
                    updatedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InstanceInstallNameConflictException)
            {
                throw new DuplicateGameInstanceNameException(sanitizedName);
            }

            instance.VersionName = sanitizedName;
            instance.InstanceDirectory = repository.GetVersionDirectory(settings.MinecraftDirectory, sanitizedName);
            await settingsService.UpdateAsync(latest =>
            {
                if (PathsEqual(latest.MinecraftDirectory, settings.MinecraftDirectory)
                    && MinecraftDirectoryPath.Equals(latest.DefaultInstanceDirectory, originalDirectory))
                    latest.DefaultInstanceDirectory = instance.InstanceDirectory;
            }, cancellationToken).ConfigureAwait(false);
        }

        instance.Name = sanitizedName;
        instance.IconSource = persistedIconSource;
        instance.UpdatedAt = updatedAt;

        var index = instances.FindIndex(existing =>
            string.Equals(existing.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            instances[index] = instance;

        if (!directoryRenamed)
        {
            var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            await repository.UpdateInstanceAsync(settings.MinecraftDirectory, instance, cancellationToken)
                .ConfigureAwait(false);
        }
        logger.LogInformation(
            "Game instance renamed. InstanceDirectory={InstanceDirectory} VersionName={VersionName} IconChanged={IconChanged}",
            instance.InstanceDirectory,
            instance.VersionName,
            iconChanged);
        return instance;
        }
        finally
        {
            pendingInstanceMutations.TryRemove(mutationKey, out _);
        }
    }

    private static string? RebaseManagedResourceProjectIcon(
        string? currentIconSource,
        string? requestedIconSource,
        string sourceDirectory,
        string destinationDirectory)
    {
        if (!string.Equals(currentIconSource, requestedIconSource, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(requestedIconSource)
            || !Uri.TryCreate(requestedIconSource, UriKind.Absolute, out var iconUri)
            || !iconUri.IsFile)
        {
            return requestedIconSource;
        }

        return InstanceIconPaths.Resolve(requestedIconSource, sourceDirectory, destinationDirectory);
    }

    public async Task<bool> SetDefaultInstanceAsync(string instanceDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return false;

        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (IsInstallingVersion(settings.MinecraftDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(instanceDirectory))))
            return false;
        if (!await repository.IsInstalledInstanceDirectoryAsync(settings.MinecraftDirectory, instanceDirectory, cancellationToken)
                .ConfigureAwait(false))
            return false;

        instanceDirectory = MinecraftDirectoryPath.Normalize(instanceDirectory);

        if (string.Equals(settings.DefaultInstanceDirectory, instanceDirectory, StringComparison.Ordinal))
            return true;

        var persisted = false;
        await settingsService.UpdateAsync(
                latest =>
                {
                    if (!PathsEqual(latest.MinecraftDirectory, settings.MinecraftDirectory))
                        return;
                    latest.DefaultInstanceDirectory = instanceDirectory;
                    persisted = true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!persisted)
            return false;
        settings.DefaultInstanceDirectory = instanceDirectory;
        logger.LogInformation("Default game instance changed. InstanceDirectory={InstanceDirectory}", instanceDirectory);
        return true;
    }

    /// <summary>
    /// 删除实例目录和持久化记录；若删除默认实例，同时选择新的默认项。
    /// </summary>
    public Task<bool> DeleteInstanceAsync(string instanceDirectory, CancellationToken cancellationToken = default)
        => DeleteInstanceCoreAsync(instanceDirectory, null, cancellationToken);

    public Task<bool> DeleteInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default)
        => DeleteInstanceCoreAsync(instance.InstanceDirectory, instance, cancellationToken);

    private async Task<bool> DeleteInstanceCoreAsync(string instanceDirectory, GameInstance? expectedInstance, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return false;

        var deletionKey = MinecraftDirectoryPath.Normalize(instanceDirectory);
        if (!pendingInstanceMutations.TryAdd(deletionKey, "delete"))
        {
            logger.LogWarning("Duplicate game instance deletion ignored. InstanceDirectory={InstanceDirectory}", instanceDirectory);
            return false;
        }

        try
        {
            var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var instances = (await GetInstancesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var instance = instances.FirstOrDefault(existing =>
                string.Equals(existing.InstanceDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase));

            if (instance is null)
                return false;
            instance = expectedInstance ?? instance;

            var versionName = GetVersionName(instance);
            if (string.IsNullOrWhiteSpace(versionName))
                return false;

            string stagedDirectory;
            try
            {
                stagedDirectory = expectedInstance is not null
                    ? await repository.StageVersionForDeletionAsync(settings.MinecraftDirectory, expectedInstance, cancellationToken).ConfigureAwait(false)
                    : await repository.StageVersionForDeletionAsync(
                        settings.MinecraftDirectory,
                        versionName,
                        instance.InstanceDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GameInstanceMutationConflictException exception)
            {
                logger.LogWarning(
                    exception,
                    "Stale game instance deletion was rejected. InstanceDirectory={InstanceDirectory} VersionName={VersionName}",
                    instance.InstanceDirectory,
                    versionName);
                return false;
            }

            // Directory.Move is the irreversible commit point. From here on the instance must never be restored.
            instances.RemoveAll(candidate => MinecraftDirectoryPath.Equals(candidate.InstanceDirectory, instance.InstanceDirectory));
            await PersistDefaultAfterDeletionBestEffortAsync(settings, instances, instance).ConfigureAwait(false);

            var physicalCleanupCompleted = await repository
                .TryDeleteStagedVersionDirectoryAsync(
                    settings.MinecraftDirectory,
                    stagedDirectory,
                    CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Game instance deletion committed. InstanceDirectory={InstanceDirectory} VersionName={VersionName} PhysicalCleanupCompleted={PhysicalCleanupCompleted}",
                instance.InstanceDirectory,
                versionName,
                physicalCleanupCompleted);
            return true;
        }
        finally
        {
            pendingInstanceMutations.TryRemove(deletionKey, out _);
        }
    }

    private async Task PersistDefaultAfterDeletionBestEffortAsync(
        LauncherSettings settings,
        IReadOnlyCollection<GameInstance> remainingInstances,
        GameInstance deletedInstance)
    {
        if (!string.Equals(settings.DefaultInstanceDirectory, deletedInstance.InstanceDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        var replacementDefaultInstanceDirectory = remainingInstances.FirstOrDefault()?.InstanceDirectory ?? string.Empty;
        try
        {
            var persisted = false;
            await settingsService.UpdateAsync(
                    latest =>
                    {
                        if (!PathsEqual(latest.MinecraftDirectory, settings.MinecraftDirectory)
                            || !string.Equals(
                                latest.DefaultInstanceDirectory,
                                deletedInstance.InstanceDirectory,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                        latest.DefaultInstanceDirectory = replacementDefaultInstanceDirectory;
                        persisted = true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!persisted)
                return;
            settings.DefaultInstanceDirectory = replacementDefaultInstanceDirectory;
            logger.LogInformation(
                "Default game instance changed after deletion. DefaultInstanceDirectory={DefaultInstanceDirectory}",
                settings.DefaultInstanceDirectory);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to persist default game instance after deletion commit. DeletedInstanceDirectory={DeletedInstanceDirectory} DefaultInstanceDirectory={DefaultInstanceDirectory}",
                deletedInstance.InstanceDirectory,
                settings.DefaultInstanceDirectory);
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameVersionIdentity(GameInstance instance, string versionName)
    {
        return string.Equals(instance.Name, versionName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.VersionName, versionName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 保留仍存在或正在安装的已存实例，补充新发现版本，并移除失效或重复记录。
    /// </summary>
}
