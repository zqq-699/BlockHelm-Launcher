/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public enum InstanceCleanupCategory { GameLogs, CrashReports, RootLogs, LauncherOutput, LauncherDiagnostics }

public sealed record InstanceCleanupEntry(InstanceCleanupCategory Category, string RelativePath, long Size, long LastWriteTimeUtcTicks, string Identity);
public sealed record InstanceCleanupPreview(GameInstance Instance, string RootIdentity, IReadOnlyList<InstanceCleanupEntry> Entries);
public sealed record InstanceCleanupResult(int Deleted, int Skipped);

public interface IInstanceCleanupService
{
    Task<InstanceCleanupPreview> ScanAsync(GameInstance instance, CancellationToken cancellationToken = default);
    Task<InstanceCleanupResult> CleanAsync(InstanceCleanupPreview preview, IReadOnlySet<InstanceCleanupCategory> categories, CancellationToken cancellationToken = default);
}
