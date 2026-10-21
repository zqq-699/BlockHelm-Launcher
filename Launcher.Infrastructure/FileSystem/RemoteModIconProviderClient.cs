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
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Resources;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 通过本地 Mod 哈希/指纹批量匹配 Modrinth 与 CurseForge 项目图标。
/// </summary>
internal sealed class RemoteModIconProviderClient
{
    // 请求按提供商批量化，返回值以本地完整路径索引，调用方无需再次进行哈希关联。
    private const string ModrinthBaseUrl = "https://api.modrinth.com/v2";
    private const string CurseForgeBaseUrl = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly ICurseForgeApiKeyResolver curseForgeApiKeyResolver;
    private readonly ILogger logger;

    public RemoteModIconProviderClient(
        HttpClient httpClient,
        ICurseForgeApiKeyResolver curseForgeApiKeyResolver,
        ILogger logger)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.curseForgeApiKeyResolver = curseForgeApiKeyResolver;
        this.logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, RemoteIconCandidate>> ResolveModrinthAsync(
        IReadOnlyList<ModIconLookupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // SHA-1 一次提交查询版本文件，再批量获取去重后的项目资料，避免逐 Mod 网络请求。
        try
        {
            var hashes = candidates.Select(candidate => candidate.Sha1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var candidatesBySha1 = candidates
                .GroupBy(candidate => candidate.Sha1, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            using var response = await httpClient.PostAsJsonAsync(
                    $"{ModrinthBaseUrl}/version_files",
                    new ModrinthVersionFilesRequest(hashes, "sha1"),
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Modrinth local mod icon lookup was rejected. StatusCode={StatusCode}",
                    response.StatusCode);
                return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
            }

            var versions = await response.Content.ReadFromJsonAsync<Dictionary<string, ModrinthVersionFileMatch>>(
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new Dictionary<string, ModrinthVersionFileMatch>(StringComparer.OrdinalIgnoreCase);
            var projectIds = versions.Values
                .Select(match => match.ProjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (projectIds.Length == 0)
                return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);

            var projectsParameter = Uri.EscapeDataString(JsonSerializer.Serialize(projectIds, JsonOptions));
            var projects = await httpClient.GetFromJsonAsync<List<ModrinthProject>>(
                    $"{ModrinthBaseUrl}/projects?ids={projectsParameter}",
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? [];
            var projectsById = projects
                .Where(project => !string.IsNullOrWhiteSpace(project.Id))
                .ToDictionary(project => project.Id, StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sha1, version) in versions)
            {
                if (string.IsNullOrWhiteSpace(version.ProjectId)
                    || !projectsById.TryGetValue(version.ProjectId, out var project)
                    || !candidatesBySha1.TryGetValue(sha1, out var candidate))
                {
                    continue;
                }

                result[sha1] = new RemoteIconCandidate(
                    "modrinth",
                    version.ProjectId,
                    project.Slug,
                    project.IconUrl ?? string.Empty,
                    ResourceProjectCategoryMapping.MapModrinth(candidate.Kind, project.Categories));
            }

            logger.LogDebug(
                "Modrinth resolved remote local mod icons. RequestedCount={RequestedCount} ResolvedCount={ResolvedCount}",
                hashes.Length,
                result.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to resolve remote local mod icons from Modrinth.");
            return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<IReadOnlyDictionary<string, RemoteIconCandidate>> ResolveCurseForgeAsync(
        IReadOnlyList<ModIconLookupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // CurseForge Murmur2 指纹可能发生无匹配，只有精确返回的项目关系才进入结果。
        var apiKey = await curseForgeApiKeyResolver.TryResolveAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogDebug("Skipping CurseForge local mod icon lookup because API key is not configured.");
            return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var fingerprints = candidates.Select(candidate => candidate.CurseForgeFingerprint).Distinct().ToArray();
            using var fingerprintRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{CurseForgeBaseUrl}/fingerprints/{MinecraftGameId}")
            {
                Content = JsonContent.Create(new CurseForgeFingerprintRequest(fingerprints), options: JsonOptions)
            };
            fingerprintRequest.Headers.Add("x-api-key", apiKey);

            using var fingerprintResponse = await httpClient.SendAsync(fingerprintRequest, cancellationToken)
                .ConfigureAwait(false);
            if (!fingerprintResponse.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "CurseForge local mod fingerprint lookup was rejected. StatusCode={StatusCode}",
                    fingerprintResponse.StatusCode);
                return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
            }

            await using var fingerprintStream = await fingerprintResponse.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var projectByFingerprint = await ParseFingerprintMatchesAsync(fingerprintStream, cancellationToken)
                .ConfigureAwait(false);
            if (projectByFingerprint.Count == 0)
                return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);

            var projectIds = projectByFingerprint.Values.Distinct().ToArray();
            using var modsRequest = new HttpRequestMessage(HttpMethod.Post, $"{CurseForgeBaseUrl}/mods")
            {
                Content = JsonContent.Create(new CurseForgeModsRequest(projectIds), options: JsonOptions)
            };
            modsRequest.Headers.Add("x-api-key", apiKey);

            using var modsResponse = await httpClient.SendAsync(modsRequest, cancellationToken).ConfigureAwait(false);
            if (!modsResponse.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "CurseForge local mod icon metadata lookup was rejected. StatusCode={StatusCode}",
                    modsResponse.StatusCode);
                return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
            }

            var mods = await modsResponse.Content.ReadFromJsonAsync<CurseForgeModsResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? new CurseForgeModsResponse();
            var modsById = mods.Data.ToDictionary(mod => mod.Id);
            var candidatesByFingerprint = candidates.ToDictionary(candidate => candidate.CurseForgeFingerprint);
            var result = new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fingerprint, projectId) in projectByFingerprint)
            {
                if (!candidatesByFingerprint.TryGetValue(fingerprint, out var candidate)
                    || !modsById.TryGetValue(projectId, out var mod))
                {
                    continue;
                }

                var iconUrl = string.IsNullOrWhiteSpace(mod.Logo?.Url) ? mod.Logo?.ThumbnailUrl : mod.Logo.Url;
                result[candidate.Sha1] = new RemoteIconCandidate(
                    "curseforge",
                    projectId.ToString(),
                    mod.Slug,
                    iconUrl ?? string.Empty,
                    ResourceProjectCategoryMapping.MapCurseForge(
                        candidate.Kind,
                        mod.Categories.SelectMany(category => new[] { category.Name, category.Slug })));
            }

            logger.LogDebug(
                "CurseForge resolved remote local mod icons. RequestedCount={RequestedCount} ResolvedCount={ResolvedCount}",
                candidates.Count,
                result.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to resolve remote local mod icons from CurseForge.");
            return new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task<Dictionary<long, long>> ParseFingerprintMatchesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // API 历史字段形状不同，解析器兼容数字和字符串，但拒绝缺失 fingerprint/file Id 的条目。
        var result = new Dictionary<long, long>();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("exactMatches", out var exactMatches)
            || exactMatches.ValueKind is not JsonValueKind.Array)
        {
            return result;
        }

        foreach (var match in exactMatches.EnumerateArray())
        {
            var projectId = TryReadLong(match, "id") ?? TryReadLong(match, "modId") ?? TryReadLong(match, "projectId");
            JsonElement? file = match.TryGetProperty("file", out var fileElement) ? fileElement : null;
            if (projectId is null && file is not null)
                projectId = TryReadLong(file.Value, "modId") ?? TryReadLong(file.Value, "projectId");

            var fingerprint = TryReadLong(match, "fileFingerprint") ?? TryReadLong(match, "fingerprint");
            if (fingerprint is null && file is not null)
            {
                fingerprint = TryReadLong(file.Value, "fileFingerprint")
                              ?? TryReadLong(file.Value, "fingerprint")
                              ?? TryReadFirstFingerprint(file.Value);
            }

            if (projectId is not null && fingerprint is not null)
                result[fingerprint.Value] = projectId.Value;
        }

        return result;
    }

