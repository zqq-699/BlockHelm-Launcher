using System.Net;
using System.Net.Http;
using System.Text.Json;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Modpacks;

public sealed class ModpackMetadataHostConcurrencyTests
{
    [Fact]
    public async Task MetadataDeadlineReportsNetworkTimeout()
    {
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            DownloadHttpDiagnostics.ExecuteMetadataAsync(httpClient, null, "test", async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public async Task MetadataDeadlineCoversStreamingResponseBody()
    {
        using var httpClient = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() })))
            { Timeout = Timeout.InfiniteTimeSpan };
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            DownloadHttpDiagnostics.ExecuteMetadataAsync(httpClient, null, "test", async token =>
            {
                using var response = await httpClient.GetAsync("https://example.test/metadata", HttpCompletionOption.ResponseHeadersRead, token);
                return await response.Content.ReadAsStringAsync(token);
            }, CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MetadataTimeoutIsNetworkFailureAndCurseForgeRetries(bool curseForge, bool stalledBody)
    {
        var calls = 0;
        using var httpClient = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            if (!stalledBody)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() };
        })) { Timeout = TimeSpan.FromMilliseconds(50) };
        var limiter = new ImportConcurrencyLimiter();
        Task request = curseForge
            ? new CurseForgeApiClient(httpClient, limiter, null, CreateController(), static (_, _) => Task.CompletedTask)
                .GetFingerprintMatchesAsync([42], "test-key", CancellationToken.None)
            : new ModrinthApiClient(httpClient, limiter, null, CreateController())
                .GetVersionFileMatchesAsync([new string('a', 40)], CancellationToken.None);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => request);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(curseForge ? 3 : 1, calls);
        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataCallerCancellationDoesNotRetry(bool curseForge)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var httpClient = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { Timeout = Timeout.InfiniteTimeSpan };
        var limiter = new ImportConcurrencyLimiter();
        using var cancellation = new CancellationTokenSource();
        Task request = curseForge
            ? new CurseForgeApiClient(httpClient, limiter, null, CreateController(), static (_, _) => Task.CompletedTask)
                .GetFingerprintMatchesAsync([42], "test-key", cancellation.Token)
            : new ModrinthApiClient(httpClient, limiter, null, CreateController())
                .GetVersionFileMatchesAsync([new string('a', 40)], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, calls);
        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
    }

    [Fact]
    public async Task ModrinthExactMatchIncludesCurrentVersionIdentityAndDate()
    {
        var sha1 = new string('a', 40);
        using var httpClient = new HttpClient(new CallbackHandler(request =>
        {
            var payload = new Dictionary<string, object>
            {
                [sha1] = new Dictionary<string, object>
                {
                    ["project_id"] = "project",
                    ["id"] = "version",
                    ["version_number"] = "1.0.0",
                    ["date_published"] = "2026-01-02T03:04:05Z",
                    ["files"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["hashes"] = new Dictionary<string, string>
                            {
                                ["sha1"] = sha1,
                                ["sha512"] = new string('b', 128)
                            },
                            ["url"] = "https://cdn.example/mod.jar",
                            ["primary"] = true,
                            ["size"] = 12
                        }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(payload))
            };
        }));
        var client = new ModrinthApiClient(
            httpClient,
            new ImportConcurrencyLimiter(),
            logger: null,
            CreateController());

        var match = Assert.Single(await client.GetVersionFileMatchesAsync([sha1], CancellationToken.None)).Value;

        Assert.Equal("project", match.ProjectId);
        Assert.Equal("version", match.VersionId);
        Assert.Equal("1.0.0", match.VersionNumber);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), match.DatePublished);
    }

    [Fact]
    public async Task CurseForgeExactMatchIncludesCurrentVersionIdentityAndDate()
    {
        using var httpClient = new HttpClient(new CallbackHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{"data":{"exactMatches":[{"file":{"id":456,"modId":123,"displayName":"1.0.0","fileDate":"2026-01-02T03:04:05Z","fileFingerprint":42}}]}}""")
            }));
        var client = new CurseForgeApiClient(
            httpClient,
            new ImportConcurrencyLimiter(),
            logger: null,
            CreateController());

        var match = Assert.Single(await client.GetFingerprintMatchesAsync([42], "test-key", CancellationToken.None)).Value;

        Assert.Equal(123, match.ProjectId);
        Assert.Equal(456, match.FileId);
        Assert.Equal("1.0.0", match.VersionNumber);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), match.FileDate);
    }

    [Fact]
    public async Task ModrinthRateLimitDoesNotReduceConfiguredGlobalConcurrency()
    {
        var controller = CreateController();
        var limiter = new ImportConcurrencyLimiter();
        limiter.SetMaximumDownloadConcurrency(128);
        using var httpClient = new HttpClient(new CallbackHandler(request =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            }));
        var client = new ModrinthApiClient(
            httpClient,
            limiter,
            logger: null,
            controller);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetVersionFileMatchesAsync([new string('a', 40)], CancellationToken.None));

        Assert.Equal(128, limiter.DownloadSnapshot.CurrentTarget);
        Assert.Equal(0, limiter.DownloadSnapshot.ActiveCount);
    }

    [Fact]
    public async Task CurseForgeDirectDownloadAddsCdnCandidatesWithoutMarkingDistributionRestricted()
    {
        var controller = CreateController();
        using var httpClient = new HttpClient(new CallbackHandler(request =>
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/download-url", StringComparison.Ordinal)
                ? """{"data":"https://download.example/example.jar"}"""
                : """{"data":{"displayName":"Example","fileName":"example.jar","downloadUrl":"https://download.example/example.jar","hashes":[]}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json)
            };
        }));
        var client = new CurseForgeApiClient(
            httpClient,
            new ImportConcurrencyLimiter(),
            logger: null,
            controller);

        var result = await client.GetFileDownloadAsync(123, 456, "test-key", CancellationToken.None);

        Assert.False(result.IsDistributionRestricted);
        Assert.Equal("https://download.example/example.jar", result.PrimaryUrl);
        Assert.Equal(
            [
                "https://edge.forgecdn.net/files/0/456/example.jar",
                "https://mediafilez.forgecdn.net/files/0/456/example.jar"
            ],
            result.FallbackUrls);
    }

    private static DownloadHostConcurrencyController CreateController() => new(
        delayAsync: static (_, _) => ValueTask.CompletedTask);

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
