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

using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 管理实例 resourcepacks 目录的枚举、导入、删除和 pack.png 图标缓存。
/// </summary>
public sealed class LocalResourcePackService : ILocalResourcePackService
{
    // 资源包可以是 zip 或目录；删除和导入必须按实际类型选择文件系统操作。
    private const string SupportedArchiveExtension = ".zip";
    private readonly LauncherPathProvider pathProvider;
    private readonly ILogger<LocalResourcePackService> logger;
    private readonly IUserFileDeletionService userFileDeletionService;
    private readonly string iconCacheDirectory;
    private readonly SemaphoreSlim snapshotLock = new(1, 1);
    private readonly BoundedLocalContentSnapshotCache<LocalContentFileIdentity, LocalResourcePack> snapshots = new();
    private readonly VersionedLocalContentCache<ResourcePackCacheValue> metadataCache;

    public LocalResourcePackService(
        LauncherPathProvider? pathProvider = null,
        ILogger<LocalResourcePackService>? logger = null,
        IUserFileDeletionService? userFileDeletionService = null)
    {
        this.pathProvider = pathProvider ?? new LauncherPathProvider();
        this.logger = logger ?? NullLogger<LocalResourcePackService>.Instance;
        this.userFileDeletionService = userFileDeletionService ?? new UserFileDeletionService();
        iconCacheDirectory = Path.Combine(this.pathProvider.DefaultDataDirectory, "cache", "resourcepacks", "icons");
        metadataCache = new VersionedLocalContentCache<ResourcePackCacheValue>(
            Path.Combine(this.pathProvider.DefaultDataDirectory, "cache", "local-content", "resource-packs.json"),
            this.logger);
    }

    public async Task<IReadOnlyList<LocalResourcePack>> GetResourcePacksAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        // 目录扫描在线程池执行，单个损坏包的图标读取失败不影响其余列表。
        ArgumentNullException.ThrowIfNull(instance);

