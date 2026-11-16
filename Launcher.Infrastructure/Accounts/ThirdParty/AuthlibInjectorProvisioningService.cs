/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts.ThirdParty;

internal sealed class AuthlibInjectorProvisioningService : IAuthlibInjectorProvisioningService
{
    private const string OfficialRoot = "https://authlib-injector.yushi.moe/";
    private const string BmclApiRoot = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/";
    private static readonly Uri LatestArtifactUri = new(OfficialRoot + "artifact/latest.json");
    private const long MaximumArtifactBytes = 16 * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly Func<CancellationToken, Task>? fallbackDelay;
    private readonly string cacheDirectory;
    private readonly ILogger<AuthlibInjectorProvisioningService> logger;
    private readonly SemaphoreSlim provisioningLock = new(1, 1);

    public AuthlibInjectorProvisioningService(
        LauncherPathProvider pathProvider,
        ILogger<AuthlibInjectorProvisioningService>? logger = null)
        : this(
            MinecraftHttpClientFactory.CreateTransportClient(),
            Path.Combine(pathProvider.DefaultDataDirectory, "cache", "authlib-injector"),
            logger)
    {
    }

    internal AuthlibInjectorProvisioningService(
        HttpClient httpClient,
        string cacheDirectory,
        ILogger<AuthlibInjectorProvisioningService>? logger = null,
        Func<CancellationToken, Task>? fallbackDelay = null)
    {
        this.httpClient = httpClient;
        this.fallbackDelay = fallbackDelay;
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.logger = logger ?? NullLogger<AuthlibInjectorProvisioningService>.Instance;
    }

    public Task<AuthlibInjectorArtifact> EnsureAvailableAsync(CancellationToken cancellationToken = default) =>
        EnsureAvailableAsync(progress: null, cancellationToken);

    internal async Task<AuthlibInjectorArtifact> EnsureAvailableAsync(
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        await provisioningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MinecraftPathGuard.EnsureSafeDirectory(
                cacheDirectory,
                cacheDirectory,
                "Authlib-injector cache directory");
            try
            {
                var latest = await GetLatestArtifactAsync(cancellationToken).ConfigureAwait(false);
                var cached = await TryUseArtifactAsync(latest, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                    return cached;
                return await DownloadArtifactAsync(latest, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Unable to prepare the latest authlib-injector build; checking verified cache. ErrorType={ErrorType}",
                    exception.GetType().FullName);
                var fallback = await FindVerifiedFallbackAsync(cancellationToken).ConfigureAwait(false);
                if (fallback is not null)
                    return fallback;
                throw new InvalidOperationException("No verified authlib-injector artifact is available.", exception);
            }
        }
        finally
        {
            provisioningLock.Release();
        }
    }

    private Task<ArtifactMetadata> GetLatestArtifactAsync(CancellationToken cancellationToken) =>
        LookupSourcesAsync(LatestArtifactUri, async (context, token) =>
        {
            var metadata = await context.Response.Content.ReadFromJsonAsync<ArtifactMetadata>(cancellationToken: token)
                .ConfigureAwait(false)
                ?? throw new JsonException("The authlib-injector metadata response is empty.");
            try
            {
                ValidateMetadata(metadata);
            }
            catch (InvalidDataException exception)
            {
                throw new DownloadContentValidationException("The authlib-injector metadata is invalid.", exception);
            }
            return metadata;
        }, cancellationToken);

    private async Task<T> LookupSourcesAsync<T>(
        Uri original,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        SpeedMeter? speedMeter = null,
        DownloadConcurrencyCategory category = DownloadConcurrencyCategory.Metadata)
    {
        var sources = GetSources(original).Select(source => new ResolvedDownloadRequest(
            original.AbsoluteUri, source.AbsoluteUri, DownloadSourcePreference.Official,
            source.Host == "bmclapi2.bangbang93.com" ? "BmclApiAuthlibInjector" : "AuthlibInjectorOfficial",
            "AuthlibInjector")).ToArray();
        var executor = new MinecraftDownloadRequestExecutor(httpClient, logger, category: category,
            retryOptions: new DownloadRetryOptions
            {
                ResponseHeadersTimeout = TimeSpan.FromSeconds(20),
                MaxAttemptsPerSource = 1
            });
        var result = await executor.ExecuteStaggeredLookupAsync(
            original.AbsoluteUri, DownloadSourcePreference.Official, categoryHint: null,
            operation, _ => false, cancellationToken, fallbackDelay,
            sourceCandidates: sources, speedMeter: speedMeter).ConfigureAwait(false);
        return result.Value!;
    }

