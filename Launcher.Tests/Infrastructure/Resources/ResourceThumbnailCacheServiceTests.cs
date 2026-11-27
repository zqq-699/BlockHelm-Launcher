/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Persistence;
using Launcher.Infrastructure.Resources;
using Launcher.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Infrastructure.Resources;

public sealed class ResourceThumbnailCacheServiceTests : TestTempDirectory
{
    [Fact]
    public async Task ResourceIconUsesOneCached64PixelThumbnail()
    {
        using var http = new HttpClient(new IconHandler(CreatePng()));
        var paths = new LauncherPathProvider(TempRoot);
        var cache = new ResourceThumbnailCacheService(
            paths, http, null, null, NullLogger.Instance);
        var project = new ResourceProject
        {
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "project",
            IconUrl = "https://icons.test/project.png"
        };

        var thumbnail = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(project, default));
        var thumbnailPath = new Uri(thumbnail).LocalPath;

        Assert.Equal((64, 64), ReadSize(thumbnailPath));
        Assert.Equal(Path.Combine(paths.DefaultDataDirectory, "cache", "resources", "thumbnails", "v1", "images"),
            Path.GetDirectoryName(thumbnailPath));
        Assert.Equal(thumbnailPath, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(thumbnailPath)!, "*.png")));
        Assert.Equal(thumbnail, cache.TryGetCachedThumbnailSource(project));
        Assert.Equal(thumbnail, await cache.GetOrCreateThumbnailSourceAsync(project, default));
    }

    [Fact]
    public async Task LocalModAndOnlineCatalogShareOneDownloadAndOneFile()
    {
        Directory.CreateDirectory(TempRoot);
        var modPath = Path.Combine(TempRoot, "example.jar");
        var modBytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(modPath, modBytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(modBytes)).ToLowerInvariant();
        var project = CreateModProject();
        var handler = new IconHandler(CreatePng(), sha1);
        using var http = new HttpClient(handler);
        var paths = new LauncherPathProvider(TempRoot);
        var catalog = new ResourceCatalogService(http, paths);
        var local = new LocalModIconEnrichmentService(paths, http, thumbnailCache: catalog.ThumbnailCache);
        var mod = new LocalMod { FullPath = modPath };
        var copyPath = Path.Combine(TempRoot, "copy.jar");
        File.Copy(modPath, copyPath);
        var copy = new LocalMod { FullPath = copyPath };

        var localTask = local.ResolveMissingIconSourcesAsync([mod, copy]);
        var onlineTask = catalog.GetOrCreateThumbnailSourceAsync(project);
        await Task.WhenAll(localTask, onlineTask);

        var icon = Assert.IsType<string>(await onlineTask);
        var resolved = await localTask;
        var cached = await local.ResolveCachedIconSourcesAsync([mod, copy]);
        Assert.Equal(2, resolved.Count);
        Assert.Equal(icon, resolved[modPath]);
        Assert.Equal(icon, resolved[copyPath]);
        Assert.Equal(2, cached.Count);
        Assert.Equal(icon, cached[modPath]);
        Assert.Equal(icon, cached[copyPath]);
        Assert.Equal(1, handler.Downloads);
        Assert.True(File.Exists(Path.Combine(catalog.ThumbnailCache.CacheDirectory, "mod-index.json")));
        Assert.Equal(new Uri(icon).LocalPath, Assert.Single(Directory.GetFiles(paths.DefaultDataDirectory, "*.png", SearchOption.AllDirectories)));
        Assert.Equal((64, 64), ReadSize(new Uri(icon).LocalPath));
    }

    [Fact]
    public async Task AllOnlineResourceKindsShareIdenticalPixelsAt64Pixels()
    {
        var handler = new IconHandler(CreatePng(), updatedPng: CreatePng(dpi: 144));
        using var http = new HttpClient(handler);
        var cache = new ResourceThumbnailCacheService(
            new LauncherPathProvider(TempRoot), http, null, null, NullLogger.Instance);

        var projects = Enum.GetValues<ResourceProjectKind>().Select(kind => new ResourceProject
        {
            Kind = kind,
            Source = kind == ResourceProjectKind.World ? ResourceProjectSource.CurseForge : ResourceProjectSource.Modrinth,
            ProjectId = "shared-id",
            IconUrl = $"https://icons.test/{kind}/updated.png"
        }).Append(CreateModProject()).ToArray();
        var icons = await Task.WhenAll(projects.Select(project => cache.GetOrCreateThumbnailSourceAsync(project, default)));

        var icon = Assert.IsType<string>(icons[0]);
        Assert.All(icons, value => Assert.Equal(icon, value));
        Assert.All(projects, project => Assert.Equal(icon, cache.TryGetCachedThumbnailSource(project)));
        Assert.Equal(projects.Length, handler.Downloads);
        Assert.Equal(projects.Length, Directory.GetFiles(Path.Combine(cache.CacheDirectory, "projects"), "*.json").Length);
        var imagePath = new Uri(icon).LocalPath;
        Assert.Equal(imagePath, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(imagePath)!, "*.png")));
        Assert.Equal((64, 64), ReadSize(imagePath));
    }

    [Fact]
    public async Task RefreshRepointsOnlyTheProjectWhoseImageChanges()
    {
        var handler = new IconHandler(CreatePng(), updatedPng: CreatePng(color: 0xffaa6600));
        using var http = new HttpClient(handler);
        var paths = new LauncherPathProvider(TempRoot);
        var cache = new ResourceThumbnailCacheService(paths, http, null, null, NullLogger.Instance);
        var project = CreateModProject();
        var first = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(project, default));
        var other = new ResourceProject { Source = ResourceProjectSource.Modrinth, ProjectId = "other", IconUrl = project.IconUrl };
        Assert.Equal(first, await cache.GetOrCreateThumbnailSourceAsync(other, default));
        var changed = CreateModProject("https://icons.test/updated.png");

        var second = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(changed, default));

        Assert.NotEqual(first, second);
        Assert.Equal(first, cache.TryGetCachedThumbnailSource(other));
        Assert.True(File.Exists(new Uri(first).LocalPath));
        var reopened = new ResourceThumbnailCacheService(paths, http, null, null, NullLogger.Instance);
        Assert.Equal(second, await reopened.GetOrCreateThumbnailSourceAsync(changed, default));
        Assert.Equal(3, handler.Downloads);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(new Uri(first).LocalPath)!, "*.png").Length);
    }

    [Fact]
    public async Task CachedResourcePackMetadataKeepsTheNewerSharedThumbnail()
    {
        Directory.CreateDirectory(TempRoot);
        var pack = Path.Combine(TempRoot, "pack.zip");
        var bytes = Encoding.UTF8.GetBytes("local resource pack");
        File.WriteAllBytes(pack, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var handler = new IconHandler(CreatePng(), sha1, updatedPng: CreatePng(color: 0xffaa6600));
        using var http = new HttpClient(handler);
        var paths = new LauncherPathProvider(TempRoot);
        var catalog = new ResourceCatalogService(http, paths);
        var local = new LocalResourceCategoryEnrichmentService(paths, http, thumbnailService: catalog);
        var candidate = new LocalResourceCategoryCandidate(pack, ResourceProjectKind.ResourcePack);
        await local.ResolveCategoriesAsync([candidate]);
        var original = Assert.IsType<string>(Assert.Single(await local.ResolveMetadataAsync([candidate])).Value.IconSource);

        var updatedProject = new ResourceProject
        {
            Kind = ResourceProjectKind.ResourcePack,
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "project",
            IconUrl = "https://icons.test/updated.png"
        };
        var updated = Assert.IsType<string>(await catalog.GetOrCreateThumbnailSourceAsync(updatedProject));
        Assert.NotEqual(original, updated);

        var reopened = new LocalResourceCategoryEnrichmentService(paths, http, thumbnailService: catalog);
        Assert.Equal(updated, Assert.Single(await reopened.ResolveMetadataAsync([candidate])).Value.IconSource);
        Assert.Equal(updated, catalog.TryGetCachedThumbnailSource(updatedProject));
        Assert.Equal(2, handler.Downloads);
    }

    [Fact]
    public async Task CachedProjectDownloadKeepsTheThumbnailPublishedWhileItWasInFlight()
    {
        var paths = new LauncherPathProvider(TempRoot);
        var delayedIcon = new DeferredIconHandler(CreatePng());
        using var cachedHttp = new HttpClient(delayedIcon);
        using var onlineHttp = new HttpClient(new IconHandler(CreatePng(color: 0xffaa6600)));
        var cached = new ResourceThumbnailCacheService(paths, cachedHttp, null, null, NullLogger.Instance);
        var online = new ResourceThumbnailCacheService(paths, onlineHttp, null, null, NullLogger.Instance);
        var original = CreateModProject();
        var updatedProject = CreateModProject("https://icons.test/updated.png");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var pending = cached.GetOrCreateThumbnailFromCachedProjectAsync(original, timeout.Token);
        await delayedIcon.Started.Task.WaitAsync(timeout.Token);
        var updated = Assert.IsType<string>(await online.GetOrCreateThumbnailSourceAsync(updatedProject, timeout.Token));
        delayedIcon.Complete.TrySetResult();

        Assert.Equal(updated, await pending);
        Assert.Equal(updated, cached.TryGetCachedThumbnailSource(original));
        Assert.Equal(new Uri(updated).LocalPath,
            Assert.Single(Directory.GetFiles(Path.Combine(cached.CacheDirectory, "images"), "*.png")));
    }

    [Fact]
    public async Task CacheTrimsAtFiftyMiBToThirtyMiBKeepingRecentlyUsedImages()
    {
        const long MiB = 1024L * 1024L;
        var paths = new LauncherPathProvider(TempRoot);
        using var seedHttp = new HttpClient(new IconHandler(CreatePng(), updatedPng: CreatePng(color: 0xffaa6600)));
        var seed = new ResourceThumbnailCacheService(paths, seedHttp, null, null, NullLogger.Instance);
        var hotProject = CreateModProject();
        var coldProject = new ResourceProject
        {
            Source = ResourceProjectSource.Modrinth, ProjectId = "cold", IconUrl = "https://icons.test/updated.png"
        };
        var hotSource = Assert.IsType<string>(await seed.GetOrCreateThumbnailSourceAsync(hotProject, default));
        var coldSource = Assert.IsType<string>(await seed.GetOrCreateThumbnailSourceAsync(coldProject, default));
        using var warmHttp = new HttpClient(new IconHandler(CreatePng(color: 0xffaa7777)));
        var warmCache = new ResourceThumbnailCacheService(paths, warmHttp, null, null, NullLogger.Instance);
        var warmProject = new ResourceProject
        {
            Source = ResourceProjectSource.Modrinth, ProjectId = "warm", IconUrl = "https://icons.test/warm.png"
        };
        var warmSource = Assert.IsType<string>(await warmCache.GetOrCreateThumbnailSourceAsync(warmProject, default));
        var hotPath = new Uri(hotSource).LocalPath;
        var coldPath = new Uri(coldSource).LocalPath;
        var warmPath = new Uri(warmSource).LocalPath;
        var images = Path.GetDirectoryName(hotPath)!;
        var orphanPath = Path.Combine(images, new string('a', 64) + ".png");
        var newPng = CreatePng(color: 0xff224488);
        var newSize = RemoteIconImageEncoder.EncodeThumbnail(newPng).Png.Length;
        SetLength(hotPath, 18 * MiB, daysOld: 4);
        SetLength(coldPath, 7 * MiB, daysOld: 3);
        SetLength(warmPath, 8 * MiB, daysOld: 2);
        SetLength(orphanPath, 17 * MiB - newSize, daysOld: 1);

        using var http = new HttpClient(new IconHandler(newPng));
        var cache = new ResourceThumbnailCacheService(paths, http, null, null, NullLogger.Instance);
        Assert.Equal(hotSource, cache.TryGetCachedThumbnailSource(hotProject));
        var hotReference = Directory.GetFiles(Path.Combine(cache.CacheDirectory, "projects"), "*.json")
            .Single(file => File.ReadAllText(file).Contains(Path.GetFileNameWithoutExtension(hotPath), StringComparison.Ordinal));
        var added = new ResourceProject
        {
            Source = ResourceProjectSource.Modrinth, ProjectId = "added", IconUrl = "https://icons.test/added.png"
        };

        using (File.Open(hotReference, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.NotNull(await cache.GetOrCreateThumbnailSourceAsync(added, default));
            Assert.True(File.Exists(orphanPath));
            Assert.True(File.Exists(hotPath));
            Assert.True(File.Exists(coldPath));
            Assert.Equal(50 * MiB, new DirectoryInfo(images).EnumerateFiles("*.png").Sum(file => file.Length));
        }

        var next = new ResourceProject
        {
            Source = ResourceProjectSource.Modrinth, ProjectId = "next", IconUrl = added.IconUrl
        };
        Assert.NotNull(await cache.GetOrCreateThumbnailSourceAsync(next, default));

        Assert.False(File.Exists(orphanPath));
        Assert.False(File.Exists(coldPath));
        Assert.Null(cache.TryGetCachedThumbnailSource(coldProject));
        Assert.Equal(hotSource, cache.TryGetCachedThumbnailSource(hotProject));
        Assert.Equal(warmSource, cache.TryGetCachedThumbnailSource(warmProject));
        Assert.True(new DirectoryInfo(images).EnumerateFiles("*.png").Sum(file => file.Length) <= 30 * MiB);

        static void SetLength(string path, long length, int daysOld)
        {
            using (var stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write))
                stream.SetLength(length);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        }
    }

    [Fact]
    public async Task LegacyCacheCleanupKeepsCurrentThumbnailAndReference()
    {
        var paths = new LauncherPathProvider(TempRoot);
        var cacheRoot = Path.Combine(paths.DefaultDataDirectory, "cache");
        var legacyLocal = Path.Combine(cacheRoot, "mods", "remote-icons");
        var legacyOnline = Path.Combine(cacheRoot, "resources", "thumbnails");
        var categories = Path.Combine(cacheRoot, "resources", "local-categories");
        Directory.CreateDirectory(legacyLocal);
        Directory.CreateDirectory(legacyOnline);
        Directory.CreateDirectory(categories);
        var legacyIcon = new string('a', 64) + ".png";
        File.WriteAllBytes(Path.Combine(legacyLocal, legacyIcon), CreatePng());
        File.WriteAllText(Path.Combine(legacyLocal, "index.json"), "{}");
        var oldRemotePng = Path.Combine(legacyOnline, legacyIcon);
        File.WriteAllBytes(oldRemotePng, CreatePng());
        var trialPng = Path.Combine(legacyOnline, "v1-" + legacyIcon);
        File.WriteAllBytes(trialPng, CreatePng());
        var customFile = Path.Combine(legacyOnline, "custom.png");
        var categoryFile = Path.Combine(categories, "index.json");
        File.WriteAllText(customFile, "keep");
        File.WriteAllText(categoryFile, "keep");

        var handler = new IconHandler(CreatePng());
        using var http = new HttpClient(handler);
        var cache = new ResourceThumbnailCacheService(paths, http, null, null, NullLogger.Instance);
        var project = CreateModProject();
        var current = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(project, default));

        var cleanup = new LegacyProjectIconCacheCleanup(paths, NullLogger<LegacyProjectIconCacheCleanup>.Instance);
        await cleanup.CleanupAsync();

        Assert.False(Directory.Exists(legacyLocal));
        Assert.False(File.Exists(oldRemotePng));
        Assert.False(File.Exists(trialPng));
        Assert.Equal("keep", File.ReadAllText(customFile));
        Assert.Equal("keep", File.ReadAllText(categoryFile));
        Assert.Equal(current, cache.TryGetCachedThumbnailSource(project));
        Assert.Equal(current, await cache.GetOrCreateThumbnailSourceAsync(project, default));
        Assert.Equal(1, handler.Downloads);

        File.WriteAllBytes(oldRemotePng, CreatePng());
        await cleanup.CleanupAsync();
        Assert.True(File.Exists(oldRemotePng));
    }

    [Fact]
    public async Task LegacyCacheCleanupRetriesWhenOldImageIsInUse()
    {
        var paths = new LauncherPathProvider(TempRoot);
        var legacy = Path.Combine(paths.DefaultDataDirectory, "cache", "resources", "thumbnails");
        Directory.CreateDirectory(legacy);
        var oldImage = Path.Combine(legacy, new string('a', 64) + ".png");
        File.WriteAllBytes(oldImage, CreatePng());
        var cleanup = new LegacyProjectIconCacheCleanup(paths, NullLogger<LegacyProjectIconCacheCleanup>.Instance);

        using (File.Open(oldImage, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await cleanup.CleanupAsync();
            Assert.True(File.Exists(oldImage));
        }

        await cleanup.CleanupAsync();
        Assert.False(File.Exists(oldImage));
    }

    [Fact]
    public async Task ThumbnailPublishRetriesInUseFileAndKeepsExistingImage()
    {
        Directory.CreateDirectory(TempRoot);
        var temporary = Path.Combine(TempRoot, "image.tmp");
        var image = Path.Combine(TempRoot, "image.png");
        File.WriteAllText(temporary, "original");
        using var occupied = File.Open(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);

        var moved = await AtomicFileMove.MoveAsync(temporary, image, overwrite: false, default,
            retryAsync: _ =>
            {
                occupied.Dispose();
                return Task.CompletedTask;
            });

        Assert.True(moved);
        File.WriteAllText(temporary, "duplicate");
        Assert.False(await AtomicFileMove.MoveAsync(temporary, image, overwrite: false, default));
        Assert.Equal("original", File.ReadAllText(image));
    }

    [Fact]
    public async Task ModIndexUpdatesFromDifferentStoresMergeAndAllowOpenReaders()
    {
        var first = new RemoteIconCacheIndexStore(TempRoot, NullLogger.Instance);
        var second = new RemoteIconCacheIndexStore(TempRoot, NullLogger.Instance);
        await first.UpdateAsync(index => index.Entries.TryAdd("original", new RemoteIconCacheEntry()), default);
        using var reader = File.Open(Path.Combine(TempRoot, "mod-index.json"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        await Task.WhenAll(
            first.UpdateAsync(index => index.Entries.TryAdd("first", new RemoteIconCacheEntry()), default),
            second.UpdateAsync(index => index.Entries.TryAdd("second", new RemoteIconCacheEntry()), default));

        var result = await first.LoadAsync(default);
        Assert.Equal(new[] { "first", "original", "second" }, result.Entries.Keys.Order());
    }

    private static ResourceProject CreateModProject(string iconUrl = "https://icons.test/project.png") =>
        new() { Source = ResourceProjectSource.Modrinth, ProjectId = "project", IconUrl = iconUrl };

    private static byte[] CreatePng(double dpi = 96, uint color = 0xff886622)
    {
        const int width = 256;
        const int height = 128;
        var pixels = new uint[width * height];
        Array.Fill(pixels, color);
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static (int Width, int Height) ReadSize(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private sealed class IconHandler(byte[] bytes, string? sha1 = null, byte[]? updatedPng = null) : HttpMessageHandler
    {
        private int downloads;

        public int Downloads => downloads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "icons.test")
            {
                Interlocked.Increment(ref downloads);
                var content = request.RequestUri.AbsolutePath.EndsWith("/updated.png", StringComparison.Ordinal)
                    ? updatedPng ?? bytes
                    : bytes;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }

            var json = request.RequestUri?.AbsolutePath switch
            {
                "/v2/version_files" when sha1 is not null => $$"""{"{{sha1}}":{"project_id":"project"} }""",
                "/v2/projects" when sha1 is not null => """[{"id":"project","slug":"project","icon_url":"https://icons.test/project.png"}]""",
                _ => throw new InvalidOperationException("Unexpected request in thumbnail cache test.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class DeferredIconHandler(byte[] png) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
        }
    }
}