        await snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resourcePacksDirectory = GetResourcePacksDirectory(instance);
            var result = await Task.Run<IReadOnlyList<LocalResourcePack>>(
                () =>
                {
                    using var contentScope = InstanceContentOwnership.Open(instance, resourcePacksDirectory);
                    cancellationToken.ThrowIfCancellationRequested();
                    var inventory = Directory.Exists(resourcePacksDirectory)
                        ? Directory.EnumerateFiles(
                                resourcePacksDirectory,
                                $"*{SupportedArchiveExtension}",
                                SearchOption.TopDirectoryOnly)
                            .Select(CreateIdentity)
                            .OrderByDescending(item => item.CreationTimeUtcTicks)
                            .ThenBy(item => Path.GetFileNameWithoutExtension(item.FullPath), StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : [];
                    var currentPaths = inventory
                        .Select(item => item.FullPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    metadataCache.PruneDirectory(resourcePacksDirectory, currentPaths);
                    if (inventory.Length == 0)
                    {
                        snapshots.Remove(resourcePacksDirectory);
                        logger.LogDebug(
                            "Local resource pack inventory checked. InstanceDirectory={InstanceDirectory} Count=0",
                            instance.InstanceDirectory);
                        return [];
                    }

                    var snapshot = snapshots.GetOrCreate(resourcePacksDirectory, contentScope.CacheIdentity);
                    var resourcePacks = snapshot.Reconcile(
                        inventory,
                        static item => item.FullPath,
                        CreateResourcePack,
                        pack => InstanceContentOwnership.CanReuse(pack, pack.FullPath));
                    foreach (var pack in resourcePacks) contentScope.Bind(pack);
                    logger.LogDebug(
                        "Local resource pack inventory checked. InstanceDirectory={InstanceDirectory} Count={ResourcePackCount}",
                        instance.InstanceDirectory,
                        resourcePacks.Count);
                    return resourcePacks;
                },
                cancellationToken).ConfigureAwait(false);
            await metadataCache.SaveIfChangedAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    public Task<LocalResourcePackImportResult> ImportAsync(
        GameInstance instance,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        return Task.Run(
            () => ImportCore(instance, archivePath, cancellationToken),
            cancellationToken);
    }

    public Task DeleteAsync(LocalResourcePack resourcePack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourcePack);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var contentLease = InstanceContentOwnership.Acquire(resourcePack, resourcePack.FullPath);

                if (!File.Exists(resourcePack.FullPath))
                {
                    logger.LogDebug(
                        "Skipping local resource pack delete because file does not exist. Path={Path}",
                        resourcePack.FullPath);
                    return;
                }

                userFileDeletionService.DeleteFile(resourcePack.FullPath);
                logger.LogInformation("Local resource pack deleted. Name={Name}", resourcePack.Name);
                logger.LogDebug("Deleted local resource pack path. Path={Path}", resourcePack.FullPath);
            },
            cancellationToken);
    }

    public Task DeleteAsync(IEnumerable<LocalResourcePack> resourcePacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourcePacks);

        return Task.Run(
            async () =>
            {
                foreach (var resourcePack in resourcePacks.DistinctBy(resourcePack => resourcePack.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DeleteAsync(resourcePack, cancellationToken);
                }
            },
            cancellationToken);
    }

    private LocalResourcePackImportResult ImportCore(
        GameInstance instance,
        string archivePath,
        CancellationToken cancellationToken)
    {
        // 同名目标使用唯一名称而非覆盖，避免导入操作破坏用户已有资源包。
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(normalizedArchivePath))
        {
            logger.LogDebug(
                "Skipping local resource pack import because archive does not exist. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                instance.InstanceDirectory,
                normalizedArchivePath);
            return LocalResourcePackImportResult.Failure(LocalResourcePackImportFailureReason.FileNotFound);
        }

        if (!normalizedArchivePath.EndsWith(SupportedArchiveExtension, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Skipping local resource pack import because archive type is unsupported. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                instance.InstanceDirectory,
                normalizedArchivePath);
            return LocalResourcePackImportResult.Failure(LocalResourcePackImportFailureReason.UnsupportedArchive);
        }

        logger.LogDebug(
            "Importing local resource pack archive. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
            instance.InstanceDirectory,
            normalizedArchivePath);

        try
        {
            var resourcePacksDirectory = GetResourcePacksDirectory(instance);
            using var contentScope = InstanceContentOwnership.Open(instance, resourcePacksDirectory, createDirectory: true);

            var targetPath = ResolveUniqueFilePath(resourcePacksDirectory, Path.GetFileName(normalizedArchivePath));
            File.Copy(normalizedArchivePath, targetPath, overwrite: false);

            var importedIdentity = CreateIdentity(targetPath);
            TryResolveCachedIconSource(new FileInfo(importedIdentity.FullPath), out var importedIconSource);
            var importedResourcePack = new LocalResourcePack
            {
                Name = Path.GetFileNameWithoutExtension(importedIdentity.FullPath),
                FileName = Path.GetFileName(importedIdentity.FullPath),
                FullPath = importedIdentity.FullPath,
                IconSource = importedIconSource,
                CreatedAt = new DateTimeOffset(importedIdentity.CreationTimeUtcTicks, TimeSpan.Zero)
            };
            logger.LogDebug(
                "Local resource pack archive imported. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath} ResourcePackPath={ResourcePackPath}",
                instance.InstanceDirectory,
                normalizedArchivePath,
                targetPath);
            return LocalResourcePackImportResult.Success(contentScope.Bind(importedResourcePack));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                exception,
                "Failed to import local resource pack archive because a file operation failed. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                instance.InstanceDirectory,
                normalizedArchivePath);
            return LocalResourcePackImportResult.Failure(LocalResourcePackImportFailureReason.UnexpectedError);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(
                exception,
                "Failed to import local resource pack archive because access was denied. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                instance.InstanceDirectory,
                normalizedArchivePath);
            return LocalResourcePackImportResult.Failure(LocalResourcePackImportFailureReason.UnexpectedError);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected failure while importing local resource pack archive. InstanceDirectory={InstanceDirectory} ArchivePath={ArchivePath}",
                instance.InstanceDirectory,
                normalizedArchivePath);
            return LocalResourcePackImportResult.Failure(LocalResourcePackImportFailureReason.UnexpectedError);
        }
    }

    private static LocalContentFileIdentity CreateIdentity(string path)
    {
        var file = new FileInfo(path);
        return new LocalContentFileIdentity(
            file.FullName,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            file.CreationTimeUtc.Ticks);
    }

    private LocalResourcePack CreateResourcePack(LocalContentFileIdentity identity)
    {
        if (!metadataCache.TryGet(identity, IsValidCacheValue, out var cacheValue))
        {
            var resolved = TryResolveCachedIconSource(new FileInfo(identity.FullPath), out var iconSource);
            cacheValue = new ResourcePackCacheValue(iconSource is not null, iconSource);
            if (resolved)
                metadataCache.Set(identity, cacheValue);
        }

        return new LocalResourcePack
        {
            Name = Path.GetFileNameWithoutExtension(identity.FullPath),
            FileName = Path.GetFileName(identity.FullPath),
            FullPath = identity.FullPath,
            IconSource = cacheValue.IconSource,
            CreatedAt = new DateTimeOffset(identity.CreationTimeUtcTicks, TimeSpan.Zero)
        };
    }

    private static bool IsValidCacheValue(ResourcePackCacheValue value)
    {
        if (!value.HasIcon)
            return true;
        if (string.IsNullOrWhiteSpace(value.IconSource)
            || !Uri.TryCreate(value.IconSource, UriKind.Absolute, out var uri)
            || !uri.IsFile)
        {
            return false;
        }

        return File.Exists(uri.LocalPath);
    }

    private bool TryResolveCachedIconSource(FileInfo archiveFile, out string? iconSource)
    {
        iconSource = null;
        // 缓存键包含归档修改信息和图标条目，包更新后自然生成新缓存而不会复用旧图。
        try
        {
            using var archive = ZipFile.OpenRead(archiveFile.FullName);
            var iconEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName.Replace('\\', '/'), "pack.png", StringComparison.OrdinalIgnoreCase));
            if (iconEntry is null)
                return true;

            Directory.CreateDirectory(iconCacheDirectory);
            var cachePath = GetCachePath(archiveFile, iconEntry.FullName);
            if (File.Exists(cachePath))
            {
                iconSource = new Uri(cachePath).AbsoluteUri;
                return true;
            }

            using var iconStream = iconEntry.Open();
            var bitmap = LoadBitmap(iconStream);
            try
            {
                SavePng(bitmap, cachePath);
            }
            catch (IOException) when (File.Exists(cachePath))
            {
            }

            iconSource = new Uri(cachePath).AbsoluteUri;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Failed to cache local resource pack icon. ResourcePackPath={ResourcePackPath}",
                archiveFile.FullName);
            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unexpected failure while caching local resource pack icon. ResourcePackPath={ResourcePackPath}",
                archiveFile.FullName);
            return false;
        }
    }

    private static BitmapSource LoadBitmap(Stream source)
    {
        // OnLoad 将像素完全读入内存，关闭 zip 后 UI 仍可安全使用缓存图像。
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;

        var decoder = BitmapDecoder.Create(
            buffer,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("Embedded resource pack icon contains no frames.");
        frame.Freeze();
        return frame;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private string GetCachePath(FileInfo archiveFile, string iconEntryName)
    {
        var hashInput = $"{archiveFile.FullName}|{archiveFile.Length}|{archiveFile.LastWriteTimeUtc.Ticks}|{iconEntryName}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(iconCacheDirectory, $"{hash}.png");
    }

    private static string ResolveUniqueFilePath(string directory, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var index = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName} ({index}){extension}");
            index++;
        }

        return candidate;
    }

    private static string GetResourcePacksDirectory(GameInstance instance)
    {
        return Path.Combine(instance.InstanceDirectory, "resourcepacks");
    }

    private sealed record ResourcePackCacheValue(bool HasIcon, string? IconSource);
}