    private static Uri[] GetSources(Uri original)
    {
        // Only rewrite known download roots, never arbitrary hosts returned by metadata.
        var url = original.AbsoluteUri;
        if (url.StartsWith(OfficialRoot, StringComparison.OrdinalIgnoreCase))
            return [original, new Uri(BmclApiRoot + url[OfficialRoot.Length..])];
        if (url.StartsWith(BmclApiRoot, StringComparison.OrdinalIgnoreCase))
            return [original, new Uri(OfficialRoot + url[BmclApiRoot.Length..])];
        return [original];
    }

    private async Task<AuthlibInjectorArtifact?> TryUseArtifactAsync(
        ArtifactMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = GetArtifactPath(metadata);
        MinecraftPathGuard.EnsureSafeFileDestination(
            path,
            cacheDirectory,
            "Authlib-injector cached artifact");
        if (!File.Exists(path))
            return null;
        if (!await HashMatchesAsync(path, metadata.Checksums.Sha256, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(path);
            TryDelete(GetManifestPath(path));
            return null;
        }
        await SaveManifestAsync(path, metadata, cancellationToken).ConfigureAwait(false);
        return ToArtifact(path, metadata);
    }

    private async Task<AuthlibInjectorArtifact> DownloadArtifactAsync(
        ArtifactMetadata metadata,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Racing sources only prepare bounded, verified bytes. Publish the winner once,
        // so a late response can never replace its artifact or manifest.
        var bytes = await LookupSourcesAsync(metadata.DownloadUrl, async (context, token) =>
        {
            if (context.Response.Content.Headers.ContentLength is > MaximumArtifactBytes)
                throw new DownloadContentValidationException("The authlib-injector artifact is too large.");

            await using var source = await context.Response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var target = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (target.Length + read > MaximumArtifactBytes)
                    throw new DownloadContentValidationException("The authlib-injector artifact is too large.");
                target.Write(buffer, 0, read);
            }
            var content = target.ToArray();
            var actualHash = Convert.ToHexString(SHA256.HashData(content));
            if (!string.Equals(actualHash, metadata.Checksums.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Authlib-injector integrity check failed. Host={Host} HashAlgorithm=SHA256 ExpectedHash={ExpectedHash} ActualHash={ActualHash}",
                    new Uri(context.Resolution.ActualUrl).Host, metadata.Checksums.Sha256, actualHash);
                throw new DownloadContentValidationException("The authlib-injector SHA-256 checksum does not match.");
            }
            return content;
        }, cancellationToken, SpeedMeterProgress.TryGet(progress), DownloadConcurrencyCategory.Runtime).ConfigureAwait(false);

        var destination = GetArtifactPath(metadata);
        var temporaryPath = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            MinecraftPathGuard.EnsureSafeFileDestination(
                destination,
                cacheDirectory,
                "Authlib-injector artifact");
            MinecraftPathGuard.EnsureNoReparsePoints(
                cacheDirectory,
                temporaryPath,
                "Authlib-injector temporary artifact");
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await target.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            MinecraftPathGuard.EnsureSafeFileDestination(
                destination,
                cacheDirectory,
                "Authlib-injector artifact");
            MinecraftPathGuard.EnsureNoReparsePoints(
                cacheDirectory,
                temporaryPath,
                "Authlib-injector temporary artifact");
            File.Move(temporaryPath, destination, overwrite: true);
            await SaveManifestAsync(destination, metadata, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Authlib-injector prepared. Version={Version} BuildNumber={BuildNumber}",
                metadata.Version,
                metadata.BuildNumber);
            return ToArtifact(destination, metadata);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<AuthlibInjectorArtifact?> FindVerifiedFallbackAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<(ArtifactMetadata Metadata, string Path)>();
        foreach (var manifestPath in Directory.EnumerateFiles(cacheDirectory, "*.manifest.json"))
        {
            try
            {
                MinecraftPathGuard.EnsureSafeFileDestination(
                    manifestPath,
                    cacheDirectory,
                    "Authlib-injector cache manifest");
                var metadata = JsonSerializer.Deserialize<ArtifactMetadata>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                if (metadata is null)
                    continue;
                ValidateMetadata(metadata);
                var path = manifestPath[..^".manifest.json".Length];
                MinecraftPathGuard.EnsureSafeFileDestination(
                    path,
                    cacheDirectory,
                    "Authlib-injector cached artifact");
                if (File.Exists(path))
                    candidates.Add((metadata, path));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogDebug("Ignoring invalid authlib-injector cache manifest. ManifestPath={ManifestPath} ErrorType={ErrorType}",
                    manifestPath, exception.GetType().FullName);
            }
        }

        foreach (var candidate in candidates.OrderByDescending(item => item.Metadata.BuildNumber))
        {
            if (await HashMatchesAsync(candidate.Path, candidate.Metadata.Checksums.Sha256, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Using cached authlib-injector. Version={Version} BuildNumber={BuildNumber}",
                    candidate.Metadata.Version,
                    candidate.Metadata.BuildNumber);
                return ToArtifact(candidate.Path, candidate.Metadata);
            }
        }
        return null;
    }

