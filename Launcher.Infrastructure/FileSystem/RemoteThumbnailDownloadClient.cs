/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// Downloads small background thumbnails through the launcher's shared transport while keeping
/// them out of the user-visible download task list.
/// </summary>
internal sealed class RemoteThumbnailDownloadClient
{
    internal const int MaximumConcurrency = 32;

    private static readonly SemaphoreSlim Concurrency = new(MaximumConcurrency, MaximumConcurrency);
    private readonly MinecraftDownloadRequestExecutor executor;

    public RemoteThumbnailDownloadClient(
        HttpClient httpClient,
        IImportConcurrencyLimiter? limiter,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger logger)
    {
        var bandwidthLimiter = DownloadBandwidthLimiter.Create(
            downloadSpeedLimitState?.DownloadSpeedLimitMbPerSecond ?? 0,
            downloadSpeedLimitState);
        executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            bandwidthLimiter,
            limiter,
            DownloadConcurrencyCategory.Metadata);
    }

    public async Task<byte[]> DownloadAsync(
        string url,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        await Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await executor.ExecuteAsync(
                url,
                DownloadSourcePreference.Official,
                categoryHint: "ThirdParty",
                async (context, token) =>
                {
                    var contentLength = context.Response.Content.Headers.ContentLength;
                    if (contentLength is > 0 && contentLength.Value > maximumBytes)
                    {
                        throw new DownloadContentValidationException(
                            $"Remote thumbnail exceeds the maximum allowed size of {maximumBytes} bytes.");
                    }

                    await using var stream = await context.Response.Content.ReadAsStreamAsync(token)
                        .ConfigureAwait(false);
                    try
                    {
                        return await RemoteIconImageEncoder.ReadLimitedAsync(stream, maximumBytes, token)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw SafeExceptionLog.Describe(new DownloadContentValidationException("Remote thumbnail exceeds the maximum allowed size.",
                            exception), "DownloadContentValidationException", ("Reason", "Remote thumbnail exceeds the maximum allowed size."));
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Concurrency.Release();
        }
    }
}
