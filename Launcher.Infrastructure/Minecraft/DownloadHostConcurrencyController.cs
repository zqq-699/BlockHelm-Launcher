/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Net;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Keeps the launcher-wide hard budget separate from per-origin congestion
/// feedback so a degraded provider cannot slow unrelated download hosts.
/// </summary>
internal sealed class DownloadHostConcurrencyController
{
    internal const int MinimumHostConcurrency = 4;
    internal const int InitialHostConcurrency = 64;
    internal const int MaximumHostConcurrency = 64;
    internal static readonly TimeSpan MaximumCooldownWait = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan DefaultAdjustmentWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMaximumJitter = TimeSpan.FromSeconds(2);

    public static DownloadHostConcurrencyController Shared { get; } = new();

    private readonly ConcurrentDictionary<string, AdaptiveHostScheduler> hosts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan adjustmentWindow;
    private readonly TimeSpan idleLifetime;
    private readonly TimeSpan maximumJitter;
    private readonly Func<double> nextJitter;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delayAsync;

    public DownloadHostConcurrencyController(
        TimeProvider? timeProvider = null,
        TimeSpan? adjustmentWindow = null,
        TimeSpan? idleLifetime = null,
        TimeSpan? maximumJitter = null,
        Func<double>? nextJitter = null,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.adjustmentWindow = adjustmentWindow ?? DefaultAdjustmentWindow;
        this.idleLifetime = idleLifetime ?? DefaultIdleLifetime;
        this.maximumJitter = maximumJitter ?? DefaultMaximumJitter;
        this.nextJitter = nextJitter ?? Random.Shared.NextDouble;
        this.delayAsync = delayAsync ?? ((delay, cancellationToken) =>
            new ValueTask(Task.Delay(delay, cancellationToken)));
    }

