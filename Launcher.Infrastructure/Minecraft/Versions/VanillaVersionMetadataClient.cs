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
        CancellationToken cancellationToken = default)
    {
        return await DownloadVersionJsonAsync(
                httpClient,
                minecraftVersion,
                downloadSourcePreference,
                requireManifestSha1: false,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<JsonObject> DownloadVerifiedVersionJsonAsync(
        HttpClient httpClient,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return await DownloadVersionJsonAsync(
                httpClient,
                minecraftVersion,
                downloadSourcePreference,
                requireManifestSha1: true,
                downloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState,
                logger,
                cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Metadata);
        var versionMetadata = await executor.ExecuteAsync(
            VersionManifestUrl,
            downloadSourcePreference,
            categoryHint: "Mojang",
            async (context, token) =>
            {
                await using var manifestStream = await context.Response.Content.ReadAsStreamAsync(token);
                var manifestNode = await JsonNode.ParseAsync(manifestStream, cancellationToken: token);
                if (manifestNode is not JsonObject manifestObject
                    || manifestObject["versions"] is not JsonArray versionEntries)
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException("Minecraft version manifest is missing a versions array."), "DownloadContentValidationException", ("Reason", "Minecraft version manifest is missing a versions array."));
                }

                if (versionEntries.Any(entry => !IsValidManifestEntry(entry)))
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException("Minecraft version manifest contains an invalid version entry."), "DownloadContentValidationException", ("Reason", "Minecraft version manifest contains an invalid version entry."));
                }

                var resolvedVersionMetadata = FindVersionMetadata(versionEntries, minecraftVersion);
                if (resolvedVersionMetadata is null)
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                        $"Minecraft version manifest does not contain {minecraftVersion}."), "VersionManifestEntryMissing", ("MinecraftVersion", minecraftVersion));
                }
                if (requireManifestSha1 && !IsSha1(resolvedVersionMetadata.Sha1))
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                        $"Minecraft version manifest does not contain a valid SHA1 for {minecraftVersion}."), "VersionManifestHashInvalid", ("MinecraftVersion", minecraftVersion), ("Field", "sha1"));
                }

                return resolvedVersionMetadata;
            },
            cancellationToken);

        return await executor.ExecuteAsync(
            versionMetadata.Url,
            downloadSourcePreference,
            categoryHint: "Mojang",
            async (context, token) =>
            {
                var versionBytes = await context.Response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (requireManifestSha1)
                {
                    var actualSha1 = Convert.ToHexString(SHA1.HashData(versionBytes));
                    if (!string.Equals(actualSha1, versionMetadata.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                            $"Minecraft version metadata SHA1 does not match the official manifest: {minecraftVersion}"), "VersionMetadataHashMismatch", ("MinecraftVersion", minecraftVersion),
                            ("HashAlgorithm", "SHA1"), ("ExpectedHash", versionMetadata.Sha1), ("ActualHash", actualSha1));
                    }
                }

                var versionNode = JsonNode.Parse(versionBytes);
                if (versionNode is not JsonObject versionObject)
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                        $"Minecraft version metadata is not a JSON object: {minecraftVersion}"), "VersionMetadataRootInvalid", ("MinecraftVersion", minecraftVersion), ("ExpectedFormat", "JSON object"));
                }
                if (requireManifestSha1
                    && (versionObject["id"] is not JsonValue idValue
                        || !idValue.TryGetValue<string>(out var id)
                        || !string.Equals(id, minecraftVersion, StringComparison.Ordinal)))
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                        $"Minecraft version metadata id does not match the requested version: {minecraftVersion}"), "VersionMetadataIdMismatch", ("MinecraftVersion", minecraftVersion), ("Field", "id"));
                }

                return versionObject;
            },
            cancellationToken);
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
            && !string.IsNullOrWhiteSpace(url);
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
