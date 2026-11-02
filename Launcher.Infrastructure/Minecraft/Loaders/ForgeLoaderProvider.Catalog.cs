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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class ForgeLoaderProvider
{
private async Task<IReadOnlyDictionary<string, ForgeCatalogEntry>> GetCatalogAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        await catalogLock.WaitAsync(cancellationToken);
        try
        {
            var cacheKey = $"{downloadSourcePreference}:{minecraftVersion}";
            if (catalogCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var executor = new MinecraftDownloadRequestExecutor(
                httpClient,
                logger,
                DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
                category: DownloadConcurrencyCategory.Metadata);
            var result = await executor.ExecuteStaggeredLookupAsync(
                GetForgeIndexUrl(minecraftVersion),
                downloadSourcePreference,
                categoryHint: "Forge",
                async (context, token) =>
                {
                    IReadOnlyDictionary<string, ForgeCatalogEntry> entries;
                    if (context.Resolution.ResolvedSourceKind.Equals("BmclApiForge", StringComparison.Ordinal))
                    {
                        await using var stream = await context.Response.Content.ReadAsStreamAsync(token);
                        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                        if (document.RootElement.ValueKind is not JsonValueKind.Array)
                        {
                            throw new DownloadContentValidationException(
                                "BMCLAPI Forge catalog is not a JSON array.");
                        }

                        entries = ParseBmclCatalogEntries(minecraftVersion, document.RootElement);
                        if (document.RootElement.GetArrayLength() > 0 && entries.Count == 0)
                        {
                            throw new DownloadContentValidationException(
                                "BMCLAPI Forge catalog contains no valid loader entries.");
                        }
                    }
                    else
                    {
                        var html = await context.Response.Content.ReadAsStringAsync(token);
                        if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase)
                            && !html.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DownloadContentValidationException(
                                "Forge catalog response is not an HTML document.");
                        }

                        entries = ParseCatalogEntries(minecraftVersion, html);
                    }

                    if (entries.Count == 0)
                        throw new DownloadNoResultException("Forge returned no matching loader versions.");

                    return entries;
                },
                statusCode => statusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                cancellationToken);
            catalogCache[cacheKey] = result.Found ? result.Value! : EmptyCatalog();
            return catalogCache[cacheKey];
        }
        finally
        {
            catalogLock.Release();
        }
    }

    private static IReadOnlyDictionary<string, ForgeCatalogEntry> ParseCatalogEntries(string minecraftVersion, string html)
    {
        var entries = new Dictionary<string, ForgeCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InstallerUrlRegex.Matches(html))
        {
            var fullVersion = match.Groups["fullVersion"].Value;
            var artifactVersion = match.Groups["artifactVersion"].Value;
            if (!string.Equals(fullVersion, artifactVersion, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!fullVersion.StartsWith($"{minecraftVersion}-", StringComparison.OrdinalIgnoreCase))
                continue;

            var forgeVersion = fullVersion[(minecraftVersion.Length + 1)..];
            if (string.IsNullOrWhiteSpace(forgeVersion))
                continue;

            if (entries.ContainsKey(forgeVersion))
                continue;

            entries[forgeVersion] = new ForgeCatalogEntry(
                minecraftVersion,
                forgeVersion,
                new Uri(match.Value, UriKind.Absolute));
        }

        return entries;
    }

    private static Version ParseForgeVersion(string version)
    {
        return Version.TryParse(version, out var parsed)
            ? parsed
            : new Version(0, 0);
    }

    private static string GetForgeIndexUrl(string minecraftVersion)
    {
        return $"https://files.minecraftforge.net/net/minecraftforge/forge/index_{minecraftVersion}.html";
    }

    private static IReadOnlyDictionary<string, ForgeCatalogEntry> EmptyCatalog()
    {
        return new Dictionary<string, ForgeCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    }
}
