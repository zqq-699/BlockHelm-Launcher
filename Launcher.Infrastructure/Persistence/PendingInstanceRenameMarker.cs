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

internal static class PendingInstanceRenameDirectory
{
    public const string Prefix = ".bhl-rename-pending-";
    public const string MarkerFileName = ".bhl-rename-pending.json";
    public const string AbortedMarkerFileName = ".bhl-rename-aborted.json";

    public static bool IsPending(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string GetMarkerPath(string directory) => Path.Combine(directory, MarkerFileName);
}

internal sealed record PendingInstanceRenameMarker
{
    public int SchemaVersion { get; init; } = 2;
    public string TransactionId { get; init; } = string.Empty;
    public string OwnerProof { get; init; } = string.Empty;
    public string OldName { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;
    public string? NewIconSource { get; init; }
    public string? OriginalJsonRelativePath { get; init; }
    public string? OriginalJsonId { get; init; }
    public string? OriginalJarReference { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

internal enum PendingInstanceRenameMarkerStatus
{
    Missing,
    Valid,
    Invalid,
    Unreadable
}

internal readonly record struct PendingInstanceRenameMarkerReadResult(
    PendingInstanceRenameMarkerStatus Status,
    PendingInstanceRenameMarker? Marker = null,
    Exception? Exception = null);

internal readonly record struct RenameStagingResult(
    string Directory,
    PendingInstanceRenameMarker Marker);

internal static class PendingInstanceRenameMarkerFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static PendingInstanceRenameMarkerReadResult Read(string directory)
    {
        var markerPath = PendingInstanceRenameDirectory.GetMarkerPath(directory);
        var presence = ProbePresence(directory);
        if (presence is not null)
            return presence.Value;
        try
        {
            using var stream = OpenRead(markerPath, useAsync: false);
            var marker = InstanceDirectoryOwnership.ReadLegacyMarker<PendingInstanceRenameMarker>(new StreamReader(stream).ReadToEnd(), JsonOptions);
            Validate(directory, marker);
            return new(PendingInstanceRenameMarkerStatus.Valid, marker);
        }
        catch (FileNotFoundException)
        {
            return new(PendingInstanceRenameMarkerStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PendingInstanceRenameMarkerStatus.Missing);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new(PendingInstanceRenameMarkerStatus.Invalid, Exception: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(PendingInstanceRenameMarkerStatus.Unreadable, Exception: exception);
        }
    }

    public static async Task<PendingInstanceRenameMarkerReadResult> ReadAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var markerPath = PendingInstanceRenameDirectory.GetMarkerPath(directory);
        var presence = ProbePresence(directory);
        if (presence is not null)
            return presence.Value;
        try
        {
            await using var stream = OpenRead(markerPath, useAsync: true);
            var text = await new StreamReader(stream).ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var marker = InstanceDirectoryOwnership.ReadLegacyMarker<PendingInstanceRenameMarker>(text, JsonOptions);
            Validate(directory, marker);
            return new(PendingInstanceRenameMarkerStatus.Valid, marker);
        }
        catch (FileNotFoundException)
        {
            return new(PendingInstanceRenameMarkerStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PendingInstanceRenameMarkerStatus.Missing);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new(PendingInstanceRenameMarkerStatus.Invalid, Exception: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(PendingInstanceRenameMarkerStatus.Unreadable, Exception: exception);
        }
    }

    private static FileStream OpenRead(string markerPath, bool useAsync) => new(
        markerPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        bufferSize: 4096,
        useAsync);

    private static PendingInstanceRenameMarkerReadResult? ProbePresence(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(
                    directory,
                    PendingInstanceRenameDirectory.MarkerFileName,
                    SearchOption.TopDirectoryOnly)
                .Any()
                ? null
                : new(PendingInstanceRenameMarkerStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PendingInstanceRenameMarkerStatus.Missing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(PendingInstanceRenameMarkerStatus.Unreadable, Exception: exception);
        }
    }

    private static void Validate(string directory, PendingInstanceRenameMarker? marker)
    {
        if (marker is null
            || marker.SchemaVersion is not (1 or 2)
            || string.IsNullOrWhiteSpace(marker.TransactionId)
            || marker.TransactionId.Length < 8
            || string.IsNullOrWhiteSpace(marker.OwnerProof)
            || !VersionDirectoryName.IsSafeDirectoryName(marker.OldName)
            || !VersionDirectoryName.IsSafeDirectoryName(marker.NewName))
        {
            throw new InvalidDataException("Pending instance rename marker is invalid.");
        }

        var directoryName = Path.GetFileName(
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (marker.OriginalJsonRelativePath is { } originalJson)
        {
            var resolved = Path.GetFullPath(Path.Combine(directory, originalJson));
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            if (Path.IsPathRooted(originalJson) || !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(resolved), ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pending rename artifact path is outside its instance.");
        }
        if (PendingInstanceRenameDirectory.IsPending(directory))
        {
            var expectedName = $"{PendingInstanceRenameDirectory.Prefix}{marker.OldName}-{marker.TransactionId[..8].ToLowerInvariant()}";
            if (!string.Equals(directoryName, expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pending instance rename marker does not match its staging directory.");
            return;
        }

        if (!string.Equals(directoryName, marker.OldName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(directoryName, marker.NewName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Pending instance rename marker does not match its directory.");
        }
    }
}
