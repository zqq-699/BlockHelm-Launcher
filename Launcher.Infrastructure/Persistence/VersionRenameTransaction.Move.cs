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
    private static void RenameOptionalArtifact(string directory, string oldName, string newName)
    {
        var oldPath = Path.Combine(directory, oldName);
        var newPath = Path.Combine(directory, newName);
        if (string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
            return;
        if (File.Exists(oldPath) && File.Exists(newPath))
            throw new IOException($"Both old and new rename artifacts exist: {oldName}, {newName}");
        if (File.Exists(oldPath))
            File.Move(oldPath, newPath);
    }

    private static void RenameOptionalDirectory(string directory, string oldName, string newName)
    {
        var oldPath = Path.Combine(directory, oldName);
        var newPath = Path.Combine(directory, newName);
        if (string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
            return;
        if (Directory.Exists(oldPath) && Directory.Exists(newPath))
            throw new IOException($"Both old and new rename directories exist: {oldName}, {newName}");
        if (Directory.Exists(oldPath))
            Directory.Move(oldPath, newPath);
    }

    private async Task MoveWithRetryAsync(
        string source,
        string destination,
        string expectedOwnerProof,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await MoveOwnedDirectoryAsync(source, destination, expectedOwnerProof, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                attempt < MaxMoveAttempts
                && exception is IOException or UnauthorizedAccessException
                && !IsPathOccupied(destination))
            {
                logger.LogWarning(exception, "Version directory move will be retried. Attempt={Attempt} MaxAttempts={MaxAttempts}", attempt, MaxMoveAttempts);
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MoveOwnedDirectoryAsync(
        string source,
        string destination,
        string expectedOwnerProof,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!useIdentitySafeMove)
        {
            await moveDirectoryAsync(source, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            WindowsDirectoryHandleMover.MoveOwnedDirectory(
                source,
                destination,
                () => GameInstanceSettingsStore.HasIdentity(source, expectedOwnerProof),
                beforeOwnedDirectoryMove is null
                    ? null
                    : () => beforeOwnedDirectoryMove(source, destination));
        }
        catch (InvalidOperationException)
        {
            throw new GameInstanceMutationConflictException(
                expectedOwnerProof,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
        }
    }

    private static Task MoveDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(source, destination);
        return Task.CompletedTask;
    }

    private string GetDestinationDirectory(string currentDirectory, string versionName)
    {
        return directoryManager.GetVersionDirectory(
            Path.GetDirectoryName(Path.GetDirectoryName(currentDirectory)!)!,
            versionName);
    }

    private static bool IsPathOccupied(string path) => Directory.Exists(path) || File.Exists(path);

}
