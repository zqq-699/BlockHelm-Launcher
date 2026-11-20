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
            if (mode == "file")
                Assert.Equal("bytes=0-", request.Headers.Range?.ToString());
            else
                Assert.Null(request.Headers.Range);
            return Task.FromResult(Response("good"));
        });
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, automaticSourceSelector: selector,
            hostConcurrencyController: new DownloadHostConcurrencyController(),
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
            // A verified file of unknown size may probe the selected mirror.
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
            hostConcurrencyController: new DownloadHostConcurrencyController());
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
    public async Task EntryHealthExpiresAndKeepsTheDegradedFallback()
    {
        var clock = new Clock();
        using var operation = new MinecraftDownloadOperationContext(TempRoot);
        var health = new DownloadHostHealthTracker(clock);
        const string url = "https://maven.neoforged.net/releases/net/neoforged/test.jar";
        var official = MinecraftDownloadSourceResolver.EnumerateRequests(url, DownloadSourcePreference.Official).First();
        for (var i = 0; i < 3; i++) health.RecordFailure(official.ResolvedSourceKind, "maven.neoforged.net", DownloadFailureReason.ResponseHeadersTimeout);
        Assert.True(health.ShouldAvoid(official.ResolvedSourceKind, "maven.neoforged.net"));
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.Host == "bmclapi2.bangbang93.com" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent("good") }));
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, hostHealthTracker: health,
            retryOptions: new DownloadRetryOptions { MaxAttemptsPerSource = 1 },
            hostConcurrencyController: new DownloadHostConcurrencyController());
        Assert.Equal("good", await executor.ExecuteAsync(url, DownloadSourcePreference.Official, null,
            (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        Assert.Equal(new[] { "bmclapi2.bangbang93.com", "maven.neoforged.net" }, handler.Requests.Select(u => u.Host));
        Assert.False(health.ShouldAvoid(official.ResolvedSourceKind, "maven.neoforged.net"));
        for (var i = 0; i < 3; i++) health.RecordFailure(official.ResolvedSourceKind, "maven.neoforged.net", DownloadFailureReason.Network);
        clock.Advance(TimeSpan.FromSeconds(46));
        Assert.False(health.ShouldAvoid(official.ResolvedSourceKind, "maven.neoforged.net"));
        health.RecordFailure(official.ResolvedSourceKind, "maven.neoforged.net", DownloadFailureReason.Network);
        Assert.False(health.ShouldAvoid(official.ResolvedSourceKind, "maven.neoforged.net"));
        clock.Advance(TimeSpan.FromSeconds(46));
        health.RecordFailure(official.ResolvedSourceKind, "maven.neoforged.net", DownloadFailureReason.Network);
        health.RecordFailure(official.ResolvedSourceKind, "maven.neoforged.net", DownloadFailureReason.Network);
        Assert.False(health.ShouldAvoid(official.ResolvedSourceKind, "maven.neoforged.net"));
        for (var i = 0; i < 3; i++) operation.HostHealth.RecordFailure("test", "test", DownloadFailureReason.Network);
        operation.Dispose();
        Assert.False(operation.HostHealth.ShouldAvoid("test", "test"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectedHostFailuresDoNotDeprioritizeTheCandidateAcrossFiles(bool timeout)
    {
        var health = new DownloadHostHealthTracker();
        var mirrorFails = false;
        var cdnRecovers = false;
        using var handler = new Handler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            if (host == "maven.neoforged.net")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://cdn.example.test" + request.RequestUri.AbsolutePath) } });
            if (host == "cdn.example.test")
            {
                if (timeout && !cdnRecovers)
                    throw new DownloadTimeoutException(DownloadFailureReason.ResponseHeadersTimeout, "simulated timeout");
                return Task.FromResult(new HttpResponseMessage(cdnRecovers ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("good") });
            }
            return Task.FromResult(new HttpResponseMessage(mirrorFails ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent("good") });
        });
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, hostHealthTracker: health,
            retryOptions: new DownloadRetryOptions { MaxFileAttemptsPerSource = 1 },
            hostConcurrencyController: new DownloadHostConcurrencyController());
        var hash = Convert.ToHexString(SHA1.HashData("good"u8));
        async Task Download(int index) => await executor.DownloadFileAsync(
            $"https://maven.neoforged.net/releases/net/neoforged/test/{index}/test.jar",
            DownloadSourcePreference.Official, "NeoForge", Path.Combine(TempRoot, $"{index}.jar"), hash, 4, CancellationToken.None);
        for (var i = 0; i < 3; i++) await Download(i);
        var before = handler.Requests.Count;
        await Download(3);
        Assert.Equal(new[] { "maven.neoforged.net", "cdn.example.test", "bmclapi2.bangbang93.com" },
            handler.Requests.Skip(before).Select(uri => uri.Host));
        mirrorFails = true;
        cdnRecovers = true;
        before = handler.Requests.Count;
        await Download(4);
        Assert.Equal(new[] { "maven.neoforged.net", "cdn.example.test" },
            handler.Requests.Skip(before).Select(uri => uri.Host));
        Assert.Equal("good", await File.ReadAllTextAsync(Path.Combine(TempRoot, "4.jar")));
        Assert.False(health.ShouldAvoid("NeoForgeOfficial", "maven.neoforged.net"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameHostAndMultiHopRedirectFailuresDoNotDegradeEntry(bool multiHop)
    {
        var health = new DownloadHostHealthTracker();
        using var handler = new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "maven.neoforged.net" && uri.AbsolutePath.EndsWith("test.jar"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri(multiHop ? "https://middle.example/next" : "https://maven.neoforged.net/node") } });
            if (uri.Host == "middle.example")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://cdn.example/node") } });
            if (uri.AbsolutePath == "/node")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(Response("good"));
        });
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, hostHealthTracker: health,
            retryOptions: new DownloadRetryOptions { MaxFileAttemptsPerSource = 1 },
            hostConcurrencyController: new DownloadHostConcurrencyController());
        for (var i = 0; i < 4; i++)
            await executor.DownloadFileAsync($"https://maven.neoforged.net/releases/test/{i}/test.jar",
                DownloadSourcePreference.Official, "NeoForge", Path.Combine(TempRoot, $"hop-{i}.jar"),
                Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
        Assert.False(health.ShouldAvoid("NeoForgeOfficial", "maven.neoforged.net"));
        Assert.Equal(4, handler.Requests.Count(uri => uri.Host == "maven.neoforged.net" && uri.AbsolutePath.EndsWith("test.jar")));
    }

    [Fact]
    public void ConcurrentEntryFailuresDegradeAndSuccessRestoresHealth()
    {
        var health = new DownloadHostHealthTracker();
        Parallel.For(0, 32, _ => health.RecordFailure("source", "entry", DownloadFailureReason.Network));
        Assert.True(health.ShouldAvoid("source", "entry"));
        health.RecordSuccess("source", "entry");
        Assert.False(health.ShouldAvoid("source", "entry"));
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
