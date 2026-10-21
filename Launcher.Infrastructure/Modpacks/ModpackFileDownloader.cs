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
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Modpacks;

internal sealed class ModpackFileDownloader
{
    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly IImportConcurrencyLimiter limiter;
    private readonly ILogger logger;

    public ModpackFileDownloader(
        HttpClient httpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        IImportConcurrencyLimiter limiter,
        ILogger logger)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.limiter = limiter;
        this.logger = logger;
    }

    public async Task<ResolvedDownloadRequest> DownloadToTemporaryFileAsync(
        IReadOnlyList<string> sourceUrls,
        string tempFilePath,
        string? curseForgeApiKey,
        DownloadSourcePreference downloadSourcePreference,
        string? expectedSha1,
        string? expectedSha512,
        int downloadSpeedLimitMbPerSecond,
        SpeedMeter? speedMeter,
        Action<int, long, long?>? reportAttemptProgress,
        Action<long>? reportTransferredBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUrls);
        if (sourceUrls.Count == 0)
            throw new ArgumentException("At least one download source URL is required.", nameof(sourceUrls));

        var unifiedBandwidthLimiter = DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            bandwidthLimiter: unifiedBandwidthLimiter,
            limiter: limiter,
            category: DownloadConcurrencyCategory.Modpack);
        var sensitiveHeaders = !string.IsNullOrWhiteSpace(curseForgeApiKey)
            && sourceUrls.Any(IsCurseForgeApiUrl)
            ? DownloadRequestHeaders.CurseForgeApiKey(curseForgeApiKey)
            : null;
        try
        {
            var hashes = new List<(HashAlgorithmName Algorithm, string Value)>();
            if (!string.IsNullOrWhiteSpace(expectedSha1))
                hashes.Add((HashAlgorithmName.SHA1, expectedSha1));
            if (!string.IsNullOrWhiteSpace(expectedSha512))
                hashes.Add((HashAlgorithmName.SHA512, expectedSha512));

            if (hashes.Count == 0)
            {
                return await executor.DownloadFileAsync(
                    sourceUrls, downloadSourcePreference, "ThirdParty", tempFilePath,
                    expectedSha1: null, expectedSize: null,
                    cancellationToken: cancellationToken,
                    sensitiveHeaders: sensitiveHeaders,
                    speedMeter: speedMeter,
                    reportAttemptProgress: reportAttemptProgress,
                    reportTransferredBytes: reportTransferredBytes).ConfigureAwait(false);
            }
            else
            {
                return await executor.DownloadFileAsync(
                    sourceUrls, downloadSourcePreference, "ThirdParty", tempFilePath,
                    new DownloadIntegrityExpectation(expectedSize: null, hashes),
                    cancellationToken: cancellationToken,
                    sensitiveHeaders: sensitiveHeaders,
                    speedMeter: speedMeter,
                    reportAttemptProgress: reportAttemptProgress,
                    reportTransferredBytes: reportTransferredBytes).ConfigureAwait(false);
            }
        }
        catch (MinecraftDownloadRequestExecutor.DownloadSourceRequestException exception)
            when (exception.InnerException is DownloadHashMismatchException)
        {
            throw new ModpackImportException(ModpackImportFailureReason.HashMismatch, "Downloaded modpack file did not match its expected hash.");
        }
    }

    private static bool IsCurseForgeApiUrl(string sourceUrl) =>
        Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri)
        && string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(sourceUri.Host, "api.curseforge.com", StringComparison.OrdinalIgnoreCase);

    public async Task VerifyHashAsync(
        string filePath,
        string expectedHash,
        HashAlgorithmName algorithmName,
        CancellationToken cancellationToken)
    {
        await using var lease = await limiter.AcquireHashSlotAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = File.OpenRead(filePath);
        var actualHashBytes = algorithmName.Name switch
        {
            "SHA1" => await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            "SHA512" => await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported hash algorithm: {algorithmName.Name}")
        };

        var actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.HashMismatch,
                $"Hash mismatch for {filePath}.");
        }
    }

    public static void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
