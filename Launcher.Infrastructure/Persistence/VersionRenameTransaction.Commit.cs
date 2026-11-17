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
    private async Task<RenameStagingResult> MoveSourceToStagingAsync(
        string sourceDirectory,
        PendingInstanceRenameMarker marker,
        CancellationToken cancellationToken,
        bool rollbackMarkerOnFailure)
    {
        var currentMarker = marker;
        for (var targetAttempt = 1; targetAttempt <= MaxStagingTargets; targetAttempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(sourceDirectory)!;
            var suffix = currentMarker.TransactionId[..8].ToLowerInvariant();
            var stagedDirectory = Path.Combine(parent, $"{PendingInstanceRenameDirectory.Prefix}{marker.OldName}-{suffix}");
            if (IsPathOccupied(stagedDirectory))
            {
                if (targetAttempt == MaxStagingTargets)
                    break;
                currentMarker = marker with { TransactionId = guidFactory().ToString("N") };
                await WriteOwnedMarkerAsync(sourceDirectory, currentMarker, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await MoveWithRetryAsync(sourceDirectory, stagedDirectory, marker.OwnerProof, cancellationToken)
                    .ConfigureAwait(false);
                logger.LogDebug(
                    "Instance rename entered committed staging. OldName={OldName} NewName={NewName} StagedDirectory={StagedDirectory}",
                    marker.OldName,
                    marker.NewName,
                    stagedDirectory);
                return new RenameStagingResult(stagedDirectory, currentMarker);
            }
            catch (Exception) when (!Directory.Exists(sourceDirectory) && Directory.Exists(stagedDirectory))
            {
                return new RenameStagingResult(stagedDirectory, currentMarker);
            }
            catch (IOException) when (Directory.Exists(sourceDirectory) && IsPathOccupied(stagedDirectory))
            {
                if (targetAttempt == MaxStagingTargets)
                    break;
                currentMarker = marker with { TransactionId = guidFactory().ToString("N") };
                await WriteOwnedMarkerAsync(sourceDirectory, currentMarker, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!rollbackMarkerOnFailure)
                    throw;
                throw;
            }
        }

        throw new IOException($"Unable to allocate a pending rename directory after {MaxStagingTargets} attempts.");
    }

    private async Task CompleteCommittedRenameAsync(
        string currentDirectory,
        PendingInstanceRenameMarker marker,
        CancellationToken cancellationToken)
    {
        await EnsureOwnedOrQuarantineAsync(currentDirectory, marker, cancellationToken).ConfigureAwait(false);
        var destinationDirectory = GetDestinationDirectory(currentDirectory, marker.NewName);

        if (PendingInstanceRenameDirectory.IsPending(currentDirectory))
        {
            if (IsPathOccupied(destinationDirectory))
            {
                await RollbackRenameAsync(currentDirectory, marker, CancellationToken.None).ConfigureAwait(false);
                throw new InstanceInstallNameConflictException(marker.NewName);
            }

            await RenameArtifactsAsync(currentDirectory, marker, cancellationToken).ConfigureAwait(false);
            if (IsPathOccupied(destinationDirectory))
            {
                await RollbackRenameAsync(currentDirectory, marker, CancellationToken.None).ConfigureAwait(false);
                throw new InstanceInstallNameConflictException(marker.NewName);
            }

            try
            {
                await MoveOwnedDirectoryAsync(
                        currentDirectory,
                        destinationDirectory,
                        marker.OwnerProof,
                        cancellationToken)
                    .ConfigureAwait(false);
                currentDirectory = destinationDirectory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(currentDirectory) && Directory.Exists(destinationDirectory))
                {
                    currentDirectory = destinationDirectory;
                }
                else if (Directory.Exists(currentDirectory) && IsPathOccupied(destinationDirectory))
                {
                    await RollbackRenameAsync(currentDirectory, marker, CancellationToken.None).ConfigureAwait(false);
                    throw new InstanceInstallNameConflictException(marker.NewName);
                }
                else
                {
                    throw;
                }
            }

            await EnsureOwnedOrQuarantineAsync(currentDirectory, marker, CancellationToken.None)
                .ConfigureAwait(false);
        }

        await instanceSettingsStore.CompleteRenameAsync(
            currentDirectory,
            marker.OwnerProof,
            marker.NewName,
            marker.NewIconSource,
            marker.UpdatedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await DeleteMarkerWithRetryAsync(
            PendingInstanceRenameDirectory.GetMarkerPath(currentDirectory),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Pending instance rename completed. OwnerProof={OwnerProof} OldName={OldName} NewName={NewName}",
            marker.OwnerProof,
            marker.OldName,
            marker.NewName);
    }

    private static async Task RenameArtifactsAsync(
        string directory,
        PendingInstanceRenameMarker marker,
        CancellationToken cancellationToken)
    {
        var originalJsonName = marker.OriginalJsonRelativePath ?? $"{marker.OldName}.json";
        RenameOptionalArtifact(directory, Path.ChangeExtension(originalJsonName, ".jar"), $"{marker.NewName}.jar");
        RenameOptionalDirectory(directory, $"{marker.OldName}-natives", $"{marker.NewName}-natives");

        var oldJsonPath = Path.Combine(directory, originalJsonName);
        var newJsonPath = Path.Combine(directory, $"{marker.NewName}.json");
        if (!string.Equals(oldJsonPath, newJsonPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldJsonPath) && File.Exists(newJsonPath))
            throw new IOException("Both old and new version JSON files exist.");
        if (!string.Equals(oldJsonPath, newJsonPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldJsonPath))
            File.Move(oldJsonPath, newJsonPath);
        if (!File.Exists(newJsonPath))
            throw new FileNotFoundException("Version JSON was not found while resuming rename.", newJsonPath);

        JsonObject json;
        await using (var stream = File.OpenRead(newJsonPath))
        {
            json = (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Version JSON is empty.")).AsObject();
        }
        var oldId = marker.OriginalJsonId ?? json["id"]?.GetValue<string>();
        json["id"] = marker.NewName;
        if (json["jar"] is JsonValue jarValue
            && (string.Equals(jarValue.ToString(), marker.OldName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(jarValue.ToString(), oldId, StringComparison.OrdinalIgnoreCase)))
        {
            json["jar"] = marker.NewName;
        }
        await AtomicJsonFileWriter.WriteAsync(newJsonPath, json, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task RollbackRenameAsync(
        string currentDirectory,
        PendingInstanceRenameMarker marker,
        CancellationToken cancellationToken)
    {
        await EnsureOwnedOrQuarantineAsync(currentDirectory, marker, cancellationToken).ConfigureAwait(false);
        await RestoreArtifactsAsync(currentDirectory, marker, cancellationToken).ConfigureAwait(false);

        if (PendingInstanceRenameDirectory.IsPending(currentDirectory))
        {
            var originalDirectory = GetDestinationDirectory(currentDirectory, marker.OldName);
            if (IsPathOccupied(originalDirectory))
                throw new IOException($"Rename rollback destination already exists: {originalDirectory}");

            await EnsureOwnedOrQuarantineAsync(currentDirectory, marker, cancellationToken).ConfigureAwait(false);
            await MoveWithRetryAsync(currentDirectory, originalDirectory, marker.OwnerProof, cancellationToken)
                .ConfigureAwait(false);
            currentDirectory = originalDirectory;
        }

        await DeleteOrQuarantineMarkerAsync(currentDirectory, cancellationToken).ConfigureAwait(false);
        logger.LogWarning(
            "Pending instance rename was rolled back because its destination is occupied. OwnerProof={OwnerProof} OldName={OldName} NewName={NewName}",
            marker.OwnerProof,
            marker.OldName,
            marker.NewName);
    }

    private static async Task RestoreArtifactsAsync(
        string directory,
        PendingInstanceRenameMarker marker,
        CancellationToken cancellationToken)
    {
        var originalJsonName = marker.OriginalJsonRelativePath ?? $"{marker.OldName}.json";
        RenameOptionalArtifact(directory, $"{marker.NewName}.jar", Path.ChangeExtension(originalJsonName, ".jar"));
        RenameOptionalDirectory(directory, $"{marker.NewName}-natives", $"{marker.OldName}-natives");

        var newJsonPath = Path.Combine(directory, $"{marker.NewName}.json");
        var oldJsonPath = Path.Combine(directory, originalJsonName);
        if (!string.Equals(newJsonPath, oldJsonPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newJsonPath) && File.Exists(oldJsonPath))
            throw new IOException("Both old and new version JSON files exist while rolling back rename.");
        if (!string.Equals(newJsonPath, oldJsonPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newJsonPath))
            File.Move(newJsonPath, oldJsonPath);
        if (!File.Exists(oldJsonPath))
            throw new FileNotFoundException("Version JSON was not found while rolling back rename.", oldJsonPath);

        JsonObject json;
        await using (var stream = File.OpenRead(oldJsonPath))
        {
            json = (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Version JSON is empty.")).AsObject();
        }
        if (marker.OriginalJsonRelativePath is not null)
        {
            if (marker.OriginalJsonId is null) json.Remove("id");
            else json["id"] = marker.OriginalJsonId;
            if (marker.OriginalJarReference is null) json.Remove("jar");
            else json["jar"] = marker.OriginalJarReference;
        }
        else
        {
            json["id"] = marker.OldName;
            if (json["jar"] is JsonValue jarValue
                && string.Equals(jarValue.ToString(), marker.NewName, StringComparison.OrdinalIgnoreCase))
            {
                json["jar"] = marker.OldName;
            }
        }
        await AtomicJsonFileWriter.WriteAsync(oldJsonPath, json, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
