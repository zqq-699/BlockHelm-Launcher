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

using Launcher.Infrastructure.FileSystem;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Persistence;

internal sealed class GameInstanceSettingsStore(ILogger logger)
{
    private const string LauncherDirectoryName = LauncherApplicationIdentity.StorageDirectoryName;
    private const string InstanceSettingsFileName = "instance-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<IReadOnlyList<GameInstance>> LoadAsync(
        string minecraftDirectory,
        int defaultMemoryMb,
        CancellationToken cancellationToken)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        var storedInstances = new List<GameInstance>();
        foreach (var versionDirectory in EnumerateDirectories(versionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldIgnoreDirectory(versionDirectory))
                continue;

            var settingsPath = GetSettingsPath(versionDirectory);
            var versionName = Path.GetFileName(versionDirectory);
            if (!File.Exists(settingsPath) && InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) is null)
                continue;
            using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(versionDirectory);
            var proof = InstanceDirectoryOwnership.Capture(versionDirectory);
            var digest = InstanceDirectoryOwnership.Digest(versionDirectory);
            var instance = File.Exists(settingsPath)
                ? await TryLoadAsync(settingsPath, cancellationToken).ConfigureAwait(false)
                : null;
            if (instance is null)
            {
                var createdAt = new DateTimeOffset(Directory.GetCreationTimeUtc(versionDirectory));
                instance = new GameInstance { CreatedAt = createdAt, UpdatedAt = createdAt, MemoryMb = defaultMemoryMb };
            }
            instance.IconSource = InstanceIconPaths.Resolve(instance.IconSource, instance.InstanceDirectory, versionDirectory);
            instance.Name = versionName;
            instance.VersionName = versionName;
            instance.InstanceDirectory = MinecraftDirectoryPath.Normalize(versionDirectory);
            if (digest != InstanceDirectoryOwnership.Digest(versionDirectory))
                throw new GameInstanceMutationConflictException(versionDirectory, versionName);
            InstanceDirectoryOwnership.Observe(instance, proof, digest);
            storedInstances.Add(instance);
        }

        return storedInstances;
    }

    public async Task<int> SaveAsync(
        IReadOnlyCollection<GameInstance> instances,
        string minecraftDirectory,
        CancellationToken cancellationToken)
    {
        var persistedCount = 0;
        var persistedVersionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versionName = GetVersionName(instance);
            if (string.IsNullOrWhiteSpace(versionName) || !persistedVersionNames.Add(versionName))
                continue;

            var versionDirectory = new VersionDirectoryManager(logger).GetVersionDirectory(minecraftDirectory, versionName);
            if (!MinecraftDirectoryPath.Equals(versionDirectory, instance.InstanceDirectory))
                throw new GameInstanceMutationConflictException(instance.InstanceDirectory, versionName);
            if (!Directory.Exists(versionDirectory))
                continue;

            using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(versionDirectory);
            var snapshot = CreateStorageSnapshot(instance, versionDirectory, versionName);
            await SaveSnapshotAsync(instance, versionDirectory, snapshot, cancellationToken)
                .ConfigureAwait(false);
            persistedCount++;
        }

        return persistedCount;
    }

    public async Task UpdateAsync(
        GameInstance instance,
        string minecraftDirectory,
        CancellationToken cancellationToken)
    {
        var versionName = GetVersionName(instance);
        var versionDirectory = new VersionDirectoryManager(logger).GetVersionDirectory(minecraftDirectory, versionName);
        if (!MinecraftDirectoryPath.Equals(versionDirectory, instance.InstanceDirectory))
            throw new GameInstanceMutationConflictException(instance.InstanceDirectory, versionName);
        using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(versionDirectory);
        InstanceDirectoryOwnership.EnsureCurrent(instance);
        var snapshot = CreateStorageSnapshot(instance, versionDirectory, versionName);
        await SaveSnapshotAsync(instance, versionDirectory, snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task SaveSnapshotAsync(
        GameInstance instance,
        string directory,
        GameInstance snapshot,
        CancellationToken cancellationToken)
    {
        var ownerProof = InstanceDirectoryOwnership.Capture(directory);
        var previousDigest = InstanceDirectoryOwnership.Digest(directory);
        if (InstanceDirectoryOwnership.IsObserved(instance))
            InstanceDirectoryOwnership.EnsureCurrent(instance);
        else if (previousDigest != "missing")
            throw new GameInstanceMutationConflictException(directory, snapshot.VersionName);
        var storage = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        var settingsPath = GetSettingsPath(directory);
        if (previousDigest != "missing")
        {
            try
            {
                var original = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken)) as JsonObject;
                if (original is not null)
                {
                    var legacyId = original.FirstOrDefault(property => property.Key.Equals("Id", StringComparison.OrdinalIgnoreCase));
                    var versionsDirectory = Path.GetDirectoryName(directory)!;
                    if (legacyId.Key is not null && legacyId.Value is not null
                        && InstanceDirectoryClassifier.HasPendingTransactions(Directory.EnumerateDirectories(versionsDirectory)))
                        storage[legacyId.Key] = legacyId.Value.DeepClone();
                }
            }
            catch (JsonException) { /* Damaged settings can be replaced by an explicit user save. */ }
        }
        var writtenDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(storage, JsonOptions)));
        await AtomicJsonFileWriter.WriteAsync(settingsPath, storage, JsonOptions, cancellationToken,
            validateDestination: () =>
            {
                if (!InstanceDirectoryOwnership.Matches(directory, ownerProof) || previousDigest != InstanceDirectoryOwnership.Digest(directory))
                    throw new GameInstanceMutationConflictException(directory, snapshot.VersionName);
            }).ConfigureAwait(false);
        if (writtenDigest != InstanceDirectoryOwnership.Digest(directory))
            throw new GameInstanceMutationConflictException(directory, snapshot.VersionName);
        // Keep the digest of our own bytes, never adopt an external writer's later contents.
        InstanceDirectoryOwnership.Observe(instance, ownerProof, writtenDigest);
    }

    public async Task EnsureIdentityAsync(
        string versionDirectory,
        string expectedInstanceDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!InstanceDirectoryOwnership.Matches(versionDirectory, expectedInstanceDirectory))
            throw new GameInstanceMutationConflictException(versionDirectory, Path.GetFileName(versionDirectory));
        await Task.CompletedTask;
    }

    public static bool HasIdentity(string versionDirectory, string expectedInstanceDirectory)
        => InstanceDirectoryOwnership.Matches(versionDirectory, expectedInstanceDirectory);

    public async Task CompleteRenameAsync(
        string versionDirectory,
        string instanceDirectory,
        string newVersionName,
        string? newIconSource,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var settingsPath = GetSettingsPath(versionDirectory);
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("Instance settings were not found while completing rename.", settingsPath);

        var digest = InstanceDirectoryOwnership.Digest(versionDirectory);
        var instance = await TryLoadAsync(settingsPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Instance settings could not be read while completing rename.");
        if (!HasIdentity(versionDirectory, instanceDirectory))
            throw new InvalidDataException("Pending rename marker does not match the instance settings.");

        instance.Name = newVersionName;
        instance.VersionName = newVersionName;
        instance.InstanceDirectory = versionDirectory;
        instance.IconSource = newIconSource;
        instance.UpdatedAt = updatedAt;
        var snapshot = CreateStorageSnapshot(instance, versionDirectory, newVersionName);
        // A legacy default may still refer to this ID. Keep that existing evidence
        // through crash recovery until selection migration has committed.
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken)) as System.Text.Json.Nodes.JsonObject;
        var stored = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        var oldId = legacy?.FirstOrDefault(property => property.Key.Equals("Id", StringComparison.OrdinalIgnoreCase));
        if (oldId?.Value is { } legacyId)
            stored["Id"] = legacyId.DeepClone();
        await AtomicJsonFileWriter.WriteAsync(settingsPath, stored, JsonOptions, cancellationToken,
                validateDestination: () =>
                {
                    if (!HasIdentity(versionDirectory, instanceDirectory) || digest != InstanceDirectoryOwnership.Digest(versionDirectory))
                        throw new GameInstanceMutationConflictException(versionDirectory, newVersionName);
                })
            .ConfigureAwait(false);
    }

    public async Task PrepareInstallAsync(
        string pendingDirectory,
        string finalDirectory,
        string logicalVersionName,
        GameInstance instance,
        string expectedOwnerProof,
        CancellationToken cancellationToken)
    {
        using var directoryLease = WindowsDirectoryHandleMover.AcquireReadLease(pendingDirectory);
        var settingsPath = GetSettingsPath(pendingDirectory);
        var snapshot = CreateStorageSnapshot(instance, finalDirectory, logicalVersionName);
        await AtomicJsonFileWriter.WriteAsync(settingsPath, snapshot, JsonOptions, cancellationToken,
            validateDestination: () =>
            {
                if (!HasIdentity(pendingDirectory, expectedOwnerProof))
                    throw new GameInstanceMutationConflictException(pendingDirectory, logicalVersionName);
            }).ConfigureAwait(false);
    }

    private async Task<GameInstance?> TryLoadAsync(string settingsPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(settingsPath);
            var instance = await JsonSerializer.DeserializeAsync<GameInstance>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (instance is not null)
                Normalize(instance);
            return instance;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Ignored invalid game instance settings. SettingsPath={SettingsPath}", settingsPath);
            return null;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to read game instance settings. SettingsPath={SettingsPath}", settingsPath);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Access denied while reading game instance settings. SettingsPath={SettingsPath}", settingsPath);
            return null;
        }
    }

    private static void Normalize(GameInstance instance)
    {
        if (!Enum.IsDefined(instance.MemorySettingsMode))
            instance.MemorySettingsMode = MemorySettingsMode.Manual;
        if (instance.MemoryMb <= 0)
            instance.MemoryMb = LauncherDefaults.DefaultMemoryMb;
    }

    private static GameInstance CreateStorageSnapshot(GameInstance instance, string versionDirectory, string versionName)
    {
        return new GameInstance
        {
            Name = versionName,
            MinecraftVersion = instance.MinecraftVersion,
            Loader = instance.Loader,
            LoaderVersion = instance.LoaderVersion,
            VersionName = versionName,
            VersionType = instance.VersionType,
            Description = instance.Description,
            IconSource = InstanceIconPaths.ToStorage(instance.IconSource, versionDirectory),
            InstanceDirectory = versionDirectory,
            BackupDirectory = instance.BackupDirectory,
            MemorySettingsMode = instance.MemorySettingsMode,
            MemoryMb = instance.MemoryMb,
            WindowWidth = instance.WindowWidth,
            WindowHeight = instance.WindowHeight,
            PreLaunchCommand = instance.PreLaunchCommand,
            WaitForPreLaunchCommand = instance.WaitForPreLaunchCommand,
            PostExitCommand = instance.PostExitCommand,
            JvmArguments = instance.JvmArguments,
            GameArguments = instance.GameArguments,
            LaunchSettingsMode = instance.LaunchSettingsMode,
            JavaSettingsMode = instance.JavaSettingsMode,
            JavaSelectionMode = instance.JavaSelectionMode,
            SelectedJavaExecutablePath = instance.SelectedJavaExecutablePath,
            CheckFilesBeforeLaunch = instance.CheckFilesBeforeLaunch,
            AutoRepairMissingFiles = instance.AutoRepairMissingFiles,
            MinimizeLauncherAfterLaunch = instance.MinimizeLauncherAfterLaunch,
            LaunchFullScreen = instance.LaunchFullScreen,
            AutoJoinServerAddress = instance.AutoJoinServerAddress,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = instance.UpdatedAt
        };
    }

    private static string GetSettingsPath(string versionDirectory)
        => Path.Combine(versionDirectory, LauncherDirectoryName, InstanceSettingsFileName);

    private static bool ShouldIgnoreDirectory(string versionDirectory)
    {
        if (!InstanceDirectoryClassifier.IsInstanceDirectory(versionDirectory))
        {
            return true;
        }

        var markerStatus = PendingInstanceRenameMarkerFile.Read(versionDirectory).Status;
        return markerStatus is PendingInstanceRenameMarkerStatus.Valid
            or PendingInstanceRenameMarkerStatus.Unreadable;
    }

    private static string GetVersionName(GameInstance instance)
        => string.IsNullOrWhiteSpace(instance.VersionName) ? instance.MinecraftVersion : instance.VersionName;

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try { return Directory.EnumerateDirectories(directory).ToArray(); }
        catch (DirectoryNotFoundException) { return []; }
    }
}
