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
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.FileSystem;
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
        var cache = new ResourceThumbnailCacheService(
            new LauncherPathProvider(TempRoot), http, null, null, NullLogger.Instance);
        var project = new ResourceProject
        {
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "project",
            IconUrl = "https://icons.test/project.png"
        };

        var thumbnail = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(project, default));
        var thumbnailPath = new Uri(thumbnail).LocalPath;

        Assert.Equal((64, 64), ReadSize(thumbnailPath));
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

        var localTask = local.ResolveMissingIconSourcesAsync([mod]);
        var onlineTask = catalog.GetOrCreateThumbnailSourceAsync(project);
        await Task.WhenAll(localTask, onlineTask);

        var icon = Assert.IsType<string>(await onlineTask);
        Assert.Equal(icon, Assert.Single(await localTask).Value);
        Assert.Equal(icon, Assert.Single(await local.ResolveCachedIconSourcesAsync([mod])).Value);
        Assert.Equal(1, handler.Downloads);
        Assert.Equal(new Uri(icon).LocalPath, Assert.Single(Directory.GetFiles(paths.DefaultDataDirectory, "*.png", SearchOption.AllDirectories)));
        Assert.Equal((64, 64), ReadSize(new Uri(icon).LocalPath));
    }

    [Fact]
    public async Task LegacyLocalAndOnlineCopiesMergeWithoutRedownloading()
    {
        var paths = new LauncherPathProvider(TempRoot);
        var localDirectory = Path.Combine(paths.DefaultDataDirectory, "cache", "mods", "remote-icons");
        var onlineDirectory = Path.Combine(paths.DefaultDataDirectory, "cache", "resources", "thumbnails");
        Directory.CreateDirectory(localDirectory);
        Directory.CreateDirectory(onlineDirectory);
        var project = CreateModProject();
        const string localKey = "modrinth:project";
        var legacyLocal = Path.Combine(localDirectory, Hash(localKey) + ".png");
        var legacyOnline = Path.Combine(onlineDirectory, Hash($"Modrinth:project|{project.IconUrl}") + ".png");
        var sourceBytes = CreatePng();
        File.WriteAllBytes(legacyLocal, sourceBytes);
        File.WriteAllBytes(legacyOnline, sourceBytes);
        File.SetLastWriteTimeUtc(legacyLocal, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(legacyOnline, DateTime.UtcNow.AddDays(-1));
        var modPath = Path.Combine(TempRoot, "example.jar");
        File.WriteAllBytes(modPath, [1]);
        var alias = Assert.IsType<string>(LocalModIconEnrichmentService.TryCreateFileAlias(modPath));
        var index = new RemoteIconCacheIndex();
        index.Entries[localKey] = new RemoteIconCacheEntry
        {
            Source = "modrinth",
            ProjectId = "project",
            IconUrl = project.IconUrl!,
            FileName = Path.GetFileName(legacyLocal),
            CachedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow
        };
        index.FileAliases[alias] = localKey;
        await new RemoteIconCacheIndexStore(localDirectory, NullLogger.Instance).SaveAsync(index, default);
        var handler = new IconHandler(sourceBytes);
        using var http = new HttpClient(handler);
        var catalog = new ResourceCatalogService(http, paths);
        var local = new LocalModIconEnrichmentService(paths, http, thumbnailCache: catalog.ThumbnailCache);

        var fromLocal = Assert.Single(await local.ResolveCachedIconSourcesAsync([new LocalMod { FullPath = modPath }])).Value;
        var fromOnline = await catalog.GetOrCreateThumbnailSourceAsync(project);

        Assert.Equal(fromLocal, fromOnline);
        Assert.Equal(0, handler.Downloads);
        Assert.Equal(new Uri(fromLocal).LocalPath, Assert.Single(Directory.GetFiles(paths.DefaultDataDirectory, "*.png", SearchOption.AllDirectories)));
        Assert.Equal((64, 64), ReadSize(new Uri(fromLocal).LocalPath));
        Assert.False(File.Exists(legacyLocal));
        Assert.False(File.Exists(legacyOnline));
    }

    [Fact]
    public async Task IconUrlChangeRefreshesTheSameProjectFile()
    {
        var handler = new IconHandler(CreatePng());
        using var http = new HttpClient(handler);
        var cache = new ResourceThumbnailCacheService(
            new LauncherPathProvider(TempRoot), http, null, null, NullLogger.Instance);
        var first = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(CreateModProject(), default));
        var changed = CreateModProject("https://icons.test/updated.png");
        var second = Assert.IsType<string>(await cache.GetOrCreateThumbnailSourceAsync(changed, default));

        Assert.Equal(new Uri(first).LocalPath, new Uri(second).LocalPath);
        Assert.Equal(second, await cache.GetOrCreateThumbnailSourceAsync(changed, default));
        Assert.Equal(2, handler.Downloads);
        Assert.Single(Directory.GetFiles(TempRoot, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingCacheCompactionPreservesFreshnessAndSkipsOtherImages()
    {
        Directory.CreateDirectory(TempRoot);
        var oldThumbnail = Path.Combine(TempRoot, new string('a', 64) + ".png");
        var legacyThumbnail = Path.Combine(TempRoot, new string('b', 64) + ".png");
        var legacyOriginal = Path.Combine(TempRoot, new string('b', 64) + ".original.png");
        var unrelated = Path.Combine(TempRoot, "custom.png");
        var bytes = CreatePng();
        var smallBytes = CreatePng(32, 32);
        File.WriteAllBytes(oldThumbnail, bytes);
        File.WriteAllBytes(legacyThumbnail, smallBytes);
        File.WriteAllBytes(legacyOriginal, bytes);
        File.WriteAllBytes(unrelated, bytes);
        File.SetLastWriteTimeUtc(oldThumbnail, DateTime.UtcNow.AddDays(-7));
        File.SetLastWriteTimeUtc(legacyThumbnail, DateTime.UtcNow.AddDays(-7));
        var lastWrite = File.GetLastWriteTimeUtc(oldThumbnail);
        var originalLastWrite = File.GetLastWriteTimeUtc(legacyOriginal);
        var bytesSaved = 0L;
        using var writeLock = new SemaphoreSlim(1);

        await RemoteIconImageEncoder.CompactCacheAsync(
            TempRoot, writeLock, NullLogger.Instance, saved => bytesSaved += saved);

        Assert.Equal((64, 64), ReadSize(oldThumbnail));
        Assert.Equal((64, 64), ReadSize(legacyThumbnail));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(oldThumbnail));
        Assert.Equal(originalLastWrite, File.GetLastWriteTimeUtc(legacyThumbnail));
        Assert.True(bytesSaved > 0);
        Assert.Equal(
            2L * bytes.Length + smallBytes.Length - new FileInfo(oldThumbnail).Length - new FileInfo(legacyThumbnail).Length,
            bytesSaved);
        Assert.False(File.Exists(legacyOriginal));
        Assert.Equal(bytes, File.ReadAllBytes(unrelated));
        Assert.Empty(Directory.EnumerateFiles(TempRoot, "*.tmp"));
    }

    private static ResourceProject CreateModProject(string iconUrl = "https://icons.test/project.png") =>
        new() { Source = ResourceProjectSource.Modrinth, ProjectId = "project", IconUrl = iconUrl };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static byte[] CreatePng(int width = 256, int height = 128)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
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

    private sealed class IconHandler(byte[] bytes, string? sha1 = null) : HttpMessageHandler
    {
        private int downloads;

        public int Downloads => downloads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "icons.test")
            {
                Interlocked.Increment(ref downloads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
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
}
