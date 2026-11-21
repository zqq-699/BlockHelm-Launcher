/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record DownloadTailRange(long Start, long TotalLength, string? StrongETag, string? LastModified, TimeSpan Budget);

internal sealed class SequentialDownloadTail(
    long totalLength,
    string? strongETag,
    string? lastModified,
    TimeProvider clock,
    DownloadRetryOptions options,
    Func<DownloadTailRange, CancellationToken, Task<FileStream?>?> start,
    Func<bool> shouldYield,
    Action<long> reportUsedBytes,
    CancellationToken token) : IAsyncDisposable
{
    private const long MaximumTailBytes = 1024L * 1024;
    private readonly CancellationTokenSource tailCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    private Task<FileStream?>? download;
    private long tailStart;
    private TimeSpan windowDuration;
    private long windowBytes;
    private bool previousWindowSlow;
    private bool consumed;
    private bool firstRead = true;

    public long Timestamp => clock.GetTimestamp();
    public long Start => tailStart;
    public TimeSpan GetElapsedTime(long started) => clock.GetElapsedTime(started);
    public void ReportUsed(long bytes) => reportUsedBytes(bytes);

    public void Observe(int bytes, TimeSpan readDuration, long persisted)
    {
        if (firstRead)
        {
            firstRead = false;
            return;
        }
        if (download is not null)
        {
            if (!download.IsCompleted && (persisted >= tailStart || shouldYield()))
                tailCancellation.Cancel();
            return;
        }

        if (options.SlowBodyReadThreshold is not { } window || window <= TimeSpan.Zero)
            return;
        windowDuration += readDuration;
        windowBytes += bytes;
        if (windowDuration < window)
            return;

        var rate = windowBytes / windowDuration.TotalSeconds;
        var remaining = totalLength - persisted;
        var estimatedSeconds = rate > 0 ? remaining / rate : 0;
        var slow = remaining >= MinecraftDownloadRequestExecutor.SegmentedSplitThreshold
            && rate >= options.MinimumBodyBytesPerSecond
            && estimatedSeconds > Math.Min(25, options.MaximumEstimatedRemainingTime.TotalSeconds);
        if (slow && previousWindowSlow && !shouldYield())
        {
            // The primary lane must reach tailStart after the tail finishes.
            // At equal per-connection rates that requires a tail smaller than
            // half of the remaining bytes; one third leaves scheduling slack.
            var tailLength = Math.Min(MaximumTailBytes, remaining / 3);
            tailStart = totalLength - tailLength;
            // Give the tail time to improve on the original rate while bounding the extra lease.
            var budget = TimeSpan.FromSeconds(Math.Clamp(tailLength / rate * 1.25, 10, 60));
            download = start(new DownloadTailRange(tailStart, totalLength, strongETag, lastModified, budget),
                tailCancellation.Token);
        }
        previousWindowSlow = slow;
        windowDuration = TimeSpan.Zero;
        windowBytes = 0;
    }

    public FileStream? TakeReadyTail(long persisted)
    {
        if (consumed || persisted < tailStart || persisted >= totalLength
            || download is not { IsCompletedSuccessfully: true })
            return null;

        consumed = true;
        return download.Result;
    }

    public async ValueTask DisposeAsync()
    {
        using (tailCancellation)
        {
            if (download is null || consumed)
                return;
            await tailCancellation.CancelAsync().ConfigureAwait(false);
            var result = await download.ConfigureAwait(false);
            if (result is not null)
                await result.DisposeAsync().ConfigureAwait(false);
        }
    }
}
