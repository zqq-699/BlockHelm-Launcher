/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Modpacks;

internal sealed class ImportConcurrencyLimiter : IImportConcurrencyLimiter, IDownloadConcurrencyLimitState
{
    internal const int MinimumDownloadConcurrency = LauncherDefaults.MinimumDownloadConcurrency;
    internal const int InitialDownloadConcurrency = LauncherDefaults.DefaultMaximumDownloadConcurrency;
    internal const int MaximumDownloadConcurrency = LauncherDefaults.MaximumDownloadConcurrency;

    public static ImportConcurrencyLimiter Shared { get; } = new();

    private readonly SemaphoreSlim hashSemaphore = new(2, 2);
    private readonly FixedDownloadScheduler downloadScheduler = new(
        MaximumDownloadConcurrency,
        InitialDownloadConcurrency);

    int IDownloadConcurrencyLimitState.MaximumDownloadConcurrency => downloadScheduler.Snapshot.CurrentTarget;

    public void SetMaximumDownloadConcurrency(int maximumDownloadConcurrency)
    {
        downloadScheduler.SetMaximum(Math.Clamp(
            maximumDownloadConcurrency,
            MinimumDownloadConcurrency,
            MaximumDownloadConcurrency));
    }

    public ValueTask<IImportConcurrencyLease> AcquireMetadataSlotAsync(CancellationToken cancellationToken = default) =>
        downloadScheduler.AcquireAsync(cancellationToken);

    public ValueTask<IImportConcurrencyLease> AcquireModpackDownloadSlotAsync(CancellationToken cancellationToken = default) =>
        downloadScheduler.AcquireAsync(cancellationToken);

    public ValueTask<IImportConcurrencyLease> AcquireRuntimeDownloadSlotAsync(CancellationToken cancellationToken = default) =>
        downloadScheduler.AcquireAsync(cancellationToken);

    public ValueTask<IImportConcurrencyLease> AcquireHashSlotAsync(CancellationToken cancellationToken = default) =>
        AcquireFixedAsync(hashSemaphore, cancellationToken);

    internal (int ActiveCount, int WaitingCount, int CurrentTarget, int ConfiguredMaximum) DownloadSnapshot =>
        downloadScheduler.Snapshot;

    internal bool TryAcquireAvailableDownloadSlot(out IImportConcurrencyLease? lease) =>
        downloadScheduler.TryAcquireAvailable(out lease);

    DownloadConcurrencySnapshot IImportConcurrencyLimiter.DownloadSnapshot
    {
        get
        {
            var snapshot = downloadScheduler.Snapshot;
            return new DownloadConcurrencySnapshot(
                snapshot.ActiveCount,
                snapshot.WaitingCount,
                snapshot.CurrentTarget);
        }
    }

    bool IImportConcurrencyLimiter.TryAcquireAvailableDownloadSlot(out IImportConcurrencyLease? lease) =>
        TryAcquireAvailableDownloadSlot(out lease);

    bool IImportConcurrencyLimiter.TryAcquirePreemptibleDownloadSlot(
        out IImportConcurrencyLease? lease, out Task? ordinaryDownloadQueued) =>
        downloadScheduler.TryAcquirePreemptible(out lease, out ordinaryDownloadQueued);

    private static async ValueTask<IImportConcurrencyLease> AcquireFixedAsync(SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreLease(semaphore);
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IImportConcurrencyLease
    {
        private SemaphoreSlim? semaphore = semaphore;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() => Interlocked.Exchange(ref semaphore, null)?.Release();
    }
}

internal sealed class FixedDownloadScheduler
{
    private readonly object syncRoot = new();
    private readonly Queue<Waiter> waiters = [];
    private readonly int maximum;
    private int currentTarget;
    private int activeCount;
    private int waitingCount;
    private TaskCompletionSource? ordinaryDownloadQueued;

