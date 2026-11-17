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

using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.GameSettings;

internal sealed class InstanceContentRefreshWatcher : IDisposable
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(200);
    private readonly object gate = new();
    private readonly IInstanceDirectoryMonitor monitor;
    private readonly InstanceDirectoryKind directoryKind;
    private readonly Func<Task> refreshAsync;
    private readonly Action<Exception> reportFailure;
    private readonly Func<InstanceDirectoryChangedEventArgs, bool>? shouldRefresh;
    private readonly Action<InstanceDirectoryChangedEventArgs>? invalidated;
    private readonly Func<bool>? canRefresh;
    private readonly ILogger logger;
    private IInstanceDirectoryWatch? watch;
    private CancellationTokenSource? pendingDelay;
    private GameInstance? instance;
    private InstanceDirectoryChangedEventArgs? latestChange;
    private bool enabled;
    private bool refreshRunning;
    private bool trailingRefresh;
    private bool rebuildAfterRefresh;
    private int suspensionCount;
    private long generation;

    public InstanceContentRefreshWatcher(
        IInstanceDirectoryMonitor monitor,
        InstanceDirectoryKind directoryKind,
        Func<Task> refreshAsync,
        Action<Exception> reportFailure,
        ILogger logger,
        Func<InstanceDirectoryChangedEventArgs, bool>? shouldRefresh = null,
        Action<InstanceDirectoryChangedEventArgs>? invalidated = null,
        Func<bool>? canRefresh = null)
    {
        this.monitor = monitor;
        this.directoryKind = directoryKind;
        this.refreshAsync = refreshAsync;
        this.reportFailure = reportFailure;
        this.logger = logger;
        this.shouldRefresh = shouldRefresh;
        this.invalidated = invalidated;
        this.canRefresh = canRefresh;
    }

    public void SetInstance(GameInstance? value)
    {
        lock (gate)
        {
            if (IsSameWatchTarget(instance, value))
            {
                instance = value;
                return;
            }

            instance = value;
            ResetWatchLocked();
        }
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (enabled == value)
                return;

            enabled = value;
            ResetWatchLocked();
        }
    }

    public void Suspend()
    {
        lock (gate)
        {
            suspensionCount++;
            if (suspensionCount == 1)
                ResetWatchLocked();
        }
    }

    public void Resume(bool restart = true)
    {
        lock (gate)
        {
            if (suspensionCount == 0)
                return;

            suspensionCount--;
            if (suspensionCount != 0)
                return;

            if (restart)
                ResetWatchLocked();
            else
                StopWatchAndInvalidateLocked();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            enabled = false;
            suspensionCount++;
            StopWatchAndInvalidateLocked();
        }
    }

    private void ResetWatchLocked()
    {
        StopWatchAndInvalidateLocked();
        if (!enabled || suspensionCount > 0 || instance is null || string.IsNullOrWhiteSpace(instance.InstanceDirectory))
            return;

        try
        {
            watch = monitor.Watch(instance, directoryKind);
            watch.Changed += Watch_Changed;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to start instance content watcher. InstanceDirectory={InstanceDirectory} DirectoryKind={DirectoryKind}",
                instance.InstanceDirectory,
                directoryKind);
        }
    }

    private void StopWatchAndInvalidateLocked()
    {
        generation++;
        trailingRefresh = false;
        rebuildAfterRefresh = false;
        latestChange = null;
        CancelPendingDelayLocked();
        if (watch is null)
            return;

        watch.Changed -= Watch_Changed;
        watch.Dispose();
        watch = null;
    }

    private void Watch_Changed(object? sender, InstanceDirectoryChangedEventArgs e)
    {
        lock (gate)
        {
            var isError = e.ChangeType.Equals("Error", StringComparison.OrdinalIgnoreCase);
            if (!ReferenceEquals(sender, watch)
                || !IsActiveLocked()
                || !isError && shouldRefresh?.Invoke(e) == false)
            {
                return;
            }

            latestChange = e;
            rebuildAfterRefresh |= isError;
            invalidated?.Invoke(e);
            if (canRefresh?.Invoke() == false)
            {
                if (rebuildAfterRefresh)
                {
                    rebuildAfterRefresh = false;
                    ResetWatchLocked();
                }
                return;
            }

            if (refreshRunning)
            {
                trailingRefresh = true;
                return;
            }

            ScheduleRefreshLocked();
        }
    }

    private void ScheduleRefreshLocked()
    {
        CancelPendingDelayLocked();
        var cancellation = new CancellationTokenSource();
        pendingDelay = cancellation;
        _ = RunAfterDelayAsync(cancellation, generation, instance!);
    }

    private async Task RunAfterDelayAsync(
        CancellationTokenSource cancellation,
        long expectedGeneration,
        GameInstance watchedInstance)
    {
        try
        {
            await Task.Delay(RefreshDelay, cancellation.Token).ConfigureAwait(false);

            InstanceDirectoryChangedEventArgs? change;
            lock (gate)
            {
                if (!IsCurrentLocked(expectedGeneration, watchedInstance)
                    || !ReferenceEquals(pendingDelay, cancellation)
                    || canRefresh?.Invoke() == false)
                {
                    return;
                }

                pendingDelay = null;
                refreshRunning = true;
                trailingRefresh = false;
                change = latestChange;
            }

            logger.LogDebug(
                "Detected instance content change. InstanceDirectory={InstanceDirectory} DirectoryKind={DirectoryKind} ChangeType={ChangeType} Path={Path}",
                watchedInstance.InstanceDirectory,
                directoryKind,
                change?.ChangeType ?? "<unknown>",
                change?.FullPath ?? "<unknown>");
            await refreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (!IsCurrentLocked(expectedGeneration, watchedInstance))
                    return;
            }

            logger.LogError(
                exception,
                "Failed to refresh instance content after directory change. InstanceDirectory={InstanceDirectory} DirectoryKind={DirectoryKind}",
                watchedInstance.InstanceDirectory,
                directoryKind);
            reportFailure(exception);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pendingDelay, cancellation))
                    pendingDelay = null;
                cancellation.Dispose();

                if (refreshRunning)
                {
                    refreshRunning = false;
                    if (IsActiveLocked() && rebuildAfterRefresh)
                    {
                        var runTrailingCheck = trailingRefresh;
                        rebuildAfterRefresh = false;
                        ResetWatchLocked();
                        if (runTrailingCheck && IsActiveLocked())
                            ScheduleRefreshLocked();
                    }
                    else if (IsActiveLocked() && trailingRefresh)
                    {
                        trailingRefresh = false;
                        ScheduleRefreshLocked();
                    }

                    if (!IsActiveLocked())
                    {
                        trailingRefresh = false;
                        rebuildAfterRefresh = false;
                    }
                }
            }
        }
    }

    private void CancelPendingDelayLocked()
    {
        var cancellation = pendingDelay;
        pendingDelay = null;
        if (cancellation is null)
            return;

        cancellation.Cancel();
    }

    private bool IsActiveLocked() =>
        enabled && suspensionCount == 0 && instance is not null;

    private bool IsCurrentLocked(long expectedGeneration, GameInstance watchedInstance) =>
        expectedGeneration == generation
        && IsActiveLocked()
        && IsSameWatchTarget(instance, watchedInstance);

    private bool IsSameWatchTarget(GameInstance? left, GameInstance? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null)
            return false;

        return monitor.IsSameWatchTarget(left, right);
    }
}
