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
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core.Files;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class MinecraftDownloadRetryTests
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclManifestUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    [Theory]
    [InlineData("metadata")]
    [InlineData("staggered")]
    [InlineData("file")]
    [InlineData("redirect")]
    public async Task DegradedOnlySourceStillReceivesARealRequest(string mode)
    {
        const string url = "https://example.test/download.bin";
        const string targetHost = "example.test";
        var health = new DownloadHostHealthTracker();
        for (var i = 0; i < 3; i++) health.RecordFailure("ThirdParty", targetHost, DownloadFailureReason.Network);
        Assert.True(health.ShouldAvoid("ThirdParty", targetHost));
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var response = CreateResponse(HttpStatusCode.OK, "good", request);
            if (mode == "redirect" && request.RequestUri!.Host == "example.test")
            {
                response.StatusCode = HttpStatusCode.Found;
                response.Headers.Location = new Uri("https://cdn.example.test/download.bin");
            }
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, new DownloadRetryOptions { MaxAttemptsPerSource = 1, MaxFileAttemptsPerSource = 1 }, hostHealthTracker: health);
        var directory = CreateTempDirectory();
        try
        {
            if (mode == "file")
            {
                var destination = Path.Combine(directory, "file.bin");
                await executor.DownloadFileAsync(url, DownloadSourcePreference.Official, null, destination,
                    Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
                Assert.Equal("good", await File.ReadAllTextAsync(destination));
            }
            else if (mode == "staggered")
                Assert.Equal("good", (await executor.ExecuteStaggeredLookupAsync(url, DownloadSourcePreference.Official, null,
                    (context, token) => context.Response.Content.ReadAsStringAsync(token), _ => false, CancellationToken.None)).Value);
            else
                Assert.Equal("good", await executor.ExecuteAsync(url, DownloadSourcePreference.Official, null,
                    (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
            Assert.Contains(handler.RequestUris, uri => uri.Host == targetHost);
            Assert.False(health.ShouldAvoid("ThirdParty", targetHost));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SuccessfulEntryRedirectRestoresHealthEvenWhenNodeFails()
    {
        var health = new DownloadHostHealthTracker();
        for (var i = 0; i < 3; i++) health.RecordFailure("ThirdParty", "example.test", DownloadFailureReason.Network);
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var response = CreateResponse(request.RequestUri!.Host == "example.test" ? HttpStatusCode.Found : HttpStatusCode.ServiceUnavailable, "", request);
            if (response.StatusCode == HttpStatusCode.Found)
                response.Headers.Location = new Uri("https://cdn.example.test/file");
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, new DownloadRetryOptions { MaxAttemptsPerSource = 1 }, hostHealthTracker: health);
        await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
            executor.ExecuteAsync("https://example.test/file", DownloadSourcePreference.Official, null,
                (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        Assert.False(health.ShouldAvoid("ThirdParty", "example.test"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyMirrorIsTriedFirstButDegradedOfficialRemainsAFallback(bool mirrorFails)
    {
        var health = new DownloadHostHealthTracker();
        var source = MinecraftDownloadSourceResolver.EnumerateRequests(ManifestUrl, DownloadSourcePreference.Official).First();
        for (var i = 0; i < 3; i++) health.RecordFailure(source.ResolvedSourceKind, "piston-meta.mojang.com", DownloadFailureReason.Network);
        var handler = new CallbackRequestHandler((_, request, _) => Task.FromResult(CreateResponse(
            mirrorFails && request.RequestUri!.Host == "bmclapi2.bangbang93.com" ? HttpStatusCode.NotFound : HttpStatusCode.OK, "good", request)));
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, hostHealthTracker: health);
        Assert.Equal("good", await executor.ExecuteAsync(ManifestUrl, DownloadSourcePreference.Official, null,
            (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        Assert.Equal(mirrorFails ? new[] { "bmclapi2.bangbang93.com", "piston-meta.mojang.com" } : new[] { "bmclapi2.bangbang93.com" },
            handler.RequestUris.Select(uri => uri.Host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MirrorForbiddenHasOneExtraAttemptAndPreservesDestinationOnFailure(bool mirrorRecovers)
    {
        var mirrorAttempts = 0;
        var handler = new CallbackRequestHandler((_, request, _) => Task.FromResult(CreateResponse(
            request.RequestUri!.Host == "bmclapi2.bangbang93.com"
                ? ++mirrorAttempts == 2 && mirrorRecovers ? HttpStatusCode.OK : HttpStatusCode.Forbidden
                : HttpStatusCode.NotFound, "good", request)));
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, new DownloadRetryOptions { RetryDelay = TimeSpan.Zero, ThrottleRetryDelay = TimeSpan.Zero });
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "library.jar");
            await File.WriteAllTextAsync(destination, "previous");
            var download = executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.BmclApi, "Mojang", destination,
                Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
            if (mirrorRecovers) await download;
            else await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() => download);
            Assert.Equal(2, mirrorAttempts);
            Assert.Equal(3, handler.RequestUris.Count);
            Assert.Equal(mirrorRecovers ? "good" : "previous", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSourceRetryAfterWaitsForTheFullServerDelay(bool fileDownload)
    {
        var clock = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (delay, _) => { delays.Add(delay); clock.Advance(delay); return ValueTask.CompletedTask; });
        var handler = new CallbackRequestHandler((number, request, _) =>
        {
            var response = CreateResponse(number == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "good", request);
            if (number == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, hostConcurrencyController: controller);
        var directory = CreateTempDirectory();
        try
        {
            if (fileDownload)
                await executor.DownloadFileAsync("https://example.test/file.jar", DownloadSourcePreference.Official, "ThirdParty",
                    Path.Combine(directory, "file.jar"), Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
            else
                Assert.Equal("good", await executor.ExecuteAsync("https://example.test/file.json", DownloadSourcePreference.Official,
                    "ThirdParty", (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
            Assert.Equal([TimeSpan.FromMinutes(2)], delays);
            Assert.Equal(2, handler.RequestUris.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExcessiveRetryAfterFailsPromptlyAndSharedCooldownNeverResends(bool fileDownload, bool distantDate)
    {
        var clock = new ManualTimeProvider();
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (_, _) => throw new InvalidOperationException("An excessive cooldown must not be waited out."));
        var logger = new CollectingLogger();
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var response = CreateResponse(HttpStatusCode.TooManyRequests, "limited", request);
            response.Headers.RetryAfter = distantDate
                ? new RetryConditionHeaderValue(DateTimeOffset.MaxValue)
                : new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "file.jar");
            await File.WriteAllTextAsync(destination, "previous");
            for (var i = 0; i < 2; i++)
            {
                var executor = CreateExecutor(client, logger: logger, hostConcurrencyController: controller);
                Task operation = fileDownload
                    ? executor.DownloadFileAsync("https://example.test/file.jar", DownloadSourcePreference.Official, null, destination,
                        Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None)
                    : executor.ExecuteAsync("https://example.test/file.json", DownloadSourcePreference.Official, null,
                        (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None);
                var failure = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(
                    () => operation.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Contains(failure.Failures, item => item is DownloadAttemptException attempt
                    && attempt.StatusCode == HttpStatusCode.TooManyRequests && attempt.Disposition == DownloadFailureDisposition.SwitchSource);
            }
            Assert.Single(handler.RequestUris);
            Assert.True(controller.GetRemainingCooldown(new Uri("https://example.test")) >= TimeSpan.FromHours(1));
            Assert.Equal("previous", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.GetFiles(directory));
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("wait budget", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("-10")]
    [InlineData("99999999999999999999999999999999")]
    [InlineData("Wed, 01 Jan 2020 00:00:00 GMT")]
    public async Task InvalidOrPastRetryAfterUsesABoundedNonnegativeFallback(string header)
    {
        var clock = new ManualTimeProvider();
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (delay, _) => { Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(2)); clock.Advance(delay); return ValueTask.CompletedTask; });
        var handler = new CallbackRequestHandler((number, request, _) =>
        {
            var response = CreateResponse(number == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "good", request);
            if (number == 1) response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        Assert.Equal("good", await CreateExecutor(client, hostConcurrencyController: controller).ExecuteAsync(
            "https://example.test/file.json", DownloadSourcePreference.Official, null,
            (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task ThrottledMirrorDoesNotBlockFallbackOrOtherExecutorsSharingTheController()
    {
        var clock = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        var controller = new DownloadHostConcurrencyController(timeProvider: clock, delayAsync: (delay, _) => { delays.Add(delay); clock.Advance(delay); return ValueTask.CompletedTask; });
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var limited = request.RequestUri!.Host == "bmclapi2.bangbang93.com";
            var response = CreateResponse(limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "good", request);
            if (limited) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var directory = CreateTempDirectory();
        try
        {
            for (var index = 0; index < 2; index++)
                await CreateExecutor(client, hostConcurrencyController: controller).DownloadFileAsync(ManifestUrl,
                    DownloadSourcePreference.BmclApi, "Mojang", Path.Combine(directory, $"file-{index}.jar"),
                    Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
            Assert.Equal(1, handler.RequestUris.Count(uri => uri.Host == "bmclapi2.bangbang93.com"));
            Assert.Equal(2, handler.RequestUris.Count(uri => uri.Host == "piston-meta.mojang.com"));
            Assert.Empty(delays);
            Assert.Equal(TimeSpan.FromHours(1), controller.GetRemainingCooldown(new Uri(BmclManifestUrl)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(1, 3L * 1024 * 1024 * 1024)]
    [InlineData(2, 1280L * 1024 * 1024)]
    public async Task LargeDeclaredDownloadsReachNetworkWithoutATwoGiBBudgetAndCancelCleanly(int files, long size)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var handler = new CallbackRequestHandler(async (_, request, token) =>
        {
            if (Interlocked.Increment(ref requests) == files) started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateResponse(HttpStatusCode.OK, "unused", request);
        });
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();
        var directory = CreateTempDirectory();
        var downloads = new List<Task>();
        try
        {
            for (var index = 0; index < files; index++)
            {
                var destination = Path.Combine(directory, $"large-{index}.jar");
                await File.WriteAllTextAsync(destination, "previous");
                downloads.Add(CreateExecutor(client, new DownloadRetryOptions { ResponseHeadersTimeout = TimeSpan.FromSeconds(30) })
                    .DownloadFileAsync($"https://example.test/{index}.jar", DownloadSourcePreference.Official,
                    "ThirdParty", destination, Convert.ToHexString(SHA1.HashData("good"u8)), size, cancellation.Token));
            }
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(downloads));
            Assert.Equal(files, Directory.GetFiles(directory).Length);
            foreach (var path in Directory.GetFiles(directory)) Assert.Equal("previous", await File.ReadAllTextAsync(path));
        }
        finally
        {
            cancellation.Cancel();
            try { await Task.WhenAll(downloads); } catch (Exception) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OrdinaryDownloadsUseTenSecondTimeoutsAndAWindowedSpeedFloor()
    {
        var options = DownloadRetryOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(10), options.ResponseHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.BodyIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.SlowBodyReadThreshold);
        Assert.Equal(2048, options.MinimumBodyBytesPerSecond);
        Assert.Null(DownloadRetryOptions.InteractiveMetadata.SlowBodyReadThreshold);
    }

    [Fact]
    public async Task SlowFirstByteDoesNotCauseFastTransferToBeRejected()
    {
        var clock = new ManualTimeProvider();
        var source = new TimedChunkReadStream(clock,
            ((byte)'a', TimeSpan.FromSeconds(9.5)),
            ((byte)'b', TimeSpan.FromSeconds(0.5)),
            ((byte)'c', TimeSpan.FromSeconds(9.5))) { BytesPerChunk = 5 * 1024 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(10), minimumBodyBytesPerSecond: 2048,
            timeProvider: clock, expectedContentLength: 1024 * 1024);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[40 * 1024];
        Assert.Equal(5 * 1024, await stream.ReadAsync(buffer));
        Assert.Equal(5 * 1024, await stream.ReadAsync(buffer));
        source.BytesPerChunk = 40 * 1024;
        Assert.Equal(40 * 1024, await stream.ReadAsync(buffer));
    }

    [Theory]
    [InlineData(5, 8, false)]
    [InlineData(15, 12, true)]
    public async Task FinishingSoonUsesConfiguredWindow(int windowSeconds, int remainingBytes, bool shouldFinish)
    {
        var clock = new ManualTimeProvider();
        var source = new TimedChunkReadStream(clock,
            ((byte)'a', TimeSpan.Zero), ((byte)'b', TimeSpan.FromSeconds(windowSeconds)),
            ((byte)'c', TimeSpan.Zero));
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        };
        // The second read's speed is one byte per second.
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(windowSeconds), minimumBodyBytesPerSecond: 2048,
            timeProvider: clock, expectedContentLength: 1 + windowSeconds + remainingBytes);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[windowSeconds];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        source.BytesPerChunk = windowSeconds;
        Assert.Equal(windowSeconds, await stream.ReadAsync(buffer));
        if (shouldFinish)
            Assert.Equal(windowSeconds, await stream.ReadAsync(buffer));
        else
            await Assert.ThrowsAsync<DownloadBodyTooSlowException>(() => stream.ReadAsync(buffer.AsMemory()).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SlowBodyWindowCombinesReadsAcrossAllStreamEntryPoints(int readMode)
    {
        var clock = new ManualTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TimedChunkReadStream(clock,
                ((byte)'s', TimeSpan.Zero), ((byte)'a', TimeSpan.FromSeconds(5)), ((byte)'b', TimeSpan.FromSeconds(5))))
        };
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(10), minimumBodyBytesPerSecond: 2048,
            timeProvider: clock, expectedContentLength: 1000);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1];
        Assert.Equal(1, await ReadAsync());
        Assert.Equal(1, await ReadAsync());
        Assert.Equal(1, await ReadAsync());
        var failure = await Assert.ThrowsAsync<DownloadBodyTooSlowException>(() => ReadAsync());
        Assert.Equal(2, failure.BytesRead);
        Assert.Equal(TimeSpan.FromSeconds(10), failure.ReadDuration);

        Task<int> ReadAsync() => readMode switch
        {
            0 => stream.ReadAsync(buffer.AsMemory()).AsTask(),
            1 => Task.FromResult(stream.Read(buffer, 0, buffer.Length)),
            _ => Task.FromResult(stream.Read(buffer.AsSpan()))
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowRangeUsesItsNewLogicalEndBeforeSwitchingSources(bool tailWasSplit)
    {
        var clock = new ManualTimeProvider();
        const int chunkSize = 64 * 1024;
        const int rangeSize = 4 * 1024 * 1024;
        long remaining = rangeSize;
        long firstSplitRemaining = 0;
        var source = new TimedChunkReadStream(clock,
            ((byte)0, TimeSpan.Zero), ((byte)1, TimeSpan.FromSeconds(10)),
            ((byte)2, TimeSpan.FromSeconds(10)), ((byte)3, TimeSpan.Zero)) { BytesPerChunk = chunkSize };
        using var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(source) };
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(10), timeProvider: clock,
            expectedContentLength: rangeSize, shouldSwitchSlowSource: seconds => seconds > 30,
            rangeProgress: new SegmentedReadProgress(() => remaining, (_, bytes) =>
            {
                if (firstSplitRemaining != 0)
                    return false;
                firstSplitRemaining = bytes;
                return true;
            }));
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[chunkSize];
        Assert.Equal(chunkSize, await ReadAndPersistAsync());
        Assert.Equal(chunkSize, await ReadAndPersistAsync());
        Assert.Equal(rangeSize - 2 * chunkSize, firstSplitRemaining);
        Assert.Equal(chunkSize, await ReadAndPersistAsync());

        if (tailWasSplit)
        {
            remaining = chunkSize;
            Assert.Equal(chunkSize, await ReadAndPersistAsync());
        }
        else
        {
            var failure = await Assert.ThrowsAsync<DownloadBodyTooSlowException>(ReadAndPersistAsync);
            Assert.NotNull(failure.EstimatedRemainingSeconds);
        }

        async Task<int> ReadAndPersistAsync()
        {
            var bytes = await stream.ReadAsync(buffer.AsMemory());
            remaining -= bytes;
            return bytes;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedBodyWithoutContentLengthIsNotRejectedAsSlow(bool knownSize)
    {
        var clock = new ManualTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TimedChunkReadStream(clock,
                ((byte)'o', TimeSpan.FromSeconds(5)), ((byte)'k', TimeSpan.FromSeconds(5))))
        };
        long transferred = 0;
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(10), minimumBodyBytesPerSecond: 2048,
            timeProvider: clock, reportBodyBytes: bytes => transferred += bytes,
            expectedContentLength: knownSize ? 2 : null);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, transferred);
    }

    [Fact]
    public async Task FinishingSoonUsesExpectedSizeAndCompletesWithItsOriginalHash()
    {
        var clock = new ManualTimeProvider();
        var handler = new CallbackRequestHandler((_, request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new TimedChunkReadStream(clock,
                ((byte)'s', TimeSpan.FromSeconds(5)), ((byte)'l', TimeSpan.FromSeconds(5)),
                ((byte)'o', TimeSpan.FromSeconds(5)), ((byte)'w', TimeSpan.FromSeconds(5))))
        }));
        using var client = CreateClient(handler);
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "finishing.txt");
            await CreateExecutor(client, new DownloadRetryOptions(), timeProvider: clock).DownloadFileAsync(
                "https://example.test/finishing.txt", DownloadSourcePreference.Official, "ThirdParty",
                destination, Convert.ToHexString(SHA1.HashData("slow"u8)), 4, CancellationToken.None);
            Assert.Equal("slow", await File.ReadAllTextAsync(destination));
            Assert.Single(handler.RequestUris);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task WindowExcludesOutsideReadTimeAndResetsAfterFastTransfers()
    {
        var clock = new ManualTimeProvider();
        var source = new TimedChunkReadStream(clock,
            ((byte)'s', TimeSpan.Zero),
            ((byte)'a', TimeSpan.FromSeconds(5)), ((byte)'b', TimeSpan.FromSeconds(5)),
            ((byte)'c', TimeSpan.FromSeconds(5)), ((byte)'d', TimeSpan.FromSeconds(5)),
            ((byte)'e', TimeSpan.FromSeconds(5)), ((byte)'f', TimeSpan.FromSeconds(5))) { BytesPerChunk = 20 * 1024 };
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        };
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(10), minimumBodyBytesPerSecond: 2048,
            timeProvider: clock, expectedContentLength: 1024 * 1024);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[20 * 1024];
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(buffer.Length, await stream.ReadAsync(buffer));
            // Model time spent in the outer bandwidth limiter or file writer.
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        source.BytesPerChunk = 1;
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(1, await stream.ReadAsync(buffer));
        var failure = await Assert.ThrowsAsync<DownloadBodyTooSlowException>(() => stream.ReadAsync(buffer.AsMemory()).AsTask());
        Assert.Equal(2, failure.BytesRead);
        Assert.Equal(TimeSpan.FromSeconds(10), failure.ReadDuration);
    }

    [Fact]
    public async Task UnknownLengthSlowBodyCountsBytesConsumedByItsEofCheck()
    {
        var clock = new ManualTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TimedChunkReadStream(clock,
                ((byte)'s', TimeSpan.Zero), ((byte)'a', TimeSpan.FromSeconds(5)), ((byte)'b', TimeSpan.FromSeconds(5)),
                ((byte)'c', TimeSpan.FromSeconds(1))))
        };
        long transferred = 0;
        await DownloadResponseThrottler.ApplyAsync(response, null, CancellationToken.None,
            slowBodyReadThreshold: TimeSpan.FromSeconds(10), minimumBodyBytesPerSecond: 2048,
            timeProvider: clock, reportBodyBytes: bytes => transferred += bytes);
        await Assert.ThrowsAsync<DownloadBodyTooSlowException>(() => response.Content.ReadAsStringAsync());
        Assert.Equal(4, transferred);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowFileRetriesRedirectEntryWithinBudgetBeforeTryingAnotherSource(bool fallbackSucceeds)
    {
        var clock = new ManualTimeProvider();
        var payload = Encoding.UTF8.GetBytes(new string('d', 64));
        var health = new DownloadHostHealthTracker();
        var handler = new CallbackRequestHandler((number, request, _) =>
        {
            if (request.RequestUri!.Host == "bmclapi2.bangbang93.com")
            {
                var redirect = CreateResponse(HttpStatusCode.Found, "", request);
                redirect.Headers.Location = new Uri($"https://node-{number}.example/file");
                return Task.FromResult(redirect);
            }
            if (fallbackSucceeds && request.RequestUri.Host == "piston-meta.mojang.com")
                return Task.FromResult(CreateResponse(HttpStatusCode.OK, Encoding.UTF8.GetString(payload), request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new TimedChunkReadStream(clock,
                    ((byte)'x', TimeSpan.Zero), ((byte)'x', TimeSpan.FromSeconds(5)), ((byte)'x', TimeSpan.FromSeconds(5))))
            });
        });
        using var client = CreateClient(handler);
        var logger = new CollectingLogger();
        var executor = CreateExecutor(client, new DownloadRetryOptions { RetryDelay = TimeSpan.Zero },
            logger, clock, health);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "file.bin");
        await File.WriteAllTextAsync(destination, "previous");
        try
        {
            var download = executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.BmclApi, "Mojang",
                destination, Convert.ToHexString(SHA1.HashData(payload)), payload.Length, CancellationToken.None);
            if (fallbackSucceeds)
            {
                await download;
                Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            }
            else
            {
                var failure = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() => download);
                Assert.Equal(8, failure.Failures.Count);
                Assert.All(failure.Failures, error => Assert.IsType<DownloadBodyTooSlowException>(error));
                Assert.Equal("previous", await File.ReadAllTextAsync(destination));
            }
            Assert.Equal(4, handler.RequestUris.Count(uri => uri.Host == "bmclapi2.bangbang93.com"));
            Assert.Equal(fallbackSucceeds ? 1 : 4, handler.RequestUris.Count(uri => uri.Host == "piston-meta.mojang.com"));
            Assert.All(handler.RequestUris.Take(8), uri => Assert.NotEqual("piston-meta.mojang.com", uri.Host));
            Assert.False(health.ShouldAvoid("BmclApiMojang", "bmclapi2.bangbang93.com"));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("SlowReadBytesPerSecond=", StringComparison.Ordinal));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RuntimeRequestsKeepWatchdogEnabledThroughTheFinalAttempt()
    {
        var clock = new ManualTimeProvider();
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new TimedChunkReadStream(clock,
                    ((byte)'x', TimeSpan.Zero), ((byte)'x', TimeSpan.FromSeconds(5)), ((byte)'x', TimeSpan.FromSeconds(5))))
            };
            response.Content.Headers.ContentLength = 1000;
            return Task.FromResult(response);
        });
        using var client = CreateClient(handler);
        var executor = new MinecraftDownloadRequestExecutor(client, category: DownloadConcurrencyCategory.Runtime,
            retryOptions: new DownloadRetryOptions { RetryDelay = TimeSpan.Zero }, timeProvider: clock);
        var failure = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
            executor.ExecuteAsync("https://example.test/runtime", DownloadSourcePreference.Official, "ThirdParty",
                (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None));
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.All(failure.Failures, error => Assert.IsType<DownloadBodyTooSlowException>(error));
    }

    [Fact]
    public async Task MetadataReadsDoNotEnableTheOrdinaryFileWatchdog()
    {
        var clock = new ManualTimeProvider();
        var handler = new CallbackRequestHandler((_, request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new TimedChunkReadStream(clock,
                ((byte)'s', TimeSpan.Zero), ((byte)'l', TimeSpan.FromSeconds(6)),
                ((byte)'o', TimeSpan.FromSeconds(6)), ((byte)'w', TimeSpan.FromSeconds(6))))
        }));
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, new DownloadRetryOptions(), timeProvider: clock);
        var text = await executor.ExecuteAsync("https://example.test/slow.txt", DownloadSourcePreference.Official,
            "ThirdParty", (context, token) => context.Response.Content.ReadAsStringAsync(token), CancellationToken.None);
        Assert.Equal("slow", text);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task FourthFileSourceRoundRecoversAfterThreeTransientlyFailedRounds()
    {
        var handler = new CallbackRequestHandler((number, request, _) => Task.FromResult(CreateResponse(
            number <= 6 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, number <= 6 ? "" : "good", request)));
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, new DownloadRetryOptions { RetryDelay = TimeSpan.Zero });
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "library.jar");
            await executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.Official, "Mojang", destination,
                Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None);
            Assert.Equal("good", await File.ReadAllTextAsync(destination));
            Assert.Equal(7, handler.RequestUris.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledBodyStillTimesOutAndPreservesExistingFile(bool receivedFirstByte)
    {
        var handler = new CallbackRequestHandler((_, request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new FaultingReadStream(receivedFirstByte ? [(byte)'p'] : [], stall: true))
        }));
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, new DownloadRetryOptions
        {
            MaxFileAttemptsPerSource = 1,
            FirstByteTimeout = TimeSpan.FromMilliseconds(50),
            BodyIdleTimeout = TimeSpan.FromMilliseconds(50)
        });
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "client.jar");
            await File.WriteAllTextAsync(destination, "previous");
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.DownloadFileAsync("https://example.test/client.jar", DownloadSourcePreference.Official, "ThirdParty",
                    destination, Convert.ToHexString(SHA1.HashData("good"u8)), 4, CancellationToken.None));
            Assert.Equal(receivedFirstByte ? DownloadFailureReason.BodyIdleTimeout : DownloadFailureReason.FirstByteTimeout,
                Assert.IsType<DownloadTimeoutException>(Assert.Single(exception.Failures)).Reason);
            Assert.Equal("previous", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("Mojang", ManifestUrl, BmclManifestUrl)]
    [InlineData(
        "NeoForge",
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.234/neoforge-21.1.234-installer.jar",
        "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.234/neoforge-21.1.234-installer.jar")]
    public void CanonicalSourceMappingsRoundTrip(
        string categoryHint,
        string officialUrl,
        string bmclUrl)
    {
        var resolvedBmcl = MinecraftDownloadSourceResolver.ResolveRequest(
            officialUrl,
            DownloadSourcePreference.BmclApi,
            useBmclApi: true,
            categoryHint: categoryHint);
        var resolvedOfficial = MinecraftDownloadSourceResolver.ResolveRequest(
            bmclUrl,
            DownloadSourcePreference.Official,
            useBmclApi: false,
            categoryHint);

        Assert.Equal(bmclUrl, resolvedBmcl.ActualUrl);
        Assert.Equal(officialUrl, resolvedOfficial.ActualUrl);
    }

    [Theory]
    [InlineData(DownloadSourcePreference.Official, "piston-meta.mojang.com", "bmclapi2.bangbang93.com")]
    public async Task ManualPreferenceFallsBackAfterPreferredSourceFails(
        DownloadSourcePreference preference,
        string expectedPrimaryHost,
        string expectedFallbackHost)
    {
        var handler = new CallbackRequestHandler((requestNumber, request, _) =>
            Task.FromResult(CreateResponse(
                requestNumber == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                "{}",
                request)));
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient);

        var result = await executor.ExecuteAsync(
            ManifestUrl,
            preference,
            categoryHint: "Mojang",
            async (context, token) => await context.Response.Content.ReadAsStringAsync(token),
            CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal([expectedPrimaryHost, expectedFallbackHost], handler.RequestUris.Select(uri => uri.Host));
    }

    [Fact]
    public async Task TransientStatusRetriesFourTimesThenSwitchesSource()
    {
        var logger = new CollectingLogger();
        var handler = new CallbackRequestHandler((requestNumber, request, _) =>
            Task.FromResult(CreateResponse(
                requestNumber <= 4 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK,
                "{}",
                request)));
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient, logger: logger);

        var result = await executor.ExecuteAsync(
            ManifestUrl,
            DownloadSourcePreference.Official,
            categoryHint: "Mojang",
            async (context, token) => await context.Response.Content.ReadAsStringAsync(token),
            CancellationToken.None);

        Assert.Equal("{}", result);
        Assert.Equal(5, handler.RequestUris.Count);
        Assert.All(handler.RequestUris.Take(4), uri => Assert.Equal(handler.RequestUris[0], uri));
        Assert.NotEqual(handler.RequestUris[0], handler.RequestUris[4]);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains("Download exhausted all sources.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExhaustedChecksumSourcesLogTimeoutAndFinalHttpStatusAtInformationLevel()
    {
        const string checksumUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/26.3.0.95/neoforge-26.3.0.95-installer.jar.sha1";
        var logger = new CollectingLogger { MinimumLevel = LogLevel.Information };
        var handler = new CallbackRequestHandler(async (_, request, token) =>
        {
            if (request.RequestUri!.Host == "maven.neoforged.net")
                await Task.Delay(Timeout.InfiniteTimeSpan, token);

            return CreateResponse(HttpStatusCode.NotFound, string.Empty, request);
        });
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient, new DownloadRetryOptions
        {
            MaxAttemptsPerSource = 2,
            RetryDelay = TimeSpan.Zero,
            ResponseHeadersTimeout = TimeSpan.FromMilliseconds(50)
        }, logger);

        await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
            executor.ExecuteAsync(
                checksumUrl,
                DownloadSourcePreference.Official,
                categoryHint: "NeoForge",
                static (_, _) => Task.FromResult(true),
                CancellationToken.None));

        var summary = Assert.Single(logger.Entries, entry =>
            entry.Message.Contains("Download exhausted all sources.", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, summary.Level);
        Assert.Contains("NeoForgeOfficial: ResponseHeadersTimeout x2", summary.Message, StringComparison.Ordinal);
        Assert.Contains("BmclApiNeoForge: HttpStatus (HTTP 404) x1", summary.Message, StringComparison.Ordinal);
        Assert.Contains(checksumUrl, summary.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task ExhaustedFileSourceRoundLogsOneSummary()
    {
        var logger = new CollectingLogger { MinimumLevel = LogLevel.Information };
        var handler = new CallbackRequestHandler((_, request, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.NotFound, string.Empty, request)));
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient, new DownloadRetryOptions
        {
            RetryDelay = TimeSpan.Zero
        }, logger);
        var directory = CreateTempDirectory();

        try
        {
            await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.DownloadFileAsync(
                    ManifestUrl,
                    DownloadSourcePreference.Official,
                    categoryHint: "Mojang",
                    Path.Combine(directory, "client.jar"),
                    expectedSha1: null,
                    expectedSize: null,
                    CancellationToken.None));

            var summary = Assert.Single(logger.Entries, entry =>
                entry.Message.Contains("Download exhausted all sources.", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Error, summary.Level);
            Assert.Contains("MojangOfficial: HttpStatus (HTTP 404) x1", summary.Message, StringComparison.Ordinal);
            Assert.Contains("BmclApiMojang: HttpStatus (HTTP 404) x1", summary.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HashMismatchSwitchesSourceWithoutRetry()
    {
        var handler = new CallbackRequestHandler((requestNumber, request, _) =>
            Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                requestNumber == 1 ? "baad" : "good",
                request)));
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "library.jar");
        var expectedSha1 = Convert.ToHexString(SHA1.HashData("good"u8.ToArray()));

        try
        {
            await executor.DownloadFileAsync(
                ManifestUrl,
                DownloadSourcePreference.Official,
                categoryHint: "Mojang",
                destination,
                expectedSha1,
                expectedSize: 4,
                CancellationToken.None);

            Assert.Equal("good", await File.ReadAllTextAsync(destination));
            Assert.Equal(2, handler.RequestUris.Count);
            Assert.NotEqual(handler.RequestUris[0], handler.RequestUris[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UserCancellationDuringRetryDelayStopsImmediately()
    {
        var handler = new CallbackRequestHandler((_, request, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.InternalServerError, string.Empty, request)));
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient, new DownloadRetryOptions
        {
            MaxAttemptsPerSource = 4,
            RetryDelay = TimeSpan.FromSeconds(10),
            ResponseHeadersTimeout = TimeSpan.FromSeconds(1),
            BodyIdleTimeout = TimeSpan.FromSeconds(1),
            MaxRedirects = 10
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(
                ManifestUrl,
                DownloadSourcePreference.Official,
                categoryHint: "Mojang",
                static (_, _) => Task.FromResult(true),
                cancellation.Token));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task FileDownloadSwitchesSourceAndRestartsAfterInterruptedBody()
    {
        var handler = new CallbackRequestHandler((requestNumber, request, _) =>
        {
            if (requestNumber == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new FaultingReadStream("part"u8.ToArray()))
                };
                first.Headers.ETag = new EntityTagHeaderValue("\"stable\"");
                first.Content.Headers.ContentLength = 8;
                return Task.FromResult(first);
            }

            Assert.Null(request.Headers.Range);
            Assert.False(request.Headers.Contains("If-Range"));
            var fallback = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent("partdata"u8.ToArray())
            };
            fallback.Headers.ETag = new EntityTagHeaderValue("\"fallback\"");
            fallback.Content.Headers.ContentLength = 8;
            return Task.FromResult(fallback);
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "client.jar");

        try
        {
            await executor.DownloadFileAsync(
                ManifestUrl,
                DownloadSourcePreference.Official,
                "Mojang",
                destination,
                Convert.ToHexString(SHA1.HashData("partdata"u8.ToArray())),
                8,
                CancellationToken.None);

            Assert.Equal("partdata", await File.ReadAllTextAsync(destination));
            Assert.Equal(2, handler.RequestUris.Count);
            Assert.NotEqual(handler.RequestUris[0].Host, handler.RequestUris[1].Host);
            Assert.False(File.Exists(destination + ".part"));
            Assert.False(File.Exists(destination + ".part.meta"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SingleSourceRecoveryRoundResumesWithMatchingValidator()
    {
        var clock = new ManualTimeProvider();
        var remaining = new string('d', 64);
        var payload = Encoding.UTF8.GetBytes("part" + remaining);
        var handler = new CallbackRequestHandler((requestNumber, request, _) =>
        {
            if (requestNumber == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new TimedChunkReadStream(
                        clock,
                        ((byte)'p', TimeSpan.Zero),
                        ((byte)'a', TimeSpan.Zero),
                        ((byte)'r', TimeSpan.Zero),
                        ((byte)'t', TimeSpan.FromSeconds(6))))
                };
                first.Headers.ETag = new EntityTagHeaderValue("\"stable\"");
                first.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(first);
            }

            Assert.Equal("bytes=4-", request.Headers.Range?.ToString());
            Assert.Equal("\"stable\"", request.Headers.GetValues("If-Range").Single());
            var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new StringContent(remaining)
            };
            resumed.Headers.ETag = new EntityTagHeaderValue("\"stable\"");
            resumed.Content.Headers.ContentLength = remaining.Length;
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, payload.Length - 1, payload.Length);
            return Task.FromResult(resumed);
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, CreateSlowBodyOptions(), timeProvider: clock);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "client.jar");
        var logScope = new ForegroundDownloadLogScope(
            logger: null,
            "Test",
            "client.jar",
            destination,
            "https://example.test/client.jar",
            expectedBytes: payload.Length);

        try
        {
            await executor.DownloadFileAsync(
                "https://example.test/client.jar",
                DownloadSourcePreference.Official,
                "ThirdParty",
                destination,
                Convert.ToHexString(SHA1.HashData(payload)),
                payload.Length,
                CancellationToken.None,
                reportAttemptProgress: logScope.BeginSource(),
                reportTransferredBytes: logScope.ReportTransferredBytes);

            Assert.Equal(2, handler.RequestUris.Count);
            Assert.Equal(handler.RequestUris[0], handler.RequestUris[1]);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Equal(payload.Length, logScope.TransferredBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssetSingleFlightDownloadsSameTargetOnce(bool largeAsset)
    {
        var payload = largeAsset
            ? new byte[MinecraftDownloadRequestExecutor.MinimumSegmentedDownloadSize]
            : new byte[MinecraftDownloadRequestExecutor.MinimumSegmentedDownloadSize - 1];
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackRequestHandler(async (_, request, token) =>
        {
            Assert.Equal(largeAsset, request.Headers.Range is not null);
            requestStarted.TrySetResult(true);
            await releaseRequest.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            };
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "assets", "objects", "aa", "asset");
        var sha1 = Convert.ToHexString(SHA1.HashData(payload));

        try
        {
            using var operation = new MinecraftDownloadOperationContext(directory);
            operation.RegisterAsset(destination, sha1, payload.Length);
            var options = DownloadFileOptions.ForAsset(operation, payload.Length);
            var first = executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.Official, "Mojang", destination, sha1, payload.Length, CancellationToken.None, options: options);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.Official, "Mojang", destination, sha1, payload.Length, CancellationToken.None, options: options);
            releaseRequest.TrySetResult(true);

            await Task.WhenAll(first, second);

            Assert.Single(handler.RequestUris);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(2 * 1024 * 1024 - 1, false)]
    [InlineData(2 * 1024 * 1024, true)]
    public async Task NonAssetsUseTheTwoMebibyteSegmentationThreshold(int size, bool expectsRange)
    {
        var payload = new byte[size];
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            Assert.Equal(expectsRange, request.Headers.Range is not null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new ByteArrayContent(payload)
            });
        });
        using var client = CreateClient(handler);
        var directory = CreateTempDirectory();
        try
        {
            await CreateExecutor(client).DownloadFileAsync(ManifestUrl, DownloadSourcePreference.Official,
                "Mojang", Path.Combine(directory, "client.jar"), Convert.ToHexString(SHA1.HashData(payload)),
                payload.Length, CancellationToken.None);
            Assert.Single(handler.RequestUris);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedMirrorNodeFailuresSkipItWhileAnAlternativeWorks(bool networkFailure)
    {
        var clock = new ManualTimeProvider();
        var health = new DownloadHostHealthTracker(clock);
        var officialWorks = true;
        var nodeWorks = false;
        var healthyNodeOnRedispatch = false;
        var dispatches = 0;
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var host = request.RequestUri!.Host;
            if (host == "bmclapi2.bangbang93.com")
            {
                var redirect = CreateResponse(HttpStatusCode.Found, "", request);
                redirect.Headers.Location = new Uri(healthyNodeOnRedispatch && ++dispatches == 2
                    ? "https://healthy.example.test/asset" : "https://node.example.test/asset");
                return Task.FromResult(redirect);
            }
            if (host == "piston-meta.mojang.com")
                return Task.FromResult(CreateResponse(officialWorks ? HttpStatusCode.OK : HttpStatusCode.NotFound, "good", request));
            if (nodeWorks || host == "healthy.example.test")
                return Task.FromResult(CreateResponse(HttpStatusCode.OK, "good", request));
            if (networkFailure)
                throw new HttpRequestException("Mirror node disconnected.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html")
            });
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, hostHealthTracker: health);
        var directory = CreateTempDirectory();
        var hash = Convert.ToHexString(SHA1.HashData("good"u8));
        try
        {
            for (var index = 0; index < 4; index++)
                Assert.True((await DownloadAsync(index)).FallbackUsed);

            Assert.Equal(3, handler.RequestUris.Count(uri => uri.Host == "node.example.test"));
            Assert.Equal(5, handler.RequestUris.Count(uri => uri.Host == "bmclapi2.bangbang93.com"));
            Assert.False(health.ShouldAvoid("BmclApiMojang", "bmclapi2.bangbang93.com"));

            var previousRequests = handler.RequestUris.Count;
            healthyNodeOnRedispatch = true;
            Assert.True((await DownloadAsync(4)).FallbackUsed);
            Assert.Equal(new[] { "bmclapi2.bangbang93.com", "piston-meta.mojang.com" },
                handler.RequestUris.Skip(previousRequests).Select(uri => uri.Host));

            clock.Advance(TimeSpan.FromSeconds(5));
            dispatches = 0;
            previousRequests = handler.RequestUris.Count;
            Assert.False((await DownloadAsync(5)).FallbackUsed);
            Assert.Equal(new[] { "bmclapi2.bangbang93.com", "bmclapi2.bangbang93.com", "healthy.example.test" },
                handler.RequestUris.Skip(previousRequests).Select(uri => uri.Host));

            healthyNodeOnRedispatch = false;
            Assert.True((await DownloadAsync(6)).FallbackUsed);
            officialWorks = false;
            nodeWorks = true;
            for (var index = 0; index < 3; index++)
                health.RecordFailure("MojangOfficial", "piston-meta.mojang.com", DownloadFailureReason.Network);
            previousRequests = handler.RequestUris.Count;
            Assert.False((await DownloadAsync(7)).FallbackUsed);
            Assert.Equal(new[] { "bmclapi2.bangbang93.com", "node.example.test" },
                handler.RequestUris.Skip(previousRequests).Select(uri => uri.Host));
            Assert.Equal("good", await File.ReadAllTextAsync(Path.Combine(directory, "7.bin")));
            Assert.False(health.ShouldAvoidInvalidContentNode("node.example.test"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<ResolvedDownloadRequest> DownloadAsync(int index) =>
            executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.BmclApi, "Mojang",
                Path.Combine(directory, $"{index}.bin"), hash, 4, CancellationToken.None);
    }

    [Fact]
    public async Task MirrorRedispatchIsSharedAcrossFilesAndCancellationReleasesTheSlot()
    {
        var health = new DownloadHostHealthTracker();
        const string cooledNode = "cooled.example.test";
        const string otherCooledNode = "other-cooled.example.test";
        for (var index = 0; index < 3; index++)
        {
            health.RecordInvalidContentNode(cooledNode);
            health.RecordInvalidContentNode(otherCooledNode);
        }

        using var cancellation = new CancellationTokenSource();
        var redispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryRequests = new Dictionary<string, int>();
        var handler = new CallbackRequestHandler(async (_, request, token) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host != "bmclapi2.bangbang93.com")
            {
                Assert.True(uri.Host is "piston-meta.mojang.com" or "healthy.example.test");
                return CreateResponse(HttpStatusCode.OK, "good", request);
            }

            var dispatches = entryRequests[uri.Query] = entryRequests.GetValueOrDefault(uri.Query) + 1;
            if (uri.Query == "?first" && dispatches == 2)
            {
                redispatchStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            var node = uri.Query == "?other" ? otherCooledNode : cooledNode;
            var response = CreateResponse(HttpStatusCode.Found, "", request);
            response.Headers.Location = new Uri($"https://{(dispatches == 1 ? node : "healthy.example.test")}/asset");
            return response;
        });
        using var client = CreateClient(handler);
        var firstExecutor = CreateExecutor(client, hostHealthTracker: health, options: new DownloadRetryOptions
        {
            ResponseHeadersTimeout = TimeSpan.FromSeconds(10),
            RetryDelay = TimeSpan.Zero
        });
        var secondExecutor = CreateExecutor(client, hostHealthTracker: health);
        var directory = CreateTempDirectory();
        var hash = Convert.ToHexString(SHA1.HashData("good"u8));
        var first = DownloadAsync(firstExecutor, "first", cancellation.Token);
        try
        {
            await redispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await DownloadAsync(secondExecutor, "blocked")).FallbackUsed);
            Assert.Equal(1, entryRequests["?blocked"]);
            Assert.False((await DownloadAsync(secondExecutor, "other")).FallbackUsed);
            Assert.Equal(2, entryRequests["?other"]);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False((await DownloadAsync(secondExecutor, "next")).FallbackUsed);
            Assert.Equal(2, entryRequests["?next"]);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAny(first);
            Directory.Delete(directory, recursive: true);
        }

        Task<ResolvedDownloadRequest> DownloadAsync(MinecraftDownloadRequestExecutor executor, string file,
            CancellationToken cancellationToken = default) =>
            executor.DownloadFileAsync($"https://piston-meta.mojang.com/v1/packages/test/version.json?{file}",
                DownloadSourcePreference.BmclApi, "Mojang",
                Path.Combine(directory, $"{file}.bin"), hash, 4, cancellationToken);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task LongEstimatedTransferSwitchesAfterTwoWindows(bool segmentedProbe, bool alternateRedirectsToSameNode, bool smallFile)
    {
        var clock = new ManualTimeProvider();
        // At 64 KiB per ten seconds the small file estimates 36.25 s, then 26.25 s.
        var payload = new byte[smallFile ? 360 * 1024 : MinecraftDownloadRequestExecutor.MinimumSegmentedDownloadSize];
        var slowNodeRequests = 0;
        var handler = new CallbackRequestHandler((_, request, _) =>
        {
            var host = request.RequestUri!.Host;
            if (host == "bmclapi2.bangbang93.com"
                || alternateRedirectsToSameNode && host == "piston-meta.mojang.com")
            {
                var redirect = CreateResponse(HttpStatusCode.Found, "", request);
                redirect.Headers.Location = new Uri("https://slow.example.test/asset");
                return Task.FromResult(redirect);
            }
            var window = TimeSpan.FromSeconds(10);
            var slowResponse = host == "slow.example.test" && ++slowNodeRequests == 1;
            if (slowResponse)
                Assert.Equal(segmentedProbe, request.Headers.Range is not null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = slowResponse
                    ? new StreamContent(new TimedChunkReadStream(clock, ((byte)1, TimeSpan.Zero),
                        ((byte)1, window), ((byte)1, window)) { BytesPerChunk = 64 * 1024 })
                    : new ByteArrayContent(payload)
            });
        });
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, timeProvider: clock);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "big.bin");
        try
        {
            var options = new DownloadFileOptions(segmentedProbe
                ? DownloadPersistenceMode.TaskScopedResumable : DownloadPersistenceMode.LightweightAtomic);
            var result = await executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.BmclApi, "Mojang",
                destination, Convert.ToHexString(SHA1.HashData(payload)), payload.Length, CancellationToken.None,
                options: options);

            Assert.Equal(!alternateRedirectsToSameNode, result.FallbackUsed);
            Assert.Equal(TimeSpan.FromSeconds(20).Ticks, clock.GetTimestamp());
            Assert.Equal(alternateRedirectsToSameNode
                    ? ["bmclapi2.bangbang93.com", "slow.example.test", "piston-meta.mojang.com", "bmclapi2.bangbang93.com", "slow.example.test"]
                    : new[] { "bmclapi2.bangbang93.com", "slow.example.test", "piston-meta.mojang.com" },
                handler.RequestUris.Select(uri => uri.Host));
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowLastRangeSwitchesAtThePersistedCursorAndCanReturnToTheOriginal(bool alternateIgnoresRange)
    {
        var clock = new ManualTimeProvider();
        const int probeLength = 1024 * 1024;
        var payload = new byte[probeLength + 768 * 1024];
        var probeWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowWindows = 0;
        var ranges = new List<(string Host, long Offset)>();
        var handler = new CallbackRequestHandler(async (_, request, token) =>
        {
            var host = request.RequestUri!.Host;
            var offset = request.Headers.Range!.Ranges.Single().From!.Value;
            ranges.Add((host, offset));
            var alternate = host != "bmclapi2.bangbang93.com";
            if (alternate && alternateIgnoresRange)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

            var end = offset == 0 ? probeLength : payload.Length;
            var slow = offset == probeLength && !alternate && Interlocked.Increment(ref slowWindows) == 1;
            if (slow)
                await probeWritten.Task.WaitAsync(token);
            HttpContent content = slow
                ? new StreamContent(new TimedChunkReadStream(clock,
                    ((byte)0, TimeSpan.Zero), ((byte)0, TimeSpan.FromSeconds(10)),
                    ((byte)0, TimeSpan.FromSeconds(10)), ((byte)0, TimeSpan.FromSeconds(10)))
                    { BytesPerChunk = 64 * 1024 })
                : new ByteArrayContent(payload, (int)offset, (int)(end - offset));
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end - 1, payload.Length);
            content.Headers.ContentLength = end - offset;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content, RequestMessage = request };
            response.Headers.ETag = new EntityTagHeaderValue(alternate ? "\"official\"" : "\"mirror\"");
            return response;
        });
        using var client = CreateClient(handler);
        var directory = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(directory, "slow-range.jar");
            var result = await CreateExecutor(client, timeProvider: clock, minimumSegmentedDownloadSize: probeLength + 1)
                .DownloadFileAsync(ManifestUrl, DownloadSourcePreference.BmclApi, "Mojang", destination,
                    Convert.ToHexString(SHA1.HashData(payload)), payload.Length, CancellationToken.None,
                    reportAttemptProgress: (_, written, _) =>
                    {
                        if (written >= probeLength)
                            probeWritten.TrySetResult();
                    },
                    options: new DownloadFileOptions(DownloadPersistenceMode.TaskScopedResumable));
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Equal(!alternateIgnoresRange, result.FallbackUsed);
            Assert.Equal(("bmclapi2.bangbang93.com", 0L), ranges[0]);
            Assert.Equal(("bmclapi2.bangbang93.com", (long)probeLength), ranges[1]);
            Assert.Equal("piston-meta.mojang.com", ranges[2].Host);
            Assert.InRange(ranges[2].Offset, probeLength + 1, payload.Length - 1);
            Assert.Equal(alternateIgnoresRange ? 4 : 3, ranges.Count);
            if (alternateIgnoresRange)
                Assert.Equal(("bmclapi2.bangbang93.com", ranges[2].Offset), ranges[3]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeTransferContinuesWhenBackupIsUnderRetryAfter(bool segmentedProbe)
    {
        var clock = new ManualTimeProvider();
        var concurrencyController = new DownloadHostConcurrencyController(clock, (delay, _) =>
        {
            clock.Advance(delay);
            return ValueTask.CompletedTask;
        });
        concurrencyController.DeferRequests(DownloadHostConcurrencyController.NormalizeOrigin(new Uri(ManifestUrl)),
            TimeSpan.FromSeconds(60));
        const int chunkSize = 64 * 1024;
        var payload = new byte[MinecraftDownloadRequestExecutor.MinimumSegmentedDownloadSize];
        var chunks = Enumerable.Range(0, payload.Length / chunkSize)
            .Select(index => ((byte)0, index is 1 or 2 ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
            .ToArray();
        var handler = new CallbackRequestHandler((_, request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = request.RequestUri!.Host == "bmclapi2.bangbang93.com"
                ? new StreamContent(new TimedChunkReadStream(clock, chunks) { BytesPerChunk = chunkSize })
                : new ByteArrayContent(payload)
        }));
        using var client = CreateClient(handler);
        var executor = CreateExecutor(client, timeProvider: clock, hostConcurrencyController: concurrencyController);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "big.bin");
        try
        {
            var options = new DownloadFileOptions(segmentedProbe
                ? DownloadPersistenceMode.TaskScopedResumable : DownloadPersistenceMode.LightweightAtomic);
            var result = await executor.DownloadFileAsync(ManifestUrl, DownloadSourcePreference.BmclApi, "Mojang",
                destination, Convert.ToHexString(SHA1.HashData(payload)), payload.Length, CancellationToken.None,
                options: options);

            Assert.False(result.FallbackUsed);
            Assert.Equal("bmclapi2.bangbang93.com", Assert.Single(handler.RequestUris).Host);
            Assert.Equal(TimeSpan.FromSeconds(20).Ticks, clock.GetTimestamp());
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDestinationFailureDoesNotStartNetworkRetry()
    {
        var handler = new CallbackRequestHandler((_, request, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "payload", request)));
        using var httpClient = CreateClient(handler);
        var executor = CreateExecutor(httpClient);
        var directory = CreateTempDirectory();
        var fileAsDirectory = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(fileAsDirectory, "file");

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(
                () => executor.DownloadFileAsync(
                    ManifestUrl,
                    DownloadSourcePreference.Official,
                    categoryHint: "Mojang",
                    Path.Combine(fileAsDirectory, "client.jar"),
                    expectedSha1: null,
                    expectedSize: null,
                    CancellationToken.None));

            Assert.Empty(handler.RequestUris);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SensitiveCurseForgeHeaderIsNotForwardedToRedirectedCdn()
    {
        var handler = new CallbackRequestHandler((requestNumber, request, _) =>
        {
            if (requestNumber == 1)
            {
                var redirect = CreateResponse(HttpStatusCode.Found, string.Empty, request);
                redirect.Headers.Location = new Uri("https://edge.forgecdn.net/files/mod.jar");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, "payload", request));
        });
        using var client = CreateClient(handler);
        var transport = new MinecraftDownloadTransport(client, new DownloadRetryOptions());

        await using var result = await transport.SendAsync(
            "https://api.curseforge.com/v1/mods/1/files/2/download-url",
            CancellationToken.None,
            sensitiveHeaders: DownloadRequestHeaders.CurseForgeApiKey("secret"));

        Assert.Equal(2, handler.RequestHeaders.Count);
        Assert.Equal("secret", handler.RequestHeaders[0]["x-api-key"]);
        Assert.False(handler.RequestHeaders[1].ContainsKey("x-api-key"));
        result.Response.Dispose();
    }

    [Theory]
    [InlineData("file:///C:/temp/file.jar")]
    public async Task NonHttpInitialAddressIsRejectedBeforeRequest(string url)
    {
        var handler = new CallbackRequestHandler((_, request, _) =>
            Task.FromResult(CreateResponse(HttpStatusCode.OK, "payload", request)));
        using var client = CreateClient(handler);
        var transport = new MinecraftDownloadTransport(client, new DownloadRetryOptions());

        var exception = await Assert.ThrowsAsync<DownloadAttemptException>(() =>
            transport.SendAsync(url, CancellationToken.None));

        Assert.Equal(DownloadFailureReason.InvalidRedirect, exception.Reason);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task SuccessNoResultAndFailureLogsRedactSensitiveUriComponents()
    {
        const string signedUrl = "https://example.test/files/signed-path-secret/mod.jar?token=super-secret#private-fragment";
        var logger = new CollectingLogger();

        using (var successClient = CreateClient(new CallbackRequestHandler((_, request, _) =>
                   Task.FromResult(CreateResponse(HttpStatusCode.OK, "payload", request)))))
        {
            var executor = CreateExecutor(successClient, logger: logger);
            await executor.ExecuteAsync(
                signedUrl,
                DownloadSourcePreference.Official,
                categoryHint: null,
                static (_, _) => Task.FromResult(true),
                CancellationToken.None);
        }

        using (var noResultClient = CreateClient(new CallbackRequestHandler((_, request, _) =>
                   Task.FromResult(CreateResponse(HttpStatusCode.NotFound, string.Empty, request)))))
        {
            var executor = CreateExecutor(noResultClient, logger: logger);
            var result = await executor.ExecuteLookupAsync(
                signedUrl,
                DownloadSourcePreference.Official,
                categoryHint: null,
                static (_, _) => Task.FromResult(true),
                status => status == HttpStatusCode.NotFound,
                CancellationToken.None);
            Assert.False(result.Found);
        }

        using (var failureClient = CreateClient(new CallbackRequestHandler((_, request, _) =>
                   Task.FromResult(CreateResponse(HttpStatusCode.Forbidden, string.Empty, request)))))
        {
            var executor = CreateExecutor(failureClient, logger: logger);
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.ExecuteAsync(
                    signedUrl,
                    DownloadSourcePreference.Official,
                    categoryHint: null,
                    static (_, _) => Task.FromResult(true),
                    CancellationToken.None));
            Assert.DoesNotContain("signed-path-secret", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", exception.Message, StringComparison.Ordinal);
        }

        Assert.NotEmpty(logger.Messages);
        Assert.Contains(logger.Messages, message => message.Contains("https://example.test/<redacted-path>", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("signed-path-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("super-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("private-fragment", StringComparison.Ordinal));
    }

    private static MinecraftDownloadRequestExecutor CreateExecutor(
        HttpClient httpClient,
        DownloadRetryOptions? options = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        DownloadHostHealthTracker? hostHealthTracker = null,
        DownloadHostConcurrencyController? hostConcurrencyController = null,
        long minimumSegmentedDownloadSize = MinecraftDownloadRequestExecutor.MinimumSegmentedDownloadSize)
    {
        return new MinecraftDownloadRequestExecutor(
            httpClient,
            limiter: new ImportConcurrencyLimiter(),
            logger: logger,
            hostConcurrencyController: hostConcurrencyController ?? new DownloadHostConcurrencyController(
                delayAsync: static (_, _) => ValueTask.CompletedTask),
            nextRetryJitter: () => 0,
            bmclApiRequestRateLimiter: new BmclApiRequestRateLimiter(TimeSpan.Zero),
            timeProvider: timeProvider,
            minimumSegmentedDownloadSize: minimumSegmentedDownloadSize,
            hostHealthTracker: hostHealthTracker,
            retryOptions: options ?? new DownloadRetryOptions
            {
                MaxAttemptsPerSource = 4,
                RetryDelay = TimeSpan.Zero,
                ResponseHeadersTimeout = TimeSpan.FromSeconds(1),
                BodyIdleTimeout = TimeSpan.FromSeconds(1),
                MaxRedirects = 10
            });
    }

    private static DownloadRetryOptions CreateSlowBodyOptions() => new()
    {
        MaxAttemptsPerSource = 4,
        RetryDelay = TimeSpan.Zero,
        ResponseHeadersTimeout = TimeSpan.FromSeconds(1),
        FirstByteTimeout = TimeSpan.FromSeconds(20),
        BodyIdleTimeout = TimeSpan.FromSeconds(20),
        SlowBodyReadThreshold = TimeSpan.FromSeconds(5),
        MinimumBodyBytesPerSecond = 1024,
        MaxRedirects = 10
    };

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        string content,
        HttpRequestMessage request)
    {
        return new HttpResponseMessage(statusCode)
        {
            RequestMessage = request,
            Content = new StringContent(content)
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "launcher-download-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CallbackRequestHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        private int requestCount;

        public List<Uri> RequestUris { get; } = [];
        public List<Dictionary<string, string>> RequestHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            RequestHeaders.Add(request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase));
            return callback(Interlocked.Increment(ref requestCount), request, cancellationToken);
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<LogEntry> Entries { get; } = [];
        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            Messages.Add(message);
            Entries.Add(new LogEntry(logLevel, message, exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class FaultingReadStream(byte[] firstChunk, bool stall = false) : Stream
    {
        private bool returnedFirstChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (stall && (returnedFirstChunk || firstChunk.Length == 0))
                return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
            if (returnedFirstChunk)
                return ValueTask.FromException<int>(new IOException("The network stream ended."));

            returnedFirstChunk = true;
            firstChunk.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(firstChunk.Length);
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private readonly DateTimeOffset initialUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => initialUtc + TimeSpan.FromTicks(Interlocked.Read(ref timestamp));
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }

    private sealed class TimedChunkReadStream(
        ManualTimeProvider timeProvider,
        params (byte Value, TimeSpan Elapsed)[] chunks) : Stream
    {
        private int readIndex;
        private int chunkOffset;
        public int BytesPerChunk { get; set; } = 1;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (readIndex >= chunks.Length || buffer.IsEmpty)
                return 0;
            var chunk = chunks[readIndex];
            if (chunkOffset == 0) timeProvider.Advance(chunk.Elapsed);
            var count = Math.Min(buffer.Length, BytesPerChunk - chunkOffset);
            buffer[..count].Fill(chunk.Value);
            chunkOffset += count;
            if (chunkOffset == BytesPerChunk) { readIndex++; chunkOffset = 0; }
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
