/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class AdaptiveSegmentDownloadSession
{
    internal const double TailSplitFraction = 0.4d;

    private readonly object syncRoot = new();
    private readonly List<AdaptiveDownloadSegment> queued = [];
    private readonly HashSet<AdaptiveDownloadSegment> active = [];
    private readonly long splitThreshold;
    private readonly long totalLength;
    private int nextChunkId = 1;
    private int completedChunks;

    public AdaptiveSegmentDownloadSession(long start, long totalLength, long splitThreshold)
    {
        if (start < 0 || totalLength <= start)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (splitThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(splitThreshold));

        this.totalLength = totalLength;
        this.splitThreshold = splitThreshold;
        queued.Add(new AdaptiveDownloadSegment(nextChunkId++, start, totalLength - 1, totalLength));
    }

    public int QueuedCount
    {
        get
        {
            lock (syncRoot)
                return queued.Count;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (syncRoot)
                return active.Count;
        }
    }

    public int CompletedChunks => Volatile.Read(ref completedChunks);

    public int TotalChunkCount
    {
        get
        {
            lock (syncRoot)
                return nextChunkId - 1;
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (syncRoot)
                return queued.Count == 0 && active.Count == 0;
        }
    }

    public bool TryTake(out AdaptiveDownloadSegment? segment)
    {
        lock (syncRoot)
        {
            if (queued.Count == 0)
            {
                segment = null;
                return false;
            }

            segment = TakeLargestQueuedNoLock();
            return true;
        }
    }

    public bool TryTakeQueuedOrSplit(
        out AdaptiveDownloadSegment? segment,
        out AdaptiveSegmentSplit? split)
    {
        lock (syncRoot)
        {
            split = null;
            if (queued.Count == 0)
            {
                if (!TrySplitLargestNoLock(out var createdSplit))
                {
                    segment = null;
                    return false;
                }
                split = createdSplit;
            }

            segment = TakeLargestQueuedNoLock();
            return true;
        }
    }

    public void Return(AdaptiveDownloadSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (syncRoot)
        {
            if (!active.Remove(segment))
                return;
            if (!segment.IsComplete)
                queued.Add(segment);
        }
    }

    public void Complete(AdaptiveDownloadSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (syncRoot)
        {
            if (!active.Remove(segment))
                return;
            if (!segment.IsComplete)
                throw SafeExceptionLog.Describe(new InvalidOperationException("An incomplete segmented range cannot be completed."), "DownloadValidation", ("Reason", "An incomplete segmented range cannot be completed."));
            completedChunks++;
        }
    }

    public bool TrySplitLargest(out AdaptiveSegmentSplit split)
    {
        lock (syncRoot)
            return TrySplitLargestNoLock(out split);
    }

    private AdaptiveDownloadSegment TakeLargestQueuedNoLock()
    {
        var index = FindLargestRemainingIndex(queued);
        var segment = queued[index];
        queued.RemoveAt(index);
        active.Add(segment);
        return segment;
    }

    private bool TrySplitLargestNoLock(out AdaptiveSegmentSplit split)
    {
        AdaptiveDownloadSegment? largest = null;
        var largestRemaining = 0L;
        var sourceWasActive = false;

        foreach (var candidate in queued)
        {
            var remaining = candidate.RemainingLength;
            if (remaining <= largestRemaining)
                continue;
            largest = candidate;
            largestRemaining = remaining;
            sourceWasActive = false;
        }

        foreach (var candidate in active)
        {
            var remaining = candidate.RemainingLength;
            if (remaining <= largestRemaining)
                continue;
            largest = candidate;
            largestRemaining = remaining;
            sourceWasActive = true;
        }

        if (largest is null
            || !largest.TrySplitTail(
                splitThreshold,
                TailSplitFraction,
                nextChunkId,
                out var suffix,
                out var originalStart,
                out var originalEnd,
                out var retainedEnd))
        {
            split = default;
            return false;
        }

        nextChunkId++;
        queued.Add(suffix);
        split = new AdaptiveSegmentSplit(
            largest.ChunkId,
            originalStart,
            originalEnd,
            retainedEnd,
            suffix.ChunkId,
            suffix.NextOffset,
            suffix.LogicalEnd,
            sourceWasActive);
        return true;
    }

    private static int FindLargestRemainingIndex(IReadOnlyList<AdaptiveDownloadSegment> segments)
    {
        var selected = 0;
        var largest = segments[0].RemainingLength;
        for (var index = 1; index < segments.Count; index++)
        {
            var remaining = segments[index].RemainingLength;
            if (remaining <= largest)
                continue;
            selected = index;
            largest = remaining;
        }
        return selected;
    }
}