    public async ValueTask<DownloadAdmissionLease> AcquireAsync(
        Uri uri,
        Func<CancellationToken, ValueTask<IImportConcurrencyLease>> acquireGlobalAsync,
        bool applyColdStartJitter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(acquireGlobalAsync);

        var now = timeProvider.GetUtcNow();
        PruneExpired(now);
        var origin = NormalizeOrigin(uri);
        var cooldownWaited = TimeSpan.Zero;

        while (true)
        {
            cooldownWaited += await WaitForCooldownAsync(origin, cooldownWaited, cancellationToken).ConfigureAwait(false);
            var scheduler = hosts.GetOrAdd(
                origin,
                _ => new AdaptiveHostScheduler(
                    origin,
                    MinimumHostConcurrency,
                    InitialHostConcurrency,
                    MaximumHostConcurrency,
                    timeProvider,
                    adjustmentWindow));
            if (!scheduler.TryAcquireAsync(cancellationToken, out var pendingLease))
            {
                hosts.TryRemove(origin, out _);
                continue;
            }
            var hostLease = await pendingLease.ConfigureAwait(false);
            try
            {
                if (GetRemainingCooldown(origin) > TimeSpan.Zero)
                {
                    hostLease.Dispose();
                    continue;
                }
                if (applyColdStartJitter && !hostLease.SkipJitter && maximumJitter > TimeSpan.Zero)
                {
                    var factor = Math.Clamp(nextJitter(), 0, 1);
                    var delay = TimeSpan.FromTicks((long)(maximumJitter.Ticks * factor));
                    if (delay > TimeSpan.Zero)
                        await delayAsync(delay, cancellationToken).ConfigureAwait(false);
                }

                var globalLease = await acquireGlobalAsync(cancellationToken).ConfigureAwait(false);
                // A request queued for a slot may have been throttled in the meantime.
                if (GetRemainingCooldown(origin) > TimeSpan.Zero)
                {
                    globalLease.Dispose();
                    hostLease.Dispose();
                    continue;
                }
                return new DownloadAdmissionLease(this, origin, hostLease, globalLease);
            }
            catch
            {
                await hostLease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public void DeferRequests(string origin, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        var now = timeProvider.GetUtcNow();
        var until = delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
        cooldowns.AddOrUpdate(origin, until, (_, current) => current > until ? current : until);
    }

    public TimeSpan GetRemainingCooldown(Uri uri) => GetRemainingCooldown(NormalizeOrigin(uri));

    public TimeSpan GetRemainingCooldown(string origin)
    {
        var now = timeProvider.GetUtcNow();
        return cooldowns.TryGetValue(origin, out var until) && until > now ? until - now : TimeSpan.Zero;
    }

    private async ValueTask<TimeSpan> WaitForCooldownAsync(string origin, TimeSpan alreadyWaited, CancellationToken token)
    {
        var started = timeProvider.GetTimestamp();
        while (GetRemainingCooldown(origin) is var remaining && remaining > TimeSpan.Zero)
        {
            token.ThrowIfCancellationRequested();
            var waited = alreadyWaited + timeProvider.GetElapsedTime(started);
            // Keep the server's cooldown intact, but do not keep a foreground
            // request waiting for hours or repeatedly extend its wait budget.
            if (remaining > MaximumCooldownWait - waited)
                throw new DownloadCooldownWaitException(remaining, MaximumCooldownWait, waited)
                    .WithFinalOrigin(origin).WithFinalHost(new Uri(origin).Host);
            await delayAsync(remaining, token).ConfigureAwait(false);
        }
        return timeProvider.GetElapsedTime(started);
    }

    public DownloadHostAdjustment? RecordResult(
        string origin,
        DownloadFailureReason? failureReason,
        HttpStatusCode? statusCode = null)
    {
        var result = ClassifyResult(failureReason, statusCode);
        var scheduler = hosts.GetOrAdd(
            origin,
            _ => new AdaptiveHostScheduler(
                origin,
                MinimumHostConcurrency,
                InitialHostConcurrency,
                MaximumHostConcurrency,
                timeProvider,
                adjustmentWindow));
        return scheduler.RecordResult(result);
    }

    public DownloadHostAdjustment? RecordResult(
        Uri uri,
        DownloadFailureReason? failureReason,
        HttpStatusCode? statusCode = null) =>
        RecordResult(NormalizeOrigin(uri), failureReason, statusCode);

    public DownloadAdmissionLease? TryAcquireAvailable(
        Uri uri,
        Func<IImportConcurrencyLease?> tryAcquireGlobal)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(tryAcquireGlobal);

        var now = timeProvider.GetUtcNow();
        PruneExpired(now);
        var origin = NormalizeOrigin(uri);
        if (GetRemainingCooldown(origin) > TimeSpan.Zero) return null;
        var scheduler = hosts.GetOrAdd(
            origin,
            _ => new AdaptiveHostScheduler(
                origin,
                MinimumHostConcurrency,
                InitialHostConcurrency,
                MaximumHostConcurrency,
                timeProvider,
                adjustmentWindow));
        if (!scheduler.TryAcquireAvailable(out var hostLease))
            return null;

        try
        {
            var globalLease = tryAcquireGlobal();
            if (globalLease is null)
            {
                hostLease.Dispose();
                return null;
            }
            if (GetRemainingCooldown(origin) > TimeSpan.Zero)
            {
                globalLease.Dispose();
                hostLease.Dispose();
                return null;
            }
            return new DownloadAdmissionLease(this, origin, hostLease, globalLease);
        }
        catch
        {
            hostLease.Dispose();
            throw;
        }
    }

    public DownloadHostConcurrencySnapshot GetSnapshot(string origin)
    {
        return hosts.TryGetValue(origin, out var scheduler)
            ? scheduler.Snapshot
            : new DownloadHostConcurrencySnapshot(
                origin,
                ActiveCount: 0,
                WaitingCount: 0,
                CurrentTarget: InitialHostConcurrency,
                ConfiguredMaximum: MaximumHostConcurrency);
    }

    internal static string NormalizeOrigin(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort
            ? string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? 443 : 80
            : uri.Port;
        return $"{scheme}://{host}:{port}";
    }

    internal static DownloadHostResultKind ClassifyResult(
        DownloadFailureReason? failureReason,
        HttpStatusCode? statusCode)
    {
        if (failureReason is null)
            return DownloadHostResultKind.Success;
        if (failureReason is DownloadFailureReason.HttpStatus)
        {
            if (statusCode is HttpStatusCode.TooManyRequests)
                return DownloadHostResultKind.RateLimited;
            if (statusCode is HttpStatusCode.RequestTimeout
                || statusCode is { } code && (int)code is >= 500 and <= 599)
            {
                return DownloadHostResultKind.CongestionFailure;
            }
            return DownloadHostResultKind.Neutral;
        }
        return failureReason is DownloadFailureReason.Network or DownloadFailureReason.Dns
            or DownloadFailureReason.ResponseHeadersTimeout or DownloadFailureReason.FirstByteTimeout
            or DownloadFailureReason.BodyIdleTimeout or DownloadFailureReason.BodyTooSlow
            or DownloadFailureReason.BodyInterrupted
            ? DownloadHostResultKind.CongestionFailure
            : DownloadHostResultKind.Neutral;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in cooldowns)
            if (entry.Value <= now)
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)cooldowns).Remove(entry);
        foreach (var (origin, scheduler) in hosts)
        {
            if (scheduler.TryRetire(now, idleLifetime))
                hosts.TryRemove(origin, out _);
        }
    }

    internal sealed class DownloadAdmissionLease : IDisposable, IAsyncDisposable
    {
        private readonly DownloadHostConcurrencyController owner;
        private AdaptiveHostScheduler.HostLease? hostLease;
        private IImportConcurrencyLease? globalLease;

        public DownloadAdmissionLease(
            DownloadHostConcurrencyController owner,
            string origin,
            AdaptiveHostScheduler.HostLease hostLease,
            IImportConcurrencyLease globalLease)
        {
            this.owner = owner;
            Origin = origin;
            this.hostLease = hostLease;
            this.globalLease = globalLease;
        }

        public string Origin { get; }
        public DownloadHostConcurrencySnapshot Snapshot => owner.GetSnapshot(Origin);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref globalLease, null)?.Dispose();
            Interlocked.Exchange(ref hostLease, null)?.Dispose();
        }
    }
}

