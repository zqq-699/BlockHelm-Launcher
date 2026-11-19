/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class DownloadRequestExecutorLeaseTests
{
    [Fact]
    public async Task DownloadRequestExecutorCancellationReleasesMetadataSlots()
    {
        var limiter = new ImportConcurrencyLimiter();
        var handler = new BlockingRequestHandler(expectedRequestCount: 2);
        using var httpClient = new HttpClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            limiter: limiter,
            category: DownloadConcurrencyCategory.Metadata,
            retryOptions: new DownloadRetryOptions
            {
                ResponseHeadersTimeout = TimeSpan.FromMinutes(1)
            },
            hostConcurrencyController: CreateHostController());
        using var cancellation = new CancellationTokenSource();

        var requests = Enumerable.Range(0, 2)
            .Select(index => executor.ExecuteAsync(
                $"https://example.test/metadata/{index}.json",
                DownloadSourcePreference.Official,
                categoryHint: "Mojang",
                static (_, _) => Task.FromResult(true),
                cancellation.Token))
            .ToArray();

        await handler.WaitForExpectedRequestsAsync();
        cancellation.Cancel();

        foreach (var request in requests)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);

        await using var leases = await SlotLeaseSet.AcquireAsync(
            limiter.AcquireMetadataSlotAsync,
            slotCount: 2);
    }

    private static DownloadHostConcurrencyController CreateHostController() => new(
        maximumJitter: TimeSpan.Zero,
        nextJitter: () => 0,
        delayAsync: static (_, _) => ValueTask.CompletedTask);
}

public sealed class DownloadSourceRoutingLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPreferredMetadataUsesValidatedFallbackAndCancelsOtherRequest(bool invalidMirror)
    {
        var fallbackArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DownloadSourceRoutingHttpMessageHandler(
            DownloadSourcePreference.Official, DownloadConcurrencyCategory.Metadata,
            new RoutingHandler(async (request, token) =>
            {
                if (request.RequestUri!.Host == "meta.fabricmc.net")
                {
                    try
                    {
                        if (!invalidMirror) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        else await fallbackArrived.Task.WaitAsync(token);
                    }
                    catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                }
                else fallbackArrived.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(invalidMirror && request.RequestUri.Host != "meta.fabricmc.net" ? "not-json" : "{\"ok\":true}")
                };
            }), hostConcurrencyController: CreateHostController()));
        var result = await httpClient.GetStringAsync("https://meta.fabricmc.net/v2/test.json").WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal("{\"ok\":true}", result);
        Assert.True(fallbackArrived.Task.IsCompletedSuccessfully);
        if (!invalidMirror) await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DownloadSourceRoutingCancellationReleasesRuntimeSlots()
    {
        var limiter = new ImportConcurrencyLimiter();
        var handler = new BlockingRequestHandler(expectedRequestCount: 8);
        using var httpClient = new HttpClient(new DownloadSourceRoutingHttpMessageHandler(
            DownloadSourcePreference.Official,
            DownloadConcurrencyCategory.Runtime,
            handler,
            limiter: limiter,
            retryOptions: new DownloadRetryOptions
            {
                ResponseHeadersTimeout = TimeSpan.FromMinutes(1)
            },
            hostConcurrencyController: CreateHostController()));
        using var cancellation = new CancellationTokenSource();

        var requests = Enumerable.Range(0, 8)
            .Select(index => httpClient.GetAsync($"https://example.test/runtime/{index}.json", cancellation.Token))
            .ToArray();

        await handler.WaitForExpectedRequestsAsync();
        cancellation.Cancel();

        foreach (var request in requests)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);

        await using var leases = await SlotLeaseSet.AcquireAsync(
            limiter.AcquireRuntimeDownloadSlotAsync,
            slotCount: 8);
    }

    private static DownloadHostConcurrencyController CreateHostController() => new(
        maximumJitter: TimeSpan.Zero,
        nextJitter: () => 0,
        delayAsync: static (_, _) => ValueTask.CompletedTask);

    private sealed class RoutingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}

internal sealed class BlockingRequestHandler(int expectedRequestCount) : HttpMessageHandler
{
    private readonly TaskCompletionSource expectedRequestsArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int requestCount;

    public Task WaitForExpectedRequestsAsync()
    {
        return expectedRequestsArrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref requestCount) == expectedRequestCount)
            expectedRequestsArrived.TrySetResult();

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent("ok"u8.ToArray())
        };
    }
}

internal sealed class ThrowingRequestHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("download failed");
    }
}

internal sealed class SlotLeaseSet(IReadOnlyList<IImportConcurrencyLease> leases) : IAsyncDisposable
{
    public static async Task<SlotLeaseSet> AcquireAsync(
        Func<CancellationToken, ValueTask<IImportConcurrencyLease>> acquireAsync,
        int slotCount)
    {
        var leases = await Task.WhenAll(
                Enumerable.Range(0, slotCount)
                    .Select(_ => acquireAsync(CancellationToken.None).AsTask()))
            .WaitAsync(TimeSpan.FromSeconds(2));

        return new SlotLeaseSet(leases);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lease in leases)
            await lease.DisposeAsync();
    }
}
