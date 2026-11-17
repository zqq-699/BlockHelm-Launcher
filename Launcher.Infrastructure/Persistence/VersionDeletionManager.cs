/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.IO;
using System.Security;
using Launcher.Application.Services;
using Launcher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Persistence;

internal sealed class VersionDeletionManager
{
    private const int MaxStageAttempts = 3;
    private readonly VersionDirectoryManager directoryManager;
    private readonly ILogger logger;
    private readonly Func<Guid> guidFactory;
    private readonly Action<string, string> moveDirectory;
    private readonly Action<string, string>? beforeMove;
    private readonly bool useIdentitySafeMove;
    private readonly Action<string> recycleDirectory;
    private readonly Action<string, bool> deleteDirectory;

    public VersionDeletionManager(
        VersionDirectoryManager directoryManager,
        ILogger logger,
        Func<Guid>? guidFactory = null,
        Action<string, string>? moveDirectory = null,
        Action<string, bool>? deleteDirectory = null,
        Action<string>? recycleDirectory = null,
        Action<string, string>? beforeMove = null)
    {
        this.directoryManager = directoryManager;
        this.logger = logger;
        this.guidFactory = guidFactory ?? Guid.NewGuid;
        this.moveDirectory = moveDirectory ?? Directory.Move;
        this.beforeMove = beforeMove;
        useIdentitySafeMove = moveDirectory is null;
        this.recycleDirectory = recycleDirectory ?? WindowsRecycleBin.MoveDirectory;
        this.deleteDirectory = deleteDirectory ?? Directory.Delete;
    }

    public async Task<string> StageAsync(
        string minecraftDirectory,
        string versionName,
        string expectedOwnerProof,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = directoryManager.GetVersionDirectory(minecraftDirectory, versionName);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Version directory not found: {sourceDirectory}");

        var versionsDirectory = GetVersionsDirectory(minecraftDirectory);
        var markerPath = PendingInstanceDeletionDirectory.GetMarkerPath(sourceDirectory);
        var context = new StageContext(
            sourceDirectory,
            versionsDirectory,
            markerPath,
            versionName,
            expectedOwnerProof);
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transactionId = guidFactory().ToString("N");
            var stagedDirectory = GetStagedDirectory(context, transactionId);

            if (PathExists(stagedDirectory))
            {
                LogCollision(versionName, stagedDirectory, attempt);
                continue;
            }

            var stagedResult = await TryStageAsync(
                context,
                transactionId,
                stagedDirectory,
                attempt,
                cancellationToken).ConfigureAwait(false);
            if (stagedResult is not null)
                return stagedResult;
        }

