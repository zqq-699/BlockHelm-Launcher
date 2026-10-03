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
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// 解析 CurseForge 文件元数据、指纹匹配与下载地址，并构造官方 CDN 回退候选。
/// </summary>
public sealed class CurseForgeApiClient
{
    // API key 仅进入请求头，异常和返回模型都不保留凭据。
    private const string BaseUrl = "https://api.curseforge.com/v1";
    private const int MaxMetadataAttempts = 3;
    private static readonly TimeSpan MaximumMetadataRetryAfter = TimeSpan.FromSeconds(60);
    private readonly HttpClient httpClient;
    private readonly IImportConcurrencyLimiter limiter;
    private readonly ILogger logger;
    private readonly DownloadHostConcurrencyController hostConcurrencyController;
    private readonly Func<TimeSpan, CancellationToken, Task> metadataDelayAsync;

    public CurseForgeApiClient(
        HttpClient? httpClient = null,
        IImportConcurrencyLimiter? limiter = null,
        ILogger<CurseForgeApiClient>? logger = null)
        : this(httpClient, limiter, logger, DownloadHostConcurrencyController.Shared)
    {
    }

    internal CurseForgeApiClient(
        HttpClient? httpClient,
        IImportConcurrencyLimiter? limiter,
        ILogger<CurseForgeApiClient>? logger,
        DownloadHostConcurrencyController hostConcurrencyController,
        Func<TimeSpan, CancellationToken, Task>? metadataDelayAsync = null)
    {
        this.httpClient = httpClient ?? MinecraftHttpClientFactory.CreateTransportClient();
        this.limiter = limiter ?? ImportConcurrencyLimiter.Shared;
        this.logger = logger ?? NullLogger<CurseForgeApiClient>.Instance;
        this.hostConcurrencyController = hostConcurrencyController;
        this.metadataDelayAsync = metadataDelayAsync ?? Task.Delay;
    }

    internal async Task<CurseForgeResolvedFileDownload> GetFileDownloadAsync(
        long projectId,
        long fileId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        // 先获取文件名和哈希，再查询受权限控制的下载 URL；缺失时按官方 CDN 规则推导候选。
        var downloadUrlResult = await TryGetDownloadUrlAsync(projectId, fileId, apiKey, cancellationToken).ConfigureAwait(false);
        var file = await GetFileMetadataAsync(projectId, fileId, apiKey, cancellationToken).ConfigureAwait(false);

        var fallbackUrls = new List<string>();
        var primaryUrl = string.Empty;
        if (!string.IsNullOrWhiteSpace(downloadUrlResult.DownloadUrl))
        {
            primaryUrl = downloadUrlResult.DownloadUrl;
            AddDistinctUrl(fallbackUrls, file.DownloadUrl, primaryUrl);
        }
        else if (!string.IsNullOrWhiteSpace(file.DownloadUrl))
        {
            primaryUrl = file.DownloadUrl;
        }

        // A missing URL from both CurseForge endpoints confirms that automatic
        // third-party distribution is unavailable. This flag controls only whether
        // final failure may become a manual item; inferred CDN URLs improve coverage
        // for every successfully resolved CurseForge file.
        var isDistributionRestricted = string.IsNullOrWhiteSpace(primaryUrl);
        var edgeUrl = BuildCdnUrl("edge.forgecdn.net", fileId, file.FileName);
        if (isDistributionRestricted)
            primaryUrl = edgeUrl;
        else
            AddDistinctUrl(fallbackUrls, edgeUrl, primaryUrl);
        AddDistinctUrl(
            fallbackUrls,
            BuildCdnUrl("mediafilez.forgecdn.net", fileId, file.FileName),
            primaryUrl);

        var hashes = ResolveHashes(file.Hashes);
        logger.LogDebug(
            "Resolved CurseForge file download candidates. ProjectId={ProjectId} FileId={FileId} FileName={FileName} DownloadUrlStatusCode={DownloadUrlStatusCode} HasDirectDownloadUrl={HasDirectDownloadUrl} FallbackUrlCount={FallbackUrlCount}",
            projectId,
            fileId,
            file.FileName,
            (int?)downloadUrlResult.StatusCode,
            !string.IsNullOrWhiteSpace(file.DownloadUrl),
            fallbackUrls.Count);

        return new CurseForgeResolvedFileDownload(
            projectId,
            fileId,
            file.FileName,
            string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
            primaryUrl,
            fallbackUrls,
            hashes.Sha1,
            hashes.Sha512,
            isDistributionRestricted);
    }

