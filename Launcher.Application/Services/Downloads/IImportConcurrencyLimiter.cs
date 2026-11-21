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

namespace Launcher.Application.Services;

public interface IImportConcurrencyLimiter
{
    /// <summary>
    /// Gets a consistent snapshot of the shared scheduler used by metadata,
    /// modpack, and runtime download leases.
    /// </summary>
    DownloadConcurrencySnapshot DownloadSnapshot { get; }

    /// <summary>
    /// Atomically acquires a download slot without waiting. Implementations must
    /// return <see langword="false"/> when an ordinary waiter exists or the
    /// current target has already been reached.
    /// </summary>
    bool TryAcquireAvailableDownloadSlot(out IImportConcurrencyLease? lease);

    /// <summary>
    /// Acquires an idle slot for speculative work and returns a signal that
    /// completes when an ordinary download queues. The caller must stop its
    /// work and release the lease when signaled.
    /// </summary>
    bool TryAcquirePreemptibleDownloadSlot(out IImportConcurrencyLease? lease, out Task? ordinaryDownloadQueued);

    ValueTask<IImportConcurrencyLease> AcquireMetadataSlotAsync(CancellationToken cancellationToken = default);

    ValueTask<IImportConcurrencyLease> AcquireModpackDownloadSlotAsync(CancellationToken cancellationToken = default);

    ValueTask<IImportConcurrencyLease> AcquireRuntimeDownloadSlotAsync(CancellationToken cancellationToken = default);

    ValueTask<IImportConcurrencyLease> AcquireHashSlotAsync(CancellationToken cancellationToken = default);
}

public readonly record struct DownloadConcurrencySnapshot(
    int ActiveCount,
    int WaitingCount,
    int CurrentTarget);

public interface IImportConcurrencyLease : IDisposable, IAsyncDisposable;
