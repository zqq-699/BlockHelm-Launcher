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
            LauncherDefaults.DefaultDownloadSourcePreference,
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
        Assert.Contains("https://maven.neoforged.net/<redacted-path>", summary.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(checksumUrl, summary.Message, StringComparison.Ordinal);
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
            MaxFileSourceRounds = 1,
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
                LauncherDefaults.DefaultDownloadSourcePreference,
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
                first.Content.Headers.ContentLength = 8;
                return Task.FromResult(first);
            }

            Assert.Equal("bytes=4-", request.Headers.Range?.ToString());
            Assert.Equal("\"stable\"", request.Headers.GetValues("If-Range").Single());
            var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent("data"u8.ToArray())
            };
            resumed.Headers.ETag = new EntityTagHeaderValue("\"stable\"");
            resumed.Content.Headers.ContentLength = 4;
            resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 7, 8);
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
            expectedBytes: 8);

        try
        {
            await executor.DownloadFileAsync(
                "https://example.test/client.jar",
                DownloadSourcePreference.Official,
                "ThirdParty",
                destination,
                Convert.ToHexString(SHA1.HashData("partdata"u8.ToArray())),
                8,
                CancellationToken.None,
                reportAttemptProgress: logScope.BeginSource(),
                reportTransferredBytes: logScope.ReportTransferredBytes);

            Assert.Equal(2, handler.RequestUris.Count);
            Assert.Equal(handler.RequestUris[0], handler.RequestUris[1]);
            Assert.Equal("partdata", await File.ReadAllTextAsync(destination));
            Assert.Equal(8, logScope.TransferredBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LightweightAssetSingleFlightDownloadsSameTargetOnce()
    {
        var payload = "asset-data"u8.ToArray();
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackRequestHandler(async (_, request, token) =>
        {
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
            var options = new DownloadFileOptions(DownloadPersistenceMode.LightweightAtomic, operation);
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
        DownloadHostHealthTracker? hostHealthTracker = null)
    {
        return new MinecraftDownloadRequestExecutor(
            httpClient,
            limiter: new ImportConcurrencyLimiter(),
            logger: logger,
            hostConcurrencyController: new DownloadHostConcurrencyController(
                maximumJitter: TimeSpan.Zero,
                nextJitter: () => 0,
                delayAsync: static (_, _) => ValueTask.CompletedTask),
            nextRetryJitter: () => 0,
            bmclApiRequestRateLimiter: new BmclApiRequestRateLimiter(TimeSpan.Zero),
            timeProvider: timeProvider,
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

    private sealed class FaultingReadStream(byte[] firstChunk) : Stream
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
            if (returnedFirstChunk)
                return ValueTask.FromException<int>(new IOException("The network stream ended."));

            returnedFirstChunk = true;
            firstChunk.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(firstChunk.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }

    private sealed class TimedChunkReadStream(
        ManualTimeProvider timeProvider,
        params (byte Value, TimeSpan Elapsed)[] chunks) : Stream
    {
        private int readIndex;

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
            cancellationToken.ThrowIfCancellationRequested();
            if (readIndex >= chunks.Length)
                return ValueTask.FromResult(0);

            var chunk = chunks[readIndex++];
            timeProvider.Advance(chunk.Elapsed);
            buffer.Span[0] = chunk.Value;
            return ValueTask.FromResult(1);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
