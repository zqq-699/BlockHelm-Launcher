/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Resources;

public sealed class ResourceThumbnailCacheService
{
    private const long MaximumThumbnailBytes = 1024L * 1024L;
    private const long MaximumCacheBytes = 100L * 1024L * 1024L;
    private const long TargetCacheBytes = 80L * 1024L * 1024L;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> entryLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim commitLock = new(1, 1);
    private readonly string cacheDirectory;
    private readonly RemoteThumbnailDownloadClient downloader;
    private readonly ILogger logger;
    private long cacheSizeBytes = -1;
    private int compactionStarted;

    public ResourceThumbnailCacheService(
        LauncherPathProvider pathProvider,
        HttpClient httpClient,
        IImportConcurrencyLimiter? limiter,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger logger)
    {
        cacheDirectory = Path.Combine(pathProvider.DefaultDataDirectory, "cache", "resources", "thumbnails");
        downloader = new RemoteThumbnailDownloadClient(httpClient, limiter, downloadSpeedLimitState, logger);
        this.logger = logger;
    }

    public string? TryGetCachedThumbnailSource(ResourceProject project)
    {
        StartCacheCompaction();
        try
        {
            return TryGetCacheEntry(project) is { } entry ? TryGetCachedSource(entry) : null;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "Failed to inspect resource project thumbnail cache. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                project.Kind,
                project.Source,
                project.ProjectId);
            return null;
        }
    }

    public async Task<string?> GetOrCreateThumbnailSourceAsync(
        ResourceProject project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        StartCacheCompaction();
        if (TryGetCacheEntry(project) is not { } entry)
            return null;

        var cachePath = entry.Path;
        var entryLock = entryLocks.GetOrAdd(cachePath, static _ => new SemaphoreSlim(1, 1));
        await entryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyFilesAsync(entry, null, cancellationToken).ConfigureAwait(false);
            if (IsFresh(entry))
                return ToFileSource(cachePath);

            var bytes = await downloader.DownloadAsync(
                project.IconUrl!,
                MaximumThumbnailBytes,
                cancellationToken).ConfigureAwait(false);
            var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                RemoteIconImageEncoder.SaveAsPng(bytes, temporaryPath, entry.UrlHash);
                await commitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Directory.CreateDirectory(cacheDirectory);
                    var previousLength = File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0;
                    File.Move(temporaryPath, cachePath, overwrite: true);
                    TrackCacheWriteAndTrim(cachePath, previousLength);
                }
                finally
                {
                    commitLock.Release();
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            logger.LogDebug(
                "Resource project thumbnail cached. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                project.Kind,
                project.Source,
                project.ProjectId);
            return ToFileSource(cachePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to cache resource project thumbnail. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                project.Kind,
                project.Source,
                project.ProjectId);
            return TryGetCachedThumbnailSource(project);
        }
        finally
        {
            entryLock.Release();
        }
    }

    internal async Task<string?> MigrateModThumbnailAsync(
        ResourceProject project,
        string? localThumbnailPath,
        CancellationToken cancellationToken)
    {
        if (TryGetCacheEntry(project) is not { LegacyOnlinePath: not null } entry)
            return null;

        StartCacheCompaction();
        var entryLock = entryLocks.GetOrAdd(entry.Path, static _ => new SemaphoreSlim(1, 1));
        await entryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyFilesAsync(entry, localThumbnailPath, cancellationToken).ConfigureAwait(false);
            return TryGetCachedSource(entry);
        }
        finally
        {
            entryLock.Release();
        }
    }

    private async Task MigrateLegacyFilesAsync(
        ThumbnailCacheEntry entry,
        string? localThumbnailPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(entry.LegacyOnlinePath)
            && !File.Exists(entry.LegacyOriginalPath)
            && !File.Exists(localThumbnailPath))
        {
            return;
        }

        await commitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = new DirectoryInfo(cacheDirectory);
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return;

            var onlineFiles = new[] { GetRegularFile(entry.LegacyOnlinePath), GetRegularFile(entry.LegacyOriginalPath) }
                .OfType<FileInfo>();
            var local = GetRegularFile(localThumbnailPath);
            var current = GetRegularFile(entry.Path);
            if (current is null && File.Exists(entry.Path))
                return;

            var previousLength = current?.Length ?? 0;
            var imported = false;
            if (current is null || !RemoteIconImageEncoder.HasUrlHash(entry.Path))
            {
                var candidates = local is null ? onlineFiles : onlineFiles.Append(local);
                imported = candidates.OrderByDescending(file => file.LastWriteTimeUtc)
                    .Any(file => TryImportLegacyFile(file, entry, overwrite: current is not null));
                if (!imported)
                    return;
            }

            foreach (var online in onlineFiles)
            {
                var bytes = online.Length;
                TryDelete(online.FullName);
                if (cacheSizeBytes >= 0 && !File.Exists(online.FullName))
                    cacheSizeBytes -= bytes;
            }
            if (local is not null)
                TryDelete(local.FullName);
            if (imported)
                TrackCacheWriteAndTrim(entry.Path, previousLength);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not merge legacy project thumbnails.");
        }
        finally
        {
            commitLock.Release();
        }
    }

    private bool TryImportLegacyFile(FileInfo candidate, ThumbnailCacheEntry entry, bool overwrite)
    {
        var temporaryPath = $"{entry.Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var lastWriteTime = candidate.LastWriteTimeUtc;
            using (var source = candidate.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                RemoteIconImageEncoder.SaveAsPng(source, temporaryPath, entry.UrlHash);

            File.SetLastWriteTimeUtc(temporaryPath, lastWriteTime);
            File.Move(temporaryPath, entry.Path, overwrite);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
                or NotSupportedException or ArgumentException or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
        {
            logger.LogDebug(exception, "Could not read a legacy project thumbnail.");
            return false;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void StartCacheCompaction()
    {
        if (Interlocked.Exchange(ref compactionStarted, 1) != 0)
            return;

        _ = Task.Run(() => RemoteIconImageEncoder.CompactCacheAsync(cacheDirectory, commitLock, logger, saved =>
        {
            if (cacheSizeBytes >= 0)
                cacheSizeBytes -= saved;
        }));
    }

    private ThumbnailCacheEntry? TryGetCacheEntry(ResourceProject project)
    {
        if (string.IsNullOrWhiteSpace(project.IconUrl)
            || !Uri.TryCreate(project.IconUrl, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var key = $"{project.Source}:{project.ProjectId}";
        var oldKey = Hash($"{key}|{uri.AbsoluteUri}");
        var oldPath = Path.Combine(cacheDirectory, $"{oldKey}.png");
        return project.Kind == ResourceProjectKind.Mod && !string.IsNullOrWhiteSpace(project.ProjectId)
            ? new ThumbnailCacheEntry(Path.Combine(cacheDirectory, $"{Hash(key)}.png"), Hash(uri.AbsoluteUri), oldPath)
            : new ThumbnailCacheEntry(oldPath, null, null);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? TryGetCachedSource(ThumbnailCacheEntry entry)
    {
        if (File.Exists(entry.Path))
            return ToFileSource(entry.Path);
        if (entry.LegacyOnlinePath is { } legacy && File.Exists(legacy))
            return ToFileSource(legacy);
        return entry.LegacyOriginalPath is { } original && File.Exists(original) ? ToFileSource(original) : null;
    }

    private static FileInfo? GetRegularFile(string? path)
    {
        if (path is null)
            return null;
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 ? file : null;
    }

    private static bool IsFresh(ThumbnailCacheEntry entry) =>
        File.Exists(entry.Path)
        && DateTime.UtcNow - File.GetLastWriteTimeUtc(entry.Path) <= RefreshAfter
        && (entry.UrlHash is null || RemoteIconImageEncoder.HasUrlHash(entry.Path, entry.UrlHash));

    private static string ToFileSource(string path)
    {
        var version = File.GetLastWriteTimeUtc(path).Ticks;
        return $"{new Uri(path).AbsoluteUri}?v={version.ToString(CultureInfo.InvariantCulture)}";
    }

    private void TrackCacheWriteAndTrim(string cachePath, long previousLength)
    {
        var currentLength = new FileInfo(cachePath).Length;
        if (cacheSizeBytes < 0)
        {
            cacheSizeBytes = new DirectoryInfo(cacheDirectory)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .Sum(file => file.Length);
        }
        else
        {
            cacheSizeBytes += currentLength - previousLength;
        }

        if (cacheSizeBytes <= MaximumCacheBytes)
            return;

        var files = new DirectoryInfo(cacheDirectory)
            .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(file => string.Equals(file.FullName, cachePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(file => file.LastWriteTimeUtc)
            .ToList();
        foreach (var file in files)
        {
            if (cacheSizeBytes <= TargetCacheBytes)
                break;
            var length = file.Length;
            TryDelete(file.FullName);
            if (!File.Exists(file.FullName))
                cacheSizeBytes -= length;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct ThumbnailCacheEntry(string Path, string? UrlHash, string? LegacyOnlinePath)
    {
        public string? LegacyOriginalPath => System.IO.Path.ChangeExtension(LegacyOnlinePath, ".original.png");
    }
}
