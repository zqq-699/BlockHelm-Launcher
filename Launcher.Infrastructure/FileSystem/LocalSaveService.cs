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
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

public sealed class LocalSaveService : ILocalSaveService
{
    private readonly ILogger<LocalSaveService> logger;
    private readonly IUserFileDeletionService userFileDeletionService;
    private readonly LocalSaveArchiveImporter archiveImporter;
    private readonly string iconCacheDirectory;
    private readonly SemaphoreSlim snapshotLock = new(1, 1);
    private readonly BoundedLocalContentSnapshotCache<LocalSaveIdentity, LocalSave> snapshots = new();

    public LocalSaveService(
        LauncherPathProvider? pathProvider = null,
        ILogger<LocalSaveService>? logger = null,
        IUserFileDeletionService? userFileDeletionService = null)
    {
        var effectivePathProvider = pathProvider ?? new LauncherPathProvider();
        this.logger = logger ?? NullLogger<LocalSaveService>.Instance;
        this.userFileDeletionService = userFileDeletionService ?? new UserFileDeletionService();
        archiveImporter = new LocalSaveArchiveImporter(this.logger);
        iconCacheDirectory = Path.Combine(effectivePathProvider.DefaultDataDirectory, "cache", "saves", "icons");
    }

    public async Task<IReadOnlyList<LocalSave>> GetSavesAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<LocalSave>>(
                    () => LoadSaves(instance, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    public Task<LocalSaveImportResult> ImportFromArchiveAsync(
        GameInstance instance,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return Task.Run(
            () =>
            {
                using var instanceScope = InstanceContentOwnership.Open(instance, instance.InstanceDirectory);
                return archiveImporter.Import(
                instance.InstanceDirectory,
                GetSavesDirectory(instance),
                archivePath,
                path =>
                {
                    using var scope = InstanceContentOwnership.Open(instance, GetSavesDirectory(instance));
                    return scope.Bind(ToLocalSave(path));
                },
                cancellationToken,
                instanceScope);
            },
            cancellationToken);
    }

    public Task DeleteAsync(LocalSave save, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        return Task.Run(
            () => DeleteCore(save, cancellationToken),
            cancellationToken);
    }

    public Task DeleteAsync(IEnumerable<LocalSave> saves, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saves);
        return DeleteManyAsync(saves, cancellationToken);
    }

    private IReadOnlyList<LocalSave> LoadSaves(GameInstance instance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var savesDirectory = GetSavesDirectory(instance);
        using var contentScope = InstanceContentOwnership.Open(instance, savesDirectory);
        if (!Directory.Exists(savesDirectory))
        {
            snapshots.Remove(savesDirectory);
            logger.LogDebug(
                "No local saves directory found. InstanceDirectory={InstanceDirectory} SavesDirectory={SavesDirectory}",
                instance.InstanceDirectory,
                savesDirectory);
            return [];
        }

        var inventory = Directory.EnumerateDirectories(savesDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(CreateIdentity)
            .OrderByDescending(item => item.CreationTimeUtcTicks)
            .ThenBy(item => Path.GetFileName(item.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inventory.Length == 0)
        {
            snapshots.Remove(savesDirectory);
            logger.LogDebug("Local save inventory checked. InstanceDirectory={InstanceDirectory} Count=0", instance.InstanceDirectory);
            return [];
        }

        var snapshot = snapshots.GetOrCreate(savesDirectory, contentScope.CacheIdentity);
        var saves = snapshot.Reconcile(inventory, static item => item.FullPath, ToLocalSave,
            save => InstanceContentOwnership.CanReuse(save, save.FullPath));
        foreach (var save in saves) contentScope.Bind(save);
        logger.LogDebug("Local save inventory checked. InstanceDirectory={InstanceDirectory} Count={SaveCount}", instance.InstanceDirectory, saves.Count);
        return saves;
    }

    private void DeleteCore(LocalSave save, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var contentLease = InstanceContentOwnership.Acquire(save, save.FullPath);
        if (!Directory.Exists(save.FullPath))
        {
            logger.LogDebug("Skipping local save delete because directory does not exist. Path={Path}", save.FullPath);
            return;
        }

        userFileDeletionService.DeleteDirectory(save.FullPath);
        logger.LogInformation("Local save deleted. Name={Name}", save.Name);
        logger.LogDebug("Deleted local save path. Path={Path}", save.FullPath);
    }

    private async Task DeleteManyAsync(IEnumerable<LocalSave> saves, CancellationToken cancellationToken)
    {
        foreach (var save in saves.DistinctBy(save => save.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(save, cancellationToken).ConfigureAwait(false);
        }
    }

    private LocalSave ToLocalSave(string path) => ToLocalSave(CreateIdentity(path));

    private static LocalSaveIdentity CreateIdentity(string path)
    {
        var directory = new DirectoryInfo(path);
        var iconPath = Path.Combine(directory.FullName, "icon.png");
        if (!File.Exists(iconPath))
        {
            return new LocalSaveIdentity(
                directory.FullName,
                directory.CreationTimeUtc.Ticks,
                false,
                0,
                0);
        }

        var icon = new FileInfo(iconPath);
        return new LocalSaveIdentity(
            directory.FullName,
            directory.CreationTimeUtc.Ticks,
            true,
            icon.Length,
            icon.LastWriteTimeUtc.Ticks);
    }

    private LocalSave ToLocalSave(LocalSaveIdentity identity)
    {
        var name = Path.GetFileName(identity.FullPath);
        return new LocalSave
        {
            Name = name,
            DirectoryName = name,
            FullPath = identity.FullPath,
            IconSource = identity.HasIcon ? TryGetCachedIconSource(new DirectoryInfo(identity.FullPath)) : null,
            CreatedAt = new DateTimeOffset(identity.CreationTimeUtcTicks, TimeSpan.Zero)
        };
    }

    private string? TryGetCachedIconSource(DirectoryInfo directory)
    {
        var iconPath = Path.Combine(directory.FullName, "icon.png");
        if (!File.Exists(iconPath))
            return null;

        try
        {
            Directory.CreateDirectory(iconCacheDirectory);
            var iconFile = new FileInfo(iconPath);
            var cachePath = GetCachePath(iconFile);
            if (!File.Exists(cachePath))
                File.Copy(iconFile.FullName, cachePath, overwrite: false);

            return new Uri(cachePath).AbsoluteUri;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "Failed to cache local save icon. SaveDirectory={SaveDirectory} IconPath={IconPath}",
                directory.FullName,
                iconPath);
            return null;
        }
    }

    private string GetCachePath(FileInfo iconFile)
    {
        var hashInput = $"{iconFile.FullName}|{iconFile.Length}|{iconFile.LastWriteTimeUtc.Ticks}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(iconCacheDirectory, $"{hash}.png");
    }

    private static string GetSavesDirectory(GameInstance instance)
    {
        return Path.Combine(instance.InstanceDirectory, "saves");
    }
}
