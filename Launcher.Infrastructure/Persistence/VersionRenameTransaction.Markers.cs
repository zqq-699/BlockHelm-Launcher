/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Infrastructure.FileSystem;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Persistence;

internal sealed partial class VersionRenameTransaction
{
    private async Task WriteOwnedMarkerAsync(string directory, PendingInstanceRenameMarker marker, CancellationToken cancellationToken)
    {
        using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(directory);
        await AtomicJsonFileWriter.WriteAsync(PendingInstanceRenameDirectory.GetMarkerPath(directory), marker, JsonOptions,
            cancellationToken, validateDestination: () =>
            {
                if (!GameInstanceSettingsStore.HasIdentity(directory, marker.OwnerProof))
                    throw new GameInstanceMutationConflictException(directory, marker.OldName);
            }).ConfigureAwait(false);
    }

    private async Task DeleteOrQuarantineMarkerAsync(string directory, CancellationToken cancellationToken)
    {
        var markerPath = PendingInstanceRenameDirectory.GetMarkerPath(directory);
        try
        {
            await DeleteMarkerWithRetryAsync(markerPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception deleteException)
        {
            var abortedMarkerPath = Path.Combine(directory, PendingInstanceRenameDirectory.AbortedMarkerFileName);
            try
            {
                quarantineMarker(markerPath, abortedMarkerPath);
                logger.LogError(
                    deleteException,
                    "Rolled-back rename marker could not be deleted and was quarantined. MarkerPath={MarkerPath} AbortedMarkerPath={AbortedMarkerPath}",
                    markerPath,
                    abortedMarkerPath);
            }
            catch (Exception quarantineException)
            {
                throw new AggregateException(
                    "Rolled-back rename marker could not be deleted or quarantined.",
                    deleteException,
                    quarantineException);
            }
        }
    }

    private async Task EnsureOwnedOrQuarantineAsync(
        string directory,
        PendingInstanceRenameMarker marker,
        CancellationToken cancellationToken)
    {
        try
        {
            var markerResult = await PendingInstanceRenameMarkerFile.ReadAsync(directory, cancellationToken)
                .ConfigureAwait(false);
            if (markerResult.Status != PendingInstanceRenameMarkerStatus.Valid
                || markerResult.Marker is null
                || !string.Equals(
                    markerResult.Marker.TransactionId,
                    marker.TransactionId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(markerResult.Marker.OwnerProof, marker.OwnerProof, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(markerResult.Marker.OldName, marker.OldName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(markerResult.Marker.NewName, marker.NewName, StringComparison.OrdinalIgnoreCase))
            {
                throw new GameInstanceMutationConflictException(marker.OwnerProof, marker.OldName);
            }

            await instanceSettingsStore.EnsureIdentityAsync(directory, marker.OwnerProof, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GameInstanceMutationConflictException)
        {
            QuarantineUnsafeMarker(directory, marker);
            throw;
        }
    }

    private void QuarantineUnsafeMarker(string directory, PendingInstanceRenameMarker marker)
    {
        var markerPath = PendingInstanceRenameDirectory.GetMarkerPath(directory);
        var abortedMarkerPath = Path.Combine(directory, PendingInstanceRenameDirectory.AbortedMarkerFileName);
        try
        {
            if (File.Exists(markerPath))
                quarantineMarker(markerPath, abortedMarkerPath);
            logger.LogError(
                "Pending rename marker was quarantined because the directory belongs to another instance. ExpectedOwnerProof={ExpectedOwnerProof} Directory={Directory}",
                marker.OwnerProof,
                directory);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unsafe pending rename marker could not be quarantined. ExpectedOwnerProof={ExpectedOwnerProof} Directory={Directory}",
                marker.OwnerProof,
                directory);
        }
    }

    private void HandleInvalidMarker(string directory, Exception? validationException)
    {
        var markerPath = PendingInstanceRenameDirectory.GetMarkerPath(directory);
        if (PendingInstanceRenameDirectory.IsPending(directory))
        {
            logger.LogError(
                validationException,
                "Invalid pending instance rename marker was preserved in a staging directory. MarkerPath={MarkerPath}",
                markerPath);
            return;
        }

        var abortedMarkerPath = Path.Combine(directory, PendingInstanceRenameDirectory.AbortedMarkerFileName);
        try
        {
            quarantineMarker(markerPath, abortedMarkerPath);
            logger.LogError(
                validationException,
                "Invalid pending instance rename marker was quarantined. MarkerPath={MarkerPath} AbortedMarkerPath={AbortedMarkerPath}",
                markerPath,
                abortedMarkerPath);
        }
        catch (Exception quarantineException)
        {
            logger.LogError(
                quarantineException,
                "Invalid pending instance rename marker could not be quarantined but will not hide the instance. MarkerPath={MarkerPath}",
                markerPath);
        }
    }

    private async Task DeleteMarkerWithRetryAsync(string markerPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxMarkerDeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(markerPath))
                    deleteMarker(markerPath);
                if (!File.Exists(markerPath))
                    return;
            }
            catch (Exception) when (attempt < MaxMarkerDeleteAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException($"Failed to delete pending rename marker: {markerPath}");
    }

    private void TryDeleteAbortedMarker(string directory)
    {
        var path = Path.Combine(directory, PendingInstanceRenameDirectory.AbortedMarkerFileName);
        try
        {
            if (File.Exists(path))
                deleteMarker(path);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to clean an aborted instance rename marker. MarkerPath={MarkerPath}", path);
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
