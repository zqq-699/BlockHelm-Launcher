/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Resources;

public sealed class ResourceThumbnailCacheService
{
    private const long MaximumDownloadBytes = 1024L * 1024L;
    private const long MaximumCacheBytes = 50L * 1024L * 1024L;
    private const long TargetCacheBytes = 30L * 1024L * 1024L;
    private const int SizeRefreshInterval = 128;
    private const int ProjectLockCount = 128;
    internal const string CacheVersion = "v1";
    private const int ImageHashLength = 64;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan AccessTimeResolution = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim[] projectLocks = Enumerable.Range(0, ProjectLockCount)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SemaphoreSlim commitLock = new(1, 1);
    private readonly string imageDirectory;
    private readonly string projectDirectory;
    private readonly string writeLockPath;
    private readonly RemoteThumbnailDownloadClient downloader;
    private readonly ILogger logger;
    private long cacheSizeBytes = -1;
    private int imagesAddedSinceScan;
    private bool trimPending;

    public ResourceThumbnailCacheService(
        LauncherPathProvider pathProvider,
        HttpClient httpClient,
        IImportConcurrencyLimiter? limiter,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger logger)
    {
        CacheDirectory = Path.Combine(pathProvider.DefaultDataDirectory, "cache", "resources", "thumbnails", CacheVersion);
        imageDirectory = Path.Combine(CacheDirectory, "images");
        projectDirectory = Path.Combine(CacheDirectory, "projects");
        writeLockPath = Path.Combine(CacheDirectory, "write.lock");
        downloader = new RemoteThumbnailDownloadClient(httpClient, limiter, downloadSpeedLimitState, logger);
        this.logger = logger;
    }

    internal string CacheDirectory { get; }

    public string? TryGetCachedThumbnailSource(ResourceProject project)
    {
        return GetProjectPaths(project) is { } paths
            ? GetCachedSource(ReadReference(paths.ReferencePath))
            : null;
    }

    public Task<string?> GetOrCreateThumbnailSourceAsync(
        ResourceProject project,
        CancellationToken cancellationToken) => ResolveThumbnailSourceAsync(project, fromCachedProject: false, cancellationToken);

    internal Task<string?> GetOrCreateThumbnailFromCachedProjectAsync(
        ResourceProject project,
        CancellationToken cancellationToken) => ResolveThumbnailSourceAsync(project, fromCachedProject: true, cancellationToken);

    private async Task<string?> ResolveThumbnailSourceAsync(
        ResourceProject project,
        bool fromCachedProject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (GetProjectPaths(project) is not { } paths)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        if (TryUseReference(ReadReference(paths.ReferencePath), paths.UrlHash, fromCachedProject, out var source))
            return source;

        var lockIndex = (uint)StringComparer.Ordinal.GetHashCode(paths.ReferencePath) % ProjectLockCount;
        var projectLock = projectLocks[lockIndex];
        await projectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryUseReference(ReadReference(paths.ReferencePath), paths.UrlHash, fromCachedProject, out source))
                return source;

            var bytes = await downloader.DownloadAsync(project.IconUrl!, MaximumDownloadBytes, cancellationToken)
                .ConfigureAwait(false);
            var thumbnail = RemoteIconImageEncoder.EncodeThumbnail(bytes);
            var imagePath = GetImagePath(thumbnail.ContentHash);

            await commitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                await using var writeLock = await CrossProcessVersionLock.AcquireAsync(writeLockPath, null, cancellationToken)
                    .ConfigureAwait(false);
                // A different launcher may have published a newer project URL during the download.
                if (fromCachedProject && TryUseReference(ReadReference(paths.ReferencePath, ignoreReadErrors: false), paths.UrlHash,
                        fromCachedProject, out source))
                {
                    return source;
                }

                var added = await PublishImageAsync(imagePath, thumbnail.Png, cancellationToken).ConfigureAwait(false);
                if (!added)
                    RecordUse(new FileInfo(imagePath));
                if (added && cacheSizeBytes >= 0)
                {
                    cacheSizeBytes += thumbnail.Png.Length;
                    imagesAddedSinceScan++;
                }

