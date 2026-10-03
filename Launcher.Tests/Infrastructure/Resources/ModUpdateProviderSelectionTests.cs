/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Resources;

namespace Launcher.Tests.Infrastructure.Resources;

public sealed class ModUpdateProviderSelectionTests
{
    [Fact]
    public void StableReleaseIsPreferredOverNewerPrerelease()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var versions = new[]
        {
            CreateVersion("beta", "beta", current.AddDays(3)),
            CreateVersion("release", "release", current.AddDays(2)),
            CreateVersion("old", "release", current.AddDays(-1)),
            CreateVersion("current", "release", current.AddDays(4))
        };

        var selected = ModrinthModUpdateProvider.SelectTarget(versions, "current", current);

        Assert.Equal("release", selected?.VersionId);
    }

    [Fact]
    public void PrereleaseIsIgnoredWhenNoReleaseExists()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var versions = new[]
        {
            CreateVersion("alpha", "alpha", current.AddDays(2)),
            CreateVersion("beta", "beta", current.AddDays(3))
        };

        var selected = ModrinthModUpdateProvider.SelectTarget(versions, "current", current);

        Assert.Null(selected);
    }

    [Fact]
    public async Task ModrinthFingerprintLookupUsesBatchesOfAtMostFifty()
    {
        var handler = new FingerprintBatchHandler("{}");
        using var httpClient = new HttpClient(handler);
        var provider = new ModrinthModUpdateProvider(
            new ModrinthApiClient(
                httpClient,
                new ImportConcurrencyLimiter(),
                logger: null,
                CreateController()),
            new UnusedCatalogService());
        var localFiles = CreateLocalFiles(51);
        var completedFiles = new RecordingProgress<string>();

        var results = await provider.FindUpdatesAsync(
            CreateInstance(),
            localFiles,
            completedFileProgress: completedFiles);

        Assert.Equal([50, 1], handler.BatchSizes);
        Assert.Equal(51, results.Count);
        Assert.Equal(
            localFiles.Select(item => item.FullPath).Order(StringComparer.OrdinalIgnoreCase),
            completedFiles.Values.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(results.Values, result =>
        {
            Assert.True(result.IsAvailable);
            Assert.False(result.IsRecognized);
        });
    }

    [Fact]
    public async Task CurseForgeFingerprintLookupUsesBatchesOfAtMostFifty()
    {
        var handler = new FingerprintBatchHandler("""{"data":{"exactMatches":[]}}""");
        using var httpClient = new HttpClient(handler);
        var provider = new CurseForgeModUpdateProvider(
            new CurseForgeApiClient(
                httpClient,
                new ImportConcurrencyLimiter(),
                logger: null,
                CreateController()),
            new StubKeyResolver(),
            new UnusedCatalogService());
        var localFiles = CreateLocalFiles(51);
        var completedFiles = new RecordingProgress<string>();

        var results = await provider.FindUpdatesAsync(
            CreateInstance(),
            localFiles,
            completedFileProgress: completedFiles);

        Assert.Equal([50, 1], handler.BatchSizes);
        Assert.Equal(51, results.Count);
        Assert.Equal(
            localFiles.Select(item => item.FullPath).Order(StringComparer.OrdinalIgnoreCase),
            completedFiles.Values.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(results.Values, result =>
        {
            Assert.True(result.IsAvailable);
            Assert.False(result.IsRecognized);
        });
    }

    private static ResourceProjectVersion CreateVersion(
        string id,
        string type,
        DateTimeOffset publishedAt) => new()
    {
        VersionId = id,
        VersionType = type,
        PublishedAt = publishedAt
    };

    private static GameInstance CreateInstance() => new()
    {
        MinecraftVersion = "1.20.1",
        Loader = LoaderKind.Fabric,
        InstanceDirectory = "instance"
    };

    private static ModUpdateLocalFileState[] CreateLocalFiles(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new ModUpdateLocalFileState(
                $"mod-{index}.jar",
                index.ToString("x40"),
                index,
                1,
                1,
                true))
            .ToArray();

    private static DownloadHostConcurrencyController CreateController() => new(
        maximumJitter: TimeSpan.Zero,
        nextJitter: () => 0,
        delayAsync: static (_, _) => ValueTask.CompletedTask);

    private sealed class FingerprintBatchHandler(string responseJson) : HttpMessageHandler
    {
        public List<int> BatchSizes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var values = root.TryGetProperty("hashes", out var hashes)
                ? hashes
                : root.GetProperty("fingerprints");
            BatchSizes.Add(values.GetArrayLength());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(responseJson)
            };
        }
    }

    private sealed class StubKeyResolver : ICurseForgeApiKeyResolver
    {
        public Task<string?> TryResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("test-key");
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class UnusedCatalogService : IResourceCatalogService
    {
        public Task<ResourceCatalogSearchResult> SearchModsAsync(
            ResourceCatalogSearchRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(
            ResourceProjectVersionsRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> InstallProjectVersionAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> DownloadProjectVersionAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ProjectVersionDownloadExistsAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ProjectVersionInstallExistsAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
