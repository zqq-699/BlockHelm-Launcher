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

internal static class PendingInstanceInstallDirectory
{
    public const string Prefix = ".bhl-install-pending-";
    private const string TransactionDirectoryName = "transactions";
    private const string InstallDirectoryName = "install";
    private const string PreparationDirectoryName = "preparing";
    public const string MarkerFileName = ".bhl-install-pending.json";
    public const string PendingLockFileName = ".bhl-install-active.lock";
    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool IsPending(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string GetPreparationRoot(string minecraftDirectory) =>
        Path.Combine(
            Path.GetFullPath(minecraftDirectory),
            LauncherApplicationIdentity.StorageDirectoryName,
            TransactionDirectoryName,
            InstallDirectoryName,
            PreparationDirectoryName);

    public static bool TryReadValidPreparationMarker(
        string preparationRoot,
        string preparationDirectory,
        out PendingInstanceInstallMarker marker)
    {
        marker = default!;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preparationRoot));
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preparationDirectory));
            if (!string.Equals(
                    Path.GetDirectoryName(normalizedDirectory),
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if ((File.GetAttributes(normalizedDirectory) & FileAttributes.ReparsePoint) != 0)
                return false;
            var directoryTransactionId = Path.GetFileName(normalizedDirectory);
            return Guid.TryParseExact(directoryTransactionId, "N", out _)
                   && TryReadStructurallyValidMarker(normalizedDirectory, out marker, out _)
                   && string.Equals(marker.TransactionId, directoryTransactionId, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            marker = default!;
            return false;
        }
    }

    public static bool IsLogicalNameReserved(string versionsDirectory, string logicalVersionName)
    {
        if (!Directory.Exists(versionsDirectory))
            return false;
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory).Where(IsPending))
        {
            if (TryGetLogicalName(directory, out var reservedName)
                && string.Equals(reservedName, logicalVersionName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool TryGetLogicalName(string pendingDirectory, out string logicalVersionName)
    {
        logicalVersionName = string.Empty;
        if (!TryReadValidPendingMarker(pendingDirectory, out var marker))
            return false;
        logicalVersionName = marker.LogicalVersionName;
        return true;
    }

    public static bool TryReadValidPendingMarker(
        string pendingDirectory,
        out PendingInstanceInstallMarker marker)
    {
        if (!TryReadStructurallyValidMarker(pendingDirectory, out marker, out _))
            return false;
        var expectedName = $"{Prefix}{marker.LogicalVersionName}-{marker.TransactionId[..8].ToLowerInvariant()}";
        return string.Equals(Path.GetFileName(pendingDirectory), expectedName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReadValidCommittedMarker(
        string versionsDirectory,
        string committedDirectory,
        out PendingInstanceInstallMarker marker,
        out string failureReason)
    {
        marker = default!;
        failureReason = string.Empty;
        try
        {
            var normalizedVersionsDirectory = Path.GetFullPath(versionsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCommittedDirectory = Path.GetFullPath(committedDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(
                    Path.GetDirectoryName(normalizedCommittedDirectory),
                    normalizedVersionsDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "not a direct child of the versions directory";
                return false;
            }
            if ((File.GetAttributes(normalizedCommittedDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "committed instance directory is a reparse point";
                return false;
            }
            if (IsPending(normalizedCommittedDirectory)
                || PendingInstanceDeletionDirectory.IsPending(normalizedCommittedDirectory)
                || PendingInstanceRenameDirectory.IsPending(normalizedCommittedDirectory))
            {
                failureReason = "not an ordinary committed instance directory";
                return false;
            }
            if (!TryReadStructurallyValidMarker(normalizedCommittedDirectory, out marker, out failureReason))
                return false;

            var directoryName = Path.GetFileName(normalizedCommittedDirectory);
            if (!string.Equals(directoryName, marker.LogicalVersionName, StringComparison.Ordinal))
            {
                failureReason = "directory name does not match the logical version name";
                return false;
            }

            var versionJsonPath = Path.Combine(normalizedCommittedDirectory, $"{marker.LogicalVersionName}.json");
            if (!File.Exists(versionJsonPath))
            {
                failureReason = "version JSON is missing";
                return false;
            }
            if ((File.GetAttributes(versionJsonPath) & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "version JSON is a reparse point";
                return false;
            }
            using (var versionJson = JsonDocument.Parse(File.ReadAllText(versionJsonPath)))
            {
                if (!versionJson.RootElement.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !string.Equals(id.GetString(), marker.LogicalVersionName, StringComparison.Ordinal))
                {
                    failureReason = "version JSON id does not match the logical version name";
                    return false;
                }
            }

            var instanceSettingsPath = Path.Combine(
                normalizedCommittedDirectory,
                LauncherApplicationIdentity.StorageDirectoryName,
                "instance-settings.json");
            if (!File.Exists(instanceSettingsPath))
            {
                failureReason = "instance settings are missing";
                return false;
            }
            if ((File.GetAttributes(instanceSettingsPath) & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "instance settings are a reparse point";
                return false;
            }
            var instance = JsonSerializer.Deserialize<GameInstance>(
                File.ReadAllText(instanceSettingsPath),
                MarkerJsonOptions);
            if (instance is null)
            {
                failureReason = "instance settings are empty";
                return false;
            }
            if (!GameInstanceSettingsStore.HasIdentity(normalizedCommittedDirectory, marker.OwnerProof))
            {
                failureReason = "instance settings id does not match the install marker";
                return false;
            }
            if (!string.Equals(instance.VersionName, marker.LogicalVersionName, StringComparison.Ordinal))
            {
                failureReason = "instance settings version name does not match the logical version name";
                return false;
            }
            if (string.IsNullOrWhiteSpace(instance.InstanceDirectory)
                || !string.Equals(
                    Path.GetFullPath(instance.InstanceDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalizedCommittedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "instance settings directory does not match the committed directory";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            failureReason = $"validation failed with {exception.GetType().Name}";
            marker = default!;
            return false;
        }
    }

    private static bool TryReadStructurallyValidMarker(
        string directory,
        out PendingInstanceInstallMarker marker,
        out string failureReason)
    {
        marker = default!;
        failureReason = string.Empty;
        try
        {
            var markerPath = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(markerPath))
            {
                failureReason = "transaction marker is missing";
                return false;
            }
            var parsed = InstanceDirectoryOwnership.ReadLegacyMarker<PendingInstanceInstallMarker>(
                File.ReadAllText(markerPath),
                MarkerJsonOptions);
            if (parsed is null)
            {
                failureReason = "transaction marker is empty";
                return false;
            }
            if (parsed.SchemaVersion is not (1 or 2))
            {
                failureReason = "unsupported schema version";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.OwnerProof))
            {
                failureReason = "instance id is missing";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.LogicalVersionName))
            {
                failureReason = "logical version name is missing";
                return false;
            }
            if (!Guid.TryParseExact(parsed.TransactionId, "N", out _))
            {
                failureReason = "transaction id is invalid";
                return false;
            }
            marker = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failureReason = $"marker could not be read ({exception.GetType().Name})";
            return false;
        }
    }
}

internal sealed record PendingInstanceInstallMarker(
    int SchemaVersion,
    string TransactionId,
    string OwnerProof,
    string LogicalVersionName,
    string InstallKind,
    bool InitializeDefaultIfEmpty,
    DateTimeOffset CreatedAtUtc);
