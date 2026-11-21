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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 让同一设置来源或同一固定限速值的并发下载共享一个总带宽预算。
/// </summary>
internal sealed class DownloadBandwidthLimiter
{
    private static readonly ConditionalWeakTable<IDownloadSpeedLimitState, SharedBudgetState> SharedStateBudgets = new();
    private static readonly ConcurrentDictionary<int, SharedBudgetState> FixedLimitBudgets = new();

    private readonly SharedBudgetState sharedBudgetState;
    private readonly int defaultDownloadSpeedLimitMbPerSecond;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;

    public bool IsEnabled => ResolveBytesPerSecond() > 0;

    private DownloadBandwidthLimiter(
        SharedBudgetState sharedBudgetState,
        int defaultDownloadSpeedLimitMbPerSecond,
        IDownloadSpeedLimitState? downloadSpeedLimitState)
    {
        this.sharedBudgetState = sharedBudgetState;
        this.defaultDownloadSpeedLimitMbPerSecond = Math.Max(defaultDownloadSpeedLimitMbPerSecond, 0);
        this.downloadSpeedLimitState = downloadSpeedLimitState;
    }

    /// <summary>
    /// 为动态设置或固定限速取得共享预算；限速为零时不创建包装器。
    /// </summary>
    public static DownloadBandwidthLimiter? Create(
        int downloadSpeedLimitMbPerSecond,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null)
    {
        var normalizedLimit = Math.Max(downloadSpeedLimitMbPerSecond, 0);
        if (downloadSpeedLimitState is not null)
        {
            // 共享设置对象对应一份预算，运行时修改限速后所有关联下载会立即采用新值。
            var sharedBudgetState = SharedStateBudgets.GetValue(downloadSpeedLimitState, static _ => new SharedBudgetState());
            return new DownloadBandwidthLimiter(sharedBudgetState, downloadSpeedLimitMbPerSecond, downloadSpeedLimitState);
        }

        if (normalizedLimit <= 0)
            return null;

        var fixedBudgetState = FixedLimitBudgets.GetOrAdd(normalizedLimit, static _ => new SharedBudgetState());
        return new DownloadBandwidthLimiter(fixedBudgetState, normalizedLimit, downloadSpeedLimitState: null);
    }

    public async ValueTask ThrottleAsync(int bytesRead, CancellationToken cancellationToken)
    {
        if (bytesRead <= 0)
            return;

        var bytesPerSecond = ResolveBytesPerSecond();
        if (bytesPerSecond <= 0)
            return;

        await sharedBudgetState.ThrottleAsync(bytesRead, bytesPerSecond, cancellationToken).ConfigureAwait(false);
    }

    public void Throttle(int bytesRead)
    {
        if (bytesRead <= 0)
            return;

        var bytesPerSecond = ResolveBytesPerSecond();
        if (bytesPerSecond > 0)
            sharedBudgetState.Throttle(bytesRead, bytesPerSecond);
    }

    private double ResolveBytesPerSecond()
    {
        var limitMbPerSecond = downloadSpeedLimitState?.DownloadSpeedLimitMbPerSecond
            ?? defaultDownloadSpeedLimitMbPerSecond;
        return limitMbPerSecond <= 0
            ? 0
            : limitMbPerSecond * 1024d * 1024d;
    }

    private sealed class SharedBudgetState
    {
        private readonly object syncRoot = new();
        private DateTime nextAvailableAtUtc = DateTime.UtcNow;

