/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public interface IModUpdateService
{
    Task<ModUpdateCheckResult> CheckAsync(
        GameInstance instance,
        LocalMod mod,
        CancellationToken cancellationToken = default);

    Task<ModUpdateBatchCheckResult> CheckManyAsync(
        GameInstance instance,
        IReadOnlyList<LocalMod> mods,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModUpdateResult> UpdateAsync(
        GameInstance instance,
        ModUpdateCandidate candidate,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModUpdateBatchResult> UpdateManyAsync(
        GameInstance instance,
        IReadOnlyList<ModUpdateCandidate> candidates,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IModUpdateProvider
{
    ResourceProjectSource Source { get; }

    Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> FindUpdatesAsync(
        GameInstance instance,
        IReadOnlyList<ModUpdateLocalFileState> localFiles,
        CancellationToken cancellationToken = default,
        IProgress<string>? completedFileProgress = null);
}

public sealed record ModUpdateProviderResult(
    bool IsAvailable,
    bool IsRecognized,
    ModUpdateCandidate? Candidate = null);

public interface IModUpdateFileTransaction
{
    Task<ModUpdateLocalFileState> CaptureAsync(
        LocalMod mod,
        CancellationToken cancellationToken = default);

    Task<ModUpdateResult> ApplyAsync(
        GameInstance instance,
        ModUpdateCandidate candidate,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum ModUpdateConflictReason
{
    SourceChanged,
    TargetExists,
    InvalidPath
}

public sealed class ModUpdateConflictException(
    string path,
    ModUpdateConflictReason reason,
    Exception? innerException = null)
    : IOException($"Mod update conflict: {reason}.", innerException)
{
    public string Path { get; } = path;

    public ModUpdateConflictReason Reason { get; } = reason;
}
