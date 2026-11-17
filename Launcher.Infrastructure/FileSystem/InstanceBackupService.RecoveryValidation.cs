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

using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class InstanceBackupService
{
    private static async Task ValidateAndPrepareRestoredInstanceAsync(
        string restoredDirectory,
        string expectedOwnerProof,
        string expectedVersionName,
        string finalInstanceDirectory,
        CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(restoredDirectory);
        var settingsPath = GetInstanceSettingsPath(restoredDirectory);
        var restoredInstance = File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<GameInstance>(await File.ReadAllTextAsync(settingsPath, cancellationToken), JsonOptions)
            : new GameInstance();
        restoredInstance ??= new GameInstance();
        var versionJsonPath = InstanceVersionFiles.TryResolveVersionJsonPath(restoredDirectory, Path.GetFileName(restoredDirectory))
            ?? throw new InvalidDataException("Backup does not contain an unambiguous version JSON.");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken))!.AsObject();
        var oldId = json["id"]?.GetValue<string>();
        if (json["jar"]?.GetValue<string>() is { } jar && string.Equals(jar, oldId, StringComparison.OrdinalIgnoreCase))
            json["jar"] = expectedVersionName;
        var targetJson = Path.Combine(restoredDirectory, $"{expectedVersionName}.json");
        var sourceJar = Path.ChangeExtension(versionJsonPath, ".jar");
        var targetJar = Path.Combine(restoredDirectory, $"{expectedVersionName}.jar");
        if (!string.Equals(sourceJar, targetJar, StringComparison.OrdinalIgnoreCase) && File.Exists(sourceJar))
            File.Move(sourceJar, targetJar, overwrite: true);
        json["id"] = expectedVersionName;
        await AtomicJsonFileWriter.WriteAsync(targetJson, json, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(versionJsonPath, targetJson, StringComparison.OrdinalIgnoreCase)) File.Delete(versionJsonPath);
        restoredInstance.IconSource = InstanceIconPaths.ToStorage(restoredInstance.IconSource, restoredInstance.InstanceDirectory);
        restoredInstance.Name = expectedVersionName;
        restoredInstance.VersionName = expectedVersionName;
        restoredInstance.InstanceDirectory = finalInstanceDirectory;
        await AtomicJsonFileWriter.WriteAsync(settingsPath, restoredInstance, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteRestoreOwnerAsync(
        string restoredDirectory,
        string transactionId,
        string ownerProof,
        CancellationToken cancellationToken)
    {
        var ownerPath = GetRestoreOwnerPath(restoredDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(ownerPath)!);
        await AtomicJsonFileWriter.WriteAsync(
                ownerPath,
                new RestoreOwnerMarker(2, transactionId, ownerProof),
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task EnsureRestoredInstanceOwnershipAsync(
        string restoredDirectory,
        RestoreTransactionMarker marker,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GameInstanceSettingsStore.HasIdentity(restoredDirectory, marker.OwnerProof)
            || !HasRestoreOwner(restoredDirectory, marker.TransactionId, marker.OwnerProof))
        {
            throw new InstanceBackupException(
                InstanceBackupFailureReason.InstanceChanged,
                "The restored directory identity changed before publication.");
        }
        return Task.CompletedTask;
    }

    private static bool TryReadValidRestoreMarker(
        string stagingDirectory,
        out RestoreTransactionMarker marker)
    {
        marker = default!;
        try
        {
            var markerPath = Path.Combine(stagingDirectory, RestoreMarkerFileName);
            if (!File.Exists(markerPath)
                || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            var parsed = InstanceDirectoryOwnership.ReadLegacyMarker<RestoreTransactionMarker>(File.ReadAllText(markerPath), JsonOptions);
            if (parsed is null
                || parsed.SchemaVersion is not (1 or 2)
                || !Guid.TryParseExact(parsed.TransactionId, "N", out _)
                || string.IsNullOrWhiteSpace(parsed.OwnerProof)
                || !VersionDirectoryName.IsSafeDirectoryName(parsed.VersionName)
                || parsed.State is not (RestoreStateExtracting or RestoreStatePrepared or RestoreStateCommitted))
            {
                return false;
            }
            if (!string.Equals(
                    Path.GetFileName(stagingDirectory),
                    $"{RestoreDirectoryPrefix}{parsed.TransactionId}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            marker = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasRestoreOwner(string directory, string transactionId, string ownerProof)
    {
        try
        {
            var path = GetRestoreOwnerPath(directory);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            var owner = InstanceDirectoryOwnership.ReadLegacyMarker<RestoreOwnerMarker>(File.ReadAllText(path), JsonOptions);
            return owner is { SchemaVersion: 1 or 2 }
                   && string.Equals(owner.TransactionId, transactionId, StringComparison.Ordinal)
                   && string.Equals(owner.OwnerProof, ownerProof, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return false;
        }
    }

    private static void DeleteRestoreOwnerIfOwned(string directory, string transactionId)
    {
        if (!Directory.Exists(directory))
            return;
        var ownerPath = GetRestoreOwnerPath(directory);
        try
        {
            if (!File.Exists(ownerPath))
                return;
            var owner = InstanceDirectoryOwnership.ReadLegacyMarker<RestoreOwnerMarker>(File.ReadAllText(ownerPath), JsonOptions);
            if (owner is not null && string.Equals(owner.TransactionId, transactionId, StringComparison.Ordinal))
                File.Delete(ownerPath);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
        }
    }

    private static void DeleteOwnedRestorePrevious(string previousDirectory, string expectedOwnerProof)
    {
        if (!Directory.Exists(previousDirectory))
            return;
        if (!GameInstanceSettingsStore.HasIdentity(previousDirectory, expectedOwnerProof))
            throw new InvalidDataException("Restore previous directory identity could not be proven.");
        DeleteTreeWithoutFollowingReparsePoints(previousDirectory);
    }

    private static void DeleteOwnedRestoredDirectory(
        string restoredDirectory,
        RestoreTransactionMarker marker)
    {
        if (!Directory.Exists(restoredDirectory))
            return;
        if (!GameInstanceSettingsStore.HasIdentity(restoredDirectory, marker.OwnerProof)
            || !HasRestoreOwner(restoredDirectory, marker.TransactionId, marker.OwnerProof))
        {
            throw new InvalidDataException("Restore candidate directory identity could not be proven.");
        }
        DeleteTreeWithoutFollowingReparsePoints(restoredDirectory);
    }

    private void QuarantineRestoreMarker(string stagingDirectory, string reason)
    {
        var markerPath = Path.Combine(stagingDirectory, RestoreMarkerFileName);
        var conflictPath = Path.Combine(stagingDirectory, RestoreConflictMarkerFileName);
        try
        {
            if (File.Exists(markerPath))
                File.Move(markerPath, conflictPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Failed to quarantine conflicted restore marker. Directory={Directory}", stagingDirectory);
        }
        logger.LogError(
            "Restore recovery stopped to preserve data because ownership was ambiguous. Directory={Directory} Reason={Reason}",
            stagingDirectory,
            reason);
    }
}
