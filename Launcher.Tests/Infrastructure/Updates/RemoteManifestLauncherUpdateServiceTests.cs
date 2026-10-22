using System.Net;
using System.Reflection;
using System.Text;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.Updates;

namespace Launcher.Tests.Infrastructure.Updates;

public sealed class RemoteManifestLauncherUpdateServiceTests
{
    private const string Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GiteeManifest = "https://gitee.com/zqq-699/BlockHelm-Launcher/raw/update-manifests/update/release/latest.json";
    private const string GitHubManifest = "https://raw.githubusercontent.com/zqq-699/BlockHelm-Launcher/update-manifests/update/release/latest.json";

    [Fact]
    public async Task ValidManifestIsAcceptedWithoutSignatureSidecar()
    {
        var service = CreateService((GiteeManifest, HttpStatusCode.OK, CreateManifest()));

        var result = await service.CheckForUpdatesAsync("1.0.0", LauncherUpdateChannel.Release);

        Assert.False(result.IsFailed);
        Assert.True(result.IsUpdateAvailable);
        Assert.True(result.Update?.CanAutoInstall);
        Assert.Equal(12, result.Update?.SizeBytes);
        Assert.Equal(Sha256, result.Update?.Sha256);
    }

    [Fact]
    public async Task ReleaseNotesMarkdownIsPreserved()
    {
        const string releaseNotes = "# 正式版 1.1.0\n\n## 新功能\n- 弹窗内显示更新日志。\n\n## 问题修复\n- 修复更新提示。";
        var service = CreateService((GiteeManifest, HttpStatusCode.OK, CreateManifest(releaseNotes: releaseNotes)));

        var result = await service.CheckForUpdatesAsync("1.0.0", LauncherUpdateChannel.Release);

        Assert.Equal(releaseNotes, result.Update?.ReleaseNotesMarkdown);
    }

    [Fact]
    public async Task ManifestRequestsUseUnifiedUserAgent()
    {
        var handler = new ResponseHandler([(GiteeManifest, HttpStatusCode.OK, CreateManifest())]);
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Legacy/1.0");
        var service = new RemoteManifestLauncherUpdateService(
            httpClient,
            logger: null,
            [DefaultSources[0]]);

        await service.CheckForUpdatesAsync("1.0.0", LauncherUpdateChannel.Release);

        var expected = LauncherHttpUserAgent.BuildValue(Assembly.GetEntryAssembly());
        Assert.NotEmpty(handler.UserAgents);
        Assert.All(handler.UserAgents, values => Assert.Equal([expected], values));
    }

    [Theory]
    [InlineData(0, Sha256)]
    [InlineData(12, "abcd")]
    public async Task MissingRequiredExecutableIntegrityMetadataIsRejected(long size, string sha256)
    {
        var service = CreateService((GiteeManifest, HttpStatusCode.OK, CreateManifest(size: size, sha256: sha256)));

        var result = await service.CheckForUpdatesAsync("1.0.0", LauncherUpdateChannel.Release);

        Assert.True(result.IsFailed);
    }

    private static IReadOnlyList<LauncherUpdateManifestSource> DefaultSources =>
    [
        new("gitee", GiteeManifest.Replace("/release/latest.json", "/{0}/latest.json"), 1),
        new("github", GitHubManifest.Replace("/release/latest.json", "/{0}/latest.json"), 2)
    ];

    private static RemoteManifestLauncherUpdateService CreateService(
        params (string Url, HttpStatusCode Status, string Content)[] responses) =>
        CreateService([DefaultSources[0]], responses);

    private static RemoteManifestLauncherUpdateService CreateService(
        IReadOnlyList<LauncherUpdateManifestSource> sources,
        params (string Url, HttpStatusCode Status, string Content)[] responses)
    {
        var handler = new ResponseHandler(responses);
        return new RemoteManifestLauncherUpdateService(new HttpClient(handler), null, sources);
    }

    private static string CreateManifest(
        string version = "1.1.0",
        long size = 12,
        string sha256 = Sha256,
        string downloadUrl = "https://github.com/zqq-699/BlockHelm-Launcher/releases/download/v1.1.0/BlockHelm_Launcher_x64.exe",
        string releaseNotes = "test") => $$"""
    {
      "schemaVersion": 1,
      "appId": "BlockHelm-Launcher",
      "channel": "release",
      "versionName": "{{version}}",
      "versionCode": 1010099,
      "publishedAt": "2026-07-12T00:00:00Z",
      "mandatory": false,
      "minSupportedVersionCode": 0,
      "releaseNotes": {{System.Text.Json.JsonSerializer.Serialize(releaseNotes)}},
      "assets": [{
        "platform": "windows", "arch": "x64", "packageType": "exe",
        "fileName": "BlockHelm_Launcher_x64.exe", "size": {{size}}, "sha256": "{{sha256}}",
        "urls": [{ "name": "github", "url": "{{downloadUrl}}", "priority": 1 }]
      }]
    }
    """;

    private sealed class ResponseHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Content)> responses;
        public List<IReadOnlyList<string>> UserAgents { get; } = [];

        public ResponseHandler(IEnumerable<(string Url, HttpStatusCode Status, string Content)> values) =>
            responses = values.ToDictionary(
                value => value.Url,
                value => (value.Status, value.Content),
                StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.Select(value => value.ToString()).ToArray());
            var value = responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var configured)
                ? configured
                : (Status: HttpStatusCode.NotFound, Content: string.Empty);
            return Task.FromResult(new HttpResponseMessage(value.Status)
            {
                RequestMessage = request,
                Content = new StringContent(value.Content, Encoding.UTF8)
            });
        }
    }
}