        TryDeleteOwnedPreparationMarker(sourceDirectory, markerPath, expectedOwnerProof);
        throw new IOException(
            $"Unable to stage version directory for deletion after {MaxStageAttempts} destination collisions: {sourceDirectory}");
    }

    private async Task<string?> TryStageAsync(
        StageContext context,
        string transactionId,
        string stagedDirectory,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await WritePreparationMarkerAsync(context, transactionId, cancellationToken).ConfigureAwait(false);
            MoveToStagedDirectory(context, stagedDirectory);
            EnsureStagedIdentity(context, stagedDirectory);
            logger.LogDebug(
                "Version directory staged for deletion. VersionName={VersionName} StagedDirectory={StagedDirectory}",
                context.VersionName,
                stagedDirectory);
            return stagedDirectory;
        }
        catch (IOException) when (Directory.Exists(context.SourceDirectory) && PathExists(stagedDirectory))
        {
            LogCollision(context.VersionName, stagedDirectory, attempt);
            TryDeleteOwnedPreparationMarker(
                context.SourceDirectory,
                context.MarkerPath,
                context.ExpectedOwnerProof);
            return null;
        }
        catch
        {
            TryDeleteOwnedPreparationMarker(
                context.SourceDirectory,
                context.MarkerPath,
                context.ExpectedOwnerProof);
            throw;
        }
    }

    private static async Task WritePreparationMarkerAsync(
        StageContext context,
        string transactionId,
        CancellationToken cancellationToken)
    {
        using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(context.SourceDirectory);
        await AtomicJsonFileWriter.WriteAsync(
            context.MarkerPath,
            new PendingInstanceDeletionMarker(
                3,
                transactionId,
                context.VersionName,
                context.ExpectedOwnerProof,
                DateTimeOffset.UtcNow),
            PendingInstanceDeletionDirectory.MarkerJsonOptions,
            cancellationToken,
            validateDestination: () =>
            {
                if (!GameInstanceSettingsStore.HasIdentity(context.SourceDirectory, context.ExpectedOwnerProof))
                    throw new GameInstanceMutationConflictException(context.SourceDirectory, context.VersionName);
            }).ConfigureAwait(false);
    }

    private void MoveToStagedDirectory(StageContext context, string stagedDirectory)
    {
        if (!GameInstanceSettingsStore.HasIdentity(context.SourceDirectory, context.ExpectedOwnerProof))
            throw new GameInstanceMutationConflictException(context.ExpectedOwnerProof, context.VersionName);

        if (useIdentitySafeMove)
        {
            MoveOwnedDirectory(context, stagedDirectory);
            return;
        }

        beforeMove?.Invoke(context.SourceDirectory, stagedDirectory);
        moveDirectory(context.SourceDirectory, stagedDirectory);
    }

    private void MoveOwnedDirectory(StageContext context, string stagedDirectory)
    {
        try
        {
            WindowsDirectoryHandleMover.MoveOwnedDirectory(
                context.SourceDirectory,
                stagedDirectory,
                () => GameInstanceSettingsStore.HasIdentity(
                    context.SourceDirectory,
                    context.ExpectedOwnerProof),
                beforeMove is null
                    ? null
                    : () => beforeMove(context.SourceDirectory, stagedDirectory));
        }
        catch (InvalidOperationException)
        {
            throw new GameInstanceMutationConflictException(
                context.ExpectedOwnerProof,
                context.VersionName);
        }
    }

    private void EnsureStagedIdentity(StageContext context, string stagedDirectory)
    {
        if (GameInstanceSettingsStore.HasIdentity(stagedDirectory, context.ExpectedOwnerProof))
            return;

        QuarantineMarker(stagedDirectory);
        throw new GameInstanceMutationConflictException(context.ExpectedOwnerProof, context.VersionName);
    }

    private static string GetStagedDirectory(StageContext context, string transactionId)
    {
        var suffix = transactionId[..8].ToLowerInvariant();
        var stagedDirectory = Path.GetFullPath(Path.Combine(
            context.VersionsDirectory,
            $"{PendingInstanceDeletionDirectory.Prefix}{context.VersionName}-{suffix}"));
        EnsureDirectChild(context.VersionsDirectory, stagedDirectory);
        return stagedDirectory;
    }

    public bool TryDelete(string minecraftDirectory, string stagedDirectory)
    {
        var versionsDirectory = GetVersionsDirectory(minecraftDirectory);
        var normalizedStagedDirectory = Path.GetFullPath(stagedDirectory);
        EnsureDirectChild(versionsDirectory, normalizedStagedDirectory);
        if (!PendingInstanceDeletionDirectory.IsPending(normalizedStagedDirectory))
            throw new ArgumentException("The directory is not a pending instance deletion directory.", nameof(stagedDirectory));

        if (!Directory.Exists(normalizedStagedDirectory))
            return true;
        var abortedMarkerPath = Path.Combine(normalizedStagedDirectory, PendingInstanceDeletionDirectory.AbortedMarkerFileName);
        if (File.Exists(abortedMarkerPath))
        {
            if (!PendingInstanceDeletionDirectory.TryReadValidMarker(normalizedStagedDirectory, out var abortedMarker, readAbortedMarker: true)
                || abortedMarker.SchemaVersion != 1)
            {
                logger.LogWarning(
                    "Pending deletion directory was preserved because its transaction was aborted. StagedDirectory={StagedDirectory}",
                    normalizedStagedDirectory);
                return false;
            }
            try
            {
                File.Move(abortedMarkerPath, PendingInstanceDeletionDirectory.GetMarkerPath(normalizedStagedDirectory));
                logger.LogInformation("Resuming legacy schema-1 staged deletion. StagedDirectory={StagedDirectory}", normalizedStagedDirectory);
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                logger.LogWarning(exception, "Failed to resume legacy staged deletion. StagedDirectory={StagedDirectory}", normalizedStagedDirectory);
                return false;
            }
        }
        if (!PendingInstanceDeletionDirectory.TryReadValidMarker(normalizedStagedDirectory, out var marker))
        {
            logger.LogWarning(
                "Pending deletion directory was preserved because its transaction marker is missing or invalid. StagedDirectory={StagedDirectory}",
                normalizedStagedDirectory);
            return false;
        }
        // Schema 1 predates ownership credentials. Keep its original cleanup contract:
        // a valid transaction marker in the matching deletion staging directory.
        if (marker.SchemaVersion >= 2
            && !GameInstanceSettingsStore.HasIdentity(normalizedStagedDirectory, marker.OwnerProof!))
        {
            QuarantineMarker(normalizedStagedDirectory);
            logger.LogError(
                "Pending deletion directory was preserved because its instance identity changed. ExpectedOwnerProof={ExpectedOwnerProof} StagedDirectory={StagedDirectory}",
                marker.OwnerProof,
                normalizedStagedDirectory);
            return false;
        }

        try
        {
            recycleDirectory(normalizedStagedDirectory);
            logger.LogDebug(
                "Staged version directory moved to recycle bin. StagedDirectory={StagedDirectory}",
                normalizedStagedDirectory);
            return true;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to move staged version directory to recycle bin; permanent deletion will be attempted. StagedDirectory={StagedDirectory}",
                normalizedStagedDirectory);
        }

        if (!CanDeleteStagedDirectory(normalizedStagedDirectory))
            return false;

        try
        {
            deleteDirectory(normalizedStagedDirectory, true);
            logger.LogDebug(
                "Staged version directory permanently deleted. StagedDirectory={StagedDirectory}",
                normalizedStagedDirectory);
            return true;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to delete staged version directory; it will be retried on startup. StagedDirectory={StagedDirectory}",
                normalizedStagedDirectory);
            return false;
        }
    }

    // The caller holds the version mutation lock. A marker still in a regular directory
    // cannot represent a staged deletion; preserve the record without blocking migration.
    public void CleanupOrphanPreparationMarkers(string minecraftDirectory, CancellationToken cancellationToken)
    {
        var versionsDirectory = GetVersionsDirectory(minecraftDirectory);
        if (!Directory.Exists(versionsDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory).Where(InstanceDirectoryClassifier.IsInstanceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerPath = PendingInstanceDeletionDirectory.GetMarkerPath(directory);
            if (!File.Exists(markerPath)) continue;
            try
            {
                using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var preservedPath = markerPath + $".{Guid.NewGuid():N}.orphaned";
                File.Move(markerPath, preservedPath);
                logger.LogWarning("Preserved orphaned deletion preparation marker. MarkerPath={MarkerPath} PreservedPath={PreservedPath}", markerPath, preservedPath);
            }
            catch (Exception exception) when (IsFileSystemFailure(exception))
            {
                logger.LogWarning(exception, "Failed to preserve orphaned deletion preparation marker. MarkerPath={MarkerPath}", markerPath);
            }
        }
    }

    public void CleanupPending(string minecraftDirectory, CancellationToken cancellationToken)
    {
        var versionsDirectory = GetVersionsDirectory(minecraftDirectory);
        if (!Directory.Exists(versionsDirectory))
            return;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(versionsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(PendingInstanceDeletionDirectory.IsPending)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogWarning(
                exception,
                "Failed to enumerate pending instance deletion directories. VersionsDirectory={VersionsDirectory}",
                versionsDirectory);
            return;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(minecraftDirectory, directory);
        }
    }

    private void LogCollision(string versionName, string stagedDirectory, int attempt)
    {
        logger.LogWarning(
            "Pending deletion directory collision. VersionName={VersionName} StagedDirectory={StagedDirectory} Attempt={Attempt} MaxAttempts={MaxAttempts}",
            versionName,
            stagedDirectory,
            attempt,
            MaxStageAttempts);
    }

    private void TryDeletePreparationMarker(string markerPath)
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogWarning(
                exception,
                "Failed to remove pending deletion preparation marker after staging failed. MarkerPath={MarkerPath}",
                markerPath);
        }
    }

    private void TryDeleteOwnedPreparationMarker(
        string sourceDirectory,
        string markerPath,
        string expectedOwnerProof)
    {
        try
        {
            using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(sourceDirectory);
            if (GameInstanceSettingsStore.HasIdentity(sourceDirectory, expectedOwnerProof))
                TryDeletePreparationMarker(markerPath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            logger.LogWarning(exception, "Deletion preparation marker retained because its source could not be verified. MarkerPath={MarkerPath}", markerPath);
        }
    }

    private void QuarantineMarker(string directory)
    {
        var markerPath = PendingInstanceDeletionDirectory.GetMarkerPath(directory);
        var abortedPath = Path.Combine(directory, PendingInstanceDeletionDirectory.AbortedMarkerFileName);
        try
        {
            if (File.Exists(markerPath))
                File.Move(markerPath, abortedPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogError(
                exception,
                "Failed to quarantine an unsafe pending deletion marker. MarkerPath={MarkerPath}",
                markerPath);
        }
    }

    private bool CanDeleteStagedDirectory(string stagedDirectory)
    {
        if (!Directory.Exists(stagedDirectory))
            return false;
        if (File.Exists(Path.Combine(stagedDirectory, PendingInstanceDeletionDirectory.AbortedMarkerFileName)))
            return false;
        if (!PendingInstanceDeletionDirectory.TryReadValidMarker(stagedDirectory, out var marker))
            return false;
        if (marker.SchemaVersion == 1 || GameInstanceSettingsStore.HasIdentity(stagedDirectory, marker.OwnerProof!))
            return true;

        QuarantineMarker(stagedDirectory);
        logger.LogError(
            "Pending deletion directory lost transaction ownership before permanent deletion. ExpectedOwnerProof={ExpectedOwnerProof} StagedDirectory={StagedDirectory}",
            marker.OwnerProof,
            stagedDirectory);
        return false;
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or PlatformNotSupportedException;

    private static string GetVersionsDirectory(string minecraftDirectory) =>
        Path.GetFullPath(Path.Combine(minecraftDirectory, "versions"));

    private static void EnsureDirectChild(string parentDirectory, string childDirectory)
    {
        if (!string.Equals(Path.GetDirectoryName(childDirectory), parentDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pending deletion directory must be a direct child of the versions directory.", nameof(childDirectory));
    }

    private sealed record StageContext(
        string SourceDirectory,
        string VersionsDirectory,
        string MarkerPath,
        string VersionName,
        string ExpectedOwnerProof);
}