    public FixedDownloadScheduler(int maximum, int? initial = null)
    {
        if (maximum < 1)
            throw new ArgumentOutOfRangeException(nameof(maximum));

        this.maximum = maximum;
        currentTarget = Math.Clamp(initial ?? maximum, 1, maximum);
    }

    internal (int ActiveCount, int WaitingCount, int CurrentTarget, int ConfiguredMaximum) Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return (
                    activeCount,
                    waitingCount,
                    currentTarget,
                    currentTarget);
            }
        }
    }

    public ValueTask<IImportConcurrencyLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            if (activeCount < currentTarget)
            {
                activeCount++;
                return ValueTask.FromResult<IImportConcurrencyLease>(new Lease(this));
            }

            var waiter = new Waiter();
            waiters.Enqueue(waiter);
            waitingCount++;
            ordinaryDownloadQueued?.TrySetResult();
            ordinaryDownloadQueued = null;
            return new ValueTask<IImportConcurrencyLease>(WaitForQueuedLeaseAsync(waiter, cancellationToken));
        }
    }

    public bool TryAcquireAvailable(out IImportConcurrencyLease? lease) =>
        TryAcquireAvailable(preemptible: false, out lease, out _);

    public bool TryAcquirePreemptible(out IImportConcurrencyLease? lease, out Task? queued) =>
        TryAcquireAvailable(preemptible: true, out lease, out queued);

    private bool TryAcquireAvailable(bool preemptible, out IImportConcurrencyLease? lease, out Task? queued)
    {
        lock (syncRoot)
        {
            if (waitingCount != 0 || activeCount >= currentTarget)
            {
                lease = null;
                queued = null;
                return false;
            }

            activeCount++;
            lease = new Lease(this);
            queued = preemptible
                ? (ordinaryDownloadQueued ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : null;
            return true;
        }
    }

    public void SetMaximum(int target)
    {
        List<Waiter>? grantedWaiters;
        lock (syncRoot)
        {
            currentTarget = Math.Clamp(target, 1, maximum);
            grantedWaiters = GrantAvailableWaiters();
        }

        CompleteGrantedWaiters(grantedWaiters);
    }

    private void Release()
    {
        List<Waiter>? grantedWaiters;
        lock (syncRoot)
        {
            activeCount--;
            grantedWaiters = GrantAvailableWaiters();
        }

        CompleteGrantedWaiters(grantedWaiters);
    }

    private async Task<IImportConcurrencyLease> WaitForQueuedLeaseAsync(
        Waiter waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var releaseGrantedLease = false;
            lock (syncRoot)
            {
                if (waiter.State is WaiterState.Queued)
                {
                    waiter.State = WaiterState.Canceled;
                    waitingCount--;
                }
                else if (waiter.State is WaiterState.Granted)
                {
                    releaseGrantedLease = true;
                }
            }

            if (releaseGrantedLease)
            {
                var lease = await waiter.Completion.Task.ConfigureAwait(false);
                lease.Dispose();
            }

            throw;
        }
    }

    private List<Waiter>? GrantAvailableWaiters()
    {
        List<Waiter>? grantedWaiters = null;
        while (activeCount < currentTarget && waiters.Count > 0)
        {
            var waiter = waiters.Dequeue();
            if (waiter.State is not WaiterState.Queued)
                continue;

            waiter.State = WaiterState.Granted;
            waitingCount--;
            activeCount++;
            (grantedWaiters ??= []).Add(waiter);
        }

        return grantedWaiters;
    }

    private void CompleteGrantedWaiters(List<Waiter>? grantedWaiters)
    {
        if (grantedWaiters is null)
            return;

        foreach (var waiter in grantedWaiters)
            waiter.Completion.TrySetResult(new Lease(this));
    }

    private sealed class Lease(FixedDownloadScheduler owner) : IImportConcurrencyLease
    {
        private FixedDownloadScheduler? owner = owner;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<IImportConcurrencyLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WaiterState State { get; set; } = WaiterState.Queued;
    }

    private enum WaiterState
    {
        Queued,
        Granted,
        Canceled
    }
}