    private static long? TryReadFirstFingerprint(JsonElement element)
    {
        if (!element.TryGetProperty("fingerprints", out var fingerprints)
            || fingerprints.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        foreach (var fingerprint in fingerprints.EnumerateArray())
        {
            var value = fingerprint.ValueKind is JsonValueKind.Object
                ? TryReadLong(fingerprint, "value")
                : TryReadLong(fingerprint);
            if (value is not null)
                return value;
        }

        return null;
    }

    private static long? TryReadLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) ? TryReadLong(property) : null;
    }

    private static long? TryReadLong(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
            _ => null
        };
    }

    private sealed record ModrinthVersionFilesRequest(
        [property: JsonPropertyName("hashes")] IReadOnlyList<string> Hashes,
        [property: JsonPropertyName("algorithm")] string Algorithm);

    private sealed class ModrinthVersionFileMatch
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; init; } = string.Empty;
    }

    private sealed class ModrinthProject
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; init; }

        [JsonPropertyName("categories")]
        public List<string?> Categories { get; init; } = [];
    }

    private sealed record CurseForgeFingerprintRequest(
        [property: JsonPropertyName("fingerprints")] IReadOnlyList<long> Fingerprints);

    private sealed record CurseForgeModsRequest(
        [property: JsonPropertyName("modIds")] IReadOnlyList<long> ModIds);

    private sealed class CurseForgeModsResponse
    {
        [JsonPropertyName("data")]
        public List<CurseForgeMod> Data { get; init; } = [];
    }

    private sealed class CurseForgeMod
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("logo")]
        public CurseForgeModLogo? Logo { get; init; }

        [JsonPropertyName("categories")]
        public List<CurseForgeCategory> Categories { get; init; } = [];
    }

    private sealed class CurseForgeCategory
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }
    }

    private sealed class CurseForgeModLogo
    {
        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }
}

internal sealed record ModIconLookupCandidate(
    string FullPath,
    string Sha1,
    string FileAlias,
    long CurseForgeFingerprint,
    ResourceProjectKind Kind)
{
    public string Sha1Alias => $"sha1:{Sha1}";
}

internal sealed record RemoteIconCandidate(
    string Source,
    string ProjectId,
    string? Slug,
    string IconUrl,
    IReadOnlyList<ResourceProjectCategory> Categories);
