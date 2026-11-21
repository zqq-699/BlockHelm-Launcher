/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Channels;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class SegmentedDownloadTests : TestTempDirectory
{
    private const string DownloadUrl = "https://downloads.example.test/client.jar";
    private const int TestSegmentedDownloadSize = 2 * 1024 * 1024;
    private static readonly byte[] LargePayload = CreatePayload(
        TestSegmentedDownloadSize);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferredMirrorIsTriedBeforeOfficialRequest(bool mirrorFails)
    {
        var hosts = new List<string>();
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(mirrorFails && request.RequestUri.Host == "bmclapi2.bangbang93.com"
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
                : CreateFullResponse(request, LargePayload));
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "mirror-first.jar");
        await CreateExecutor(client).DownloadFileAsync("https://piston-data.mojang.com/v1/objects/example/client.jar",
            DownloadSourcePreference.BmclApi, "Mojang", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None);
        Assert.Equal(mirrorFails ? new[] { "bmclapi2.bangbang93.com", "piston-data.mojang.com" } : ["bmclapi2.bangbang93.com"], hosts);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("ignored")]
    [InlineData("invalid")]
    [InlineData("unsatisfiable")]
    [InlineData("changes-after-probe")]
    public async Task RedirectedMirrorUsesRangeOnlyWhenActualResponsesSupportIt(string behavior)
    {
        var requests = new ConcurrentQueue<(string Host, string? Range)>();
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            var host = request.RequestUri!.Host;
            var range = request.Headers.Range;
            requests.Enqueue((host, range?.ToString()));
            if (host == "bmclapi2.bangbang93.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri("https://cdn.example.test/mirror-file") }
                });
            if (host != "cdn.example.test")
                return Task.FromResult(CreateFullResponse(request, LargePayload));
            if (range is null || behavior == "ignored"
                || behavior == "changes-after-probe" && range.Ranges.Single().From != 0)
                return Task.FromResult(CreateFullResponse(request, LargePayload));
            if (behavior == "unsatisfiable")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request });
            var response = CreatePartialResponse(request, LargePayload);
            if (behavior == "invalid")
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 2, LargePayload.Length);
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, $"mirror-{behavior}.jar");
        await CreateExecutor(client).DownloadFileAsync("https://piston-data.mojang.com/v1/objects/example/client.jar",
            DownloadSourcePreference.BmclApi, "Mojang", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None);

        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        Assert.Equal("bmclapi2.bangbang93.com", requests.First().Host);
        Assert.DoesNotContain(requests, item => item.Host == "piston-data.mojang.com");
        Assert.Contains(requests, item => item.Host == "cdn.example.test" && item.Range is not null);
        if (behavior == "supported")
            Assert.Contains(requests, item => item.Host == "cdn.example.test" && item.Range is not null && !item.Range.StartsWith("bytes=0-"));
        else if (behavior == "ignored")
            Assert.Single(requests.Where(item => item.Host == "cdn.example.test"));
        else
            Assert.Contains(requests, item => item.Host == "cdn.example.test" && item.Range is null);
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task InterruptedFullProbeCanRetryAsVerifiedSegmentedDownload()
    {
        var handler = new RecordingRequestHandler((number, request, _) =>
        {
            if (number > 1)
                return Task.FromResult(CreatePartialResponse(request, LargePayload));

            var response = CreateFullResponse(request, LargePayload[..4096]);
            response.Content.Headers.ContentLength = LargePayload.Length;
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "interrupted-probe.jar");

        var result = await CreateExecutor(client, maxAttempts: 2).DownloadFileAsync(
            DownloadUrl, DownloadSourcePreference.Official, "ThirdParty",
            destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None);

        Assert.Equal(1, result.RetryCount);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task InterruptedPartialProbeRetriesFromTheWrittenCursorWithItsETag()
    {
        const int probeLength = 1024 * 1024;
        const int writtenPrefix = 64 * 1024;
        var probeRanges = new ConcurrentQueue<(string Range, string? IfRange)>();
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            if (range.From < probeLength)
                probeRanges.Enqueue((request.Headers.Range!.ToString(), request.Headers.IfRange?.ToString()));
            if (range.From != 0)
            {
                var remainder = CreatePartialResponse(request, LargePayload);
                remainder.Headers.ETag = new EntityTagHeaderValue("\"original\"");
                return Task.FromResult(remainder);
            }

            var interrupted = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(LargePayload, 0, writtenPrefix)
            };
            interrupted.Headers.ETag = new EntityTagHeaderValue("\"original\"");
            interrupted.Content.Headers.ContentLength = probeLength;
            interrupted.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, probeLength - 1, LargePayload.Length);
            return Task.FromResult(interrupted);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "interrupted-partial-probe.jar");

        await CreateExecutor(client, maxAttempts: 2).DownloadFileAsync(
            DownloadUrl, DownloadSourcePreference.Official, "ThirdParty", destination, Sha1(LargePayload),
            LargePayload.Length, CancellationToken.None);

        Assert.Equal([("bytes=0-", null), ($"bytes={writtenPrefix}-{probeLength - 1}", "\"original\"")], probeRanges);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowProbeOverlapsRemainingRangesAndResumesFromItsWrittenBytes(bool alternateIgnoresRange)
    {
        const string mirror = "bmclapi2.bangbang93.com";
        const string official = "piston-data.mojang.com";
        const int probeLength = 1024 * 1024;
        const int chunkLength = 32 * 1024;
        var clock = new SteppedTimeProvider();
        var waitingReads = Channel.CreateUnbounded<TaskCompletionSource>();
        var suffixWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeRequests = new ConcurrentQueue<(string Host, string Range)>();
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            var host = request.RequestUri!.Host;
            var range = Assert.Single(request.Headers.Range!.Ranges);
            if (range.From < probeLength)
                probeRequests.Enqueue((host, request.Headers.Range!.ToString()));
            if (host == official && alternateIgnoresRange)
                return Task.FromResult(CreateFullResponse(request, LargePayload));
            if (range.From != 0)
                return Task.FromResult(CreatePartialResponse(request, LargePayload));

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new StreamContent(new SteppedRangeStream(
                    LargePayload, 0, LargePayload.Length, chunkLength, 3, waitingReads.Writer))
            };
            response.Content.Headers.ContentLength = LargePayload.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, LargePayload.Length - 1, LargePayload.Length);
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(2);
        var destination = Path.Combine(TempRoot, "slow-probe.jar");
        var download = CreateExecutor(client, limiter, timeProvider: clock).DownloadFileAsync(
            "https://piston-data.mojang.com/v1/objects/example/client.jar",
            DownloadSourcePreference.BmclApi, "Mojang", destination, Sha1(LargePayload), LargePayload.Length, cancellation.Token,
            reportAttemptProgress: (_, written, _) =>
            {
                if (written == LargePayload.Length - probeLength)
                    suffixWritten.TrySetResult();
            });

        try
        {
            var firstRead = await waitingReads.Reader.ReadAsync(cancellation.Token);
            await suffixWritten.Task.WaitAsync(cancellation.Token);
            Assert.False(download.IsCompleted);
            firstRead.SetResult();
            for (var window = 0; window < 2; window++)
            {
                var pendingRead = await waitingReads.Reader.ReadAsync(cancellation.Token);
                clock.Advance(TimeSpan.FromSeconds(10));
                pendingRead.SetResult();
            }

            var result = await download;
            var resumedRange = $"bytes={3 * chunkLength}-{probeLength - 1}";
            Assert.Equal(alternateIgnoresRange
                ? [(mirror, "bytes=0-"), (official, resumedRange), (mirror, resumedRange)]
                : [(mirror, "bytes=0-"), (official, resumedRange)], probeRequests);
            Assert.Equal(!alternateIgnoresRange, result.FallbackUsed);
            Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            cancellation.Cancel();
            if (!download.IsCompleted)
            {
                try { await download; }
                catch (OperationCanceledException) { }
            }
        }

        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task DegradedOnlySourceCanStillUseRanges()
    {
        var health = new DownloadHostHealthTracker();
        var candidate = MinecraftDownloadSourceResolver.EnumerateRequests(DownloadUrl, DownloadSourcePreference.Official).First();
        for (var i = 0; i < 3; i++)
            health.RecordFailure(candidate.ResolvedSourceKind, "downloads.example.test", DownloadFailureReason.Network);
        Assert.True(health.ShouldAvoid(candidate.ResolvedSourceKind, "downloads.example.test"));
        var handler = new RecordingRequestHandler((_, request, _) => Task.FromResult(CreatePartialResponse(request, LargePayload)));
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "degraded.jar");
        await CreateExecutor(client, health: health).DownloadFileAsync(DownloadUrl, DownloadSourcePreference.Official,
            "ThirdParty", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None);
        Assert.Contains(handler.RangeHeaders, range => range is not null && !range.StartsWith("bytes=0-"));
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task UnsupportedSecondSourceIsRetriedSequentiallyBeforeTheFirstSource()
    {
        var requests = new ConcurrentQueue<(string Host, string? Range)>();
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            var host = request.RequestUri!.Host;
            requests.Enqueue((host, request.Headers.Range?.ToString()));
            if (host == "bmclapi2.bangbang93.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            if (request.Headers.Range is null)
                return Task.FromResult(CreateFullResponse(request, LargePayload));
            var response = CreatePartialResponse(request, LargePayload);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 2, LargePayload.Length);
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "second-source.jar");
        var result = await CreateExecutor(client).DownloadFileAsync("https://piston-data.mojang.com/v1/objects/example/client.jar",
            DownloadSourcePreference.BmclApi, "Mojang", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None);
        Assert.Equal(new[] { "bmclapi2.bangbang93.com", "piston-data.mojang.com", "piston-data.mojang.com" }, requests.Select(item => item.Host));
        Assert.Null(requests.Last().Range);
        Assert.True(result.FallbackUsed);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task CompletedRangeDoesNotWaitForResponseStreamToEnd()
    {
        var handler = new RecordingRequestHandler(async (_, request, _) =>
        {
            var response = CreatePartialResponse(request, LargePayload);
            if (request.Headers.Range!.Ranges.Single().From == 0) return response;
            var range = response.Content.Headers.ContentRange;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            response.Content.Dispose();
            response.Content = new StreamContent(new TailBlockingStream(bytes));
            response.Content.Headers.ContentLength = bytes.Length;
            response.Content.Headers.ContentRange = range;
            return response;
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "tail-blocked.jar");
        await CreateExecutor(client).DownloadFileAsync(DownloadUrl, DownloadSourcePreference.Official,
            "ThirdParty", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        Assert.Contains(handler.Requests, request => request.Range is not null && !request.Range.StartsWith("bytes=0-"));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task RetryAfterFinalRangeWasWrittenCompletesAndVerifiesFile()
    {
        var raised = 0;
        var handler = new RecordingRequestHandler(async (_, request, token) =>
        {
            if (request.Headers.Range!.Ranges.Single().From != 0) await Task.Delay(50, token);
            return CreatePartialResponse(request, LargePayload);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "completed-before-retry.jar");
        await CreateExecutor(client, maxAttempts: 2).DownloadFileAsync(DownloadUrl, DownloadSourcePreference.Official,
            "ThirdParty", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None,
            reportAttemptProgress: (_, downloaded, total) =>
            {
                if (downloaded == total && Interlocked.Exchange(ref raised, 1) == 0)
                    throw new DownloadTimeoutException(DownloadFailureReason.BodyIdleTimeout, "Simulated final-read timeout");
            });
        Assert.Equal(1, raised);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task TrustedLargeFileUsesRangesAndPublishesVerifiedPayload()
    {
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            Assert.Equal("identity", request.Headers.AcceptEncoding.Single().Value);
            return Task.FromResult(CreatePartialResponse(request, LargePayload));
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "client.jar");

        var result = await CreateExecutor(client).DownloadFileAsync(
            DownloadUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destination,
            new DownloadIntegrityExpectation(
                LargePayload.Length,
                [(HashAlgorithmName.SHA512, Sha512(LargePayload))]),
            CancellationToken.None);

        Assert.True(result.NetworkAttempts > 1);
        Assert.Equal(0, result.RetryCount);
        Assert.Contains(handler.RangeHeaders, range => range is not null);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task AFailedSegmentRetryIsCountedSeparatelyFromOtherRequests()
    {
        var failures = 0;
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            if (request.Headers.Range!.Ranges.Single().From != 0 && Interlocked.CompareExchange(ref failures, 1, 0) == 0)
                throw new HttpRequestException("simulated temporary failure");
            return Task.FromResult(CreatePartialResponse(request, LargePayload));
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "segment-retry.jar");
        var result = await CreateExecutor(client, maxAttempts: 2).DownloadFileAsync(DownloadUrl,
            DownloadSourcePreference.Official, "ThirdParty", destination, Sha1(LargePayload), LargePayload.Length, CancellationToken.None);
        Assert.Equal(1, failures);
        Assert.Equal(1, result.RetryCount);
        Assert.True(result.NetworkAttempts > result.RetryCount);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
    }

    [Theory]
    [InlineData(64, 16)]
    [InlineData(4, 4)]
    public async Task LargeFileExpandsUpToItsLimitWithinGlobalAdmission(int globalLimit, int expectedWorkers)
    {
        var payload = CreatePayload(20 * 1024 * 1024);
        var targetReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedConcurrency = new ConcurrentBag<int>();
        var active = 0;
        var handler = new RecordingRequestHandler(async (_, request, token) =>
        {
            if (request.Headers.Range!.Ranges.Single().From == 0)
                return CreatePartialResponse(request, payload);

            var count = Interlocked.Increment(ref active);
            observedConcurrency.Add(count);
            if (count >= expectedWorkers)
                targetReached.TrySetResult(true);
            try
            {
                await release.Task.WaitAsync(token);
                return CreatePartialResponse(request, payload);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        using var client = CreateClient(handler);
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(globalLimit);
        var destination = Path.Combine(TempRoot, "worker-limit.jar");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var download = CreateExecutor(client, limiter).DownloadFileAsync(
            DownloadUrl, DownloadSourcePreference.Official, "ThirdParty", destination, Sha1(payload),
            payload.Length, cancellation.Token);
        try
        {
            await targetReached.Task.WaitAsync(cancellation.Token);
        }
        finally
        {
            release.TrySetResult(true);
        }

        await download;
        Assert.Equal(expectedWorkers, observedConcurrency.Max());
        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
        Assert.Equal(Sha1(payload), Sha1(await File.ReadAllBytesAsync(destination)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastRangeUsesLowerThresholdOnlyWhenNoOtherBytesArePending(bool hasQueuedRange)
    {
        const long fileSize = 3850;
        long written = 0;
        double? appliedThreshold = null;
        var clock = new SteppedTimeProvider();
        var window = TimeSpan.FromSeconds(10);
        var backup = MinecraftDownloadSourceResolver.EnumerateRequests(DownloadUrl, DownloadSourcePreference.Official).First();
        var recovery = new SegmentedSlowSourceRecovery(
            clock, window, 30, 15, fileSize,
            () => written, () => true, () => backup, () => true,
            (_, _, _, _, threshold) => appliedThreshold = threshold);
        recovery.BodyStarted();

        written += 1000;
        clock.Advance(window);
        Assert.Null(recovery.GetBackup(100, RemainingInActiveRange(), RemainingInActiveRange));

        written += 1000;
        clock.Advance(window);
        Assert.Same(hasQueuedRange ? null : backup, recovery.GetBackup(100, RemainingInActiveRange(), RemainingInActiveRange));
        Assert.Equal(hasQueuedRange ? null : 15d, appliedThreshold);
        recovery.BodyEnded();

        long RemainingInActiveRange() => fileSize - written - (hasQueuedRange ? 100 : 0);
    }

    [Fact]
    public void SelectedBackupDoesNotMoveAFasterRangeWithALongSuffix()
    {
        const long fileSize = 10_000;
        long written = 0;
        var clock = new SteppedTimeProvider();
        var window = TimeSpan.FromSeconds(10);
        var backup = MinecraftDownloadSourceResolver.EnumerateRequests(DownloadUrl, DownloadSourcePreference.Official).First();
        var recovery = new SegmentedSlowSourceRecovery(
            clock, window, 30, 15, fileSize,
            () => written, () => true, () => backup, () => true, (_, _, _, _, _) => { });
        recovery.BodyStarted();
        recovery.BodyStarted();

        written += 1000;
        clock.Advance(window);
        Assert.Null(recovery.GetBackup(25, 2000, () => 2000));
        written += 1000;
        clock.Advance(window);
        Assert.Same(backup, recovery.GetBackup(25, 2000, () => 2000));
        Assert.Null(recovery.GetBackup(100, 6000, () => 6000));
        Assert.Same(backup, recovery.GetBackup(20, 2000, () => 2000));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ConcurrentSmallRangesSwitchOnlyForSustainedSlowTransfer(
        bool sustainedSlow, bool cancelOnBackup, bool fastTail)
    {
        const string mirror = "bmclapi2.bangbang93.com";
        const int probeLength = 1024 * 1024;
        const int chunkLength = 32 * 1024;
        var payload = CreatePayload(probeLength + 1536 * 1024);
        var clock = new SteppedTimeProvider();
        var waitingReads = Channel.CreateUnbounded<TaskCompletionSource>();
        var switched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mirrorOffsets = new ConcurrentQueue<long>();
        var backupOffsets = new ConcurrentQueue<long>();
        var handler = new RecordingRequestHandler(async (_, request, token) =>
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = (int)range.From!.Value;
            var end = (int)(range.To ?? payload.Length - 1);
            var response = CreatePartialResponse(request, payload, maximumEnd: start == 0 ? probeLength - 1 : null);
            if (request.RequestUri!.Host != mirror)
            {
                backupOffsets.Enqueue(start);
                switched.TrySetResult();
                if (cancelOnBackup)
                {
                    using (response)
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
            else if (start > 0)
            {
                mirrorOffsets.Enqueue(start);
                response.Content.Dispose();
                var steppedChunkLength = fastTail && start > probeLength ? 112 * 1024 : chunkLength;
                response.Content = new StreamContent(new SteppedRangeStream(
                    payload, start, end - start + 1, steppedChunkLength, sustainedSlow ? 3 : 2, waitingReads.Writer));
                response.Content.Headers.ContentLength = end - start + 1;
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
            }

            return response;
        });
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(2);
        var destination = Path.Combine(TempRoot, "slow-pair.jar");
        var download = CreateExecutor(client, limiter, timeProvider: clock).DownloadFileAsync(
            "https://piston-data.mojang.com/v1/objects/example/client.jar",
            DownloadSourcePreference.BmclApi, "Mojang", destination, Sha1(payload), payload.Length, cancellation.Token);
        try
        {
            // A long first-byte wait must not enter the throughput window.
            await ReleaseReadPairAsync(TimeSpan.FromSeconds(60));
            await ReleaseReadPairAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(backupOffsets);
            if (sustainedSlow)
                await ReleaseReadPairAsync(TimeSpan.FromSeconds(10));

            if (cancelOnBackup)
            {
                await switched.Task.WaitAsync(cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
                Assert.False(File.Exists(destination));
            }
            else
            {
                var result = await download;
                Assert.Equal(sustainedSlow, result.FallbackUsed);
                Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
                Assert.Equal(sustainedSlow ? mirrorOffsets.Where(offset => !fastTail || offset == probeLength)
                        .Order().Select(offset => offset + 3L * chunkLength) : [],
                    backupOffsets.Order());
            }
        }
        finally
        {
            cancellation.Cancel();
            if (!download.IsCompleted)
            {
                try { await download; }
                catch (OperationCanceledException) { }
            }
        }

        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
        AssertNoPendingFiles(destination);

        async Task ReleaseReadPairAsync(TimeSpan elapsed)
        {
            var first = await waitingReads.Reader.ReadAsync(cancellation.Token);
            var second = await waitingReads.Reader.ReadAsync(cancellation.Token);
            clock.Advance(elapsed);
            first.SetResult();
            second.SetResult();
        }
    }

    [Fact]
    public void SchedulerSplitsTheMarkedSlowTailBeforeALargerRange()
    {
        const long unit = MinecraftDownloadRequestExecutor.SegmentedSplitThreshold;
        var session = new AdaptiveSegmentDownloadSession(0, unit * 10, unit);
        Assert.True(session.TryTake(out var first));
        Assert.True(session.TryTakeQueuedOrSplit(out var slower, out _));
        Assert.True(first!.RemainingLength > slower!.RemainingLength);
        Assert.True(slower.TryPreferSplitting(unit));

        Assert.True(session.TryTakeQueuedOrSplit(out var newTail, out var split));

        Assert.Equal(slower.ChunkId, split!.Value.SourceChunkId);
        Assert.Equal(slower.LogicalEnd + 1, newTail!.NextOffset);
        Assert.Equal(split.Value.SourceOriginalEnd, newTail.LogicalEnd);
    }

    [Fact]
    public async Task UnverifiedLargeFileUsesRangesOnlyWithStableStrongEtag()
    {
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            var response = CreatePartialResponse(request, LargePayload);
            response.Headers.ETag = new EntityTagHeaderValue("\"custom-v1\"");
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "custom-large.bin");

        await CreateExecutor(client).DownloadUnverifiedFileAsync(
            DownloadUrl,
            destination,
            CancellationToken.None);

        Assert.Contains(handler.RangeHeaders, range => range is not null);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        Assert.All(
            handler.Requests.Skip(1).Where(request => request.Range is not null),
            request => Assert.Equal("\"custom-v1\"", request.IfRange));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task UnverifiedFileWithoutStrongEtagFallsBackToSingleStream()
    {
        var handler = new RecordingRequestHandler((_, request, _) =>
            Task.FromResult(request.Headers.Range is null
                ? CreateFullResponse(request, LargePayload)
                : CreatePartialResponse(request, LargePayload)));
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "custom-no-etag.bin");

        await CreateExecutor(client).DownloadUnverifiedFileAsync(
            DownloadUrl,
            destination,
            CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.NotNull(handler.RangeHeaders.ElementAt(0));
        Assert.Null(handler.RangeHeaders.ElementAt(1));
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task ChangedStrongEtagDiscardsSegmentsAndFallsBack()
    {
        var handler = new RecordingRequestHandler((requestNumber, request, _) =>
        {
            if (request.Headers.Range is null)
                return Task.FromResult(CreateFullResponse(request, LargePayload));

            var response = CreatePartialResponse(request, LargePayload);
            response.Headers.ETag = new EntityTagHeaderValue(
                requestNumber == 1 ? "\"custom-v1\"" : "\"custom-v2\"");
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "custom-changed-etag.bin");

        await CreateExecutor(client).DownloadUnverifiedFileAsync(
            DownloadUrl,
            destination,
            CancellationToken.None);

        Assert.Contains(handler.RangeHeaders, range => range is null);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task FailedUnverifiedDownloadPreservesExistingDestination()
    {
        var existing = new byte[] { 1, 2, 3, 4 };
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            if (request.Headers.Range is not null)
                return Task.FromResult(CreatePartialResponse(request, LargePayload));
            throw new HttpRequestException("simulated failure");
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "custom-existing.bin");
        Directory.CreateDirectory(TempRoot);
        await File.WriteAllBytesAsync(destination, existing);

        await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
            CreateExecutor(client).DownloadUnverifiedFileAsync(
                DownloadUrl,
                destination,
                CancellationToken.None));

        Assert.Equal(existing, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task InvalidContentRangeFallsBackOnceToSingleStream()
    {
        var handler = new RecordingRequestHandler((requestNumber, request, _) =>
        {
            if (requestNumber > 1)
                return Task.FromResult(CreateFullResponse(request, LargePayload));

            var response = CreatePartialResponse(request, LargePayload);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 2, LargePayload.Length);
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "invalid-range.jar");

        await CreateExecutor(client).DownloadFileAsync(
            DownloadUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destination,
            Sha1(LargePayload),
            LargePayload.Length,
            CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.NotNull(handler.RangeHeaders.ElementAt(0));
        Assert.Null(handler.RangeHeaders.ElementAt(1));
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task CorruptSegmentFallsBackToVerifiedSingleStream()
    {
        var corrupted = 0;
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            if (request.Headers.Range is null)
                return Task.FromResult(CreateFullResponse(request, LargePayload));

            var shouldCorrupt = Interlocked.CompareExchange(ref corrupted, 1, 0) == 0;
            return Task.FromResult(CreatePartialResponse(request, LargePayload, shouldCorrupt));
        });
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "corrupt-segment.jar");

        await CreateExecutor(client).DownloadFileAsync(
            DownloadUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destination,
            Sha1(LargePayload),
            LargePayload.Length,
            CancellationToken.None);

        Assert.Null(handler.RangeHeaders.Last());
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
    }

    [Fact]
    public async Task LightweightAtomicLargeFileKeepsSingleStreamPath()
    {
        var handler = FullResponseHandler(LargePayload);
        using var client = CreateClient(handler);
        var destination = Path.Combine(TempRoot, "lightweight.jar");

        await CreateExecutor(client).DownloadFileAsync(
            DownloadUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destination,
            Sha1(LargePayload),
            LargePayload.Length,
            CancellationToken.None,
            options: new DownloadFileOptions(
                DownloadPersistenceMode.LightweightAtomic,
                ManagedRoot: TempRoot));

        Assert.Equal([null], handler.RangeHeaders);
    }

    [Fact]
    public async Task CancellationCleansTemporaryFileAndReleasesDownloadSlots()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 2)
                started.TrySetResult(true);
            return Task.FromResult(CreateBlockingPartialResponse(request, LargePayload.Length));
        });
        using var client = CreateClient(handler);
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(2);
        var destination = Path.Combine(TempRoot, "cancelled.jar");
        using var cancellation = new CancellationTokenSource();

        var download = CreateExecutor(client, limiter).DownloadFileAsync(
            DownloadUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destination,
            Sha1(LargePayload),
            LargePayload.Length,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
        Assert.Equal(0, limiter.DownloadSnapshot.WaitingCount);
        Assert.False(File.Exists(destination));
        AssertNoPendingFiles(destination);
    }

    private static MinecraftDownloadRequestExecutor CreateExecutor(
        HttpClient client,
        IImportConcurrencyLimiter? limiter = null,
        int maxAttempts = 1,
        DownloadHostHealthTracker? health = null,
        TimeProvider? timeProvider = null)
    {
        var effectiveLimiter = limiter;
        if (effectiveLimiter is null)
        {
            var defaultLimiter = new ImportConcurrencyLimiter();
            defaultLimiter.SetMaximumDownloadConcurrency(64);
            effectiveLimiter = defaultLimiter;
        }

        return new MinecraftDownloadRequestExecutor(
            client,
            limiter: effectiveLimiter,
            category: DownloadConcurrencyCategory.Runtime,
            retryOptions: new DownloadRetryOptions
            {
                MaxAttemptsPerSource = maxAttempts,
                MaxFileAttemptsPerSource = 1,
                RetryDelay = TimeSpan.Zero,
                ResponseHeadersTimeout = TimeSpan.FromSeconds(5),
                FirstByteTimeout = TimeSpan.FromSeconds(5),
                BodyIdleTimeout = TimeSpan.FromSeconds(5),
                MaxRedirects = 10
            },
            hostConcurrencyController: new DownloadHostConcurrencyController(
                delayAsync: static (_, _) => ValueTask.CompletedTask),
            bmclApiRequestRateLimiter: new BmclApiRequestRateLimiter(TimeSpan.Zero),
            nextRetryJitter: () => 0,
            timeProvider: timeProvider,
            hostHealthTracker: health,
            segmentedDownloadCoordinator: new SegmentedDownloadCoordinator());
    }

    private static RecordingRequestHandler FullResponseHandler(byte[] payload) =>
        new((_, request, _) => Task.FromResult(CreateFullResponse(request, payload)));

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage CreateFullResponse(HttpRequestMessage request, byte[] payload) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(payload)
        };

    private static HttpResponseMessage CreatePartialResponse(
        HttpRequestMessage request,
        byte[] payload,
        bool corrupt = false,
        long? maximumEnd = null)
    {
        var range = Assert.Single(request.Headers.Range!.Ranges);
        var start = range.From!.Value;
        var end = Math.Min(range.To ?? payload.Length - 1, maximumEnd ?? payload.Length - 1);
        var bytes = payload.AsSpan(
            checked((int)start),
            checked((int)(end - start + 1))).ToArray();
        if (corrupt)
            bytes[0] ^= 0xFF;

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
        return response;
    }

    private static HttpResponseMessage CreateBlockingPartialResponse(
        HttpRequestMessage request,
        long totalLength)
    {
        var range = Assert.Single(request.Headers.Range!.Ranges);
        var start = range.From!.Value;
        var end = range.To ?? totalLength - 1;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = request,
            Content = new StreamContent(new BlockingReadStream())
        };
        response.Content.Headers.ContentLength = end - start + 1;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, totalLength);
        return response;
    }

    private static void AssertNoPendingFiles(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
            return;

        Assert.Empty(Directory.EnumerateFiles(
            directory,
            $".{Path.GetFileName(destination)}.bhl-pending-*.tmp",
            SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(
            directory,
            $"{Path.GetFileName(destination)}.bhl-pending-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    private static string Sha1(byte[] payload) =>
        Convert.ToHexString(SHA1.HashData(payload));

    private static string Sha512(byte[] payload) =>
        Convert.ToHexString(SHA512.HashData(payload));

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index % 251);
        return payload;
    }

    private sealed class RecordingRequestHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);
        public ConcurrentQueue<string?> RangeHeaders { get; } = new();
        public ConcurrentQueue<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RangeHeaders.Enqueue(request.Headers.Range?.ToString());
            Requests.Enqueue(new RequestSnapshot(
                request.Headers.Range?.ToString(),
                request.Headers.TryGetValues("If-Range", out var values) ? values.Single() : null));
            return callback(Interlocked.Increment(ref requestCount), request, cancellationToken);
        }
    }

    private sealed record RequestSnapshot(string? Range, string? IfRange);

    private sealed class SteppedTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    private sealed class SteppedRangeStream(
        byte[] payload,
        int start,
        int length,
        int chunkLength,
        int steppedReads,
        ChannelWriter<TaskCompletionSource> waitingReads) : MemoryStream(payload, start, length, writable: false)
    {
        private int reads;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (reads++ < steppedReads)
            {
                var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await waitingReads.WriteAsync(released, cancellationToken);
                await released.Task.WaitAsync(cancellationToken);
                buffer = buffer[..Math.Min(chunkLength, buffer.Length)];
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class TailBlockingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
