/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class AutomaticDownloadSourceTests : TestTempDirectory
{
    [Theory]
    [InlineData("CN", "AU", "AU", DownloadSourcePreference.BmclApi, "Trace")]
    [InlineData("AU", "", "CN", DownloadSourcePreference.Official, "Trace")]
    [InlineData("", "", "CN", DownloadSourcePreference.BmclApi, "SystemRegion")]
    [InlineData("", "", "AU", DownloadSourcePreference.Official, "SystemRegion")]
    [InlineData("", "", null, DownloadSourcePreference.BmclApi, "UnknownRegionMirrorFallback")]
    [InlineData("XX", "not-country", null, DownloadSourcePreference.BmclApi, "UnknownRegionMirrorFallback")]
    public async Task RegionSelectionUsesTraceThenSystemThenMirror(string first, string second, string? system,
        DownloadSourcePreference expected, string reason)
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(
            "ip=private-ip\nloc=" + (request.RequestUri!.Host == "cloudflare.com" ? first : second)))));
        using var selector = new AutomaticDownloadSourceSelector(client, systemRegion: () => system);
        var selected = await selector.SelectAsync(NullLogger.Instance, CancellationToken.None);
        Assert.Equal(expected, selected.Preference);
        Assert.Equal(reason, selected.Reason);
    }

    [Fact]
    public async Task CoalescingCancellationCacheExpiryAndNetworkInvalidationDoNotPoisonTheResult()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock();
        var country = "AU";
        using var handler = new Handler(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response("loc=" + country);
        });
        using var client = new HttpClient(handler);
        using var selector = new AutomaticDownloadSourceSelector(client, clock, () => null);
        using var cancellation = new CancellationTokenSource();
        var first = selector.SelectAsync(NullLogger.Instance, cancellation.Token);
        await started.Task;
        var second = selector.SelectAsync(NullLogger.Instance, CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        Assert.Equal(DownloadSourcePreference.Official, (await second).Preference);
        await selector.SelectAsync(NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        clock.Advance(TimeSpan.FromMinutes(11));
        country = "CN";
        Assert.Equal(DownloadSourcePreference.BmclApi, (await selector.SelectAsync(NullLogger.Instance, CancellationToken.None)).Preference);
        Assert.Equal(4, handler.Requests.Count);
        selector.Invalidate();
        await selector.SelectAsync(NullLogger.Instance, CancellationToken.None);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task HangingAndOversizedProbesFallBackWithinTheirBudget()
    {
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "cloudflare.com") return Response(new string('x', 4097) + "\nloc=AU");
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response("loc=AU");
        }));
        using var selector = new AutomaticDownloadSourceSelector(client, systemRegion: () => null, budget: TimeSpan.FromMilliseconds(50));
        var result = await selector.SelectAsync(NullLogger.Instance, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DownloadSourcePreference.BmclApi, result.Preference);
        Assert.Equal("UnknownRegionMirrorFallback", result.Reason);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("region")]
    [InlineData("information-log")]
    [InlineData("warning-log")]
    public async Task UnexpectedDetectionFailuresUseCachedMirrorFallback(string stage)
    {
        var clock = new Clock();
        var losingProbeCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, token) =>
        {
            if (stage is "request" or "warning-log")
            {
                if (request.RequestUri!.Host == "cloudflare.com")
                    throw new InvalidOperationException("private-probe-content");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { if (token.IsCancellationRequested) losingProbeCanceled.TrySetResult(); }
            }
            return Response(stage == "region" ? "" : "loc=AU");
        });
        using var client = new HttpClient(handler);
        using var selector = new AutomaticDownloadSourceSelector(client, clock,
            () => throw new NotSupportedException("private-probe-content"));
        var logger = new FaultLogger(stage == "information-log" ? LogLevel.Information
            : stage == "warning-log" ? LogLevel.Warning : null);

        var result = await selector.SelectAsync(logger, CancellationToken.None);
        Assert.Equal(DownloadSourcePreference.BmclApi, result.Preference);
        Assert.Equal("UnknownRegionMirrorFallback", result.Reason);
        Assert.Equal(result, await selector.SelectAsync(logger, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        if (stage is "request" or "warning-log")
            await losingProbeCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("private-probe-content", StringComparison.Ordinal));
        if (stage != "warning-log") Assert.Contains(logger.Messages, message => message.Contains("FailureType=", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromMinutes(2));
        await selector.SelectAsync(logger, CancellationToken.None);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("staggered")]
    [InlineData("file")]
    [InlineData("unverified")]
    public async Task AutoIsResolvedBeforeRequestsAndSegmentedProbes(string mode)
    {
        using var probe = new HttpClient(new Handler((_, _) => Task.FromResult(Response("loc=CN"))));
        using var selector = new AutomaticDownloadSourceSelector(probe);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("bmclapi2.bangbang93.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Range);
            return Task.FromResult(Response("good"));
        });
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, automaticSourceSelector: selector,
            hostConcurrencyController: new DownloadHostConcurrencyController(maximumJitter: TimeSpan.Zero),
            minimumSegmentedDownloadSize: 2 * 1024 * 1024);
        const string url = "https://piston-data.mojang.com/v1/objects/example/client.jar";
        if (mode == "metadata")
            Assert.Equal("good", await executor.ExecuteAsync(url, DownloadSourcePreference.Auto, null,
                (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        else if (mode == "staggered")
            Assert.Equal("good", (await executor.ExecuteStaggeredLookupAsync(url, DownloadSourcePreference.Auto, null,
                (context, token) => context.Response.Content.ReadAsStringAsync(token), _ => false, CancellationToken.None)).Value);
        else
        {
            // Unknown size remains eligible for segmentation; preferred mirror must prevent the probe.
            var result = await executor.DownloadFileAsync(url, DownloadSourcePreference.Auto, null,
                Path.Combine(TempRoot, "download.jar"), mode == "unverified" ? null : Convert.ToHexString(SHA1.HashData("good"u8)),
                null, CancellationToken.None);
            Assert.Equal(DownloadSourcePreference.Auto, result.RequestedSourcePreference);
            Assert.Equal("good", await File.ReadAllTextAsync(Path.Combine(TempRoot, "download.jar")));
        }
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSourceAndVerifiedLocalFilesDoNotProbeTheRegion(bool localFile)
    {
        using var probe = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No region request is needed.")));
        using var selector = new AutomaticDownloadSourceSelector(probe);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("good"))));
        var executor = new MinecraftDownloadRequestExecutor(client, automaticSourceSelector: selector,
            hostConcurrencyController: new DownloadHostConcurrencyController(maximumJitter: TimeSpan.Zero));
        if (localFile)
        {
            Directory.CreateDirectory(TempRoot);
            var path = Path.Combine(TempRoot, "local.jar");
            await File.WriteAllTextAsync(path, "good");
            await executor.DownloadFileAsync("https://piston-data.mojang.com/v1/objects/example/client.jar", DownloadSourcePreference.Auto,
                null, path, Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
        }
        else await executor.ExecuteAsync("https://example.test/file", DownloadSourcePreference.Auto, null,
            (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None);
    }

    [Fact]
    public void StaticUrlConverterRejectsUnresolvedAuto()
    {
        Assert.Throws<ArgumentException>(() => MinecraftDownloadSourceResolver.EnumerateRequests(
            "https://example.test/file", DownloadSourcePreference.Auto).ToArray());
    }

    [Fact]
    public async Task OperationHealthIsStickyAcrossFilesAndKeepsTheDegradedFallback()
    {
        var clock = new Clock();
        using var operation = new MinecraftDownloadOperationContext(TempRoot);
        var health = new DownloadHostHealthTracker(sticky: true, clock);
        const string url = "https://maven.neoforged.net/releases/net/neoforged/test.jar";
        var official = MinecraftDownloadSourceResolver.EnumerateRequests(url, DownloadSourcePreference.Official).First();
        for (var i = 0; i < 3; i++) health.RecordFailure(official.ResolvedSourceKind, "maven.neoforged.net", DownloadFailureReason.ResponseHeadersTimeout);
        clock.Advance(TimeSpan.FromMinutes(20));
        health.RecordSuccess(official.ResolvedSourceKind, "maven.neoforged.net");
        Assert.True(health.ShouldAvoid(official.ResolvedSourceKind, "maven.neoforged.net"));
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.Host == "bmclapi2.bangbang93.com" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent("good") }));
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, hostHealthTracker: health,
            retryOptions: new DownloadRetryOptions { MaxAttemptsPerSource = 1 },
            hostConcurrencyController: new DownloadHostConcurrencyController(maximumJitter: TimeSpan.Zero));
        Assert.Equal("good", await executor.ExecuteAsync(url, DownloadSourcePreference.Official, null,
            (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        Assert.Equal(new[] { "bmclapi2.bangbang93.com", "maven.neoforged.net" }, handler.Requests.Select(u => u.Host));
        for (var i = 0; i < 3; i++) operation.HostHealth.RecordFailure("test", "test", DownloadFailureReason.Network);
        operation.Dispose();
        Assert.False(operation.HostHealth.ShouldAvoid("test", "test"));
    }

    [Fact]
    public async Task LoaderLibraryPhasesShareFailuresInsteadOfRetryingTheDeadHostForEveryFile()
    {
        Directory.CreateDirectory(TempRoot);
        var installer = Path.Combine(TempRoot, "installer.jar");
        using (var archive = ZipFile.Open(installer, ZipArchiveMode.Create))
            archive.CreateEntry("install_profile.json");
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "maven.neoforged.net")
                throw new DownloadTimeoutException(DownloadFailureReason.ResponseHeadersTimeout, "Simulated timeout");
            return Task.FromResult(Response("good"));
        });
        using var client = new HttpClient(handler);
        var libraries = Enumerable.Range(0, 4).Select(index => new ForgeInstallerLibrary(new ManagedLibraryArtifact(
            $"https://maven.neoforged.net/releases/net/neoforged/test/{index}/test.jar",
            $"net/neoforged/test/{index}/test.jar", null, "NeoForge",
            Convert.ToHexString(SHA1.HashData("good"u8)), 4), null)).ToArray();
        var plan = new ForgeInstallerPlan(libraries[..3], libraries[3..], [], new JsonArray());
        var game = Path.Combine(TempRoot, "game");
        using var operation = new MinecraftDownloadOperationContext(game);
        var service = new LoaderInstallerArtifactService(client);
        await service.MaterializePrerequisitesAsync(installer, plan, game, DownloadSourcePreference.Official, 0,
            CancellationToken.None, operation);
        await service.MaterializeRuntimeLibrariesAsync(installer, plan, game, DownloadSourcePreference.Official, 0,
            CancellationToken.None, operation);

        Assert.Equal(3, handler.Requests.Count(uri => uri.Host == "maven.neoforged.net"));
        Assert.Equal(4, handler.Requests.Count(uri => uri.Host == "bmclapi2.bangbang93.com"));
        foreach (var library in libraries)
            Assert.Equal("good", await File.ReadAllTextAsync(Path.Combine(game, "libraries", library.Artifact.RelativePath)));
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class FaultLogger(LogLevel? failingLevel) : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == failingLevel) throw new InvalidOperationException("private-probe-content");
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Enqueue(request.RequestUri!);
            return respond(request, token);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan time) => now += time;
    }
}
