/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed partial class MinecraftDownloadRequestExecutor
{
    private Task WriteSequentialFileAsync(
        DownloadAttemptContext context,
        ResumableDownloadFileSession session,
        DownloadIntegrityExpectation integrity,
        FileDownloadRecovery recovery,
        bool rangeNotYetTried,
        Action<int, long, long?>? reportAttemptProgress,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        Action<long>? reportTransferredBytes,
        CancellationToken cancellationToken)
    {
        var uri = context.Transport.FinalUri;
        Func<long, string?, string?, SequentialDownloadTail>? prepareTail =
            rangeNotYetTried && integrity.HasStrongHash && bandwidthLimiter?.IsEnabled != true
                ? (length, etag, lastModified) => new SequentialDownloadTail(
                    length, etag, lastModified, timeProvider, retryOptions, StartTail, ShouldYield,
                    bytes => logger.LogDebug("Slow sequential download reused its completed tail. Host={Host} TailBytesUsed={TailBytesUsed}", uri.Host, bytes),
                    cancellationToken)
                : null;
        return session.WriteAsync(context.Response, context.Resolution, context.AttemptNumber, reportAttemptProgress,
            cancellationToken, reportTransferredBytes, prepareTail);

        bool ShouldYield() => bandwidthLimiter?.IsEnabled == true
            || GetGlobalConcurrencySnapshot().WaitingCount > 0
            || context.HasReadyAlternative?.Invoke() == true
            || hostConcurrencyController.GetRemainingCooldown(uri) > TimeSpan.Zero;

        Task<FileStream?>? StartTail(DownloadTailRange range, CancellationToken token)
        {
            Task? ordinaryDownloadQueued = null;
            var admission = hostConcurrencyController.TryAcquireAvailable(uri,
                () => limiter.TryAcquirePreemptibleDownloadSlot(out var lease, out ordinaryDownloadQueued)
                    ? lease
                    : null);
            if (admission is null)
                return null;
            if (ordinaryDownloadQueued is null || ShouldYield() || !recovery.TryStartTail())
            {
                admission.Dispose();
                return null;
            }

            return DownloadTailAsync(uri, range, admission, ordinaryDownloadQueued, session, ShouldYield, sensitiveHeaders,
                speedMeter, reportTransferredBytes, token);
        }
    }

    private async Task<FileStream?> DownloadTailAsync(
        Uri uri,
        DownloadTailRange range,
        DownloadHostConcurrencyController.DownloadAdmissionLease admission,
        Task ordinaryDownloadQueued,
        ResumableDownloadFileSession session,
        Func<bool> shouldYield,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        Action<long>? reportTransferredBytes,
        CancellationToken cancellationToken)
    {
        using var reserved = admission;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(range.Budget);
        var token = deadline.Token;
        var yieldOnQueue = YieldTailOnQueueAsync(ordinaryDownloadQueued, deadline);
        FileStream? suffix = null;
        try
        {
            await bmclApiRequestRateLimiter.WaitAsync(uri, token).ConfigureAwait(false);
            if (shouldYield())
                return null;
            logger.LogDebug("Trying a tail range for a slow sequential download. Host={Host} Start={Start} TotalBytes={TotalBytes}",
                uri.Host, range.Start, range.TotalLength);
            await using var result = await transport.SendAsync(uri.AbsoluteUri, token, request =>
            {
                request.Headers.Range = new RangeHeaderValue(range.Start, range.TotalLength - 1);
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                if ((range.StrongETag ?? range.LastModified) is { } validator)
                    request.Headers.TryAddWithoutValidation("If-Range", validator);
            }, sensitiveHeaders, preacquiredAdmission: admission, shouldAvoidHost: target => target != uri).ConfigureAwait(false);

            using var response = result.Response;
            var expected = new DownloadSegmentRange(range.Start, range.TotalLength - 1, range.TotalLength, 0);
            var actual = ValidateSegmentResponse(response, expected, range.StrongETag);
            if (actual.End != expected.End || range.StrongETag is null && range.LastModified is { } modified
                && !string.Equals(modified, response.Content.Headers.LastModified?.ToString("R"), StringComparison.Ordinal))
                return null;

            await DownloadResponseThrottler.ApplyAsync(response, bandwidthLimiter, token,
                bodyIdleTimeout: retryOptions.BodyIdleTimeout, firstByteTimeout: retryOptions.FirstByteTimeout,
                speedMeter: speedMeter, reportBodyBytes: reportTransferredBytes).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            suffix = session.OpenTailFile();
            var remaining = range.TotalLength - range.Start;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (remaining > 0 && !shouldYield())
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                        .ConfigureAwait(false);
                    if (read == 0)
                        return null;
                    await suffix.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            if (remaining != 0)
                return null;

            var completed = suffix;
            suffix = null;
            logger.LogDebug("Slow sequential download tail range is ready. Host={Host} Start={Start}", uri.Host, range.Start);
            return completed;
        }
        catch (Exception exception) when (exception is DownloadAttemptException or OperationCanceledException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            logger.LogDebug("Optional tail range ended without a usable result. Host={Host} Reason={Reason}",
                uri.Host, exception is DownloadAttemptException failure ? failure.Reason.ToString() : exception.GetType().Name);
            return null;
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            await yieldOnQueue.ConfigureAwait(false);
            if (suffix is not null)
                await suffix.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task YieldTailOnQueueAsync(Task ordinaryDownloadQueued, CancellationTokenSource deadline)
    {
        try
        {
            await ordinaryDownloadQueued.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return;
        }

        await deadline.CancelAsync().ConfigureAwait(false);
    }
}
