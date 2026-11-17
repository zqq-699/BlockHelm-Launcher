/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public sealed record LoaderInstallTarget(
    string MinecraftDirectory,
    string LogicalVersionName,
    string PhysicalOutputDirectory);

public sealed class InstanceInstallNameConflictException(string logicalVersionName)
    : IOException($"An instance installation named '{logicalVersionName}' is already active or installed.")
{
    public string LogicalVersionName { get; } = logicalVersionName;
}

public interface IInstanceInstallTransaction : IAsyncDisposable
{
    string MinecraftDirectory { get; }
    string LogicalVersionName { get; }
    string PendingDirectory { get; }
    string FinalDirectory { get; }
    bool IsCommitted { get; }

    Task CommitAsync(GameInstance instance, CancellationToken cancellationToken = default);

    Task CompleteLogicalCommitAsync(CancellationToken cancellationToken = default);

    Task AbortAsync(CancellationToken cancellationToken = default);
}

public interface IInstanceInstallTransactionService
{
    Task<IInstanceInstallTransaction> BeginAsync(
        string minecraftDirectory,
        string logicalVersionName,
        string installKind,
        bool initializeDefaultIfEmpty,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IInstanceInstallCleanupService
{
    Task CleanupPendingAsync(CancellationToken cancellationToken = default);
}