        public async ValueTask ThrottleAsync(int bytesRead, double bytesPerSecond, CancellationToken cancellationToken)
        {
            var delay = Reserve(bytesRead, bytesPerSecond);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        public void Throttle(int bytesRead, double bytesPerSecond)
        {
            var delay = Reserve(bytesRead, bytesPerSecond);
            if (delay > TimeSpan.Zero)
                Thread.Sleep(delay);
        }

        /// <summary>
        /// 在线性时间轴上预留本批字节对应的发送时段，并返回调用方需要等待的时间。
        /// </summary>
        private TimeSpan Reserve(int bytesRead, double bytesPerSecond)
        {
            lock (syncRoot)
            {
                // 每次读取在共享时间线上预留传输时段，并发流因此共同受总速率约束。
                var now = DateTime.UtcNow;
                var startAt = now > nextAvailableAtUtc ? now : nextAvailableAtUtc;
                var delay = startAt - now;
                nextAvailableAtUtc = startAt.AddSeconds(bytesRead / bytesPerSecond);
                return delay;
            }
        }
    }
}

internal sealed record SegmentedReadProgress(
    Func<long> GetRemainingBytes,
    Func<double, long, bool> RequestSplit,
    Func<SegmentedSlowSourceRecovery?>? GetSlowSourceRecovery = null);

internal sealed class SegmentedRangeSourceSwitchException(ResolvedDownloadRequest backup) : Exception
{
    public ResolvedDownloadRequest Backup { get; } = backup;
}

internal static class DownloadResponseThrottler
{
    /// <summary>
    /// 用空闲超时和可选限速流包装响应体，并把并发租约延长到内容消费完成。
    /// </summary>
    public static async Task ApplyAsync(
        HttpResponseMessage response,
        DownloadBandwidthLimiter? bandwidthLimiter,
        CancellationToken cancellationToken,
        IImportConcurrencyLease? completionLease = null,
        TimeSpan? bodyIdleTimeout = null,
        TimeSpan? firstByteTimeout = null,
        TimeSpan? slowBodyReadThreshold = null,
        long minimumBodyBytesPerSecond = 0,
        TimeProvider? timeProvider = null,
        Action<long>? reportBodyBytes = null,
        SpeedMeter? speedMeter = null,
        long? expectedContentLength = null,
        Func<double, bool>? shouldSwitchSlowSource = null,
        SegmentedReadProgress? rangeProgress = null)
    {
        if (response.Content is null)
        {
            if (completionLease is not null)
                await completionLease.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (bandwidthLimiter is null && completionLease is null && bodyIdleTimeout is null
            && slowBodyReadThreshold is null
            && reportBodyBytes is null && speedMeter is null)
            return;

        var originalContent = response.Content;
        var originalStream = await originalContent.ReadAsStreamAsync(cancellationToken);
        var effectiveIdleTimeout = bodyIdleTimeout ?? firstByteTimeout ?? Timeout.InfiniteTimeSpan;
        Stream networkStream = bodyIdleTimeout is not null || slowBodyReadThreshold is not null
            ? new BodyProgressReadStream(
                originalStream,
                firstByteTimeout ?? effectiveIdleTimeout,
                effectiveIdleTimeout,
                slowBodyReadThreshold,
                minimumBodyBytesPerSecond,
                originalContent.Headers.ContentLength ?? expectedContentLength,
                cancellationToken,
                timeProvider ?? TimeProvider.System,
                reportBodyBytes,
                shouldSwitchSlowSource,
                rangeProgress)
            : originalStream;
        if (reportBodyBytes is not null)
        {
            networkStream = new ObservedReadStream(
                networkStream,
                reportBodyBytes);
        }
        Stream consumedStream = new ThrottledReadStream(
            networkStream,
            originalContent,
            bandwidthLimiter,
            completionLease);
        if (speedMeter is not null)
            consumedStream = new SpeedMeasuredReadStream(consumedStream, speedMeter);

        var throttledContent = new StreamContent(consumedStream);
        foreach (var header in originalContent.Headers)
            throttledContent.Headers.TryAddWithoutValidation(header.Key, header.Value);

        response.Content = throttledContent;
    }

    internal static bool IsBodyReadTooSlow(
        long bytesRead,
        TimeSpan readDuration,
        TimeSpan slowReadThreshold,
        long minimumBytesPerSecond)
    {
        if (bytesRead <= 0 || slowReadThreshold < TimeSpan.Zero || minimumBytesPerSecond <= 0
            || readDuration < slowReadThreshold || readDuration.TotalSeconds <= 0)
        {
            return false;
        }

        return bytesRead / readDuration.TotalSeconds < minimumBytesPerSecond;
    }

    /// <summary>
    /// Observes each successful raw response read exactly once, before any file
    /// writer, progress adapter, hash verifier, or throttling delay can report it.
    /// </summary>
    private sealed class ObservedReadStream : Stream
    {
        private readonly Stream innerStream;
        private readonly Action<long> reportBodyBytes;

        public ObservedReadStream(Stream innerStream, Action<long> reportBodyBytes)
        {
            this.innerStream = innerStream;
            this.reportBodyBytes = reportBodyBytes;
        }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;
        public override long Position { get => innerStream.Position; set => innerStream.Position = value; }
        public override void Flush() => innerStream.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = innerStream.Read(buffer, offset, count);
            Report(read);
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = innerStream.Read(buffer);
            Report(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(read);
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Report(read);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                innerStream.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await innerStream.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
        private void Report(int read)
        {
            if (read > 0)
                reportBodyBytes(read);
        }
    }

    /// <summary>
    /// Measures bytes delivered to the consumer over the complete read duration,
    /// including network waits and any configured bandwidth-throttling delay.
    /// </summary>
    private sealed class SpeedMeasuredReadStream(Stream innerStream, SpeedMeter speedMeter) : Stream
    {
        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;
        public override long Position { get => innerStream.Position; set => innerStream.Position = value; }
        public override void Flush() => innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var observation = speedMeter.BeginRead();
            var read = 0;
            try
            {
                read = innerStream.Read(buffer, offset, count);
                return read;
            }
            finally
            {
                speedMeter.CompleteRead(observation, read);
            }
        }

        public override int Read(Span<byte> buffer)
        {
            var observation = speedMeter.BeginRead();
            var read = 0;
            try
            {
                read = innerStream.Read(buffer);
                return read;
            }
            finally
            {
                speedMeter.CompleteRead(observation, read);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var observation = speedMeter.BeginRead();
            var read = 0;
            try
            {
                read = await innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                return read;
            }
            finally
            {
                speedMeter.CompleteRead(observation, read);
            }
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var observation = speedMeter.BeginRead();
            var read = 0;
            try
            {
                read = await innerStream.ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken).ConfigureAwait(false);
                return read;
            }
            finally
            {
                speedMeter.CompleteRead(observation, read);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            innerStream.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                innerStream.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await innerStream.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class BodyProgressReadStream : Stream
    {
        private readonly Stream innerStream;
        private readonly TimeSpan firstByteTimeout;
        private readonly TimeSpan idleTimeout;
        private readonly TimeSpan? slowReadThreshold;
        private readonly long minimumBytesPerSecond;
        private readonly long? contentLength;
        private readonly CancellationToken operationCancellationToken;
        private readonly TimeProvider timeProvider;
        private readonly Action<long>? reportDiscardedBytes;
        private readonly Func<double, bool>? shouldSwitchSlowSource;
        private readonly SegmentedReadProgress? rangeProgress;
        private bool hasReadFirstByte;
        private long totalBytesRead;
        private long windowBytes;
        private TimeSpan windowReadDuration;
        private bool previousWindowExceededEstimate;
        private DownloadBodyTooSlowException? pendingSlowFailure;
        private SegmentedSlowSourceRecovery? activeRangeRecovery;
        private double? pendingRangeSourceRate;

        public BodyProgressReadStream(
            Stream innerStream,
            TimeSpan firstByteTimeout,
            TimeSpan idleTimeout,
            TimeSpan? slowReadThreshold,
            long minimumBytesPerSecond,
            long? contentLength,
            CancellationToken operationCancellationToken,
            TimeProvider timeProvider,
            Action<long>? reportDiscardedBytes,
            Func<double, bool>? shouldSwitchSlowSource,
            SegmentedReadProgress? rangeProgress)
        {
            this.innerStream = innerStream;
            this.firstByteTimeout = firstByteTimeout;
            this.idleTimeout = idleTimeout;
            this.slowReadThreshold = slowReadThreshold;
            this.minimumBytesPerSecond = minimumBytesPerSecond;
            this.contentLength = contentLength;
            this.operationCancellationToken = operationCancellationToken;
            this.timeProvider = timeProvider;
            this.reportDiscardedBytes = reportDiscardedBytes;
            this.shouldSwitchSlowSource = shouldSwitchSlowSource;
            this.rangeProgress = rangeProgress;
        }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;
        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public override void Flush() => innerStream.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowPendingSlowFailure();
            var startedAt = timeProvider.GetTimestamp();
            var read = innerStream.Read(buffer, offset, count);
            ObserveRead(read, timeProvider.GetElapsedTime(startedAt));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowPendingSlowFailure();
            var startedAt = timeProvider.GetTimestamp();
            var read = innerStream.Read(buffer);
            ObserveRead(read, timeProvider.GetElapsedTime(startedAt));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowPendingSlowFailure();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                operationCancellationToken,
                cancellationToken);
            var timeoutWindow = hasReadFirstByte ? idleTimeout : firstByteTimeout;
            timeout.CancelAfter(timeoutWindow);

            try
            {
                var startedAt = timeProvider.GetTimestamp();
                var read = await innerStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                ObserveRead(read, timeProvider.GetElapsedTime(startedAt));
                return read;
            }
            catch (OperationCanceledException exception)
                when (!operationCancellationToken.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
            {
                throw new DownloadTimeoutException(
                    hasReadFirstByte ? DownloadFailureReason.BodyIdleTimeout : DownloadFailureReason.FirstByteTimeout,
                    $"The response body produced no data for {timeoutWindow}.",
                    exception);
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        private void ObserveRead(int read, TimeSpan readDuration)
        {
            if (read <= 0)
            {
                pendingSlowFailure = null;
                return;
            }

            if (pendingSlowFailure is { } failure)
            {
                // Unknown-length bodies need one more read to distinguish EOF
                // from a slow transfer. Account for these discarded wire bytes.
                pendingSlowFailure = null;
                reportDiscardedBytes?.Invoke(read);
                throw failure;
            }

            var firstRead = !hasReadFirstByte;
            hasReadFirstByte = true;
            totalBytesRead += read;
            // The first read includes the wait for the first byte, which is
            // governed by its own timeout and is not transfer throughput.
            if (firstRead)
            {
                activeRangeRecovery = rangeProgress?.GetSlowSourceRecovery?.Invoke();
                activeRangeRecovery?.BodyStarted();
            }
            if (firstRead || slowReadThreshold is not { } threshold)
                return;
            windowBytes += read;
            windowReadDuration += readDuration;
            if (windowReadDuration < threshold)
                return;

            var rate = windowReadDuration.TotalSeconds > 0 ? windowBytes / windowReadDuration.TotalSeconds : 0;
            // This chunk has been read but the range writer has not advanced its cursor yet.
            var remaining = GetRemainingBytes(read);
            var estimatedSeconds = remaining is { } bytes && rate > 0 ? bytes / rate : (double?)null;
            var exceedsEstimate = estimatedSeconds is { } seconds && shouldSwitchSlowSource?.Invoke(seconds) == true;
            var finishingSoon = remaining <= 0 || estimatedSeconds <= threshold.TotalSeconds;
            var requestedSplit = !finishingSoon && estimatedSeconds is { } estimate && remaining is { } rangeBytes
                && rangeProgress?.RequestSplit(estimate, rangeBytes) == true;
            if (activeRangeRecovery is not null)
                pendingRangeSourceRate = finishingSoon || requestedSplit ? 0 : rate;
            if (!requestedSplit && exceedsEstimate && previousWindowExceededEstimate)
            {
                pendingSlowFailure = new DownloadBodyTooSlowException(windowBytes, windowReadDuration, estimatedSeconds);
            }
            else if (!requestedSplit && !finishingSoon
                && IsBodyReadTooSlow(windowBytes, windowReadDuration, threshold, minimumBytesPerSecond))
            {
                // Return this chunk first so the file session persists it. The
                // next read fails, except for the unknown-length EOF check above.
                pendingSlowFailure = new DownloadBodyTooSlowException(windowBytes, windowReadDuration);
            }
            previousWindowExceededEstimate = exceedsEstimate;
            windowBytes = 0;
            windowReadDuration = TimeSpan.Zero;
        }

        private void ThrowPendingSlowFailure()
        {
            if (pendingRangeSourceRate is { } rangeRate)
            {
                pendingRangeSourceRate = null;
                // The preceding read has now been persisted and the next request can
                // use the updated cursor without interrupting a write.
                if (activeRangeRecovery?.GetBackup(rangeRate, GetRemainingBytes() ?? 0,
                        rangeProgress!.GetRemainingBytes) is { } backup)
                    throw new SegmentedRangeSourceSwitchException(backup);
            }
            if ((contentLength is null && rangeProgress is null) || pendingSlowFailure is not { } failure)
                return;
            pendingSlowFailure = null;
            // The allocator may have split this range while the latest chunk was being written.
            var remaining = GetRemainingBytes();
            var seconds = remaining is { } bytes && failure.BytesPerSecond > 0
                ? bytes / failure.BytesPerSecond
                : failure.EstimatedRemainingSeconds;
            if (rangeProgress is not null && seconds <= slowReadThreshold?.TotalSeconds
                || failure.EstimatedRemainingSeconds is not null
                    && (seconds is not { } estimate || shouldSwitchSlowSource?.Invoke(estimate) != true))
            {
                previousWindowExceededEstimate = false;
                return;
            }
            throw failure;
        }

        private long? GetRemainingBytes(int bytesNotWritten = 0)
        {
            var responseRemaining = contentLength - totalBytesRead;
            if (rangeProgress is null)
                return responseRemaining;
            var rangeRemaining = Math.Max(0, rangeProgress.GetRemainingBytes() - bytesNotWritten);
            return Math.Min(responseRemaining ?? rangeRemaining, rangeRemaining);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                activeRangeRecovery?.BodyEnded();
                activeRangeRecovery = null;
                innerStream.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await innerStream.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ThrottledReadStream : Stream
    {
        private readonly Stream innerStream;
        private readonly HttpContent ownedContent;
        private readonly DownloadBandwidthLimiter? bandwidthLimiter;
        private readonly IImportConcurrencyLease? completionLease;

        public ThrottledReadStream(
            Stream innerStream,
            HttpContent ownedContent,
            DownloadBandwidthLimiter? bandwidthLimiter,
            IImportConcurrencyLease? completionLease)
        {
            this.innerStream = innerStream;
            this.ownedContent = ownedContent;
            this.bandwidthLimiter = bandwidthLimiter;
            this.completionLease = completionLease;
        }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => innerStream.CanWrite;
        public override long Length => innerStream.Length;
        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public override void Flush()
        {
            innerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = innerStream.Read(buffer, offset, count);
            if (read > 0 && bandwidthLimiter is not null)
                bandwidthLimiter.Throttle(read);

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = innerStream.Read(buffer);
            if (read > 0 && bandwidthLimiter is not null)
                bandwidthLimiter.Throttle(read);

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await innerStream.ReadAsync(buffer, cancellationToken);
            if (read > 0 && bandwidthLimiter is not null)
                await bandwidthLimiter.ThrottleAsync(read, cancellationToken);

            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (read > 0 && bandwidthLimiter is not null)
                await bandwidthLimiter.ThrottleAsync(read, cancellationToken);

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return innerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            innerStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            innerStream.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            innerStream.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return innerStream.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                innerStream.Dispose();
                ownedContent.Dispose();
                completionLease?.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await innerStream.DisposeAsync();
            ownedContent.Dispose();
            if (completionLease is not null)
                await completionLease.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync();
        }
    }
}
