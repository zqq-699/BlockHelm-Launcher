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

using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal static class VanillaVersionMetadataClient
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public static async Task<JsonObject> DownloadVersionJsonAsync(
        HttpClient httpClient,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        VanillaVersionMetadataCache? cache = null,
        DownloadHostConcurrencyController? hostConcurrencyController = null)
    {
        return await DownloadVersionJsonAsync(
                httpClient,
                minecraftVersion,
                downloadSourcePreference,
                requireManifestSha1: false,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                cancellationToken, cache, hostConcurrencyController)
            .ConfigureAwait(false);
    }

    public static async Task<JsonObject> DownloadVerifiedVersionJsonAsync(
        HttpClient httpClient,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        VanillaVersionMetadataCache? cache = null,
        DownloadHostConcurrencyController? hostConcurrencyController = null)
    {
        return await DownloadVersionJsonAsync(
                httpClient,
                minecraftVersion,
                downloadSourcePreference,
                requireManifestSha1: true,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                cancellationToken, cache, hostConcurrencyController)
            .ConfigureAwait(false);
    }

    private static async Task<JsonObject> DownloadVersionJsonAsync(
        HttpClient httpClient,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        bool requireManifestSha1,
        int downloadSpeedLimitMbPerSecond,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger? logger,
        CancellationToken cancellationToken,
        VanillaVersionMetadataCache? cache,
        DownloadHostConcurrencyController? hostConcurrencyController)
    {
        cache ??= VanillaVersionMetadataCache.ForClient(httpClient);
        await cache.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executor = new MinecraftDownloadRequestExecutor(
                httpClient,
                logger,
                DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
                category: DownloadConcurrencyCategory.Metadata,
                retryOptions: GameVersionService.VersionCatalogRetryOptions,
                hostConcurrencyController: hostConcurrencyController);
            var manifestKey = $"manifest-{downloadSourcePreference}.json";
            var manifestBytes = await cache.ReadAsync(manifestKey, VanillaVersionMetadataCache.ManifestLifetime, logger, cancellationToken).ConfigureAwait(false);
            VersionMetadataLocation? versionMetadata = null;
            if (manifestBytes is not null)
            {
                try { versionMetadata = ParseManifest(manifestBytes, minecraftVersion, requireManifestSha1); }
                catch (Exception exception) when (exception is DownloadContentValidationException or JsonException)
                {
                    cache.Forget(manifestKey);
                    logger?.LogInformation("Cached Minecraft manifest cannot resolve the requested version; refreshing. MinecraftVersion={MinecraftVersion}", minecraftVersion);
                }
            }
            if (versionMetadata is null)
            {
                var manifest = await executor.ExecuteStaggeredLookupAsync(VersionManifestUrl, downloadSourcePreference, "Mojang",
                    async (context, token) =>
                    {
                        var bytes = await context.Response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                        return (Bytes: bytes, Metadata: ParseManifest(bytes, minecraftVersion, requireManifestSha1));
                    }, static _ => false, cancellationToken).ConfigureAwait(false);
                versionMetadata = manifest.Value.Metadata;
                await cache.WriteAsync(manifestKey, manifest.Value.Bytes, logger, cancellationToken).ConfigureAwait(false);
            }

            var hasHash = IsSha1(versionMetadata.Sha1);
            var versionKey = hasHash ? $"version-{versionMetadata.Sha1!.ToLowerInvariant()}.json" : null;
            if (versionKey is not null)
            {
                var cachedVersion = await cache.ReadAsync(versionKey, null, logger, cancellationToken).ConfigureAwait(false);
                if (cachedVersion is not null)
                {
                    try { return ParseVersion(cachedVersion, minecraftVersion, versionMetadata.Sha1, requireIdentity: true); }
                    catch (Exception exception) when (exception is DownloadContentValidationException or JsonException)
                    {
                        cache.Forget(versionKey);
                        logger?.LogWarning("Cached Minecraft version metadata is invalid; downloading again. MinecraftVersion={MinecraftVersion}", minecraftVersion);
                    }
                }
            }

            var downloaded = await executor.ExecuteStaggeredLookupAsync(versionMetadata.Url, downloadSourcePreference, "Mojang",
                async (context, token) =>
                {
                    var bytes = await context.Response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    var json = ParseVersion(bytes, minecraftVersion, hasHash ? versionMetadata.Sha1 : null, requireManifestSha1 || hasHash);
                    return (Bytes: bytes, Json: json);
                }, static _ => false, cancellationToken).ConfigureAwait(false);
            // Never promote legacy/hashless responses into a trusted version cache.
            if (versionKey is not null)
                await cache.WriteAsync(versionKey, downloaded.Value.Bytes, logger, cancellationToken).ConfigureAwait(false);
            return downloaded.Value.Json;
        }
        finally { cache.Gate.Release(); }
    }

    private static VersionMetadataLocation ParseManifest(byte[] bytes, string minecraftVersion, bool requireHash)
    {
        if (JsonNode.Parse(bytes) is not JsonObject manifest || manifest["versions"] is not JsonArray versions)
            throw SafeExceptionLog.Describe(new DownloadContentValidationException("Minecraft version manifest is missing a versions array."), "DownloadContentValidationException", ("Reason", "Minecraft version manifest is missing a versions array."));
        if (versions.Any(entry => !IsValidManifestEntry(entry)))
            throw SafeExceptionLog.Describe(new DownloadContentValidationException("Minecraft version manifest contains an invalid version entry."), "DownloadContentValidationException", ("Reason", "Minecraft version manifest contains an invalid version entry."));
        var metadata = FindVersionMetadata(versions, minecraftVersion)
            ?? throw SafeExceptionLog.Describe(new DownloadContentValidationException($"Minecraft version manifest does not contain {minecraftVersion}."),
                "VersionManifestEntryMissing", ("MinecraftVersion", minecraftVersion));
        if (requireHash && !IsSha1(metadata.Sha1))
            throw SafeExceptionLog.Describe(new DownloadContentValidationException($"Minecraft version manifest does not contain a valid SHA1 for {minecraftVersion}."),
                "VersionManifestHashInvalid", ("MinecraftVersion", minecraftVersion), ("Field", "sha1"));
        return metadata;
    }

    private static JsonObject ParseVersion(byte[] bytes, string minecraftVersion, string? expectedSha1, bool requireIdentity)
    {
        if (expectedSha1 is not null)
        {
            var actualSha1 = Convert.ToHexString(SHA1.HashData(bytes));
            if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                    $"Minecraft version metadata SHA1 does not match the official manifest: {minecraftVersion}"), "VersionMetadataHashMismatch", ("MinecraftVersion", minecraftVersion),
                    ("HashAlgorithm", "SHA1"), ("ExpectedHash", expectedSha1), ("ActualHash", actualSha1));
        }
        var versionObject = JsonNode.Parse(bytes) as JsonObject
            ?? throw SafeExceptionLog.Describe(new DownloadContentValidationException($"Minecraft version metadata is not a JSON object: {minecraftVersion}"),
                "VersionMetadataRootInvalid", ("MinecraftVersion", minecraftVersion), ("ExpectedFormat", "JSON object"));
        if (requireIdentity
            && (versionObject["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || id != minecraftVersion))
            throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                $"Minecraft version metadata id does not match the requested version: {minecraftVersion}"), "VersionMetadataIdMismatch", ("MinecraftVersion", minecraftVersion), ("Field", "id"));
        return versionObject;
    }

    public static string? GetClientJarUrl(JsonObject versionJson)
    {
        return versionJson["downloads"]?["client"]?["url"]?.GetValue<string>();
    }

    public static string? GetServerJarUrl(JsonObject versionJson)
    {
        return versionJson["downloads"]?["server"]?["url"]?.GetValue<string>();
    }

    private static VersionMetadataLocation? FindVersionMetadata(JsonArray versionEntries, string minecraftVersion)
    {
        foreach (var entry in versionEntries.OfType<JsonObject>())
        {
            if (entry["id"] is not JsonValue idValue
                || !idValue.TryGetValue<string>(out var id)
                || !string.Equals(id, minecraftVersion, StringComparison.OrdinalIgnoreCase)
                || entry["url"] is not JsonValue urlValue
                || !urlValue.TryGetValue<string>(out var url)
                || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            return new VersionMetadataLocation(
                url,
                entry["sha1"] is JsonValue sha1Value
                    && sha1Value.TryGetValue<string>(out var sha1)
                    ? sha1
                    : null);
        }

        return null;
    }

    private static bool IsSha1(string? value)
    {
        return value is { Length: 40 } && value.All(Uri.IsHexDigit);
    }

    private static bool IsValidManifestEntry(JsonNode? entry)
    {
        return entry is JsonObject versionObject
            && versionObject["id"] is JsonValue idValue
            && idValue.TryGetValue<string>(out var id)
            && !string.IsNullOrWhiteSpace(id)
            && versionObject["url"] is JsonValue urlValue
            && urlValue.TryGetValue<string>(out var url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }

    public static string? GetClientJarSha1(JsonObject versionJson)
    {
        return versionJson["downloads"]?["client"]?["sha1"]?.GetValue<string>();
    }

    public static long? GetClientJarSize(JsonObject versionJson)
    {
        return versionJson["downloads"]?["client"]?["size"]?.GetValue<long?>();
    }

    public static string? GetServerJarSha1(JsonObject versionJson)
    {
        return versionJson["downloads"]?["server"]?["sha1"]?.GetValue<string>();
    }

    public static long? GetServerJarSize(JsonObject versionJson)
    {
        return versionJson["downloads"]?["server"]?["size"]?.GetValue<long?>();
    }

    private sealed record VersionMetadataLocation(string Url, string? Sha1);
}
