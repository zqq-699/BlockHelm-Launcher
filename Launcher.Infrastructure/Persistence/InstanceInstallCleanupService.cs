/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Persistence;

public sealed class InstanceInstallCleanupService(
    ISettingsService settingsService,
    ILogger<InstanceInstallCleanupService>? logger = null) : IInstanceInstallCleanupService
{
    private readonly ILogger logger = logger ?? NullLogger<InstanceInstallCleanupService>.Instance;

    public async Task CleanupPendingAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var versionsDirectory = Path.GetFullPath(Path.Combine(settings.MinecraftDirectory, "versions"));
        var preparationRoot = PendingInstanceInstallDirectory.GetPreparationRoot(settings.MinecraftDirectory);
        if (!Directory.Exists(versionsDirectory) && !Directory.Exists(preparationRoot))
            return;

        await using var coordinationLock = await CrossProcessVersionLock.AcquireAsync(
            CrossProcessVersionLock.GetInstallCoordinationPath(settings.MinecraftDirectory),
            progress: null,
            cancellationToken).ConfigureAwait(false);
        await using var mutationLock = await CrossProcessVersionLock.AcquireAsync(
            CrossProcessVersionLock.GetMutationPath(settings.MinecraftDirectory),
            progress: null,
            cancellationToken).ConfigureAwait(false);
        CrossProcessVersionLock.DeleteLegacyVersionDirectoryLocks(versionsDirectory);
        CrossProcessVersionLock.DeleteLegacyLauncherLocks(settings.MinecraftDirectory);

        CleanupInstallPreparationDirectories(settings.MinecraftDirectory, cancellationToken);
        if (!Directory.Exists(versionsDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(directory);
            await CleanupVersionDirectoryAsync(
                settings,
                versionsDirectory,
                normalized).ConfigureAwait(false);
        }
    }

    private async Task CleanupVersionDirectoryAsync(
        LauncherSettings settings,
        string versionsDirectory,
        string normalizedDirectory)
    {
        if (!string.Equals(
                Path.GetDirectoryName(normalizedDirectory),
                versionsDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (PendingInstanceInstallDirectory.IsPending(normalizedDirectory))
        {
            await CleanupPendingInstallDirectoryAsync(normalizedDirectory).ConfigureAwait(false);
            return;
        }

        await CleanupCommittedInstallMarkerAsync(
            settings,
            versionsDirectory,
            normalizedDirectory).ConfigureAwait(false);
    }

    private async Task CleanupPendingInstallDirectoryAsync(string pendingDirectory)
    {
        if (!PendingInstanceInstallDirectory.TryReadValidPendingMarker(pendingDirectory, out var marker)
            || !InstanceDirectoryOwnership.Matches(pendingDirectory, marker.OwnerProof))
        {
            logger.LogWarning(
                "Install staging directory was preserved because its transaction marker is missing or invalid. Directory={Directory}",
                pendingDirectory);
            return;
        }

        var activeLock = CrossProcessVersionLock.TryAcquire(
            Path.Combine(pendingDirectory, PendingInstanceInstallDirectory.PendingLockFileName));
        if (activeLock is null)
        {
            logger.LogDebug(
                "Skipping active instance installation staging directory. Directory={Directory}",
                pendingDirectory);
            return;
        }

        await activeLock.DisposeAsync().ConfigureAwait(false);
        InstanceInstallTransactionService.TryDeleteTree(pendingDirectory, logger);
    }

    private async Task CleanupCommittedInstallMarkerAsync(
        LauncherSettings settings,
        string versionsDirectory,
        string committedDirectory)
    {
        var markerPath = Path.Combine(committedDirectory, PendingInstanceInstallDirectory.MarkerFileName);
        if (!File.Exists(markerPath))
            return;

        if (!PendingInstanceInstallDirectory.TryReadValidCommittedMarker(
                versionsDirectory,
                committedDirectory,
                out var marker,
                out var failureReason))
        {
            logger.LogWarning(
                "Committed install marker was preserved because validation failed. MarkerPath={MarkerPath} Reason={Reason}",
                markerPath,
                failureReason);
            return;
        }

        try
        {
            if (!await CanRemoveCommittedMarkerAsync(settings, marker, markerPath).ConfigureAwait(false))
                return;

            File.Delete(Path.Combine(committedDirectory, PendingInstanceInstallDirectory.PendingLockFileName));
            File.Delete(markerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(
                exception,
                "Failed to delete committed install marker. MarkerPath={MarkerPath}",
                markerPath);
        }
    }

    private async Task<bool> CanRemoveCommittedMarkerAsync(
        LauncherSettings settings,
        PendingInstanceInstallMarker marker,
        string markerPath)
    {
        if (!marker.InitializeDefaultIfEmpty)
            return true;

        if (settingsService is JsonSettingsService jsonSettings)
            await jsonSettings.MigrateInstanceSelectionAsync(settings.MinecraftDirectory, CancellationToken.None).ConfigureAwait(false);
        var rootMatches = false;
        await settingsService.UpdateAsync(
                latestSettings =>
                {
                    rootMatches = PathsEqual(
                        latestSettings.MinecraftDirectory,
                        settings.MinecraftDirectory);
                    if (rootMatches && string.IsNullOrWhiteSpace(latestSettings.DefaultInstanceDirectory))
                        latestSettings.DefaultInstanceDirectory = Path.GetDirectoryName(markerPath);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (rootMatches)
            return true;

        logger.LogWarning(
            "Committed install marker was retained because the active Minecraft directory changed. MarkerPath={MarkerPath} InstallMinecraftDirectory={InstallMinecraftDirectory}",
            markerPath,
            settings.MinecraftDirectory);
        return false;
    }

    private void CleanupInstallPreparationDirectories(
        string minecraftDirectory,
        CancellationToken cancellationToken)
    {
        var preparationRoot = PendingInstanceInstallDirectory.GetPreparationRoot(minecraftDirectory);
        try
        {
            InstanceInstallTransactionService.EnsureOrdinaryPathBelowRoot(
                minecraftDirectory,
                preparationRoot,
                "Install preparation root");
            if (!Directory.Exists(preparationRoot))
                return;

            foreach (var directory in Directory.EnumerateDirectories(preparationRoot).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PendingInstanceInstallDirectory.TryReadValidPreparationMarker(
                        preparationRoot,
                        directory,
                        out var marker))
                {
                    logger.LogWarning(
                        "Install preparation directory was preserved because ownership could not be proven. Directory={Directory}",
                        directory);
                    continue;
                }

                if (!InstanceDirectoryOwnership.Matches(directory, marker.OwnerProof))
                {
                    logger.LogWarning("Install preparation was preserved because operation ownership changed. Directory={Directory}", directory);
                    continue;
                }
                InstanceInstallTransactionService.TryDeleteTree(directory, logger);
                logger.LogDebug(
                    "Cleaned interrupted instance install preparation. TransactionId={TransactionId} Directory={Directory}",
                    marker.TransactionId,
                    directory);
            }

            try
            {
                Directory.Delete(preparationRoot, recursive: false);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or DirectoryNotFoundException)
            {
                logger.LogDebug(
                    exception,
                    "Install preparation root was retained because it is not empty or could not be removed. Directory={Directory}",
                    preparationRoot);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "Install preparation cleanup was skipped because its path is unsafe. Directory={Directory}",
                preparationRoot);
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }
}
