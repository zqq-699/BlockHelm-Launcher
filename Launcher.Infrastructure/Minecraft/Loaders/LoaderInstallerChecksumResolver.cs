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
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal static class LoaderInstallerChecksumResolver
{
    internal const int MaximumChecksumMetadataBytes = 4 * 1024;

    public static async Task<string?> ResolveOptionalSha1Async(
        HttpClient httpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger logger,
        string installerUrl,
        DownloadSourcePreference downloadSourcePreference,
        string categoryHint,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryHint);

        var checksumUrl = installerUrl + ".sha1";
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Metadata,
            retryOptions: new DownloadRetryOptions
            {
                MaxAttemptsPerSource = 1,
                ResponseHeadersTimeout = TimeSpan.FromSeconds(3),
                FirstByteTimeout = TimeSpan.FromSeconds(3),
                BodyIdleTimeout = TimeSpan.FromSeconds(3)
            });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await executor.ExecuteAsync(
                checksumUrl,
                downloadSourcePreference,
                categoryHint,
                async (context, token) =>
                {
                    if (context.Response.Content.Headers.ContentLength is > MaximumChecksumMetadataBytes)
                    {
                        throw new DownloadContentValidationException(
                            "Loader installer SHA1 metadata exceeded the permitted size.");
                    }

                    var text = await ReadBoundedTextAsync(context.Response.Content, token).ConfigureAwait(false);
                    return NormalizeSha1(text)
                        ?? throw new DownloadContentValidationException(
                            "Loader installer SHA1 metadata has an invalid format.");
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or MinecraftDownloadRequestExecutor.DownloadSourceRequestException)
        {
            logger.LogWarning("Loader installer SHA1 metadata unavailable; using structural validation. Loader={Loader} FailureType={FailureType}",
                categoryHint, exception.GetType().Name);
            return null;
        }
    }

    internal static string? NormalizeSha1(string? value)
    {
        var candidate = value?
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return candidate is { Length: 40 } && candidate.All(Uri.IsHexDigit)
            ? candidate
            : null;
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (memory.Length + read > MaximumChecksumMetadataBytes)
            {
                throw new DownloadContentValidationException(
                    "Loader installer SHA1 metadata exceeded the permitted size.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.ASCII.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }
}
