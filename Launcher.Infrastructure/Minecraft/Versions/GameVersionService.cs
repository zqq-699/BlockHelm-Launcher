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

using System.Text.Json.Nodes;
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed class GameVersionService : IGameVersionService
{
    internal static DownloadRetryOptions VersionCatalogRetryOptions { get; } = new()
    {
        // 版本目录是交互式元数据；各来源使用较短的超时，查询调度负责启动备用源。
        MaxAttemptsPerSource = 1,
        ResponseHeadersTimeout = TimeSpan.FromSeconds(5),
        FirstByteTimeout = TimeSpan.FromSeconds(5),
        BodyIdleTimeout = TimeSpan.FromSeconds(8)
    };

    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger<GameVersionService> logger;

    public GameVersionService(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<GameVersionService>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<GameVersionService>.Instance;
    }

    public async Task<IReadOnlyList<MinecraftVersionInfo>> GetVersionsAsync(
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        var bandwidthLimiter = DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            bandwidthLimiter,
            category: DownloadConcurrencyCategory.Metadata,
            retryOptions: VersionCatalogRetryOptions);
        var lookup = await executor.ExecuteStaggeredLookupAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
            downloadSourcePreference,
            categoryHint: "Mojang",
            async (context, token) =>
            {
                await using var stream = await context.Response.Content.ReadAsStreamAsync(token);
                var manifestNode = await JsonNode.ParseAsync(stream, cancellationToken: token);
                if (manifestNode is not JsonObject manifestObject
                    || manifestObject["versions"] is not JsonArray versionEntries)
                {
                    throw new DownloadContentValidationException(
                        "Minecraft version manifest is missing a versions array.");
                }

                if (versionEntries.Count == 0 || versionEntries.Any(entry => !IsValidVersionEntry(entry)))
                {
                    throw new DownloadContentValidationException(
                        "Minecraft version manifest contains an invalid version entry.");
                }

                try
                {
                    var versions = versionEntries
                        .Select(entry => (JsonObject)entry!)
                        .Select(entry => new MinecraftVersionInfo(
                            entry["id"]!.GetValue<string>(),
                            entry["type"]!.GetValue<string>(),
                            false,
                            entry["releaseTime"]?.GetValue<DateTimeOffset?>()))
                        .OrderBy(v => VersionTypeRank(v.Type))
                        .ThenByDescending(v => v.Type.Equals("Release", StringComparison.OrdinalIgnoreCase) ? VersionSortKey(v.Name) : new Version(0, 0))
                        .ThenByDescending(v => v.ReleaseTime ?? DateTimeOffset.MinValue)
                        .ThenByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return (Versions: versions, context.Resolution);
                }
                catch (Exception exception) when (exception is FormatException or InvalidOperationException)
                {
                    throw new DownloadContentValidationException("Minecraft version manifest contains invalid version metadata.", exception);
                }
            },
            _ => false,
            cancellationToken);

        var manifestResult = lookup.Value;

        logger.LogDebug(
            "Minecraft versions loaded. RequestedSourcePreference={RequestedSourcePreference} ResolvedSourceKind={ResolvedSourceKind} VersionCount={VersionCount}",
            downloadSourcePreference,
            manifestResult.Resolution.ResolvedSourceKind,
            manifestResult.Versions.Count);
        return manifestResult.Versions;
    }

    private static int VersionTypeRank(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "release" => 0,
            "snapshot" => 1,
            "old_beta" => 2,
            "old_alpha" => 3,
            _ => 4
        };
    }

    private static bool IsValidVersionEntry(JsonNode? entry)
    {
        if (entry is not JsonObject versionObject
            || versionObject["id"] is not JsonValue idValue
            || !idValue.TryGetValue<string>(out var id)
            || string.IsNullOrWhiteSpace(id)
            || versionObject["type"] is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type)
            || string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        return true;
    }

    private static Version VersionSortKey(string name)
    {
        var clean = name.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        return Version.TryParse(clean, out var version) ? version : new Version(0, 0);
    }
}
