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

using System.IO;
using System.IO.Compression;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Modpacks;

internal static class ModpackArchiveUtility
{
    public const long MaxManifestBytes = 8L * 1024 * 1024;
    public const long MaxEmbeddedModpackBytes = 256L * 1024 * 1024;
    public const long MaxOverrideTotalBytes = 20L * 1024 * 1024 * 1024;
    public const long OverrideFreeSpaceReserveBytes = 1024L * 1024 * 1024;

    public static string NormalizeArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        normalized = normalized.TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("/", segments);
    }

    public static string GetValidatedTargetPath(string targetDirectory, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(targetDirectory);
        var relativePathForDisk = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePathForDisk));
        var comparisonRoot = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(comparisonRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                "Archive entry resolved outside the target directory.");
        }

        return fullPath;
    }

    public static async Task ExtractZipEntryAsync(
        ZipArchiveEntry entry,
        string targetDirectory,
        string relativePath,
        ZipExtractionBudget extractionBudget,
        CancellationToken cancellationToken)
    {
        var targetPath = GetValidatedTargetPath(targetDirectory, relativePath);
        var parentDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        cancellationToken.ThrowIfCancellationRequested();
        await using var source = entry.Open();
        await using var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await CopyToAsync(
            source,
            destination,
            MaxOverrideTotalBytes,
            cancellationToken,
            extractionBudget).ConfigureAwait(false);
    }

    public static void EnsureSufficientOverrideDiskSpace(string targetDirectory, long requiredBytes)
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(targetDirectory));
        if (string.IsNullOrWhiteSpace(driveRoot))
            return;

        var availableBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
        EnsureSufficientOverrideDiskSpace(requiredBytes, availableBytes);
    }

    internal static void EnsureSufficientOverrideDiskSpace(long requiredBytes, long availableBytes)
    {
        var usableBytes = Math.Max(0, availableBytes - OverrideFreeSpaceReserveBytes);
        if (requiredBytes <= usableBytes)
            return;

        throw new ModpackImportException(
            ModpackImportFailureReason.InsufficientDiskSpace,
            "Insufficient disk space for modpack overrides. " +
            $"RequiredBytes={requiredBytes} AvailableBytes={availableBytes} " +
            $"ReserveBytes={OverrideFreeSpaceReserveBytes}");
    }

    public static async Task<MemoryStream> CopyZipEntryToMemoryAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maxBytes)
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                $"Archive entry is too large: {entry.FullName}");
        }

        var capacity = entry.Length is > 0 and <= int.MaxValue ? (int)entry.Length : 0;
        var destination = new MemoryStream(capacity);
        await using var source = entry.Open();
        await CopyToAsync(source, destination, maxBytes, cancellationToken).ConfigureAwait(false);
        destination.Position = 0;
        return destination;
    }

    private static async Task CopyToAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken,
        ZipExtractionBudget? extractionBudget = null)
    {
        var buffer = new byte[81920];
        long copiedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                return;

            copiedBytes += read;
            extractionBudget?.Reserve(read);
            if (copiedBytes > maxBytes)
            {
                throw new ModpackImportException(
                    ModpackImportFailureReason.InvalidManifest,
                    $"Archive entry exceeded the allowed size while extracting. " +
                    $"CopiedBytes={copiedBytes} LimitBytes={maxBytes}");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool IsSupportedHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    public static string RemovePrefix(string path, string prefix)
    {
        if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return path[(prefix.Length + 1)..];
    }
}

internal sealed class ZipExtractionBudget(long maxTotalBytes)
{
    private long extractedBytes;

    public long ReservedBytes => Interlocked.Read(ref extractedBytes);

    public void Reserve(long bytes)
    {
        if (bytes < 0)
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                "Archive entry has an invalid size.");
        }

        while (true)
        {
            var current = Interlocked.Read(ref extractedBytes);
            if (bytes > maxTotalBytes - current)
            {
                throw new ModpackImportException(
                    ModpackImportFailureReason.ArchiveTooLarge,
                    "Archive contents exceed the allowed total size. " +
                    $"PreviousBytes={current} AdditionalBytes={bytes} LimitBytes={maxTotalBytes}");
            }
            if (Interlocked.CompareExchange(ref extractedBytes, current + bytes, current) == current)
                return;
        }
    }
}
