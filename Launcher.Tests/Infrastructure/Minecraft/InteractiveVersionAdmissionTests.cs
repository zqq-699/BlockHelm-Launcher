/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class InteractiveVersionAdmissionTests
{
    [Fact]
    public async Task VersionQueryRunsWhenFileBudgetIsFullWhileOrdinaryMetadataWaits()
    {
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(1);
        using var occupied = await limiter.AcquireRuntimeDownloadSlotAsync();
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var hosts = new DownloadHostConcurrencyController();
        var ordinary = new MinecraftDownloadRequestExecutor(client, limiter: limiter,
            category: DownloadConcurrencyCategory.Metadata, hostConcurrencyController: hosts);
        var interactive = new MinecraftDownloadRequestExecutor(client, limiter: limiter,
            category: DownloadConcurrencyCategory.InteractiveVersionCatalog, hostConcurrencyController: hosts);
        using var cancellation = new CancellationTokenSource();

        var waiting = GetAsync(ordinary, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal("ok", await GetAsync(interactive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, handler.Calls);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, limiter.DownloadSnapshot.ActiveCount);
    }

    [Fact]
    public async Task InteractiveRequestsHaveBoundedConcurrencyAndQueuedRequestsCanCancel()
    {
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client,
            category: DownloadConcurrencyCategory.InteractiveVersionCatalog,
            hostConcurrencyController: new DownloadHostConcurrencyController());
        var first = GetAsync(executor, CancellationToken.None);
        var second = GetAsync(executor, CancellationToken.None);
        try
        {
            await handler.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var third = GetAsync(executor, cancellation.Token);
            Assert.False(third.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            handler.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Task<string> GetAsync(MinecraftDownloadRequestExecutor executor, CancellationToken token) =>
        executor.ExecuteAsync("https://example.test/versions", DownloadSourcePreference.Official, "ThirdParty",
            (context, ct) => context.Response.Content.ReadAsStringAsync(ct), token);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 2)
                BothStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
}
