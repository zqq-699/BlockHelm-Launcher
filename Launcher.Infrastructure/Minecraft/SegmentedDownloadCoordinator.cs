/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Shares opportunistic segmented workers fairly between all active files.
/// Baseline workers continue to use the ordinary admission queue; additional
/// workers are only reserved while the global queue is idle.
/// </summary>
internal sealed class SegmentedDownloadCoordinator
{
    public static SegmentedDownloadCoordinator Shared { get; } = new();

    private readonly object syncRoot = new();
    private readonly List<SessionState> sessions = [];
    private long nextSessionId;
    private long fairnessRounds;
    private int peakLiveWorkers;

    public Registration Register(
        int usefulWorkerCount,
        Func<SegmentedGlobalConcurrencySnapshot> getGlobalSnapshot)
    {
        ArgumentNullException.ThrowIfNull(getGlobalSnapshot);

        lock (syncRoot)
        {
            if (sessions.Count == 0)
                peakLiveWorkers = 0;
            var state = new SessionState(
                ++nextSessionId,
                Math.Max(1, usefulWorkerCount),
                getGlobalSnapshot);
            sessions.Add(state);
            UpdatePeakNoLock();
            RecalculateTargetsNoLock();
            return new Registration(this, state);
        }
    }

    private bool TryReserveAdditionalWorker(
        SessionState state,
        out SegmentedSchedulingSnapshot snapshot)
    {
        lock (syncRoot)
        {
            if (state.Disposed)
            {
                snapshot = CreateSnapshotNoLock(state);
                return false;
            }

            RecalculateTargetsNoLock();
            if (state.LiveWorkers >= state.TargetWorkers)
            {
                snapshot = CreateSnapshotNoLock(state);
                return false;
            }

            state.LiveWorkers++;
            snapshot = CreateSnapshotNoLock(state);
            return true;
        }
    }

    private void ConfirmAdditionalWorkerActivated(SessionState state)
    {
        lock (syncRoot)
        {
            state.AdditionalWorkersGranted++;
            UpdatePeakNoLock();
        }
    }

    private bool TryBeginAdditionalWorkerRetirement(
        SessionState state,
        out SegmentedSchedulingSnapshot snapshot)
    {
        lock (syncRoot)
        {
            if (state.Disposed)
            {
                state.RetiringWorkers++;
                snapshot = CreateSnapshotNoLock(state);
                return true;
            }

            RecalculateTargetsNoLock();
            var retainedWorkers = state.LiveWorkers - state.RetiringWorkers;
            if (retainedWorkers <= state.TargetWorkers)
            {
                snapshot = CreateSnapshotNoLock(state);
                return false;
            }

            state.RetiringWorkers++;
            snapshot = CreateSnapshotNoLock(state);
            return true;
        }
    }

    private void ReleaseWorker(
        SessionState state,
        bool additional,
        bool retirementReserved)
    {
        lock (syncRoot)
        {
            if (state.LiveWorkers > 0)
                state.LiveWorkers--;
            if (retirementReserved && state.RetiringWorkers > 0)
                state.RetiringWorkers--;
            if (additional)
                state.AdditionalWorkersReturned++;
            RecalculateTargetsNoLock();
        }
    }

    private void CancelAdditionalWorkerReservation(
        SessionState state,
        bool activated)
    {
        lock (syncRoot)
        {
            if (state.LiveWorkers > 0)
                state.LiveWorkers--;
            if (activated && state.AdditionalWorkersGranted > 0)
                state.AdditionalWorkersGranted--;
            RecalculateTargetsNoLock();
        }
    }

    private SegmentedSchedulingSnapshot GetSnapshot(SessionState state)
    {
        lock (syncRoot)
        {
            RecalculateTargetsNoLock();
            return CreateSnapshotNoLock(state);
        }
    }

    private void Unregister(SessionState state)
    {
        lock (syncRoot)
        {
            if (state.Disposed)
                return;
            state.Disposed = true;
            sessions.Remove(state);
            RecalculateTargetsNoLock();
        }
    }

