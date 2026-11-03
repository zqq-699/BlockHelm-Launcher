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

internal static class ModpackOverrideExtractor
{
    public static bool HasModrinthOverrides(
        ZipArchive archive,
        ModpackInstallEnvironment environment = ModpackInstallEnvironment.Client)
    {
        return ValidateOverrides(archive, environment).HasOverrides;
    }

    public static bool HasCurseForgeOverrides(ZipArchive archive)
    {
        return ValidateOverrides(archive, environment: null).HasOverrides;
    }

    public static async Task CopyOverridesAsync(
        PreparedModpack preparedModpack,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        if (preparedModpack.PackageKind is ModpackPackageKind.CurseForge)
        {
            await using var stream = File.OpenRead(preparedModpack.SourceArchivePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            await ExtractOverridesAsync(archive, instanceDirectory, environment: null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(preparedModpack.EmbeddedModrinthEntryName))
        {
            await using var outerStream = File.OpenRead(preparedModpack.SourceArchivePath);
            using var outerArchive = new ZipArchive(outerStream, ZipArchiveMode.Read, leaveOpen: false);
            var mrpackEntry = outerArchive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, preparedModpack.EmbeddedModrinthEntryName, StringComparison.Ordinal));
            if (mrpackEntry is null)
            {
                throw new ModpackImportException(
                    ModpackImportFailureReason.InvalidManifest,
                    "Embedded Modrinth archive was not found.");
            }

            await using var innerStream = await ModpackArchiveUtility.CopyZipEntryToMemoryAsync(
                mrpackEntry,
                ModpackArchiveUtility.MaxEmbeddedModpackBytes,
                cancellationToken).ConfigureAwait(false);
            using var innerArchive = new ZipArchive(innerStream, ZipArchiveMode.Read, leaveOpen: false);
            await ExtractOverridesAsync(innerArchive, instanceDirectory, preparedModpack.Environment, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var modrinthStream = File.OpenRead(preparedModpack.SourceArchivePath);
        using var modrinthArchive = new ZipArchive(modrinthStream, ZipArchiveMode.Read, leaveOpen: false);
        await ExtractOverridesAsync(modrinthArchive, instanceDirectory, preparedModpack.Environment, cancellationToken)
            .ConfigureAwait(false);
    }

    private static OverrideValidationResult ValidateOverrides(
        ZipArchive archive,
        ModpackInstallEnvironment? environment)
    {
        var foundAny = false;
        var extractionBudget = new ZipExtractionBudget(ModpackArchiveUtility.MaxOverrideTotalBytes);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in ResolveOverridePrefixes(environment))
        {
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
            {
                var relativePath = ModpackArchiveUtility.RemovePrefix(
                    ModpackArchiveUtility.NormalizeArchivePath(entry.FullName),
                    prefix);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                extractionBudget.Reserve(entry.Length);
                var target = ModpackArchiveUtility.GetValidatedTargetPath(Path.GetTempPath(), relativePath);
                if (!targets.Add($"{prefix}:{Path.GetFullPath(target)}"))
                {
                    throw new ModpackImportException(
                        ModpackImportFailureReason.InvalidManifest,
                        $"Multiple override entries resolve to the same target path: {relativePath}");
                }
                foundAny = true;
            }
        }

        return new OverrideValidationResult(foundAny, extractionBudget.ReservedBytes);
    }

    private static async Task ExtractOverridesAsync(
        ZipArchive archive,
        string instanceDirectory,
        ModpackInstallEnvironment? environment,
        CancellationToken cancellationToken)
    {
        var validation = ValidateOverrides(archive, environment);
        ModpackArchiveUtility.EnsureSufficientOverrideDiskSpace(
            instanceDirectory,
            validation.TotalBytes);

        var extractionBudget = new ZipExtractionBudget(ModpackArchiveUtility.MaxOverrideTotalBytes);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in ResolveOverridePrefixes(environment))
        {
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
            {
                var relativePath = ModpackArchiveUtility.RemovePrefix(
                    ModpackArchiveUtility.NormalizeArchivePath(entry.FullName),
                    prefix);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var target = ModpackArchiveUtility.GetValidatedTargetPath(instanceDirectory, relativePath);
                if (!targets.Add($"{prefix}:{Path.GetFullPath(target)}"))
                {
                    throw new ModpackImportException(
                        ModpackImportFailureReason.InvalidManifest,
                        $"Multiple override entries resolve to the same target path: {relativePath}");
                }

                await ModpackArchiveUtility.ExtractZipEntryAsync(
                    entry,
                    instanceDirectory,
                    relativePath,
                    extractionBudget,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<string> ResolveOverridePrefixes(ModpackInstallEnvironment? environment)
    {
        return environment switch
        {
            ModpackInstallEnvironment.Server => ["overrides", "server-overrides"],
            ModpackInstallEnvironment.Client => ["overrides", "client-overrides"],
            _ => ["overrides"]
        };
    }

    private sealed record OverrideValidationResult(bool HasOverrides, long TotalBytes);
}
