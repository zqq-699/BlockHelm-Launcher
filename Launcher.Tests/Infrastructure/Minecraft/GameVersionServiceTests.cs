/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using System.Collections.Concurrent;
using System.Net;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class GameVersionServiceTests
{
    [Fact]
    public void VersionCatalogUsesInteractiveMetadataTimeouts()
    {
        var options = GameVersionService.VersionCatalogRetryOptions;

        Assert.Equal(1, options.MaxAttemptsPerSource);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ResponseHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), options.BodyIdleTimeout);
    }

    [Fact]
    public async Task VersionCatalogFailureTriesPreferredAndFallbackSourceOnce()
    {
        var handler = new FailingRequestHandler();
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new GameVersionService(httpClient);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetVersionsAsync(DownloadSourcePreference.BmclApi));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("bmclapi2.bangbang93.com", handler.Requests[0].Host);
        Assert.Equal("piston-meta.mojang.com", handler.Requests[1].Host);
    }

    [Fact]
    public async Task SlowPreferredManifestStartsMirrorAndCancelsPreferredRequest()
    {
        var handler = new CatalogHandler(hangPreferred: true);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new GameVersionService(httpClient);

        var versions = await service.GetVersionsAsync(DownloadSourcePreference.Official).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("1.21.1", Assert.Single(versions).Name);
        await handler.PreferredCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["piston-meta.mojang.com", "bmclapi2.bangbang93.com"], handler.Requests.ToArray());
    }

    [Theory]
    [InlineData("{\"versions\":[]}")]
    [InlineData("{\"versions\":[{\"id\":\"1.21.1\",\"type\":\"release\",\"releaseTime\":\"invalid\"}]}")]
    [InlineData("{\"versions\":[{\"id\":\"1.21.1\"}]}")]
    public async Task UnusablePreferredManifestFallsBackToValidMirror(string invalidManifest)
    {
        var handler = new CatalogHandler(preferredManifest: invalidManifest);
        using var httpClient = new HttpClient(handler);
        var service = new GameVersionService(httpClient);

        var versions = await service.GetVersionsAsync(DownloadSourcePreference.Official).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("1.21.1", Assert.Single(versions).Name);
        Assert.Equal(["piston-meta.mojang.com", "bmclapi2.bangbang93.com"], handler.Requests.ToArray());
    }

    [Fact]
    public async Task ValidPreferredManifestDoesNotStartMirror()
    {
        var handler = new CatalogHandler();
        using var httpClient = new HttpClient(handler);
        var service = new GameVersionService(httpClient);

        var versions = await service.GetVersionsAsync(DownloadSourcePreference.Official).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("1.21.1", Assert.Single(versions).Name);
        Assert.Equal(["piston-meta.mojang.com"], handler.Requests.ToArray());
    }

    private sealed class CatalogHandler(bool hangPreferred = false, string? preferredManifest = null) : HttpMessageHandler
    {
        private const string ValidManifest = "{\"versions\":[{\"id\":\"1.21.1\",\"type\":\"release\",\"releaseTime\":\"2024-08-08T12:00:00Z\"}]}";
        public ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource PreferredCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Requests.Enqueue(host);
            if (hangPreferred && host == "piston-meta.mojang.com")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { PreferredCanceled.TrySetResult(); throw; }
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(host == "piston-meta.mojang.com" ? preferredManifest ?? ValidManifest : ValidManifest)
            };
        }
    }

    private sealed class FailingRequestHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
        }
    }
}
