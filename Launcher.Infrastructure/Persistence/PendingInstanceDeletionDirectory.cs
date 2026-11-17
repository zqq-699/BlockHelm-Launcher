/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;

namespace Launcher.Infrastructure.Persistence;

internal static class PendingInstanceDeletionDirectory
{
    public const string Prefix = ".bhl-delete-pending-";
    public const string MarkerFileName = ".bhl-delete-pending-.json";
    public const string AbortedMarkerFileName = ".bhl-delete-aborted.json";
    public static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static bool IsPending(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetMarkerPath(string directory) => Path.Combine(directory, MarkerFileName);

    public static bool TryReadValidMarker(string directory, out PendingInstanceDeletionMarker marker, bool readAbortedMarker = false)
    {
        marker = default!;
        try
        {
            var markerPath = readAbortedMarker ? Path.Combine(directory, AbortedMarkerFileName) : GetMarkerPath(directory);
            if (!File.Exists(markerPath))
                return false;
            var parsed = InstanceDirectoryOwnership.ReadLegacyMarker<PendingInstanceDeletionMarker>(
                File.ReadAllText(markerPath),
                MarkerJsonOptions);
            if (parsed is null
                || parsed.SchemaVersion is not (1 or 2 or 3)
                || string.IsNullOrWhiteSpace(parsed.VersionName)
                || (parsed.SchemaVersion >= 2 && string.IsNullOrWhiteSpace(parsed.OwnerProof))
                || !Guid.TryParseExact(parsed.TransactionId, "N", out _))
                return false;
            var expectedName = $"{Prefix}{parsed.VersionName}-{parsed.TransactionId[..8].ToLowerInvariant()}";
            if (!string.Equals(Path.GetFileName(directory), expectedName, StringComparison.OrdinalIgnoreCase))
                return false;
            marker = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}

internal sealed record PendingInstanceDeletionMarker(
    int SchemaVersion,
    string TransactionId,
    string VersionName,
    string? OwnerProof,
    DateTimeOffset CreatedAtUtc);
