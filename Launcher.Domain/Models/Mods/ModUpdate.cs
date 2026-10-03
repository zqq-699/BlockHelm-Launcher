/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Domain.Models;

public enum ModUpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    NotRecognized,
    Unavailable
}

public sealed record ModUpdateLocalFileState(
    string FullPath,
    string Sha1,
    long CurseForgeFingerprint,
    long Length,
    long LastWriteTimeUtcTicks,
    bool IsEnabled);

public sealed record ModUpdateCandidate(
    ModUpdateLocalFileState LocalFile,
    ResourceProjectSource Source,
    string ProjectId,
    string CurrentVersionId,
    string CurrentVersionNumber,
    DateTimeOffset? CurrentPublishedAt,
    ResourceProjectVersion TargetVersion);

public sealed record ModUpdateCheckResult(
    ModUpdateCheckStatus Status,
    ModUpdateCandidate? Candidate = null,
    int SuccessfulProviderCount = 0,
    int RecognizedProviderCount = 0);

public sealed record ModUpdateBatchCheckItem(
    string FullPath,
    ModUpdateCheckResult Result);

public sealed record ModUpdateBatchCheckResult(
    IReadOnlyList<ModUpdateBatchCheckItem> Items,
    int ProviderCount);

public sealed record ModUpdateResult(
    string InstalledPath,
    string BackupPath);

public enum ModUpdateBatchFailureReason
{
    SourceChanged,
    TargetExists,
    InvalidPath,
    IntegrityFailed,
    Failed
}

public sealed record ModUpdateBatchItemResult(
    ModUpdateCandidate Candidate,
    ModUpdateResult? Result = null,
    ModUpdateBatchFailureReason? FailureReason = null,
    string? FailurePath = null);

public sealed record ModUpdateBatchResult(
    IReadOnlyList<ModUpdateBatchItemResult> Items);

public static class ModUpdateProgressStages
{
    public const string Preparing = "ModUpdate.Preparing";
    public const string Downloading = "ModUpdate.Downloading";
    public const string Applying = "ModUpdate.Applying";
    public const string BatchChecking = "ModUpdate.BatchChecking";
    public const string BatchWaitingForConfirmation = "ModUpdate.BatchWaitingForConfirmation";
    public const string BatchUpdating = "ModUpdate.BatchUpdating";
}