internal sealed class AdaptiveHostScheduler
{
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly string origin;
    private readonly int minimum;
    private readonly int maximum;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan adjustmentWindow;
    private int activeCount;
    private int waitingCount;
    private int currentTarget;
    private int successes;
    private int failures;
    private DateTimeOffset windowStartedAt;
    private DateTimeOffset lastReductionAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastUsedAt;
    // Stagger only the initial burst. Reapplying jitter to steady-state work
    // turns every successful high-volume batch into an artificial bottleneck.
    private bool coldStartCompleted;
    private bool retired;

    public AdaptiveHostScheduler(
        string origin,
        int minimum,
        int initial,
        int maximum,
        TimeProvider timeProvider,
        TimeSpan adjustmentWindow)
    {
        this.origin = origin;
        this.minimum = minimum;
        this.maximum = maximum;
        this.timeProvider = timeProvider;
        this.adjustmentWindow = adjustmentWindow;
        currentTarget = initial;
        windowStartedAt = timeProvider.GetUtcNow();
        lastUsedAt = windowStartedAt;
    }

    public DownloadHostConcurrencySnapshot Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return new DownloadHostConcurrencySnapshot(
                    origin,
                    activeCount,
                    waitingCount,
                    currentTarget,
                    maximum);
            }
        }
    }

    public bool TryAcquireAsync(CancellationToken cancellationToken, out ValueTask<HostLease> lease)
    {
        bool skipJitter;
        lock (syncRoot)
        {
            if (retired)
            {
                lease = default;
                return false;
            }
            skipJitter = coldStartCompleted || activeCount == 0 && waitingCount == 0;
            waitingCount++;
            lastUsedAt = timeProvider.GetUtcNow();
        }
        lease = AcquireRegisteredAsync(skipJitter, cancellationToken);
        return true;
    }

    public bool TryAcquireAvailable(out HostLease lease)
    {
        lock (syncRoot)
        {
            if (retired || waitingCount != 0 || activeCount >= currentTarget)
            {
                lease = null!;
                return false;
            }

            activeCount++;
            lastUsedAt = timeProvider.GetUtcNow();
            lease = new HostLease(this, skipJitter: true);
            return true;
        }
    }

    public DownloadHostAdjustment? RecordResult(DownloadHostResultKind result)
    {
        lock (syncRoot)
        {
            var now = timeProvider.GetUtcNow();
            lastUsedAt = now;
            coldStartCompleted = true;
            if (result is DownloadHostResultKind.Neutral)
                return null;
            if (result is DownloadHostResultKind.Success)
                successes++;
            else
                failures++;

            if (result is DownloadHostResultKind.RateLimited
                && now - lastReductionAt >= adjustmentWindow)
            {
                var reducedTarget = Math.Max(minimum, (currentTarget + 1) / 2);
                if (reducedTarget < currentTarget)
                {
                    return AdjustTarget(
                        reducedTarget,
                        DownloadHostAdjustmentReason.RateLimited,
                        now);
                }
            }

            if (now - windowStartedAt < adjustmentWindow)
                return null;

            var previousSuccesses = successes;
            var previousFailures = failures;
            var total = previousSuccesses + previousFailures;
            var target = currentTarget;
            var reason = DownloadHostAdjustmentReason.None;
            if (previousFailures >= 3 && previousFailures * 4 >= total)
            {
                target = Math.Max(minimum, (currentTarget + 1) / 2);
                reason = DownloadHostAdjustmentReason.CongestionThreshold;
            }
            else if (previousFailures == 0 && previousSuccesses >= currentTarget && waitingCount > 0)
            {
                target = Math.Min(maximum, currentTarget * 2);
                reason = DownloadHostAdjustmentReason.HealthyRecovery;
            }

            successes = 0;
            failures = 0;
            windowStartedAt = now;
            if (target == currentTarget)
                return null;
            return ApplyTarget(target, reason, previousSuccesses, previousFailures, now);
        }
    }

    public bool TryRetire(DateTimeOffset now, TimeSpan idleLifetime)
    {
        lock (syncRoot)
        {
            if (retired || activeCount != 0 || waitingCount != 0 || now - lastUsedAt < idleLifetime)
                return false;
            retired = true;
            return true;
        }
    }

    private async ValueTask<HostLease> AcquireRegisteredAsync(
        bool skipJitter,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                lock (syncRoot)
                {
                    if (activeCount < currentTarget)
                    {
                        activeCount++;
                        lastUsedAt = timeProvider.GetUtcNow();
                        return new HostLease(this, skipJitter);
                    }
                }
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (syncRoot)
            {
                waitingCount--;
                lastUsedAt = timeProvider.GetUtcNow();
            }
        }
    }

    private DownloadHostAdjustment AdjustTarget(
        int target,
        DownloadHostAdjustmentReason reason,
        DateTimeOffset now)
    {
        var previousSuccesses = successes;
        var previousFailures = failures;
        successes = 0;
        failures = 0;
        windowStartedAt = now;
        return ApplyTarget(target, reason, previousSuccesses, previousFailures, now);
    }

    private DownloadHostAdjustment ApplyTarget(
        int target,
        DownloadHostAdjustmentReason reason,
        int previousSuccesses,
        int previousFailures,
        DateTimeOffset now)
    {
        var previousTarget = currentTarget;
        currentTarget = target;
        if (target < previousTarget)
            lastReductionAt = now;
        if (target > previousTarget)
            SignalWaiters();
        return new DownloadHostAdjustment(
            origin,
            previousTarget,
            target,
            reason,
            previousSuccesses,
            previousFailures);
    }

    private void Release()
    {
        lock (syncRoot)
        {
            activeCount--;
            lastUsedAt = timeProvider.GetUtcNow();
            SignalWaiters();
        }
    }

    private void SignalWaiters()
    {
        var available = Math.Max(0, currentTarget - activeCount);
        while (available-- > 0 && signal.CurrentCount < waitingCount)
            signal.Release();
    }

    internal sealed class HostLease(AdaptiveHostScheduler owner, bool skipJitter) : IDisposable, IAsyncDisposable
    {
        private AdaptiveHostScheduler? owner = owner;
        public bool SkipJitter { get; } = skipJitter;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}

internal readonly record struct DownloadHostConcurrencySnapshot(
    string Origin,
    int ActiveCount,
    int WaitingCount,
    int CurrentTarget,
    int ConfiguredMaximum);

internal sealed record DownloadHostAdjustment(
    string Origin,
    int PreviousTarget,
    int CurrentTarget,
    DownloadHostAdjustmentReason Reason,
    int Successes,
    int Failures);

internal enum DownloadHostResultKind
{
    Neutral,
    Success,
    CongestionFailure,
    RateLimited
}

internal enum DownloadHostAdjustmentReason
{
    None,
    RateLimited,
    CongestionThreshold,
    HealthyRecovery
}
