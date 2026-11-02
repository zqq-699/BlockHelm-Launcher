/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Net;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class StaggeredLoaderLookupTests
{
    private const string FabricUrl = "https://meta.fabricmc.net/v2/versions/loader/1.21.1";
    private const string QuiltUrl = "https://meta.quiltmc.org/v3/versions/loader/1.21.1";
    private const string Official = "meta.fabricmc.net";
    private const string Mirror = "bmclapi2.bangbang93.com";

    [Theory]
    [InlineData(DownloadSourcePreference.Official, Official)]
    [InlineData(DownloadSourcePreference.BmclApi, Mirror)]
    public async Task FastPreferredResultDoesNotStartFallback(DownloadSourcePreference preference, string preferred)
    {
        var handler = new ScriptedHandler();
        handler.Reply(preferred, HttpStatusCode.OK, "version");
        using var client = new HttpClient(handler);
        var delay = NewSignal();
        var result = await Lookup(CreateExecutor(client), preference, delay.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("version", result.Value);
        Assert.Equal([preferred], handler.Requests.ToArray());
    }

    [Fact]
    public async Task SlowPreferredSourceStartsFallbackAndCancelsLosingRequest()
    {
        var handler = new ScriptedHandler();
        handler.Reply(Mirror, HttpStatusCode.OK, "mirror version");
        using var client = new HttpClient(handler);
        var delay = NewSignal();
        var lookup = Lookup(CreateExecutor(client), DownloadSourcePreference.Official, delay.Task);
        await handler.Started(Official).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Mirror, handler.Requests);
        delay.SetResult();
        var result = await lookup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("mirror version", result.Value);
        await handler.Canceled(Official).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PreferredSourceCanWinAfterFallbackStarts()
    {
        var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        var delay = NewSignal();
        var lookup = Lookup(CreateExecutor(client), DownloadSourcePreference.Official, delay.Task);
        await handler.Started(Official).WaitAsync(TimeSpan.FromSeconds(5));
        delay.SetResult();
        await handler.Started(Mirror).WaitAsync(TimeSpan.FromSeconds(5));
        handler.Reply(Official, HttpStatusCode.OK, "official version");
        Assert.Equal("official version", (await lookup.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        await handler.Canceled(Mirror).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(DownloadSourcePreference.Official, "files.minecraftforge.net", "<html>https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar</html>")]
    [InlineData(DownloadSourcePreference.BmclApi, Mirror, "[{\"version\":\"47.2.0\"}]")]
    public async Task ForgeProviderParsesPreferredSourceFormat(DownloadSourcePreference preference, string host, string body)
    {
        var handler = new ScriptedHandler();
        handler.Reply(host, HttpStatusCode.OK, body);
        using var client = new HttpClient(handler);
        var provider = new ForgeLoaderProvider(client);
        var versions = await provider.GetLoaderVersionsAsync("1.20.1", preference).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("47.2.0", Assert.Single(versions).Version);
        Assert.Equal([host], handler.Requests.ToArray());
    }

    [Fact]
    public async Task ForgeProviderParsesMirrorAfterOfficialNoResult()
    {
        var handler = new ScriptedHandler();
        handler.Reply("files.minecraftforge.net", HttpStatusCode.NotFound, "");
        handler.Reply(Mirror, HttpStatusCode.OK, "[{\"version\":\"47.2.0\"}]");
        using var client = new HttpClient(handler);
        var provider = new ForgeLoaderProvider(client);
        var versions = await provider.GetLoaderVersionsAsync("1.20.1", DownloadSourcePreference.Official)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("47.2.0", Assert.Single(versions).Version);
        Assert.Contains(Mirror, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "empty")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    public async Task PreferredFailureOrEmptyStartsFallbackImmediately(HttpStatusCode status, string content)
    {
        var handler = new ScriptedHandler();
        handler.Reply(Official, status, content);
        handler.Reply(Mirror, HttpStatusCode.OK, "mirror version");
        using var client = new HttpClient(handler);
        var delay = NewSignal();
        var result = await Lookup(CreateExecutor(client, attempts: 2), DownloadSourcePreference.Official, delay.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("mirror version", result.Value);
        Assert.False(delay.Task.IsCompleted);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task AllEmptyOrMixedEmptyAndFailureAreDistinguished(HttpStatusCode mirrorStatus, bool shouldFail)
    {
        var handler = new ScriptedHandler();
        handler.Reply(Official, HttpStatusCode.NotFound, "");
        handler.Reply(Mirror, mirrorStatus, "");
        using var client = new HttpClient(handler);
        var lookup = Lookup(CreateExecutor(client), DownloadSourcePreference.Official, NewSignal().Task);
        if (shouldFail)
            await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() => lookup);
        else
            Assert.False((await lookup).Found);
    }

    [Fact]
    public async Task CancellationStopsPreferredWithoutStartingFallback()
    {
        var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var lookup = Lookup(CreateExecutor(client), DownloadSourcePreference.Official, NewSignal().Task, cancellation.Token);
        await handler.Started(Official).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        await handler.Canceled(Official).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Mirror, handler.Requests);
    }

    [Fact]
    public async Task CancellationStopsBothSourcesAfterFallbackStarts()
    {
        var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var delay = NewSignal();
        var lookup = Lookup(CreateExecutor(client), DownloadSourcePreference.Official, delay.Task, cancellation.Token);
        await handler.Started(Official).WaitAsync(TimeSpan.FromSeconds(5));
        delay.SetResult();
        await handler.Started(Mirror).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        await Task.WhenAll(handler.Canceled(Official), handler.Canceled(Mirror)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WinnerDoesNotWaitForSourceThatIgnoresCancellation()
    {
        var handler = new ScriptedHandler(ignoreCancellation: true);
        using var client = new HttpClient(handler);
        var delay = NewSignal();
        var lookup = Lookup(CreateExecutor(client), DownloadSourcePreference.Official, delay.Task);
        await handler.Started(Official).WaitAsync(TimeSpan.FromSeconds(5));
        delay.SetResult();
        await handler.Started(Mirror).WaitAsync(TimeSpan.FromSeconds(5));
        handler.Reply(Mirror, HttpStatusCode.OK, "mirror version");
        try
        {
            Assert.Equal("mirror version", (await lookup.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        }
        finally
        {
            handler.Reply(Official, HttpStatusCode.OK, "late version");
        }
    }

    [Fact]
    public async Task SingleSourceDoesNotStartAnIdenticalFallback()
    {
        var handler = new ScriptedHandler();
        handler.Reply("meta.quiltmc.org", HttpStatusCode.OK, "quilt version");
        using var client = new HttpClient(handler);
        var result = await Lookup(CreateExecutor(client), DownloadSourcePreference.BmclApi, NewSignal().Task, url: QuiltUrl, category: "Quilt")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("quilt version", result.Value);
        Assert.Single(handler.Requests);
    }

    private static Task<DownloadLookupResult<string>> Lookup(MinecraftDownloadRequestExecutor executor,
        DownloadSourcePreference preference, Task delay, CancellationToken token = default, string url = FabricUrl, string category = "Fabric") =>
        executor.ExecuteStaggeredLookupAsync(url, preference, category, async (context, ct) =>
        {
            var value = await context.Response.Content.ReadAsStringAsync(ct);
            if (value == "empty")
                throw new DownloadNoResultException("No versions.");
            return value;
        }, status => status is HttpStatusCode.NotFound, token, ct => delay.WaitAsync(ct));

    private static MinecraftDownloadRequestExecutor CreateExecutor(HttpClient client, int attempts = 1) => new(client,
        retryOptions: new DownloadRetryOptions { MaxAttemptsPerSource = attempts, RetryDelay = TimeSpan.FromSeconds(30) },
        nextRetryJitter: () => 0);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ScriptedHandler(bool ignoreCancellation = false) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<HttpResponseMessage>> responses = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> starts = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> cancellations = new();
        public ConcurrentQueue<string> Requests { get; } = new();

        public void Reply(string host, HttpStatusCode status, string text) =>
            responses.GetOrAdd(host, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(new HttpResponseMessage(status) { Content = new StringContent(text) });
        public Task Started(string host) => starts.GetOrAdd(host, _ => NewSignal()).Task;
        public Task Canceled(string host) => cancellations.GetOrAdd(host, _ => NewSignal()).Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Requests.Enqueue(host);
            starts.GetOrAdd(host, _ => NewSignal()).TrySetResult();
            try
            {
                return await responses.GetOrAdd(host, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellations.GetOrAdd(host, _ => NewSignal()).TrySetResult();
                throw;
            }
        }
    }
}
