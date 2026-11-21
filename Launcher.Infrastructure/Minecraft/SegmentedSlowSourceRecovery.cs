/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Infrastructure.Minecraft;

internal sealed class SegmentedSlowSourceRecovery(
    TimeProvider timeProvider,
    TimeSpan window,
    double maximumRemainingSeconds,
    double lastRangeMaximumRemainingSeconds,
    long fileSize,
    Func<long> getWrittenBytes,
    Func<bool> isEnabled,
    Func<ResolvedDownloadRequest?> findBackup,
    Func<bool> tryBeginSwitch,
    Action<ResolvedDownloadRequest, int, long, double, double> reportSwitch)
{
    private readonly object syncRoot = new();
    private int activeBodies;
    private long windowStart;
    private long windowStartBytes;
    private int slowWindows;
    private int slowLastRangeWindows;
    private double estimatedRemainingSeconds;
    private ResolvedDownloadRequest? backup;
    private double switchedRangeRate;
    private bool rejected;

    public void BodyStarted()
    {
        lock (syncRoot)
        {
            if (activeBodies++ == 0)
            {
                windowStart = timeProvider.GetTimestamp();
                windowStartBytes = getWrittenBytes();
                slowWindows = 0;
                slowLastRangeWindows = 0;
            }
        }
    }

    public void BodyEnded()
    {
        lock (syncRoot)
            activeBodies--;
    }

    public ResolvedDownloadRequest? GetBackup(
        double rangeBytesPerSecond, long responseRemainingBytes, Func<long> getRangeRemainingBytes)
    {
        lock (syncRoot)
        {
            if (rejected)
                return null;

            var now = timeProvider.GetTimestamp();
            var writtenBytes = getWrittenBytes();
            if (!isEnabled())
            {
                windowStart = now;
                windowStartBytes = writtenBytes;
                slowWindows = 0;
                slowLastRangeWindows = 0;
                return null;
            }

            var remaining = fileSize - writtenBytes;
            // A single open response can coexist with queued ranges. The last
            // range accounts for every byte that is still unwritten.
            var rangeRemaining = getRangeRemainingBytes();
            var isLastRange = remaining > 0 && remaining == rangeRemaining;
            var rangeEstimate = rangeBytesPerSecond > 0
                ? Math.Min(responseRemainingBytes, rangeRemaining) / rangeBytesPerSecond
                : 0;
            if (backup is not null)
            {
                var threshold = isLastRange ? lastRangeMaximumRemainingSeconds : maximumRemainingSeconds;
                // The same backup can help another slow lane while faster responses
                // stay on the original connection. A lone tail has no competing lane.
                return rangeEstimate > threshold && (isLastRange || rangeBytesPerSecond <= switchedRangeRate)
                    ? backup
                    : null;
            }

            if (!isLastRange)
                slowLastRangeWindows = 0;

            var elapsed = timeProvider.GetElapsedTime(windowStart, now);
            if (elapsed >= window)
            {
                var bytesInWindow = writtenBytes - windowStartBytes;
                estimatedRemainingSeconds = bytesInWindow > 0
                    ? remaining / (bytesInWindow / elapsed.TotalSeconds)
                    : double.PositiveInfinity;
                windowStart = now;
                windowStartBytes = writtenBytes;
                slowWindows = remaining > 0 && estimatedRemainingSeconds > maximumRemainingSeconds
                    ? Math.Min(slowWindows + 1, 2)
                    : 0;
                slowLastRangeWindows = isLastRange && estimatedRemainingSeconds > lastRangeMaximumRemainingSeconds
                    ? Math.Min(slowLastRangeWindows + 1, 2)
                    : 0;
            }

            var recoveryThreshold = slowLastRangeWindows >= 2 ? lastRangeMaximumRemainingSeconds : maximumRemainingSeconds;
            if ((slowWindows < 2 && slowLastRangeWindows < 2)
                || remaining <= 0 || rangeEstimate <= recoveryThreshold || findBackup() is not { } candidate)
                return null;

            if (!tryBeginSwitch())
            {
                rejected = true;
                return null;
            }

            backup = candidate;
            switchedRangeRate = rangeBytesPerSecond;
            reportSwitch(candidate, activeBodies, remaining, estimatedRemainingSeconds, recoveryThreshold);
            return backup;
        }
    }

    public void RejectBackup()
    {
        lock (syncRoot)
            rejected = true;
    }
}
