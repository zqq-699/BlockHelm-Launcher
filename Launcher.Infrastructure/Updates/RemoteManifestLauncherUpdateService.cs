using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Updates;

public sealed class RemoteManifestLauncherUpdateService : ILauncherUpdateService
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient httpClient;
    private readonly ILogger<RemoteManifestLauncherUpdateService>? logger;
    private readonly IReadOnlyList<LauncherUpdateManifestSource> manifestSources;

    public RemoteManifestLauncherUpdateService(
        HttpClient? httpClient = null,
        ILogger<RemoteManifestLauncherUpdateService>? logger = null)
        : this(httpClient, logger, LauncherUpdateManifestSource.DefaultSources)
    {
    }

    internal RemoteManifestLauncherUpdateService(
        HttpClient? httpClient,
        ILogger<RemoteManifestLauncherUpdateService>? logger,
        IReadOnlyList<LauncherUpdateManifestSource> manifestSources)
    {
        this.httpClient = httpClient ?? OfficialUpdateHttp.CreateClient(DefaultRequestTimeout);
        this.logger = logger;
        this.manifestSources = manifestSources.OrderBy(source => source.Priority).ToArray();
        EnsureDefaultHeaders(this.httpClient);
    }

    public async Task<LauncherUpdateCheckResult> CheckForUpdatesAsync(
        string currentVersion,
        LauncherUpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (!TryCalculateVersionCode(currentVersion, out var currentVersionCode))
            return LauncherUpdateCheckResult.Failed(currentVersion, "The current launcher version is invalid.");

        var channelText = channel is LauncherUpdateChannel.Beta ? "beta" : "release";
        foreach (var source in manifestSources)
        {
            var result = await LoadSourceAsync(source, channelText, cancellationToken).ConfigureAwait(false);
            if (result.Status is not ManifestSourceStatus.Valid)
                continue;
            var manifest = result.Manifest!;
            logger?.LogInformation(
                "Validated launcher update manifest. Source={Source} Channel={Channel} VersionCode={VersionCode}",
                result.Source.Name,
                channelText,
                manifest.VersionCode);
            return CreateResult(currentVersion, currentVersionCode, manifest);
        }

        logger?.LogWarning("All launcher update manifest sources failed or were unavailable. Channel={Channel}", channelText);
        return LauncherUpdateCheckResult.Failed(currentVersion, "All update manifest sources failed or were unavailable.");
    }

    private async Task<ManifestSourceResult> LoadSourceAsync(
        LauncherUpdateManifestSource source,
        string expectedChannel,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(source.CreateManifestUrl(expectedChannel), UriKind.Absolute, out var manifestUri))
                throw new UpdateSecurityException("The update manifest URL is invalid.");
            var manifestBytes = await DownloadBytesAsync(manifestUri, MaximumManifestBytes, cancellationToken)
                .ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<RemoteUpdateManifestDto>(manifestBytes, JsonOptions)
                ?? throw new UpdateSecurityException("The update manifest JSON is empty.");
            ValidateManifest(manifest, expectedChannel);
            return ManifestSourceResult.Valid(source, manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UpdateSourceUnavailableException exception)
        {
            logger?.LogWarning(exception, "Launcher update manifest source unavailable. Source={Source}", source.Name);
            return ManifestSourceResult.Unavailable(source, exception.Message);
        }
        catch (Exception exception) when (exception is UpdateSecurityException or JsonException or InvalidDataException)
        {
            logger?.LogError(exception, "Launcher update manifest source failed security validation. Source={Source}", source.Name);
            return ManifestSourceResult.Invalid(source, exception.Message);
        }
    }

    private async Task<byte[]> DownloadBytesAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultRequestTimeout);
        try
        {
            using var response = await OfficialUpdateHttp.SendAsync(
                httpClient, uri, OfficialUpdateUriKind.Manifest, timeout.Token).ConfigureAwait(false);
            return await OfficialUpdateHttp.ReadLimitedBytesAsync(response, maximumBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateSourceUnavailableException("The update manifest source timed out.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new UpdateSourceUnavailableException("The update manifest source could not be read.", exception);
        }
    }

    private static LauncherUpdateCheckResult CreateResult(
        string currentVersion,
        int currentVersionCode,
        RemoteUpdateManifestDto manifest)
    {
        if (manifest.VersionCode <= currentVersionCode)
            return LauncherUpdateCheckResult.Latest(currentVersion);
        var asset = manifest.Assets.Single();
        var urls = asset.Urls.OrderBy(url => url.Priority)
            .Select(url => new LauncherUpdateDownloadUrl(url.Name.Trim(), url.Url.Trim(), url.Priority))
            .ToArray();
        var versionName = manifest.VersionName.Trim();
        return LauncherUpdateCheckResult.Available(currentVersion, new LauncherUpdateInfo(
            versionName,
            versionName,
            LauncherProjectLinks.GitHubReleasesUrl,
            urls[0].Url,
            string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? null : manifest.ReleaseNotes,
            asset.FileName.Trim(),
            LauncherUpdateAssetKind.WindowsX64Executable,
            asset.Size,
            asset.Sha256.Trim().ToLowerInvariant(),
            VersionCode: manifest.VersionCode,
            IsMandatory: manifest.Mandatory || currentVersionCode < manifest.MinSupportedVersionCode,
            MinSupportedVersionCode: manifest.MinSupportedVersionCode,
            PublishedAt: manifest.PublishedAt,
            DownloadUrls: urls,
            ReleaseNotesSections: LauncherReleaseNotesParser.Parse(manifest.ReleaseNotes)));
    }

    private static void ValidateManifest(RemoteUpdateManifestDto manifest, string expectedChannel)
    {
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.AppId?.Trim(), "BlockHelm-Launcher", StringComparison.Ordinal)
            || !string.Equals(manifest.Channel?.Trim(), expectedChannel, StringComparison.OrdinalIgnoreCase)
            || manifest.VersionCode < 0
            || string.IsNullOrWhiteSpace(manifest.VersionName)
            || manifest.Assets.Count != 1)
        {
            throw new UpdateSecurityException("The update manifest metadata is invalid.");
        }

        var asset = manifest.Assets[0];
        if (!string.Equals(asset.Platform?.Trim(), "windows", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(asset.Arch?.Trim(), "x64", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(asset.PackageType?.Trim(), "exe", StringComparison.OrdinalIgnoreCase)
            || asset.Size <= 0
            || !IsHex64(asset.Sha256)
            || string.IsNullOrWhiteSpace(asset.FileName)
            || !string.Equals(Path.GetFileName(asset.FileName), asset.FileName, StringComparison.Ordinal)
            || !asset.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || asset.Urls.Count == 0)
        {
            throw new UpdateSecurityException("The update executable metadata is invalid.");
        }

        foreach (var url in asset.Urls)
        {
            if (string.IsNullOrWhiteSpace(url.Name)
                || !Uri.TryCreate(url.Url, UriKind.Absolute, out var uri))
                throw new UpdateSecurityException("The update download URL is invalid.");
            OfficialUpdateHttp.ValidateInitialUri(uri, OfficialUpdateUriKind.Executable);
        }
    }

    private static bool IsHex64(string? value) => value?.Trim() is { Length: 64 } text && text.All(Uri.IsHexDigit);

    private static void EnsureDefaultHeaders(HttpClient client)
    {
        LauncherHttpUserAgent.Apply(client);
        if (!client.DefaultRequestHeaders.Accept.Any(header => header.MediaType == "application/json"))
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static bool TryCalculateVersionCode(string? value, out int versionCode)
    {
        versionCode = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        var metadataIndex = text.IndexOf('+');
        if (metadataIndex >= 0) text = text[..metadataIndex];
        var betaRevision = 99;
        var preReleaseIndex = text.IndexOf('-');
        if (preReleaseIndex >= 0)
        {
            var preRelease = text[(preReleaseIndex + 1)..];
            text = text[..preReleaseIndex];
            if (!preRelease.StartsWith("beta.", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(preRelease[5..], out betaRevision)
                || betaRevision is <= 0 or > 98) return false;
        }
        var segments = text.Split('.');
        var patch = 0;
        if (segments.Length is < 2 or > 3
            || !int.TryParse(segments[0], out var major)
            || !int.TryParse(segments[1], out var minor)
            || (segments.Length == 3 && !int.TryParse(segments[2], out patch))) return false;
        if (major is < 0 or > 99 || minor is < 0 or > 99 || patch is < 0 or > 99) return false;
        versionCode = major * 1_000_000 + minor * 10_000 + patch * 100 + betaRevision;
        return true;
    }

    internal sealed class RemoteUpdateManifestDto
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
        [JsonPropertyName("appId")] public string? AppId { get; init; }
        [JsonPropertyName("channel")] public string? Channel { get; init; }
        [JsonPropertyName("versionName")] public string VersionName { get; init; } = string.Empty;
        [JsonPropertyName("versionCode")] public int VersionCode { get; init; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; init; }
        [JsonPropertyName("mandatory")] public bool Mandatory { get; init; }
        [JsonPropertyName("minSupportedVersionCode")] public int MinSupportedVersionCode { get; init; }
        [JsonPropertyName("releaseNotes")] public string? ReleaseNotes { get; init; }
        [JsonPropertyName("assets")] public List<RemoteUpdateAssetDto> Assets { get; init; } = [];
    }

    internal sealed class RemoteUpdateAssetDto
    {
        [JsonPropertyName("platform")] public string? Platform { get; init; }
        [JsonPropertyName("arch")] public string? Arch { get; init; }
        [JsonPropertyName("packageType")] public string? PackageType { get; init; }
        [JsonPropertyName("fileName")] public string FileName { get; init; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
        [JsonPropertyName("urls")] public List<RemoteUpdateUrlDto> Urls { get; init; } = [];
    }

    internal sealed class RemoteUpdateUrlDto
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;
        [JsonPropertyName("priority")] public int Priority { get; init; }
    }

    private enum ManifestSourceStatus { Valid, Unavailable, Invalid }
    private sealed record ManifestSourceResult(
        LauncherUpdateManifestSource Source,
        ManifestSourceStatus Status,
        RemoteUpdateManifestDto? Manifest,
        string? FailureReason)
    {
        public static ManifestSourceResult Valid(LauncherUpdateManifestSource source, RemoteUpdateManifestDto manifest) =>
            new(source, ManifestSourceStatus.Valid, manifest, null);
        public static ManifestSourceResult Unavailable(LauncherUpdateManifestSource source, string reason) =>
            new(source, ManifestSourceStatus.Unavailable, null, reason);
        public static ManifestSourceResult Invalid(LauncherUpdateManifestSource source, string reason) =>
            new(source, ManifestSourceStatus.Invalid, null, reason);
    }
}

public sealed record LauncherUpdateManifestSource(string Name, string UrlTemplate, int Priority)
{
    public static IReadOnlyList<LauncherUpdateManifestSource> DefaultSources { get; } =
    [
        new("gitee", LauncherProjectLinks.GiteeUpdateManifestUrlTemplate, 1),
        new("github", LauncherProjectLinks.GitHubUpdateManifestUrlTemplate, 2)
    ];

    public string CreateManifestUrl(string channel) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, UrlTemplate, channel);
}