    private async Task SaveManifestAsync(
        string artifactPath,
        ArtifactMetadata metadata,
        CancellationToken cancellationToken)
    {
        var manifestPath = GetManifestPath(artifactPath);
        var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            MinecraftPathGuard.EnsureSafeFileDestination(
                manifestPath,
                cacheDirectory,
                "Authlib-injector cache manifest");
            MinecraftPathGuard.EnsureNoReparsePoints(
                cacheDirectory,
                temporaryPath,
                "Authlib-injector temporary manifest");
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata),
                cancellationToken).ConfigureAwait(false);
            MinecraftPathGuard.EnsureSafeFileDestination(
                manifestPath,
                cacheDirectory,
                "Authlib-injector cache manifest");
            MinecraftPathGuard.EnsureNoReparsePoints(
                cacheDirectory,
                temporaryPath,
                "Authlib-injector temporary manifest");
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private string GetArtifactPath(ArtifactMetadata metadata)
    {
        var safeVersion = string.Concat(metadata.Version.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
        return Path.Combine(cacheDirectory, $"authlib-injector-{safeVersion}-{metadata.BuildNumber}.jar");
    }

    private static string GetManifestPath(string artifactPath) => artifactPath + ".manifest.json";

    private static void ValidateMetadata(ArtifactMetadata metadata)
    {
        if (metadata.BuildNumber <= 0
            || string.IsNullOrWhiteSpace(metadata.Version)
            || metadata.DownloadUrl is null
            || !metadata.DownloadUrl.IsAbsoluteUri
            || (!string.Equals(metadata.DownloadUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(metadata.DownloadUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || metadata.Checksums is null
            || string.IsNullOrWhiteSpace(metadata.Checksums.Sha256)
            || metadata.Checksums.Sha256.Length != 64
            || !metadata.Checksums.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The authlib-injector metadata is invalid.");
        }
    }

    private static async Task<bool> HashMatchesAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static AuthlibInjectorArtifact ToArtifact(string path, ArtifactMetadata metadata) =>
        new(path, metadata.Version, metadata.BuildNumber);

    private void TryDelete(string path)
    {
        try
        {
            MinecraftPathGuard.EnsureNoReparsePoints(
                cacheDirectory,
                path,
                "Authlib-injector cache cleanup");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (InvalidDataException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record ArtifactMetadata(
        [property: JsonPropertyName("build_number")] int BuildNumber,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("download_url")] Uri DownloadUrl,
        [property: JsonPropertyName("checksums")] ArtifactChecksums Checksums);

    internal sealed record ArtifactChecksums(
        [property: JsonPropertyName("sha256")] string Sha256);
}
