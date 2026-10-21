/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class LocalResourceCategoryEnrichmentServiceTests : TestTempDirectory
{
    [Theory]
    [InlineData("sodium", "钠")]
    [InlineData("architects-palette-fabric", null)]
    public async Task LocalModUsesExactOnlineProjectSlugForChineseName(string slug, string? expectedName)
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, $"{slug}.jar");
        var bytes = Encoding.UTF8.GetBytes(slug);
        File.WriteAllBytes(path, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var pathProvider = new LauncherPathProvider(TempRoot);
        var candidate = new LocalResourceCategoryCandidate(path, ResourceProjectKind.Mod);

        using (var client = new HttpClient(new ModrinthMatchHandler(sha1, slug)))
        {
            var service = new LocalResourceCategoryEnrichmentService(pathProvider, client);
            var matched = Assert.Single(await service.ResolveMetadataAsync([candidate])).Value;
            Assert.Equal(expectedName, matched.LocalizedName);
            Assert.Equal(ResourceProjectSource.Modrinth, matched.ProjectReference?.Source);
        }

        using var offlineClient = new HttpClient(new RejectingHandler());
        var restartedService = new LocalResourceCategoryEnrichmentService(pathProvider, offlineClient);
        var cached = Assert.Single(await restartedService.ResolveCachedMetadataAsync([candidate])).Value;
        Assert.Equal(expectedName, cached.LocalizedName);
    }

    [Fact]
    public async Task CachedModProjectWithoutSlugIsRefreshedForLocalization()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "sodium.jar");
        var bytes = Encoding.UTF8.GetBytes("cached sodium mod");
        File.WriteAllBytes(path, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var pathProvider = new LauncherPathProvider(TempRoot);
        var candidate = new LocalResourceCategoryCandidate(path, ResourceProjectKind.Mod);

        using (var client = new HttpClient(new ModrinthMatchHandler(sha1, "sodium")))
            await new LocalResourceCategoryEnrichmentService(pathProvider, client)
                .ResolveMetadataAsync([candidate]);

        var cachePath = Path.Combine(
            pathProvider.DefaultDataDirectory, "cache", "resources", "local-categories", "index.json");
        var cacheJson = File.ReadAllText(cachePath);
        Assert.Contains("\"projectSlug\":\"sodium\"", cacheJson);
        File.WriteAllText(cachePath, cacheJson.Replace(
            "\"projectSlug\":\"sodium\"", "\"projectSlug\":null", StringComparison.Ordinal));

        using var refreshedClient = new HttpClient(new ModrinthMatchHandler(sha1, "sodium"));
        var refreshedService = new LocalResourceCategoryEnrichmentService(pathProvider, refreshedClient);
        var refreshed = Assert.Single(await refreshedService.ResolveMetadataAsync([candidate])).Value;

        Assert.Equal("钠", refreshed.LocalizedName);
    }

    [Fact]
    public async Task CurseForgeFileMatchUsesSameProjectSlugLocalization()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "curseforge-sodium.jar");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("CurseForge sodium file"));
        using var client = new HttpClient(new CurseForgeMatchHandler());
        var service = new LocalResourceCategoryEnrichmentService(
            new LauncherPathProvider(TempRoot),
            client,
            new TestCurseForgeKeyResolver());

        var matched = Assert.Single(await service.ResolveMetadataAsync(
            [new LocalResourceCategoryCandidate(path, ResourceProjectKind.Mod)])).Value;

        Assert.Equal(ResourceProjectSource.CurseForge, matched.ProjectReference?.Source);
        Assert.Equal("钠", matched.LocalizedName);
    }

    [Fact]
    public async Task ExactModrinthFileMatchMapsCategoriesForRequestedResourceKind()
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "shader.zip");
        var duplicatePath = Path.Combine(TempRoot, "shader-copy.zip");
        var bytes = Encoding.UTF8.GetBytes("recognized shader archive");
        File.WriteAllBytes(path, bytes);
        File.WriteAllBytes(duplicatePath, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        using var httpClient = new HttpClient(new ModrinthMatchHandler(sha1));
        var service = new LocalResourceCategoryEnrichmentService(
            new LauncherPathProvider(TempRoot),
            httpClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance);

        var result = await service.ResolveCategoriesAsync(
        [
            new LocalResourceCategoryCandidate(path, ResourceProjectKind.ShaderPack),
            new LocalResourceCategoryCandidate(duplicatePath, ResourceProjectKind.ShaderPack)
        ]);

        Assert.Equal(2, result.Count);
        Assert.All(result.Values, categories => Assert.Equal(
            [ResourceProjectCategory.Fantasy, ResourceProjectCategory.Realistic],
            categories));
    }

    [Fact]
    public async Task PersistedCategoriesAreAvailableAfterServiceRestartWithoutNetworkRequests()
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "cached-mod.jar");
        var bytes = Encoding.UTF8.GetBytes("persisted recognized mod");
        File.WriteAllBytes(path, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var pathProvider = new LauncherPathProvider(TempRoot);

        using (var firstClient = new HttpClient(new ModrinthMatchHandler(sha1)))
        {
            var firstService = new LocalResourceCategoryEnrichmentService(
                pathProvider,
                firstClient,
                logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance);
            var first = await firstService.ResolveCategoriesAsync(
                [new LocalResourceCategoryCandidate(path, ResourceProjectKind.ShaderPack)]);
            Assert.Equal(
                [ResourceProjectCategory.Fantasy, ResourceProjectCategory.Realistic],
                Assert.Single(first).Value);
        }

        using var secondClient = new HttpClient(new RejectingHandler());
        var restartedService = new LocalResourceCategoryEnrichmentService(
            pathProvider,
            secondClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance);
        var candidate = new LocalResourceCategoryCandidate(path, ResourceProjectKind.ShaderPack);

        var cached = await restartedService.ResolveCachedCategoriesAsync([candidate]);
        var resolved = await restartedService.ResolveCategoriesAsync([candidate]);

        Assert.Equal(
            [ResourceProjectCategory.Fantasy, ResourceProjectCategory.Realistic],
            Assert.Single(cached).Value);
        Assert.Equal(cached.Single().Value, Assert.Single(resolved).Value);
        Assert.True(File.Exists(Path.Combine(
            pathProvider.DefaultDataDirectory,
            "cache",
            "resources",
            "local-categories",
            "index.json")));
    }

    [Fact]
    public async Task ResourcePackMetadataDownloadsMatchedRemoteProjectIcon()
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "matched-resource-pack.zip");
        var bytes = Encoding.UTF8.GetBytes("recognized resource pack");
        File.WriteAllBytes(path, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var thumbnailService = new RecordingThumbnailService(
            downloadedSource: "file:///cache/remote-resource-pack.png");
        using var httpClient = new HttpClient(new ModrinthMatchHandler(sha1));
        var service = new LocalResourceCategoryEnrichmentService(
            new LauncherPathProvider(TempRoot),
            httpClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
            thumbnailService: thumbnailService);

        var result = await service.ResolveMetadataAsync(
            [new LocalResourceCategoryCandidate(path, ResourceProjectKind.ResourcePack)]);

        var metadata = Assert.Single(result).Value;
        Assert.Equal("file:///cache/remote-resource-pack.png", metadata.IconSource);
        Assert.Equal(
            [ResourceProjectCategory.Realistic],
            metadata.Categories);
        Assert.Equal(
            new ResourceProjectReference(
                ResourceProjectKind.ResourcePack,
                ResourceProjectSource.Modrinth,
                "shader-project"),
            metadata.ProjectReference);
        var project = Assert.Single(thumbnailService.DownloadedProjects);
        Assert.Equal(ResourceProjectKind.ResourcePack, project.Kind);
        Assert.Equal("https://cdn.example/shader.png", project.IconUrl);
    }

    [Fact]
    public async Task CachedResourcePackMetadataUsesCachedThumbnailWithoutNetworkOrDownload()
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "cached-resource-pack.zip");
        var bytes = Encoding.UTF8.GetBytes("cached resource pack");
        File.WriteAllBytes(path, bytes);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var pathProvider = new LauncherPathProvider(TempRoot);
        using (var firstClient = new HttpClient(new ModrinthMatchHandler(sha1)))
        {
            var firstService = new LocalResourceCategoryEnrichmentService(
                pathProvider,
                firstClient,
                logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
                thumbnailService: new RecordingThumbnailService(
                    downloadedSource: "file:///cache/first-download.png"));
            await firstService.ResolveMetadataAsync(
                [new LocalResourceCategoryCandidate(path, ResourceProjectKind.ResourcePack)]);
        }

        var thumbnailService = new RecordingThumbnailService(
            cachedSource: "file:///cache/cached-resource-pack.png");
        using var rejectingClient = new HttpClient(new RejectingHandler());
        var restartedService = new LocalResourceCategoryEnrichmentService(
            pathProvider,
            rejectingClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
            thumbnailService: thumbnailService);

        var result = await restartedService.ResolveCachedMetadataAsync(
            [new LocalResourceCategoryCandidate(path, ResourceProjectKind.ResourcePack)]);

        var metadata = Assert.Single(result).Value;
        Assert.Equal("file:///cache/cached-resource-pack.png", metadata.IconSource);
        Assert.Equal(ResourceProjectKind.ResourcePack, metadata.ProjectReference?.Kind);
        Assert.Empty(thumbnailService.DownloadedProjects);
        Assert.Single(thumbnailService.CachedProjects);
    }

    [Fact]
    public async Task MatchedProjectIconsReportIndividuallyBeforeWholeBatchCompletes()
    {
        Directory.CreateDirectory(TempRoot);
        var paths = Enumerable.Range(0, 2)
            .Select(index =>
            {
                var path = Path.Combine(TempRoot, $"resource-pack-{index}.zip");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"resource-pack-{index}"));
                return path;
            })
            .ToArray();
        var hashes = paths
            .Select(path => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
            .ToArray();
        var thumbnailService = new ControlledThumbnailService(2);
        using var httpClient = new HttpClient(new MultipleModrinthMatchHandler(hashes));
        var service = new LocalResourceCategoryEnrichmentService(
            new LauncherPathProvider(TempRoot),
            httpClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
            thumbnailService: thumbnailService);
        var firstIconReported = new TaskCompletionSource<LocalContentIconResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalContentIconResolution>(resolution =>
        {
            if (string.Equals(resolution.FullPath, paths[0], StringComparison.OrdinalIgnoreCase))
                firstIconReported.TrySetResult(resolution);
        });
        var candidates = paths
            .Select(path => new LocalResourceCategoryCandidate(path, ResourceProjectKind.ResourcePack))
            .ToArray();

        var enrichment = service.ResolveMetadataAsync(candidates, iconProgress: progress);
        await thumbnailService.AllDownloadsStarted.WaitAsync(TimeSpan.FromSeconds(5));
        thumbnailService.Release(0);
        var firstResolution = await firstIconReported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(enrichment.IsCompleted);
        Assert.Equal(paths[0], firstResolution.FullPath);
        Assert.Equal("file:///cache/project-0.png", firstResolution.IconSource);

        thumbnailService.ReleaseAll();
        var resolved = await enrichment.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, resolved.Count);
        Assert.All(resolved.Values, metadata => Assert.False(string.IsNullOrWhiteSpace(metadata.IconSource)));
    }

    [Theory]
    [InlineData(ResourceProjectKind.ResourcePack)]
    [InlineData(ResourceProjectKind.ShaderPack)]
    public async Task ContentMetadataCacheReportsFirstIconBeforeLaterFingerprintCompletes(
        ResourceProjectKind kind)
    {
        Directory.CreateDirectory(TempRoot);
        var oldPaths = CreatePaths("old");
        var hashes = oldPaths
            .Select(path => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
            .ToArray();
        var pathProvider = new LauncherPathProvider(TempRoot);
        using (var seedClient = new HttpClient(new MultipleModrinthMatchHandler(hashes)))
        {
            var seedService = new LocalResourceCategoryEnrichmentService(
                pathProvider,
                seedClient,
                logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
                thumbnailService: new RecordingThumbnailService(
                    downloadedSource: "file:///cache/seed.png"));
            var seeded = await seedService.ResolveMetadataAsync(oldPaths
                .Select(path => new LocalResourceCategoryCandidate(path, kind))
                .ToArray());
            Assert.Equal(2, seeded.Count);
        }

        var newPaths = CreatePaths("new");
        var secondFingerprintStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondFingerprint = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fingerprintService = new LocalFileFingerprintService(path =>
        {
            var stream = File.OpenRead(path);
            return string.Equals(path, newPaths[1], StringComparison.OrdinalIgnoreCase)
                ? new BlockingReadStream(stream, secondFingerprintStarted, releaseSecondFingerprint)
                : stream;
        });
        var thumbnailService = new RecordingThumbnailService(
            cachedSource: "file:///cache/reused.png");
        using var rejectingClient = new HttpClient(new RejectingHandler());
        var service = new LocalResourceCategoryEnrichmentService(
            pathProvider,
            rejectingClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
            thumbnailService: thumbnailService,
            fingerprintService: fingerprintService);
        var firstReported = new TaskCompletionSource<LocalContentIconResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalContentIconResolution>(resolution =>
        {
            if (string.Equals(resolution.FullPath, newPaths[0], StringComparison.OrdinalIgnoreCase))
                firstReported.TrySetResult(resolution);
        });

        var enrichment = service.ResolveMetadataAsync(
            newPaths.Select(path => new LocalResourceCategoryCandidate(path, kind)).ToArray(),
            iconProgress: progress);
        await secondFingerprintStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = await firstReported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(newPaths[0], first.FullPath);
        Assert.Equal("file:///cache/reused.png", first.IconSource);
        Assert.False(enrichment.IsCompleted);

        releaseSecondFingerprint.TrySetResult();
        var resolved = await enrichment.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, resolved.Count);
        Assert.Empty(thumbnailService.DownloadedProjects);
        return;

        string[] CreatePaths(string prefix) => Enumerable.Range(0, 2)
            .Select(index =>
            {
                var path = Path.Combine(TempRoot, $"{prefix}-{kind}-{index}.zip");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"shared-{kind}-content-{index}"));
                return path;
            })
            .ToArray();
    }

    [Fact]
    public async Task FirstProjectBatchReportsBeforeLaterResourceFingerprintCompletes()
    {
        Directory.CreateDirectory(TempRoot);
        var paths = Enumerable.Range(0, LocalResourceCategoryEnrichmentService.ProviderBatchSize + 1)
            .Select(index =>
            {
                var path = Path.Combine(TempRoot, $"batched-resource-{index}.zip");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"batched-resource-content-{index}"));
                return path;
            })
            .ToArray();
        var hashes = paths
            .Select(path => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
            .ToArray();
        var laterFingerprintStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLaterFingerprint = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fingerprintService = new LocalFileFingerprintService(path =>
        {
            var stream = File.OpenRead(path);
            return string.Equals(path, paths[^1], StringComparison.OrdinalIgnoreCase)
                ? new BlockingReadStream(stream, laterFingerprintStarted, releaseLaterFingerprint)
                : stream;
        });
        var thumbnailService = new ControlledThumbnailService(
            paths.Length,
            LocalResourceCategoryEnrichmentService.ProviderBatchSize);
        using var httpClient = new HttpClient(new MultipleModrinthMatchHandler(hashes));
        var service = new LocalResourceCategoryEnrichmentService(
            new LauncherPathProvider(TempRoot),
            httpClient,
            logger: NullLogger<LocalResourceCategoryEnrichmentService>.Instance,
            thumbnailService: thumbnailService,
            fingerprintService: fingerprintService);
        var firstReported = new TaskCompletionSource<LocalContentIconResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalContentIconResolution>(resolution =>
        {
            if (string.Equals(resolution.FullPath, paths[0], StringComparison.OrdinalIgnoreCase))
                firstReported.TrySetResult(resolution);
        });

        var enrichment = service.ResolveMetadataAsync(
            paths.Select(path => new LocalResourceCategoryCandidate(
                path,
                ResourceProjectKind.ResourcePack)).ToArray(),
            iconProgress: progress);
        await laterFingerprintStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await thumbnailService.AllDownloadsStarted.WaitAsync(TimeSpan.FromSeconds(5));
        thumbnailService.Release(0);
        await firstReported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(enrichment.IsCompleted);

        releaseLaterFingerprint.TrySetResult();
        thumbnailService.ReleaseAll();
        var resolved = await enrichment.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(paths.Length, resolved.Count);
    }

    private sealed class ModrinthMatchHandler(string sha1, string? slug = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/v2/version_files" => $"{{\"{sha1}\":{{\"project_id\":\"shader-project\"}}}}",
                "/v2/projects" => $$"""[{"id":"shader-project","slug":"{{slug}}","icon_url":"https://cdn.example/shader.png","categories":["fabric","fantasy","realistic","fantasy"]}]""",
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Directory resources must not trigger remote matching.");
    }

    private sealed class TestCurseForgeKeyResolver : ICurseForgeApiKeyResolver
    {
        public Task<string?> TryResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("test-key");
    }

    private sealed class CurseForgeMatchHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string json;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v2/version_files":
                    json = "{}";
                    break;
                case "/v1/fingerprints/432":
                    using (var document = JsonDocument.Parse(
                               await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        var fingerprint = document.RootElement.GetProperty("fingerprints")[0].GetInt64();
                        json = JsonSerializer.Serialize(new
                        {
                            data = new { exactMatches = new[] { new { id = 123, fileFingerprint = fingerprint } } }
                        });
                    }
                    break;
                case "/v1/mods":
                    json = """{"data":[{"id":123,"slug":"sodium","categories":[]}]}""";
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class MultipleModrinthMatchHandler(IReadOnlyList<string> hashes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            object response = request.RequestUri!.AbsolutePath switch
            {
                "/v2/version_files" => hashes
                    .Select((hash, index) => new KeyValuePair<string, object>(
                        hash,
                        new Dictionary<string, string> { ["project_id"] = $"project-{index}" }))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                "/v2/projects" => hashes
                    .Select((_, index) => new Dictionary<string, object>
                    {
                        ["id"] = $"project-{index}",
                        ["icon_url"] = $"https://cdn.example/project-{index}.png",
                        ["categories"] = new[] { "realistic" }
                    })
                    .ToArray(),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RecordingThumbnailService(
        string? cachedSource = null,
        string? downloadedSource = null) : IResourceThumbnailService
    {
        public List<ResourceProject> CachedProjects { get; } = [];

        public List<ResourceProject> DownloadedProjects { get; } = [];

        public string? TryGetCachedThumbnailSource(ResourceProject project)
        {
            CachedProjects.Add(project);
            return cachedSource;
        }

        public Task<string?> GetOrCreateThumbnailSourceAsync(
            ResourceProject project,
            CancellationToken cancellationToken = default)
        {
            DownloadedProjects.Add(project);
            return Task.FromResult(downloadedSource);
        }
    }

    private sealed class ControlledThumbnailService(
        int count,
        int? expectedStartedCount = null) : IResourceThumbnailService
    {
        private readonly TaskCompletionSource allDownloadsStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource[] releases = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private int startedCount;

        public Task AllDownloadsStarted => allDownloadsStarted.Task;

        public string? TryGetCachedThumbnailSource(ResourceProject project) => null;

        public async Task<string?> GetOrCreateThumbnailSourceAsync(
            ResourceProject project,
            CancellationToken cancellationToken = default)
        {
            var index = int.Parse(project.ProjectId.AsSpan("project-".Length));
            if (Interlocked.Increment(ref startedCount) == (expectedStartedCount ?? count))
                allDownloadsStarted.TrySetResult();
            await releases[index].Task.WaitAsync(cancellationToken);
            return $"file:///cache/project-{index}.png";
        }

        public void Release(int index) => releases[index].TrySetResult();

        public void ReleaseAll()
        {
            foreach (var release in releases)
                release.TrySetResult();
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class BlockingReadStream(
        Stream inner,
        TaskCompletionSource started,
        TaskCompletionSource release) : Stream
    {
        private int blocked;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

}
