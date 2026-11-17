/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Persistence;

public sealed partial class JsonSettingsService
{
    private bool selectionMigrationCommitted;

    internal async Task MigrateInstanceSelectionAsync(string minecraftDirectory, CancellationToken cancellationToken)
    {
        await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(settingsPath)) return;
            var raw = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken))!.AsObject();
            var current = raw.Deserialize<LauncherSettings>(JsonOptions)!;
            if (!MinecraftDirectoryPath.Equals(current.MinecraftDirectory, minecraftDirectory)) return;
            var versions = Path.Combine(minecraftDirectory, "versions");
            if (!Directory.Exists(versions)) return;
            var allDirectories = Directory.EnumerateDirectories(versions).ToArray();
            var directories = allDirectories
                .Where(InstanceDirectoryClassifier.IsInstanceDirectory)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
            // An unresolved transaction must not prevent selecting a usable instance,
            // but its legacy ownership credentials must remain available to recovery.
            var hasPendingTransactions = InstanceDirectoryClassifier.HasPendingTransactions(allDirectories);
            var validDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (raw.ContainsKey("DefaultInstanceId") && string.IsNullOrWhiteSpace(current.DefaultInstanceDirectory))
            {
                var installedVersions = await new InstalledVersionMetadataReader(logger)
                    .DiscoverAsync(minecraftDirectory, cancellationToken).ConfigureAwait(false);
                validDirectories.UnionWith(installedVersions.Select(version => MinecraftDirectoryPath.Normalize(version.Directory)));
            }
            var legacyId = raw["DefaultInstanceId"] is JsonValue legacyValue && legacyValue.TryGetValue<string>(out var legacyText) ? legacyText : null;
            var candidates = new List<string>();
            foreach (var directory in directories)
            {
                if (!validDirectories.Contains(MinecraftDirectoryPath.Normalize(directory))) continue;
                var path = InstanceDirectoryOwnership.SettingsPath(directory);
                if (!File.Exists(path)) continue;
                try
                {
                    var node = (JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? throw new JsonException("Instance settings must be an object."));
                    var idNode = node.FirstOrDefault(p => p.Key.Equals("Id", StringComparison.OrdinalIgnoreCase)).Value;
                    var id = idNode is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : null;
                    if (!string.IsNullOrWhiteSpace(legacyId) && string.Equals(id, legacyId, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(MinecraftDirectoryPath.Normalize(directory));
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { logger.LogWarning(e, "Could not read legacy instance selection candidate. Directory={Directory}", directory); }
            }
            if (raw.ContainsKey("DefaultInstanceId"))
            {
                if (string.IsNullOrWhiteSpace(current.DefaultInstanceDirectory))
                    current.DefaultInstanceDirectory = candidates.Count == 1 ? candidates[0]
                        : directories.FirstOrDefault(p => validDirectories.Contains(MinecraftDirectoryPath.Normalize(p))) ?? string.Empty;
                selectionMigrationCommitted = true;
                current.Revision = checked(current.Revision + 1);
                try { await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false); }
                finally { selectionMigrationCommitted = false; }
                logger.LogInformation("Migrated instance selection to its directory. MatchingCandidates={MatchingCandidates}", candidates.Count);
            }
            if (hasPendingTransactions)
                return;
            foreach (var directory in directories)
            {
                if (Directory.EnumerateFiles(directory, ".bhl-*.json").Any()) continue;
                var path = InstanceDirectoryOwnership.SettingsPath(directory);
                if (!File.Exists(path)) continue;
                try
                {
                    using var directoryLease = Launcher.Infrastructure.FileSystem.WindowsDirectoryHandleMover.AcquireReadLease(directory);
                    var digest = InstanceDirectoryOwnership.Digest(directory);
                    var node = (JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? throw new JsonException("Instance settings must be an object."));
                    var keys = node.Select(p => p.Key).Where(k => k.Equals("Id", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (keys.Length == 0) continue;
                    foreach (var key in keys) node.Remove(key);
                    var oldDirectory = node.FirstOrDefault(p => p.Key.Equals("InstanceDirectory", StringComparison.OrdinalIgnoreCase)).Value;
                    var iconProperty = node.FirstOrDefault(p => p.Key.Equals("IconSource", StringComparison.OrdinalIgnoreCase));
                    if (oldDirectory is JsonValue directoryValue && directoryValue.TryGetValue<string>(out var oldPath)
                        && iconProperty.Value is JsonValue iconValue && iconValue.TryGetValue<string>(out var icon))
                        node[iconProperty.Key] = InstanceIconPaths.ToStorage(icon, oldPath);
                    node["Name"] = Path.GetFileName(directory);
                    node["VersionName"] = Path.GetFileName(directory);
                    node["InstanceDirectory"] = MinecraftDirectoryPath.Normalize(directory);
                    await AtomicJsonFileWriter.WriteAsync(path, node, JsonOptions, cancellationToken,
                        validateDestination: () =>
                        {
                            if (digest != InstanceDirectoryOwnership.Digest(directory))
                                throw new IOException("Instance settings changed during migration.");
                        }).ConfigureAwait(false);
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { logger.LogWarning(e, "Instance settings migration deferred. Directory={Directory}", directory); }
            }
        }
        finally { ioLock.Release(); }
    }
}
