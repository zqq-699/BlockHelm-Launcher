/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class SegmentedDownloadTests : TestTempDirectory
{
    private const string DownloadUrl = "https://downloads.example.test/client.jar";
    private const int TestSegmentedDownloadSize = 512 * 1024;
    private static readonly byte[] LargePayload = CreatePayload(
        TestSegmentedDownloadSize);

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

        await CreateExecutor(client).DownloadFileAsync(
            DownloadUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destination,
            new DownloadIntegrityExpectation(
                LargePayload.Length,
                [(HashAlgorithmName.SHA512, Sha512(LargePayload))]),
            CancellationToken.None);

        Assert.Contains(handler.RangeHeaders, range => range is not null);
        Assert.Equal(LargePayload, await File.ReadAllBytesAsync(destination));
        AssertNoPendingFiles(destination);
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
        var handler = new RecordingRequestHandler((_, request, _) =>
        {
            started.TrySetResult(true);
            return Task.FromResult(CreateBlockingPartialResponse(request, LargePayload.Length));
        });
        using var client = CreateClient(handler);
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(4);
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
        IImportConcurrencyLimiter? limiter = null)
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
                MaxAttemptsPerSource = 1,
                MaxFileSourceRounds = 1,
                RetryDelay = TimeSpan.Zero,
                ResponseHeadersTimeout = TimeSpan.FromSeconds(5),
                FirstByteTimeout = TimeSpan.FromSeconds(5),
                BodyIdleTimeout = TimeSpan.FromSeconds(5),
                MaxRedirects = 10
            },
            hostConcurrencyController: new DownloadHostConcurrencyController(
                maximumJitter: TimeSpan.Zero,
                nextJitter: () => 0,
                delayAsync: static (_, _) => ValueTask.CompletedTask),
            bmclApiRequestRateLimiter: new BmclApiRequestRateLimiter(TimeSpan.Zero),
            nextRetryJitter: () => 0,
            segmentedDownloadCoordinator: new SegmentedDownloadCoordinator(),
            minimumSegmentedDownloadSize: TestSegmentedDownloadSize);
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
        bool corrupt = false)
    {
        var range = Assert.Single(request.Headers.Range!.Ranges);
        var start = range.From!.Value;
        var end = range.To ?? payload.Length - 1;
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
