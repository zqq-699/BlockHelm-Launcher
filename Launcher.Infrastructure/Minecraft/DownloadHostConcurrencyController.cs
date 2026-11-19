/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Coordinates global download admission with server-requested cooldowns.
/// Hosts have no separate concurrency quota or failure-driven adjustment.
/// </summary>
internal sealed class DownloadHostConcurrencyController
{
    internal static readonly TimeSpan MaximumCooldownWait = TimeSpan.FromSeconds(120);
    public static DownloadHostConcurrencyController Shared { get; } = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> delayAsync;

    public DownloadHostConcurrencyController(
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delayAsync = delayAsync ?? ((delay, cancellationToken) =>
            new ValueTask(Task.Delay(delay, cancellationToken)));
    }

    public async ValueTask<DownloadAdmissionLease> AcquireAsync(
        Uri uri,
        Func<CancellationToken, ValueTask<IImportConcurrencyLease>> acquireGlobalAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(acquireGlobalAsync);
        var origin = NormalizeOrigin(uri);
        var cooldownWaited = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cooldownWaited += await WaitForCooldownAsync(origin, cooldownWaited, cancellationToken).ConfigureAwait(false);
            var globalLease = await acquireGlobalAsync(cancellationToken).ConfigureAwait(false);
            // Cooldown may begin while this request waits for a global slot.
            // Return that slot before waiting for the server's cooldown.
            if (GetRemainingCooldown(origin) > TimeSpan.Zero)
            {
                globalLease.Dispose();
                continue;
            }
            return new DownloadAdmissionLease(origin, globalLease);
        }
    }

    public void DeferRequests(string origin, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        var now = timeProvider.GetUtcNow();
        foreach (var entry in cooldowns)
            if (entry.Value <= now)
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)cooldowns).Remove(entry);
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

    public DownloadAdmissionLease? TryAcquireAvailable(Uri uri, Func<IImportConcurrencyLease?> tryAcquireGlobal)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(tryAcquireGlobal);
        var origin = NormalizeOrigin(uri);
        if (GetRemainingCooldown(origin) > TimeSpan.Zero) return null;
        var globalLease = tryAcquireGlobal();
        if (globalLease is null) return null;
        if (GetRemainingCooldown(origin) > TimeSpan.Zero)
        {
            globalLease.Dispose();
            return null;
        }
        return new DownloadAdmissionLease(origin, globalLease);
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

    internal sealed class DownloadAdmissionLease(string origin, IImportConcurrencyLease globalLease) : IDisposable, IAsyncDisposable
    {
        private IImportConcurrencyLease? globalLease = globalLease;
        public string Origin { get; } = origin;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() => Interlocked.Exchange(ref globalLease, null)?.Dispose();
    }
}
