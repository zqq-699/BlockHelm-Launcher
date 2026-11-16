/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Launcher.Infrastructure.Accounts.ThirdParty;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Accounts;

public sealed class AuthlibInjectorProvisioningServiceTests : TestTempDirectory
{
    private const string OfficialArtifactUrl = "https://authlib-injector.yushi.moe/artifact/55/authlib-injector-1.2.7.jar";
    private const string MirrorArtifactUrl = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/55/authlib-injector-1.2.7.jar";

    [Fact]
    public async Task DownloadsVerifiesAndReusesLatestArtifact()
    {
        var bytes = Encoding.UTF8.GetBytes("verified-jar");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifactRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal))
                return Task.FromResult(Json(Metadata(hash)));
            artifactRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });
        var service = new AuthlibInjectorProvisioningService(
            new HttpClient(handler),
            Path.Combine(TempRoot, "authlib"));

        var first = await service.EnsureAvailableAsync();
        var second = await service.EnsureAvailableAsync();

        Assert.Equal(first, second);
        Assert.Equal(55, first.BuildNumber);
        Assert.Equal("1.2.7", first.Version);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.FilePath));
        Assert.Equal(1, artifactRequests);
    }

    [Fact]
    public async Task RejectsChecksumMismatchWhenNoCacheExists()
    {
        var service = new AuthlibInjectorProvisioningService(
            new HttpClient(new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
                    ? Json(Metadata(new string('0', 64)))
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes("tampered"))
                    }))),
            Path.Combine(TempRoot, "authlib"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAvailableAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(TempRoot, "authlib")));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("http")]
    [InlineData("invalid-json")]
    [InlineData("invalid-metadata")]
    public async Task UsesMirrorWhenOfficialMetadataFailsWithEmptyCache(string failure)
    {
        var bytes = Encoding.UTF8.GetBytes("verified-mirror-jar");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri.AbsoluteUri);
            if (uri.Host == "authlib-injector.yushi.moe")
            {
                return failure switch
                {
                    "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated timeout.")),
                    "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                    "invalid-json" => Task.FromResult(Json("not-json")),
                    _ => Task.FromResult(Json(Metadata("invalid-hash")))
                };
            }
            return Task.FromResult(uri.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
                ? Json(Metadata(hash, MirrorArtifactUrl))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });
        var cacheDirectory = Path.Combine(TempRoot, "authlib");
        var service = new AuthlibInjectorProvisioningService(new HttpClient(handler), cacheDirectory);

        var artifact = await service.EnsureAvailableAsync();

        Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact.FilePath));
        Assert.Contains("https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json", requests);
        Assert.Contains(MirrorArtifactUrl, requests);
        Assert.True(File.Exists(artifact.FilePath + ".manifest.json"));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData(OfficialArtifactUrl, "http")]
    [InlineData(MirrorArtifactUrl, "http")]
    [InlineData(OfficialArtifactUrl, "checksum")]
    [InlineData(OfficialArtifactUrl, "interrupted")]
    public async Task SwitchesArtifactSourceAndVerifiesDownloadedContent(string downloadUrl, string failure)
    {
        var bytes = Encoding.UTF8.GetBytes("verified-fallback-jar");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var artifactRequests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal))
                return Task.FromResult(Json(Metadata(hash, downloadUrl)));
            artifactRequests.Add(uri.AbsoluteUri);
            if (uri.AbsoluteUri == downloadUrl)
            {
                return Task.FromResult(failure == "http"
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = failure == "interrupted"
                            ? new StreamContent(new InterruptedReadStream(bytes))
                            : new ByteArrayContent(Encoding.UTF8.GetBytes("tampered"))
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });
        var cacheDirectory = Path.Combine(TempRoot, "authlib");
        var service = new AuthlibInjectorProvisioningService(new HttpClient(handler), cacheDirectory);

        var artifact = await service.EnsureAvailableAsync();

        Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact.FilePath));
        Assert.Contains(OfficialArtifactUrl, artifactRequests);
        Assert.Contains(MirrorArtifactUrl, artifactRequests);
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDoesNotSwitchSourcesOrPublishArtifact(bool cancelMetadata)
    {
        using var cancellation = new CancellationTokenSource();
        var requests = new List<Uri>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (!cancelMetadata && request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal))
                return Task.FromResult(Json(Metadata(new string('0', 64))));
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        var cacheDirectory = Path.Combine(TempRoot, "authlib");
        var service = new AuthlibInjectorProvisioningService(new HttpClient(handler), cacheDirectory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureAvailableAsync(cancellation.Token));

        Assert.All(requests, uri => Assert.Equal("authlib-injector.yushi.moe", uri.Host));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SlowSourceStartsFallbackAndFirstVerifiedResultWins(bool slowMetadata, bool primaryWins)
    {
        var bytes = Encoding.UTF8.GetBytes("verified-racing-jar");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new[] { NewSignal(), NewSignal() };
        var canceled = new[] { NewSignal(), NewSignal() };
        var responses = new[] { NewResponse(), NewResponse() };
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(async (request, token) =>
        {
            var isMetadata = request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal);
            if (isMetadata != slowMetadata)
                return isMetadata ? Json(Metadata(hash)) : ArtifactResponse();
            var index = request.RequestUri.Host == "authlib-injector.yushi.moe" ? 0 : 1;
            started[index].TrySetResult();
            try
            {
                return await responses[index].Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                canceled[index].TrySetResult();
                throw;
            }
        });
        using var client = new HttpClient(handler);
        var cacheDirectory = Path.Combine(TempRoot, "authlib");
        var service = new AuthlibInjectorProvisioningService(client, cacheDirectory,
            fallbackDelay: token => delay.Task.WaitAsync(token));
        var preparation = service.EnsureAvailableAsync(cancellation.Token);
        try
        {
            await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(started[1].Task.IsCompleted);
            delay.SetResult();
            await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Starting the mirror must not cancel a still-usable primary request.
            Assert.False(canceled[0].Task.IsCompleted);
            var winner = primaryWins ? 0 : 1;
            responses[winner].SetResult(slowMetadata ? Json(Metadata(hash, MirrorArtifactUrl)) : ArtifactResponse());

            var artifact = await preparation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(artifact.FilePath));
            await canceled[1 - winner].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Directory.EnumerateFiles(cacheDirectory).Count());
            Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp"));
        }
        finally
        {
            cancellation.Cancel();
        }

        HttpResponseMessage ArtifactResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static TaskCompletionSource<HttpResponseMessage> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    public async Task UsesVerifiedCachedArtifactWhenLatestMetadataIsUnavailable()
    {
        var bytes = Encoding.UTF8.GetBytes("verified-cached-jar");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var cacheDirectory = Path.Combine(TempRoot, "authlib");
        var online = new AuthlibInjectorProvisioningService(
            new HttpClient(new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
                    ? Json(Metadata(hash))
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes)
                    }))),
            cacheDirectory);
        var expected = await online.EnsureAvailableAsync();
        var offline = new AuthlibInjectorProvisioningService(
            new HttpClient(new StubHttpMessageHandler(_ =>
                Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Simulated disconnected network.")))),
            cacheDirectory);

        var actual = await offline.EnsureAvailableAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(actual.FilePath));
    }

    [Fact]
    public async Task RejectsCorruptedCachedArtifactWhenDisconnected()
    {
        var bytes = Encoding.UTF8.GetBytes("verified-cached-jar");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var cacheDirectory = Path.Combine(TempRoot, "authlib");
        var online = new AuthlibInjectorProvisioningService(
            new HttpClient(new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
                    ? Json(Metadata(hash))
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes)
                    }))),
            cacheDirectory);
        var artifact = await online.EnsureAvailableAsync();
        await File.WriteAllBytesAsync(artifact.FilePath, Encoding.UTF8.GetBytes("corrupted"));
        var offline = new AuthlibInjectorProvisioningService(
            new HttpClient(new StubHttpMessageHandler(_ =>
                Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Simulated disconnected network.")))),
            cacheDirectory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => offline.EnsureAvailableAsync());
    }

    private static string Metadata(string hash, string downloadUrl = OfficialArtifactUrl) =>
        $"{{\"build_number\":55,\"version\":\"1.2.7\",\"download_url\":\"{downloadUrl}\",\"checksums\":{{\"sha256\":\"{hash}\"}}}}";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class InterruptedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position > 0
                ? ValueTask.FromException<int>(new IOException("Simulated interrupted response."))
                : base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((request, _) => handler(request))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
