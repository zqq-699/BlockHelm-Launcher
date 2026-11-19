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

    private readonly ConcurrentDictionary<(string Source, string Host), HostState> states = new();
    private readonly TimeSpan cooldown = TimeSpan.FromSeconds(45);
    private readonly TimeProvider clock;

    internal DownloadHostHealthTracker(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
    }

    internal void Clear() => states.Clear();

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
        var now = clock.GetUtcNow();
        states.AddOrUpdate(
            (resolvedSourceKind, host),
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
}