    internal async Task<IReadOnlyDictionary<long, CurseForgeFingerprintMatch>> GetFingerprintMatchesAsync(
        IReadOnlyList<long> fingerprints,
        string apiKey,
        CancellationToken cancellationToken)
    {
        // 指纹批量提交并按输入 fingerprint 建映射，未知或模糊匹配不写入结果。
        if (fingerprints.Count == 0)
            return new Dictionary<long, CurseForgeFingerprintMatch>();

        var fingerprintRequest = new CurseForgeFingerprintRequest(fingerprints.Distinct().ToArray());

        return await SendMetadataAsync(
            () =>
            {
                var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/fingerprints/432", apiKey);
                request.Content = JsonContent.Create(fingerprintRequest);
                return request;
            },
            async (response, token) =>
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await ParseFingerprintMatchesAsync(stream, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CurseForgeFile> GetFileMetadataAsync(
        long projectId,
        long fileId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        return await SendMetadataAsync(
            () => CreateRequest($"{BaseUrl}/mods/{projectId}/files/{fileId}", apiKey),
            async (response, token) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new ModpackImportException(
                        ModpackImportFailureReason.CurseForgeFileUnavailable,
                        $"CurseForge file {projectId}/{fileId} was not found.");
                }

                response.EnsureSuccessStatusCode();
                var fileResponse = await response.Content.ReadFromJsonAsync<CurseForgeFileResponse>(cancellationToken: token)
                    .ConfigureAwait(false);
                var file = fileResponse?.Data;
                if (file is null || string.IsNullOrWhiteSpace(file.FileName))
                {
                    throw new ModpackImportException(
                        ModpackImportFailureReason.InvalidManifest,
                        $"CurseForge file metadata is missing for {projectId}/{fileId}.");
                }

                return file;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadUrlResult> TryGetDownloadUrlAsync(
        long projectId,
        long fileId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        // 403/404 可能表示作者禁用 API 下载，不等同于元数据请求失败，调用方仍可回退 CDN。
        return await SendMetadataAsync(
            () => CreateRequest($"{BaseUrl}/mods/{projectId}/files/{fileId}/download-url", apiKey),
            async (response, token) =>
            {
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    logger.LogDebug(
                        "CurseForge download-url endpoint did not provide a direct URL. ProjectId={ProjectId} FileId={FileId} StatusCode={StatusCode}",
                        projectId,
                        fileId,
                        (int)response.StatusCode);
                    return new DownloadUrlResult(response.StatusCode, null);
                }

                response.EnsureSuccessStatusCode();
                var downloadUrlResponse = await response.Content.ReadFromJsonAsync<CurseForgeDownloadUrlResponse>(cancellationToken: token)
                    .ConfigureAwait(false);
                return new DownloadUrlResult(response.StatusCode, downloadUrlResponse?.Data);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendMetadataAsync<T>(
        Func<HttpRequestMessage> createRequest,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxMetadataAttempts; attempt++)
        {
            using var request = createRequest();
            try
            {
                return await SendMetadataAttemptAsync(request, readAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < MaxMetadataAttempts
                && IsTransientMetadataFailure(exception, cancellationToken))
            {
                var delay = GetMetadataRetryDelay(exception, attempt);
                logger.LogDebug(
                    exception,
                    "CurseForge metadata request will be retried. Attempt={Attempt} MaxAttempts={MaxAttempts} DelayMs={DelayMs}",
                    attempt,
                    MaxMetadataAttempts,
                    delay.TotalMilliseconds);
                await metadataDelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("CurseForge metadata retry loop completed without a result.");
    }

    private async Task<T> SendMetadataAttemptAsync<T>(
        HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("CurseForge request URI is missing.");
        await using var admission = await hostConcurrencyController.AcquireAsync(
            uri,
            limiter.AcquireMetadataSlotAsync,
            applyColdStartJitter: true,
            cancellationToken).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        DownloadFailureReason? failureReason = null;
        HttpStatusCode? statusCode = null;
        var recordResult = false;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            statusCode = response.StatusCode;
            if (IsTransientMetadataStatus(response.StatusCode))
            {
                throw new CurseForgeMetadataTransientException(
                    response.StatusCode,
                    ParseRetryAfter(response));
            }
            var result = await readAsync(response, cancellationToken).ConfigureAwait(false);
            failureReason = response.IsSuccessStatusCode ? null : DownloadFailureReason.HttpStatus;
            recordResult = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            failureReason = response is null
                ? DownloadFailureReason.ResponseHeadersTimeout
                : DownloadFailureReason.BodyInterrupted;
            statusCode = response?.StatusCode;
            recordResult = true;
            throw;
        }
        catch (HttpRequestException)
        {
            failureReason = response is null
                ? DownloadFailureReason.Network
                : response.IsSuccessStatusCode
                    ? DownloadFailureReason.BodyInterrupted
                    : DownloadFailureReason.HttpStatus;
            statusCode = response?.StatusCode;
            recordResult = true;
            throw;
        }
        catch (IOException)
        {
            failureReason = response is null
                ? DownloadFailureReason.Network
                : DownloadFailureReason.BodyInterrupted;
            statusCode = response?.StatusCode;
            recordResult = true;
            throw;
        }
        finally
        {
            response?.Dispose();
            if (recordResult)
                RecordHostResult(admission.Origin, failureReason, statusCode);
        }
    }

    private static bool IsTransientMetadataFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
            return !cancellationToken.IsCancellationRequested;
        if (exception is CurseForgeMetadataTransientException)
            return true;
        if (exception is HttpRequestException httpException)
            return httpException.StatusCode is null || IsTransientMetadataStatus(httpException.StatusCode.Value);
        return exception is IOException;
    }

    private static bool IsTransientMetadataStatus(HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || value is >= 500 and <= 599;
    }

    private static TimeSpan GetMetadataRetryDelay(Exception exception, int attempt)
    {
        if (exception is CurseForgeMetadataTransientException { RetryAfter: { } retryAfter })
            return retryAfter > MaximumMetadataRetryAfter ? MaximumMetadataRetryAfter : retryAfter;

        return TimeSpan.FromSeconds(Math.Pow(2, Math.Max(attempt - 1, 0)));
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is not { } date)
            return null;
        var delay = date - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private void RecordHostResult(
        string origin,
        DownloadFailureReason? failureReason,
        HttpStatusCode? statusCode)
    {
        var adjustment = hostConcurrencyController.RecordResult(origin, failureReason, statusCode);
        if (adjustment is null)
            return;
        logger.LogDebug(
            "Download host concurrency adjusted. HostOrigin={HostOrigin} PreviousTarget={PreviousTarget} CurrentTarget={CurrentTarget} AdjustmentReason={AdjustmentReason} SuccessCount={SuccessCount} FailureCount={FailureCount}",
            adjustment.Origin,
            adjustment.PreviousTarget,
            adjustment.CurrentTarget,
            adjustment.Reason,
            adjustment.Successes,
            adjustment.Failures);
    }

    private static HttpRequestMessage CreateRequest(string requestUri, string apiKey)
    {
        return CreateRequest(HttpMethod.Get, requestUri, apiKey);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string requestUri, string apiKey)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    private static async Task<IReadOnlyDictionary<long, CurseForgeFingerprintMatch>> ParseFingerprintMatchesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, CurseForgeFingerprintMatch>();
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
            JsonElement? file = match.TryGetProperty("file", out var fileElement) ? fileElement : null;
            var projectId = file is null
                ? null
                : TryReadLong(file.Value, "modId") ?? TryReadLong(file.Value, "projectId");
            projectId ??= TryReadLong(match, "modId") ?? TryReadLong(match, "projectId") ?? TryReadLong(match, "id");

            var fileId = file is null
                ? null
                : TryReadLong(file.Value, "id") ?? TryReadLong(file.Value, "fileId");
            fileId ??= TryReadLong(match, "fileId") ?? TryReadLong(match, "fileID");

            var fingerprint = TryReadLong(match, "fileFingerprint") ?? TryReadLong(match, "fingerprint");
            if (fingerprint is null && file is not null)
            {
                fingerprint = TryReadLong(file.Value, "fileFingerprint")
                              ?? TryReadLong(file.Value, "fingerprint")
                              ?? TryReadFirstFingerprint(file.Value);
            }

            if (fingerprint is not null && projectId is not null && fileId is not null)
            {
                result[fingerprint.Value] = new CurseForgeFingerprintMatch(
                    projectId.Value,
                    fileId.Value,
                    file is null ? string.Empty : TryReadString(file.Value, "displayName")
                        ?? TryReadString(file.Value, "fileName")
                        ?? string.Empty,
                    file is null ? null : TryReadDateTimeOffset(file.Value, "fileDate"));
            }
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
        return element.TryGetProperty(propertyName, out var property)
            ? TryReadLong(property)
            : null;
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

    private static (string? Sha1, string? Sha512) ResolveHashes(IReadOnlyList<CurseForgeFileHash>? hashes)
    {
        // CurseForge 用数值算法标识，转换为明确字段避免调用方猜测列表顺序。
        string? sha1 = null;
        string? sha512 = null;
        if (hashes is null)
            return (null, null);

        foreach (var hash in hashes)
        {
            if (string.IsNullOrWhiteSpace(hash.Value))
                continue;

            if (hash.Algo == 1 || hash.Value.Length == 40)
                sha1 ??= hash.Value;
            else if (hash.Value.Length == 128)
                sha512 ??= hash.Value;
        }

        return (sha1, sha512);
    }

    private static string BuildCdnUrl(string host, long fileId, string fileName)
    {
        var part1 = fileId / 1000;
        var part2 = fileId % 1000;
        var encodedFileName = Uri.EscapeDataString(fileName);
        return $"https://{host}/files/{part1}/{part2}/{encodedFileName}";
    }

    private static void AddDistinctUrl(ICollection<string> urls, string? candidateUrl, string primaryUrl)
    {
        // URL 按优先级去重，主地址不会在 fallback 列表再次尝试。
        if (string.IsNullOrWhiteSpace(candidateUrl)
            || string.Equals(candidateUrl, primaryUrl, StringComparison.OrdinalIgnoreCase)
            || urls.Contains(candidateUrl, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        urls.Add(candidateUrl);
    }

    internal sealed record CurseForgeResolvedFileDownload(
        long ProjectId,
        long FileId,
        string FileName,
        string DisplayName,
        string PrimaryUrl,
        IReadOnlyList<string> FallbackUrls,
        string? Sha1,
        string? Sha512,
        bool IsDistributionRestricted);

    internal sealed record CurseForgeFingerprintMatch(
        long ProjectId,
        long FileId,
        string VersionNumber,
        DateTimeOffset? FileDate);

    private sealed record DownloadUrlResult(HttpStatusCode StatusCode, string? DownloadUrl);

    private sealed class CurseForgeMetadataTransientException : HttpRequestException
    {
        public CurseForgeMetadataTransientException(HttpStatusCode statusCode, TimeSpan? retryAfter)
            : base($"CurseForge metadata endpoint returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan? RetryAfter { get; }
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? TryReadDateTimeOffset(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.String
        && DateTimeOffset.TryParse(property.GetString(), out var value)
            ? value
            : null;

    private sealed record CurseForgeFingerprintRequest(
        [property: JsonPropertyName("fingerprints")] IReadOnlyList<long> Fingerprints);

    private sealed class CurseForgeFileResponse
    {
        [JsonPropertyName("data")]
        public CurseForgeFile? Data { get; init; }
    }

    private sealed class CurseForgeFile
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; init; } = string.Empty;

        [JsonPropertyName("fileName")]
        public string FileName { get; init; } = string.Empty;

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; init; }

        [JsonPropertyName("hashes")]
        public IReadOnlyList<CurseForgeFileHash>? Hashes { get; init; }
    }

    private sealed class CurseForgeFileHash
    {
        [JsonPropertyName("algo")]
        public int Algo { get; init; }

        [JsonPropertyName("value")]
        public string Value { get; init; } = string.Empty;
    }

    private sealed class CurseForgeDownloadUrlResponse
    {
        [JsonPropertyName("data")]
        public string? Data { get; init; }
    }
}
