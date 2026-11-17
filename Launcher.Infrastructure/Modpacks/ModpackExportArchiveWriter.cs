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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Infrastructure.Modpacks;

internal sealed class ModpackExportArchiveWriter
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> WriteAsync<TManifest>(
        string outputArchivePath,
        string manifestEntryName,
        TManifest manifest,
        IReadOnlyList<ModpackExportArchiveFile> overrideFiles,
        CancellationToken cancellationToken,
        Func<bool>? isCurrent = null)
    {
        var outputPath = Path.GetFullPath(outputArchivePath);
        var tempPath = CreateTempArchivePath(outputPath);
        try
        {
            EnsureCurrent(isCurrent);
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await WriteArchiveAsync(
                    tempPath,
                    manifestEntryName,
                    manifest,
                    overrideFiles,
                    cancellationToken,
                    isCurrent)
                .ConfigureAwait(false);
            EnsureCurrent(isCurrent);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPath, outputPath, overwrite: true);
            return outputPath;
        }
        catch
        {
            if (isCurrent?.Invoke() is not false) TryDeleteFile(tempPath);
            throw;
        }
    }

    private static async Task WriteArchiveAsync<TManifest>(
        string archivePath,
        string manifestEntryName,
        TManifest manifest,
        IReadOnlyList<ModpackExportArchiveFile> overrideFiles,
        CancellationToken cancellationToken,
        Func<bool>? isCurrent)
    {
        await using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        var manifestEntry = archive.CreateEntry(manifestEntryName, CompressionLevel.Optimal);
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                    manifestStream,
                    manifest,
                    ManifestJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in overrideFiles)
        {
            EnsureCurrent(isCurrent);
            await AddOverrideFileAsync(archive, file, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureCurrent(Func<bool>? isCurrent)
    {
        if (isCurrent?.Invoke() is false) throw new IOException("The source instance changed during export.");
    }

    private static async Task AddOverrideFileAsync(
        ZipArchive archive,
        ModpackExportArchiveFile file,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(file.SourcePath))
            return;

        var entryName = $"overrides/{file.RelativePath}"
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var source = new FileStream(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateTempArchivePath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        var fileName = Path.GetFileName(outputPath);
        return Path.Combine(directory!, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
