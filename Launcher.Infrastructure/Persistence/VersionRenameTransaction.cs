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
    private const int MaxMoveAttempts = 5;
    private const int MaxStagingTargets = 3;
    private const int MaxMarkerDeleteAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly VersionDirectoryManager directoryManager;
    private readonly GameInstanceSettingsStore instanceSettingsStore;
    private readonly Func<string, string, CancellationToken, Task> moveDirectoryAsync;
    private readonly bool useIdentitySafeMove;
    private readonly Action<string, string>? beforeOwnedDirectoryMove;
    private readonly Func<Guid> guidFactory;
    private readonly Action<string> deleteMarker;
    private readonly Action<string, string> quarantineMarker;
    private readonly ILogger logger;

    public VersionRenameTransaction(
        VersionDirectoryManager directoryManager,
        GameInstanceSettingsStore instanceSettingsStore,
        ILogger logger,
        Func<string, string, CancellationToken, Task>? moveDirectoryAsync = null,
        Func<Guid>? guidFactory = null,
        Action<string>? deleteMarker = null,
        Action<string, string>? quarantineMarker = null,
        Action<string, string>? beforeOwnedDirectoryMove = null)
    {
        this.directoryManager = directoryManager;
        this.instanceSettingsStore = instanceSettingsStore;
        this.logger = logger;
        this.moveDirectoryAsync = moveDirectoryAsync ?? MoveDirectoryAsync;
        useIdentitySafeMove = moveDirectoryAsync is null;
        this.beforeOwnedDirectoryMove = beforeOwnedDirectoryMove;
        this.guidFactory = guidFactory ?? Guid.NewGuid;
        this.deleteMarker = deleteMarker ?? File.Delete;
        this.quarantineMarker = quarantineMarker ?? ((source, destination) => File.Move(source, destination, overwrite: true));
    }

    public async Task ExecuteAsync(
        string minecraftDirectory,
        GameInstance instance,
        string newVersionName,
        string? newIconSource,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var oldVersionName = string.IsNullOrWhiteSpace(instance.VersionName)
            ? instance.MinecraftVersion
            : instance.VersionName;
        if (string.Equals(oldVersionName, newVersionName, StringComparison.Ordinal))
            return;

        var sourceDirectory = directoryManager.GetVersionDirectory(minecraftDirectory, oldVersionName);
        var destinationDirectory = directoryManager.GetVersionDirectory(minecraftDirectory, newVersionName);
        if (!MinecraftDirectoryPath.Equals(sourceDirectory, instance.InstanceDirectory))
            throw new GameInstanceMutationConflictException(instance.InstanceDirectory, oldVersionName);
        PendingInstanceRenameMarker marker;
        var markerPath = PendingInstanceRenameDirectory.GetMarkerPath(sourceDirectory);
        using (InstanceDirectoryOwnership.AcquireReadLease(instance))
        {
            ValidateNewTransaction(sourceDirectory, destinationDirectory, oldVersionName);
            var ownerProof = InstanceDirectoryOwnership.EnsureCurrent(instance);
            var sourceJsonPath = InstanceVersionFiles.GetJsonPath(sourceDirectory, oldVersionName);
            var originalJson = JsonNode.Parse(await File.ReadAllTextAsync(sourceJsonPath, cancellationToken))!.AsObject();
            foreach (var pair in new[]
            {
                (sourceJsonPath, Path.Combine(sourceDirectory, newVersionName + ".json")),
                (Path.ChangeExtension(sourceJsonPath, ".jar"), Path.Combine(sourceDirectory, newVersionName + ".jar")),
                (Path.Combine(sourceDirectory, oldVersionName + "-natives"), Path.Combine(sourceDirectory, newVersionName + "-natives"))
            })
            {
                if (!string.Equals(pair.Item1, pair.Item2, StringComparison.OrdinalIgnoreCase) && IsPathOccupied(pair.Item2))
                    throw new IOException("A destination artifact already exists in the instance directory.");
            }
            await instanceSettingsStore.UpdateAsync(instance, minecraftDirectory, cancellationToken)
                .ConfigureAwait(false);
            marker = CreateMarker(ownerProof, oldVersionName, newVersionName, newIconSource, updatedAt) with
            {
                OriginalJsonRelativePath = Path.GetRelativePath(sourceDirectory, sourceJsonPath),
                OriginalJsonId = originalJson["id"]?.GetValue<string>(),
                OriginalJarReference = originalJson["jar"]?.GetValue<string>()
            };
            await WriteOwnedMarkerAsync(sourceDirectory, marker, cancellationToken).ConfigureAwait(false);
        }

        string stagedDirectory;
        try
        {
            var staging = await MoveSourceToStagingAsync(
                sourceDirectory, marker, cancellationToken, rollbackMarkerOnFailure: true).ConfigureAwait(false);
            stagedDirectory = staging.Directory;
            marker = staging.Marker;
        }
        catch (Exception moveException)
        {
            if (!InstanceDirectoryOwnership.Matches(sourceDirectory, marker.OwnerProof))
                throw;
            using var cleanupLease = WindowsDirectoryHandleMover.AcquireReadLease(sourceDirectory);
            if (!InstanceDirectoryOwnership.Matches(sourceDirectory, marker.OwnerProof))
                throw;
            try
            {
                await DeleteMarkerWithRetryAsync(markerPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                var abortedMarkerPath = Path.Combine(sourceDirectory, PendingInstanceRenameDirectory.AbortedMarkerFileName);
                try
                {
                    File.Move(markerPath, abortedMarkerPath, overwrite: true);
                    logger.LogError(
                        cleanupException,
                        "Pending rename marker could not be deleted and was quarantined after staging failed. MarkerPath={MarkerPath}",
                        markerPath);
                }
                catch (Exception quarantineException)
                {
                    throw new AggregateException(
                        "Instance rename staging and marker rollback both failed.",
                        moveException,
                        cleanupException,
                        quarantineException);
                }
            }
            throw;
        }

        await CompleteCommittedRenameAsync(stagedDirectory, marker, CancellationToken.None).ConfigureAwait(false);
    }
}