    private void RecalculateTargetsNoLock()
    {
        if (sessions.Count == 0)
            return;

        var global = sessions[0].GetGlobalSnapshot();
        var totalLiveWorkers = sessions.Sum(session => session.LiveWorkers);
        var unrelatedGlobalActive = Math.Max(0, global.ActiveCount - totalLiveWorkers);
        var ordinaryWorkWaiting = global.WaitingCount > 0;
        var globalCapacity = ordinaryWorkWaiting
            ? sessions.Count
            : Math.Max(
                sessions.Count,
                global.CurrentTarget - unrelatedGlobalActive);

        foreach (var session in sessions)
        {
            var baseline = Math.Min(1, session.UsefulWorkerCount);
            session.TargetWorkers = baseline;
        }

        var allocated = sessions.Sum(session => session.TargetWorkers);
        var madeProgress = true;
        while (allocated < globalCapacity && madeProgress)
        {
            madeProgress = false;
            foreach (var session in sessions)
            {
                if (allocated >= globalCapacity)
                    break;
                if (session.TargetWorkers >= session.UsefulWorkerCount)
                    continue;

                session.TargetWorkers++;
                allocated++;
                madeProgress = true;
            }
        }

        if (sessions.Any(session => session.TargetWorkers != session.LastReportedTarget))
        {
            fairnessRounds++;
            foreach (var session in sessions)
                session.LastReportedTarget = session.TargetWorkers;
        }
    }

    private void UpdatePeakNoLock()
    {
        peakLiveWorkers = Math.Max(
            peakLiveWorkers,
            sessions.Sum(session => session.LiveWorkers));
    }

    private SegmentedSchedulingSnapshot CreateSnapshotNoLock(SessionState state)
    {
        var global = state.GetGlobalSnapshot();
        return new SegmentedSchedulingSnapshot(
            state.Id,
            sessions.Count,
            state.LiveWorkers,
            state.TargetWorkers,
            sessions.Sum(session => session.LiveWorkers),
            peakLiveWorkers,
            state.AdditionalWorkersGranted,
            state.AdditionalWorkersReturned,
            fairnessRounds,
            global);
    }

    internal sealed class Registration : IDisposable
    {
        private readonly SegmentedDownloadCoordinator owner;
        private readonly SessionState state;
        private int disposed;

        internal Registration(SegmentedDownloadCoordinator owner, SessionState state)
        {
            this.owner = owner;
            this.state = state;
        }

        public long SessionId => state.Id;

        public bool TryReserveAdditionalWorker(out SegmentedSchedulingSnapshot snapshot)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                snapshot = owner.GetSnapshot(state);
                return false;
            }
            return owner.TryReserveAdditionalWorker(state, out snapshot);
        }

        public bool TryBeginAdditionalWorkerRetirement(
            out SegmentedSchedulingSnapshot snapshot) =>
            owner.TryBeginAdditionalWorkerRetirement(state, out snapshot);

        public void ReleaseBaselineWorker() =>
            owner.ReleaseWorker(state, additional: false, retirementReserved: false);

        public void ReleaseAdditionalWorker(bool retirementReserved) =>
            owner.ReleaseWorker(state, additional: true, retirementReserved);

        public void ConfirmAdditionalWorkerActivated() =>
            owner.ConfirmAdditionalWorkerActivated(state);

        public void CancelAdditionalWorkerReservation(bool activated = false) =>
            owner.CancelAdditionalWorkerReservation(state, activated);

        public SegmentedSchedulingSnapshot Snapshot => owner.GetSnapshot(state);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Unregister(state);
        }
    }

    internal sealed class SessionState(
        long id,
        int usefulWorkerCount,
        Func<SegmentedGlobalConcurrencySnapshot> getGlobalSnapshot)
    {
        public long Id { get; } = id;
        public int UsefulWorkerCount { get; } = usefulWorkerCount;
        public Func<SegmentedGlobalConcurrencySnapshot> GetGlobalSnapshot { get; } = getGlobalSnapshot;
        public int LiveWorkers { get; set; } = 1;
        public int TargetWorkers { get; set; } = 1;
        public int LastReportedTarget { get; set; }
        public int RetiringWorkers { get; set; }
        public int AdditionalWorkersGranted { get; set; }
        public int AdditionalWorkersReturned { get; set; }
        public bool Disposed { get; set; }
    }
}

internal readonly record struct SegmentedGlobalConcurrencySnapshot(
    int ActiveCount,
    int WaitingCount,
    int CurrentTarget);

internal readonly record struct SegmentedSchedulingSnapshot(
    long SessionId,
    int ActiveSessionCount,
    int LiveWorkerCount,
    int TargetWorkerCount,
    int GlobalSegmentedWorkerCount,
    int GlobalPeakSegmentedWorkerCount,
    int AdditionalWorkersGranted,
    int AdditionalWorkersReturned,
    long FairnessRounds,
    SegmentedGlobalConcurrencySnapshot Global);
