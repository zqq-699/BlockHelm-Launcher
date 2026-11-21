/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>Temporary source ordering hint; it must never exclude a download source.</summary>
internal sealed class DownloadHostHealthTracker
{
    public static DownloadHostHealthTracker Shared { get; } = new();

    private static readonly TimeSpan RedispatchCooldown = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<(string Source, string Host), HostState> states = new();
    private readonly ConcurrentDictionary<string, HostState> invalidContentNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostState> networkNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string EntryOrigin, string NodeHost), RedispatchState> redispatches = new();
    private readonly TimeSpan cooldown = TimeSpan.FromSeconds(45);
    private readonly TimeProvider clock;

    internal DownloadHostHealthTracker(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
    }

    internal void Clear()
    {
        states.Clear();
        invalidContentNodes.Clear();
        networkNodes.Clear();
        redispatches.Clear();
    }

    public bool ShouldAvoidInvalidContentNode(string host) =>
        invalidContentNodes.TryGetValue(host, out var state) && state.AvoidUntil > clock.GetUtcNow();

    public void RecordInvalidContentNode(string host) => RecordFailure(invalidContentNodes, host);

    public void RecordValidNodeResponse(string host) => invalidContentNodes.TryRemove(host, out _);

    public bool ShouldAvoidNode(string host) => ShouldAvoidInvalidContentNode(host)
        || networkNodes.TryGetValue(host, out var state) && state.AvoidUntil > clock.GetUtcNow();

    internal RedispatchLease? TryAcquireRedispatch(Uri entry, Uri node)
    {
        var key = (DownloadHostConcurrencyController.NormalizeOrigin(entry), node.IdnHost.ToLowerInvariant());
        var state = redispatches.GetOrAdd(key, static _ => new RedispatchState());
        lock (state)
        {
            if (state.InFlight || state.RetryAfter > clock.GetUtcNow())
                return null;
            state.InFlight = true;
            return new RedispatchLease(this, state);
        }
    }

    public void RecordNodeNetworkFailure(string host, DownloadFailureReason reason, System.Net.HttpStatusCode? statusCode)
    {
        if (AffectsHealth(reason, statusCode))
            RecordFailure(networkNodes, host);
    }

    public bool ShouldAvoid(string resolvedSourceKind, string host)
    {
        return states.TryGetValue((resolvedSourceKind, host), out var state)
            && state.AvoidUntil > clock.GetUtcNow();
    }

    public void RecordSuccess(string resolvedSourceKind, string host)
    {
        states.TryRemove((resolvedSourceKind, host), out _);
    }

    public void RecordFailure(
        string resolvedSourceKind,
        string host,
        DownloadFailureReason reason,
        System.Net.HttpStatusCode? statusCode = null)
    {
        if (!AffectsHealth(reason, statusCode))
            return;
        RecordFailure(states, (resolvedSourceKind, host));
    }

    private void RecordFailure<TKey>(ConcurrentDictionary<TKey, HostState> failuresByHost, TKey host)
        where TKey : notnull
    {
        var now = clock.GetUtcNow();
        failuresByHost.AddOrUpdate(
            host,
            _ => new HostState(1, now + cooldown, DateTimeOffset.MinValue),
            (_, current) =>
            {
                var failures = now >= current.ExpiresAt ? 1 : current.Failures + 1;
                var avoidUntil = failures >= 3 ? now + cooldown : DateTimeOffset.MinValue;
                return new HostState(failures, now + cooldown, avoidUntil);
            });
    }

    internal static bool AffectsHealth(DownloadFailureReason reason, System.Net.HttpStatusCode? statusCode = null) => reason is
        DownloadFailureReason.Dns or DownloadFailureReason.Network or DownloadFailureReason.ResponseHeadersTimeout
        or DownloadFailureReason.FirstByteTimeout or DownloadFailureReason.BodyIdleTimeout
        or DownloadFailureReason.BodyInterrupted
        || (reason is DownloadFailureReason.HttpStatus && statusCode is { } code && (int)code is >= 500 and <= 599);

    private sealed record HostState(int Failures, DateTimeOffset ExpiresAt, DateTimeOffset AvoidUntil);

    internal sealed class RedispatchState
    {
        public bool InFlight { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
    }

    internal sealed class RedispatchLease(DownloadHostHealthTracker owner, RedispatchState state) : IDisposable
    {
        private bool redirectedToCooledNode;

        public void RecordRejectedRedirect(string host) => redirectedToCooledNode = owner.ShouldAvoidNode(host);

        public void Dispose()
        {
            lock (state)
            {
                if (redirectedToCooledNode)
                    state.RetryAfter = owner.clock.GetUtcNow() + RedispatchCooldown;
                state.InFlight = false;
            }
        }
    }
}