internal sealed class AdaptiveDownloadSegment
{
    private readonly object syncRoot = new();
    private long logicalEnd;
    private long nextOffset;
    private bool shortened;

    public AdaptiveDownloadSegment(int chunkId, long start, long end, long totalLength)
    {
        if (start < 0 || end < start || totalLength <= end)
            throw new ArgumentOutOfRangeException(nameof(start));
        ChunkId = chunkId;
        nextOffset = start;
        logicalEnd = end;
        TotalLength = totalLength;
    }

    public int ChunkId { get; }
    public long TotalLength { get; }

    public long NextOffset
    {
        get
        {
            lock (syncRoot)
                return nextOffset;
        }
    }

    public long LogicalEnd
    {
        get
        {
            lock (syncRoot)
                return logicalEnd;
        }
    }

    public long RemainingLength
    {
        get
        {
            lock (syncRoot)
                return Math.Max(0, logicalEnd - nextOffset + 1);
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (syncRoot)
                return nextOffset > logicalEnd;
        }
    }

    public bool TryGetAttemptRange(out AdaptiveSegmentAttemptRange range)
    {
        lock (syncRoot)
        {
            if (nextOffset > logicalEnd)
            {
                range = default;
                return false;
            }
            range = new AdaptiveSegmentAttemptRange(nextOffset, logicalEnd, TotalLength, ChunkId);
            return true;
        }
    }

    public int ReserveWrite(long offset, int availableBytes)
    {
        if (availableBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(availableBytes));

        lock (syncRoot)
        {
            if (offset != nextOffset)
                throw SafeExceptionLog.Describe(new InvalidOperationException("The segmented write cursor did not match the adaptive range cursor."), "DownloadValidation", ("Reason", "The segmented write cursor did not match the adaptive range cursor."));
            if (offset > logicalEnd || availableBytes == 0)
                return 0;

            var accepted = checked((int)Math.Min(availableBytes, logicalEnd - offset + 1));
            nextOffset += accepted;
            return accepted;
        }
    }

    public bool WasShortenedFrom(long requestedEnd)
    {
        lock (syncRoot)
            return shortened && logicalEnd < requestedEnd;
    }

    public bool TrySplitTail(
        long splitThreshold,
        double tailFraction,
        int suffixChunkId,
        out AdaptiveDownloadSegment suffix,
        out long originalStart,
        out long originalEnd,
        out long retainedEnd)
    {
        lock (syncRoot)
        {
            originalStart = nextOffset;
            originalEnd = logicalEnd;
            retainedEnd = logicalEnd;
            var remaining = logicalEnd - nextOffset + 1;
            if (remaining < splitThreshold || remaining < 2)
            {
                suffix = null!;
                return false;
            }

            var tailLength = checked((long)Math.Floor(remaining * tailFraction));
            tailLength = Math.Clamp(tailLength, 1, remaining - 1);
            var splitStart = logicalEnd - tailLength + 1;
            retainedEnd = splitStart - 1;
            logicalEnd = retainedEnd;
            shortened = true;
            suffix = new AdaptiveDownloadSegment(
                suffixChunkId,
                splitStart,
                originalEnd,
                TotalLength);
            return true;
        }
    }
}

internal readonly record struct AdaptiveSegmentAttemptRange(
    long Start,
    long End,
    long TotalLength,
    int ChunkId);

internal readonly record struct AdaptiveSegmentSplit(
    int SourceChunkId,
    long SourceStart,
    long SourceOriginalEnd,
    long SourceRetainedEnd,
    int NewChunkId,
    long NewStart,
    long NewEnd,
    bool SourceWasActive);
