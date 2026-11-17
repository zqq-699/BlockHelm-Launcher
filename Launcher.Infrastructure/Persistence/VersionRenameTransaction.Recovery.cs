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
    public async Task RecoverAllAsync(string minecraftDirectory, CancellationToken cancellationToken)
    {
        var versionsDirectory = Path.GetFullPath(Path.Combine(minecraftDirectory, "versions"));
        if (!Directory.Exists(versionsDirectory))
            return;

        foreach (var directory in EnumerateDirectories(versionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteAbortedMarker(directory);
            var markerResult = await PendingInstanceRenameMarkerFile.ReadAsync(directory, cancellationToken)
                .ConfigureAwait(false);
            if (markerResult.Status == PendingInstanceRenameMarkerStatus.Missing)
                continue;
            if (markerResult.Status == PendingInstanceRenameMarkerStatus.Invalid)
            {
                HandleInvalidMarker(directory, markerResult.Exception);
                continue;
            }
            if (markerResult.Status == PendingInstanceRenameMarkerStatus.Unreadable)
            {
                logger.LogWarning(
                    markerResult.Exception,
                    "Pending instance rename marker could not be read and remains for a later retry. MarkerPath={MarkerPath}",
                    PendingInstanceRenameDirectory.GetMarkerPath(directory));
                continue;
            }

            var marker = markerResult.Marker!;
            if (marker.SchemaVersion == 1)
            {
                using var preparationLease = WindowsDirectoryHandleMover.AcquireReadLease(directory);
                if (InstanceDirectoryOwnership.Matches(directory, marker.OwnerProof))
                {
                    marker = marker with { SchemaVersion = 2, OwnerProof = InstanceDirectoryOwnership.Capture(directory) };
                    await WriteOwnedMarkerAsync(directory, marker, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                if (PendingInstanceRenameDirectory.IsPending(directory))
                {
                    await CompleteCommittedRenameAsync(directory, marker, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                var directoryName = Path.GetFileName(directory);
                if (string.Equals(directoryName, marker.NewName, StringComparison.Ordinal))
                {
                    await CompleteCommittedRenameAsync(directory, marker, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(directoryName, marker.OldName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Pending rename marker does not match its directory.");

                var destinationDirectory = GetDestinationDirectory(directory, marker.NewName);
                if (!MinecraftDirectoryPath.Equals(directory, destinationDirectory) && IsPathOccupied(destinationDirectory))
                {
                    await RollbackRenameAsync(directory, marker, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                var staging = await MoveSourceToStagingAsync(
                    directory, marker, CancellationToken.None, rollbackMarkerOnFailure: false).ConfigureAwait(false);
                await CompleteCommittedRenameAsync(staging.Directory, staging.Marker, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Pending instance rename remains for a later retry. Directory={Directory} OldName={OldName} NewName={NewName}",
                    directory,
                    marker.OldName,
                    marker.NewName);
            }
        }
    }

    private static void ValidateNewTransaction(string sourceDirectory, string destinationDirectory, string oldVersionName)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Version directory not found: {sourceDirectory}");
        _ = InstanceVersionFiles.GetJsonPath(sourceDirectory, oldVersionName);
        if (!MinecraftDirectoryPath.Equals(sourceDirectory, destinationDirectory) && IsPathOccupied(destinationDirectory))
            throw new InstanceInstallNameConflictException(Path.GetFileName(destinationDirectory));
        if (PendingInstanceInstallDirectory.IsLogicalNameReserved(
                Path.GetDirectoryName(destinationDirectory)!,
                Path.GetFileName(destinationDirectory)))
        {
            throw new InstanceInstallNameConflictException(Path.GetFileName(destinationDirectory));
        }
        if (File.Exists(PendingInstanceRenameDirectory.GetMarkerPath(sourceDirectory)))
            throw new IOException($"Version directory already has a pending rename marker: {sourceDirectory}");
    }

    private PendingInstanceRenameMarker CreateMarker(
        string ownerProof,
        string oldName,
        string newName,
        string? newIconSource,
        DateTimeOffset updatedAt) => new()
    {
        TransactionId = guidFactory().ToString("N"),
        OwnerProof = ownerProof,
        OldName = oldName,
        NewName = newName,
        NewIconSource = newIconSource,
        UpdatedAtUtc = updatedAt
    };
}