                // Publish the reference only after the immutable image is available.
                await AtomicJsonFileWriter.WriteAsync(
                    paths.ReferencePath, new ThumbnailReference(thumbnail.ContentHash, paths.UrlHash, DateTimeOffset.UtcNow),
                    JsonOptions, cancellationToken).ConfigureAwait(false);
                TrimCache(imagePath);
            }
            finally
            {
                commitLock.Release();
            }

            logger.LogDebug(
                "Resource project thumbnail cached. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                project.Kind, project.Source, project.ProjectId);
            return new Uri(imagePath).AbsoluteUri;
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
                project.Kind, project.Source, project.ProjectId);
            return TryGetCachedThumbnailSource(project);
        }
        finally
        {
            projectLock.Release();
        }
    }

    private static async Task<bool> PublishImageAsync(string imagePath, byte[] png, CancellationToken cancellationToken)
    {
        if (File.Exists(imagePath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var temporaryPath = $"{imagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, png, cancellationToken).ConfigureAwait(false);
            return await AtomicFileMove.MoveAsync(temporaryPath, imagePath, overwrite: false, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private (string ReferencePath, string UrlHash)? GetProjectPaths(ResourceProject project)
    {
        if (!Uri.TryCreate(project.IconUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var urlHash = Hash(uri.AbsoluteUri);
        var key = string.IsNullOrWhiteSpace(project.ProjectId)
            ? $"{project.Source}:{project.Kind}:url:{urlHash}"
            : $"{project.Source}:{project.Kind}:id:{project.ProjectId}";
        return (Path.Combine(projectDirectory, $"{Hash(key)}.json"), urlHash);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private ThumbnailReference? ReadReference(string path, bool ignoreReadErrors = true)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var reference = JsonSerializer.Deserialize<ThumbnailReference>(stream, JsonOptions);
            return reference?.ContentHash is { Length: ImageHashLength } hash && hash.All(Uri.IsHexDigit) ? reference : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException
            || (ignoreReadErrors && exception is IOException or UnauthorizedAccessException))
        {
            logger.LogDebug(exception, "Failed to read a cached project thumbnail reference.");
            return null;
        }
    }

    private bool TryUseReference(ThumbnailReference? reference, string urlHash, bool fromCachedProject, out string? source)
    {
        source = null;
        if (reference is null)
            return false;

        if (reference.UrlHash != urlHash)
        {
            if (fromCachedProject)
                source = GetCachedSource(reference);
            return fromCachedProject;
        }

        if (DateTimeOffset.UtcNow - reference.FetchedAt <= RefreshAfter)
            source = GetCachedSource(reference);
        return source is not null;
    }

    private string? GetCachedSource(ThumbnailReference? reference)
    {
        if (reference is null)
            return null;

        var image = new FileInfo(GetImagePath(reference.ContentHash));
        if (!image.Exists)
            return null;

        RecordUse(image);
        return new Uri(image.FullName).AbsoluteUri;
    }

    private void RecordUse(FileInfo image)
    {
        try
        {
            // Treat the PNG timestamp as recent use and limit touches on repeated hits.
            var now = DateTime.UtcNow;
            if (now - image.LastWriteTimeUtc >= AccessTimeResolution)
                image.LastWriteTimeUtc = now;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not record resource thumbnail use.");
        }
    }

    private string GetImagePath(string contentHash) => Path.Combine(imageDirectory, $"{contentHash}.png");

    private void TrimCache(string currentImagePath)
    {
        // Reconcile writes from other launcher processes periodically.
        if (!trimPending && cacheSizeBytes >= 0 && cacheSizeBytes < MaximumCacheBytes
            && imagesAddedSinceScan < SizeRefreshInterval)
            return;

        var images = new DirectoryInfo(imageDirectory).EnumerateFiles("*.png")
            .Where(file => file.Name.Length == ImageHashLength + ".png".Length
                && file.Name[..ImageHashLength].All(Uri.IsHexDigit)
                && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .ToArray();
        cacheSizeBytes = images.Sum(file => file.Length);
        imagesAddedSinceScan = 0;
        trimPending = cacheSizeBytes >= MaximumCacheBytes || (trimPending && cacheSizeBytes > TargetCacheBytes);
        if (!trimPending)
            return;

        (string Path, string? ContentHash)[] references;
        try
        {
            references = Directory.EnumerateFiles(projectDirectory, "*.json")
                .Select(path => (path, ReadReference(path, ignoreReadErrors: false)?.ContentHash))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Resource thumbnail cleanup deferred because project references could not be read.");
            return;
        }

        var referencedHashes = new HashSet<string>(references.Select(reference => reference.ContentHash)
            .OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var remainingHashes = new HashSet<string>(images.Select(file => file.Name[..ImageHashLength]),
            StringComparer.OrdinalIgnoreCase);
        var initialSize = cacheSizeBytes;

        foreach (var file in images.OrderBy(file => referencedHashes.Contains(file.Name[..ImageHashLength]))
                     .ThenBy(file => file.LastWriteTimeUtc))
        {
            if (cacheSizeBytes <= TargetCacheBytes)
                break;
            if (string.Equals(file.FullName, currentImagePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var length = file.Length;
            if (TryDelete(file.FullName))
            {
                cacheSizeBytes -= length;
                remainingHashes.Remove(file.Name[..ImageHashLength]);
            }
        }

        foreach (var (path, contentHash) in references)
        {
            if (contentHash is null || !remainingHashes.Contains(contentHash))
                TryDelete(path);
        }

        trimPending = cacheSizeBytes > TargetCacheBytes;
        logger.LogDebug("Resource thumbnail cache trimmed. InitialBytes={InitialBytes} RemainingBytes={RemainingBytes}",
            initialSize, cacheSizeBytes);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ThumbnailReference(string ContentHash, string UrlHash, DateTimeOffset FetchedAt);
}
