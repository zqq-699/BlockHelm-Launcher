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
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal static class QuiltVersionComposer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<string> CreateFinalVersionAsync(
        HttpClient httpClient,
        string minecraftVersion,
        string loaderVersion,
        string finalVersionName,
        string minecraftDirectory,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareFinalVersionAsync(
            httpClient, minecraftVersion, loaderVersion, finalVersionName, minecraftDirectory,
            downloadSourcePreference, downloadSpeedLimitMbPerSecond, downloadSpeedLimitState,
            logger, cancellationToken).ConfigureAwait(false);
        return prepared.VersionName;
    }

    public static async Task<PreparedVersionInstall> PrepareFinalVersionAsync(
        HttpClient httpClient,
        string minecraftVersion,
        string loaderVersion,
        string finalVersionName,
        string minecraftDirectory,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var finalVersionDirectory = Path.Combine(minecraftDirectory, "versions", finalVersionName);
        var finalVersionJsonPath = Path.Combine(finalVersionDirectory, $"{finalVersionName}.json");

        if (Directory.Exists(finalVersionDirectory))
            throw new IOException($"Version directory already exists: {finalVersionName}");

        var baseVersionJsonTask = VanillaVersionMetadataClient.DownloadVersionJsonAsync(
            httpClient,
            minecraftVersion,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            downloadSpeedLimitState,
            logger,
            cancellationToken);
        var quiltProfileJsonTask = DownloadQuiltProfileJsonAsync(
            httpClient,
            minecraftVersion,
            loaderVersion,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            downloadSpeedLimitState,
            logger,
            cancellationToken);
        await Task.WhenAll(baseVersionJsonTask, quiltProfileJsonTask).ConfigureAwait(false);
        var baseVersionJson = await baseVersionJsonTask.ConfigureAwait(false);
        var quiltProfileJson = await quiltProfileJsonTask.ConfigureAwait(false);
        var finalVersionJson = BuildFinalVersionJson(baseVersionJson, quiltProfileJson, finalVersionName, minecraftVersion);

        Directory.CreateDirectory(finalVersionDirectory);

        try
        {
            await File.WriteAllTextAsync(
                finalVersionJsonPath,
                finalVersionJson.ToJsonString(JsonOptions),
                cancellationToken);

            return new PreparedVersionInstall(finalVersionName, finalVersionDirectory);
        }
        catch
        {
            if (Directory.Exists(finalVersionDirectory))
                Directory.Delete(finalVersionDirectory, recursive: true);

            throw;
        }
    }

    internal static JsonObject BuildFinalVersionJson(
        JsonObject baseVersionJson,
        JsonObject quiltProfileJson,
        string finalVersionName,
        string minecraftVersion)
    {
        return VersionJsonMergeHelper.MergeFlattenedVersion(
            baseVersionJson,
            quiltProfileJson,
            finalVersionName,
            minecraftVersion);
    }

    private static async Task<JsonObject> DownloadQuiltProfileJsonAsync(
        HttpClient httpClient,
        string minecraftVersion,
        string loaderVersion,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var profileUrl = $"https://meta.quiltmc.org/v3/versions/loader/{minecraftVersion}/{loaderVersion}/profile/json";
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Metadata,
            retryOptions: DownloadRetryOptions.InteractiveMetadata);
        var result = await executor.ExecuteStaggeredLookupAsync(
            profileUrl,
            downloadSourcePreference,
            categoryHint: "Quilt",
            async (context, token) =>
            {
                await using var profileStream = await context.Response.Content.ReadAsStreamAsync(token);
                var profileNode = await JsonNode.ParseAsync(profileStream, cancellationToken: token);
                if (profileNode is not JsonObject profileObject)
                {
                    throw SafeExceptionLog.Describe(new DownloadContentValidationException(
                        $"Quilt profile metadata is not a JSON object: {minecraftVersion} {loaderVersion}"), "LoaderProfileRootInvalid",
                        ("ExpectedFormat", "JSON object"), ("MinecraftVersion", minecraftVersion), ("LoaderVersion", loaderVersion), ("Loader", "Quilt"));
                }

                return profileObject;
            },
            static _ => false,
            cancellationToken);
        return result.Value!;
    }

}
