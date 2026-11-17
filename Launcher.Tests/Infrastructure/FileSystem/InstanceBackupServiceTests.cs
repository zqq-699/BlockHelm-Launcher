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

using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Persistence;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class InstanceBackupServiceTests
{
    [Fact]
    public async Task InterruptedBackupCleanupSkipsActiveAndUnrelatedFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var inactive = Path.Combine(root, $".launcher-backup.{Guid.NewGuid():N}.tmp");
            var active = Path.Combine(root, $".launcher-backup.{Guid.NewGuid():N}.tmp");
            var unrelated = Path.Combine(root, ".launcher-backup.my-file.tmp");
            await File.WriteAllTextAsync(inactive, "partial");
            await File.WriteAllTextAsync(active, "writing");
            await File.WriteAllTextAsync(unrelated, "keep");
            await using (var activeLock = new FileStream(active + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await new InstanceBackupService().GetBackupsAsync(root);
                Assert.False(File.Exists(inactive));
                Assert.True(File.Exists(active));
                Assert.True(File.Exists(unrelated));
            }
            await new InstanceBackupService().GetBackupsAsync(root);
            Assert.False(File.Exists(active));
        }
        finally { DeleteDirectoryIfExists(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedExtractionIsCleanedButActiveOrPreviousDataIsPreserved(bool legacy)
    {
        var root = CreateTempDirectory();
        try
        {
            var instance = CreateRestorableInstance(root);
            var minecraft = GetMinecraftDirectory(instance);
            var versions = Path.GetDirectoryName(instance.InstanceDirectory)!;
            var id = Guid.NewGuid().ToString("N");
            var staging = Path.Combine(versions, ".launcher-restore-" + id);
            Directory.CreateDirectory(Path.Combine(staging, "extract", "Pack"));
            await File.WriteAllTextAsync(Path.Combine(staging, "extract", "Pack", "partial"), "partial");
            if (!legacy)
                await File.WriteAllTextAsync(Path.Combine(staging, ".launcher-restore-transaction.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 2, transactionId = id, ownerProof = InstanceDirectoryOwnership.Capture(staging),
                    versionName = instance.VersionName, state = "extracting", createdAtUtc = DateTimeOffset.UtcNow
                }));
            var lockPath = CrossProcessVersionLock.GetMutationPath(minecraft) + ".restore-" + id;
            await using (var activeLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await new InstanceBackupService().RecoverPendingRestoresAsync(minecraft);
                Assert.True(Directory.Exists(staging));
            }
            Directory.CreateDirectory(Path.Combine(staging, "previous"));
            await new InstanceBackupService().RecoverPendingRestoresAsync(minecraft);
            Assert.True(Directory.Exists(staging));
            Directory.Delete(Path.Combine(staging, "previous"));
            await new InstanceBackupService().RecoverPendingRestoresAsync(minecraft);
            Assert.False(Directory.Exists(staging));
            Assert.True(Directory.Exists(instance.InstanceDirectory));
        }
        finally { DeleteDirectoryIfExists(root); }
    }

    [Fact]
    public async Task CreateBackupAsyncCreatesZipWithInstanceRootAndManifestRecord()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instanceDirectory = Path.Combine(rootDirectory, ".minecraft", "versions", "instance-a");
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            Directory.CreateDirectory(Path.Combine(instanceDirectory, "saves"));
            File.WriteAllText(Path.Combine(instanceDirectory, "options.txt"), "options");
            File.WriteAllText(Path.Combine(instanceDirectory, "saves", "level.dat"), "level");
            var instance = CreateInstance(instanceDirectory);
            var service = new InstanceBackupService();

            var record = await service.CreateBackupAsync(instance, backupDirectory, "Nightly");

            Assert.Equal("Nightly", record.Name);
            Assert.Equal("Nightly.zip", record.FileName);
            Assert.True(File.Exists(record.FullPath));
            Assert.Equal(new FileInfo(record.FullPath).Length, record.SizeBytes);
            using (var archive = ZipFile.OpenRead(record.FullPath))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName == "instance-a/options.txt");
                Assert.Contains(archive.Entries, entry => entry.FullName == "instance-a/saves/level.dat");
            }

            var manifestRecords = await ReadManifestAsync(backupDirectory);
            var manifestRecord = Assert.Single(manifestRecords);
            Assert.Equal("Nightly", manifestRecord.Name);
            Assert.Equal("Nightly.zip", manifestRecord.FileName);
            Assert.Equal(record.FullPath, manifestRecord.FullPath);
            Assert.Equal(record.SizeBytes, manifestRecord.SizeBytes);
            Assert.True(manifestRecord.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task DeleteBackupUsesUserDeletionAndRemovesManifestRecord()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instanceDirectory = Path.Combine(rootDirectory, ".minecraft", "versions", "instance-a");
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            Directory.CreateDirectory(instanceDirectory);
            File.WriteAllText(Path.Combine(instanceDirectory, "options.txt"), "options");
            var deletion = new RecordingUserFileDeletionService();
            var service = new InstanceBackupService(userFileDeletionService: deletion);
            var backup = await service.CreateBackupAsync(
                CreateInstance(instanceDirectory),
                backupDirectory,
                "Recoverable");

            await service.DeleteBackupAsync(backupDirectory, backup.FullPath);

            Assert.Equal(backup.FullPath, Assert.Single(deletion.Files));
            Assert.Empty(await service.GetBackupsAsync(backupDirectory));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task DeleteBackupFailurePreservesFileAndManifestRecord()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instanceDirectory = Path.Combine(rootDirectory, ".minecraft", "versions", "instance-a");
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            Directory.CreateDirectory(instanceDirectory);
            File.WriteAllText(Path.Combine(instanceDirectory, "options.txt"), "options");
            var deletion = new RecordingUserFileDeletionService();
            var service = new InstanceBackupService(userFileDeletionService: deletion);
            var backup = await service.CreateBackupAsync(
                CreateInstance(instanceDirectory),
                backupDirectory,
                "Preserved");
            deletion.Failure = new IOException("deletion failed");

            await Assert.ThrowsAsync<IOException>(
                () => service.DeleteBackupAsync(backupDirectory, backup.FullPath));

            Assert.True(File.Exists(backup.FullPath));
            Assert.Equal(backup.FullPath, Assert.Single(deletion.Files));
            Assert.Equal(backup.FullPath, Assert.Single(await service.GetBackupsAsync(backupDirectory)).FullPath);
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task CreateBackupAsyncRejectsDirectoryReparsePointWithoutArchivingExternalFiles()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instanceDirectory = Path.Combine(rootDirectory, ".minecraft", "versions", "instance-a");
            var externalDirectory = Path.Combine(rootDirectory, "external");
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            Directory.CreateDirectory(instanceDirectory);
            Directory.CreateDirectory(externalDirectory);
            File.WriteAllText(Path.Combine(externalDirectory, "secret.txt"), "outside-instance");
            CreateDirectoryReparsePoint(Path.Combine(instanceDirectory, "linked-external"), externalDirectory);
            var instance = CreateInstance(instanceDirectory);
            var service = new InstanceBackupService();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.CreateBackupAsync(instance, backupDirectory, "Unsafe"));

            Assert.False(File.Exists(Path.Combine(backupDirectory, "Unsafe.zip")));
            Assert.Empty(Directory.Exists(backupDirectory)
                ? Directory.EnumerateFiles(backupDirectory, "*.tmp")
                : []);
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task ConcurrentCreatesAndManifestCleanupDoNotLoseCreatedRecords()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instanceDirectory = Path.Combine(rootDirectory, ".minecraft", "versions", "instance-a");
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            Directory.CreateDirectory(instanceDirectory);
            Directory.CreateDirectory(backupDirectory);
            File.WriteAllText(Path.Combine(instanceDirectory, "options.txt"), "options");
            await File.WriteAllTextAsync(
                Path.Combine(backupDirectory, "launcher-backups.json"),
                JsonSerializer.Serialize(
                    new[]
                    {
                        new InstanceBackupRecord
                        {
                            Name = "Missing",
                            FileName = "missing.zip",
                            FullPath = Path.Combine(backupDirectory, "missing.zip"),
                            CreatedAt = DateTimeOffset.UtcNow
                        }
                    }));
            var instance = CreateInstance(instanceDirectory);
            var service = new InstanceBackupService();
            var createTasks = Enumerable.Range(0, 8)
                .Select(index => service.CreateBackupAsync(instance, backupDirectory, $"Concurrent {index}"))
                .ToArray();
            var readTasks = Enumerable.Range(0, 8)
                .Select(_ => service.GetBackupsAsync(backupDirectory))
                .ToArray();

            await Task.WhenAll(createTasks.Cast<Task>().Concat(readTasks));

            var createdRecords = await Task.WhenAll(createTasks);
            var manifestRecords = await ReadManifestAsync(backupDirectory);
            Assert.Equal(8, manifestRecords.Count);
            Assert.DoesNotContain(manifestRecords, record => record.FileName == "missing.zip");
            Assert.Equal(
                createdRecords.Select(record => record.FileName).Order(StringComparer.OrdinalIgnoreCase),
                manifestRecords.Select(record => record.FileName).Order(StringComparer.OrdinalIgnoreCase));
            Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.tmp"));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreBackupAsyncAdaptsExplicitBackupToCurrentDirectory(bool hasDuplicateJar)
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var current = CreateRestorableInstance(rootDirectory);
            File.WriteAllText(Path.Combine(current.InstanceDirectory, "current.txt"), "keep-current");
            var versionsDirectory = Path.GetDirectoryName(current.InstanceDirectory)!;
            var otherDirectory = Path.Combine(versionsDirectory, "instance-b");
            Directory.CreateDirectory(otherDirectory);
            var other = new GameInstance
            {

                Name = "Instance B",
                VersionName = "instance-b",
                MinecraftVersion = "1.20.1",
                InstanceDirectory = otherDirectory
            };
            Directory.CreateDirectory(Path.Combine(otherDirectory, "BHL"));
            var iconPath = Path.Combine(otherDirectory, "BHL", "icon.png");
            await File.WriteAllBytesAsync(iconPath, [4, 5]);
            other.IconSource = new Uri(iconPath).AbsoluteUri;
            WriteInstanceSettings(otherDirectory, other);
            File.WriteAllText(Path.Combine(otherDirectory, "instance-b.json"), """{"id":"instance-b"}""");
            File.WriteAllText(Path.Combine(otherDirectory, "instance-b.jar"), "archive-client");
            if (hasDuplicateJar) File.WriteAllText(Path.Combine(otherDirectory, current.VersionName + ".jar"), "stale-client");
            File.WriteAllText(Path.Combine(otherDirectory, "other.txt"), "other-data");
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            var service = new InstanceBackupService();
            InstanceDirectoryOwnership.Observe(other);
            var backup = await service.CreateBackupAsync(other, backupDirectory, "Other");
            var archiveBytes = await File.ReadAllBytesAsync(backup.FullPath);

            await service.RestoreBackupAsync(current, backupDirectory, backup.FullPath);
            await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => new GameInstanceSettingsStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                .UpdateAsync(current, GetMinecraftDirectory(current), CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(current.InstanceDirectory, "current.txt")));
            Assert.Equal("other-data", File.ReadAllText(Path.Combine(current.InstanceDirectory, "other.txt")));
            Assert.True(File.Exists(Path.Combine(current.InstanceDirectory, current.VersionName + ".json")));
            Assert.Equal("archive-client", await File.ReadAllTextAsync(Path.Combine(current.InstanceDirectory, current.VersionName + ".jar")));
            Assert.Equal([4, 5], await File.ReadAllBytesAsync(Path.Combine(current.InstanceDirectory, "BHL", "icon.png")));
            using var restoredSettings = JsonDocument.Parse(await File.ReadAllTextAsync(InstanceDirectoryOwnership.SettingsPath(current.InstanceDirectory)));
            Assert.Equal("instance:BHL/icon.png", restoredSettings.RootElement.GetProperty("IconSource").GetString());
            Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(backup.FullPath));
            Assert.Empty(Directory.EnumerateDirectories(versionsDirectory, ".launcher-restore-*"));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("committed")]
    public async Task RecoverPendingRestoresAsyncCompletesCommittedCleanupWithoutRestoringPrevious(string state)
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instance = CreateRestorableInstance(rootDirectory);
            var minecraftDirectory = GetMinecraftDirectory(instance);
            var versionsDirectory = Path.GetDirectoryName(instance.InstanceDirectory)!;
            var transactionId = "b94e32d5000000000000000000000000";
            var staging = Path.Combine(versionsDirectory, $".launcher-restore-{transactionId}");
            Directory.CreateDirectory(staging);
            Directory.Move(instance.InstanceDirectory, Path.Combine(staging, "previous"));
            File.WriteAllText(Path.Combine(staging, "previous", "old.txt"), "old-data");
            CreateOwnedRestoreCandidate(staging, instance, transactionId, "restored-data");
            Directory.Move(Path.Combine(staging, "restored"), instance.InstanceDirectory);
            WriteRestoreMarker(staging, transactionId, instance, state);

            await new InstanceBackupService().RecoverPendingRestoresAsync(minecraftDirectory);

            Assert.Equal("restored-data", File.ReadAllText(Path.Combine(instance.InstanceDirectory, "restored.txt")));
            Assert.False(File.Exists(Path.Combine(instance.InstanceDirectory, "old.txt")));
            Assert.False(File.Exists(Path.Combine(instance.InstanceDirectory, "BHL", ".launcher-restore-owner.json")));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task RecoverySkipsPreparedRestoreWhileMutationsAreActive()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instance = CreateRestorableInstance(rootDirectory);
            var minecraftDirectory = GetMinecraftDirectory(instance);
            var transactionId = Guid.NewGuid().ToString("N");
            var staging = Path.Combine(Path.GetDirectoryName(instance.InstanceDirectory)!, $".launcher-restore-{transactionId}");
            CreateOwnedRestoreCandidate(staging, instance, transactionId, "restored-data");
            WriteRestoreMarker(staging, transactionId, instance, "prepared");
            var service = new InstanceBackupService();
            Task? scan = null;
            var mutationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetMutationPath(minecraftDirectory), null, CancellationToken.None);
            try
            {
                scan = service.RecoverPendingRestoresAsync(minecraftDirectory);
                await scan.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(Directory.Exists(Path.Combine(staging, "restored")));
                Assert.True(File.Exists(Path.Combine(staging, ".launcher-restore-transaction.json")));
            }
            finally
            {
                await mutationLock.DisposeAsync();
                if (scan is not null) await scan;
            }
            await service.RecoverPendingRestoresAsync(minecraftDirectory);
            Assert.True(Directory.Exists(instance.InstanceDirectory));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task RefreshDuringRestoreLockWaitLeavesStagingIntactAndRestoreCompletes()
    {
        var rootDirectory = CreateTempDirectory();
        using var cancellation = new CancellationTokenSource();
        FileStream? mutationLock = null;
        Task? restore = null;
        try
        {
            var instance = CreateRestorableInstance(rootDirectory);
            var minecraftDirectory = GetMinecraftDirectory(instance);
            var versionsDirectory = Path.GetDirectoryName(instance.InstanceDirectory)!;
            var payload = Path.Combine(instance.InstanceDirectory, "payload.txt");
            await File.WriteAllTextAsync(payload, "backup-state");
            var service = new InstanceBackupService();
            var backupDirectory = Path.Combine(rootDirectory, "backups");
            var backup = await service.CreateBackupAsync(instance, backupDirectory, "Before");
            await File.WriteAllTextAsync(payload, "current-state");
            mutationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetMutationPath(minecraftDirectory), null, CancellationToken.None);

            var candidateReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(versionsDirectory, ".launcher-restore-owner.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
            };
            watcher.Created += (_, args) => candidateReady.TrySetResult(args.FullPath);
            watcher.Renamed += (_, args) => candidateReady.TrySetResult(args.FullPath);
            watcher.EnableRaisingEvents = true;
            restore = service.RestoreBackupAsync(instance, backupDirectory, backup.FullPath, cancellation.Token);
            var ownerPath = await candidateReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var staging = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(ownerPath)))!;

            await new InstanceBackupService().RecoverPendingRestoresAsync(minecraftDirectory)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(Directory.Exists(Path.Combine(staging, "restored")));
            Assert.Contains("extracting", await File.ReadAllTextAsync(Path.Combine(staging, ".launcher-restore-transaction.json")));
            Assert.Equal("current-state", await File.ReadAllTextAsync(payload));

            await mutationLock.DisposeAsync();
            mutationLock = null;
            await restore.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("backup-state", await File.ReadAllTextAsync(payload));
            Assert.Empty(Directory.EnumerateDirectories(versionsDirectory, ".launcher-restore-*"));
        }
        finally
        {
            cancellation.Cancel();
            if (mutationLock is not null) await mutationLock.DisposeAsync();
            if (restore is not null)
            {
                try { await restore.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    [Fact]
    public async Task RecoverPendingRestoresAsyncPreservesSameIdReplacementWithoutOwnerProof()
    {
        var rootDirectory = CreateTempDirectory();
        try
        {
            var instance = CreateRestorableInstance(rootDirectory);
            var minecraftDirectory = GetMinecraftDirectory(instance);
            var versionsDirectory = Path.GetDirectoryName(instance.InstanceDirectory)!;
            var transactionId = "d16a54f7000000000000000000000000";
            var staging = Path.Combine(versionsDirectory, $".launcher-restore-{transactionId}");
            Directory.CreateDirectory(staging);
            Directory.Move(instance.InstanceDirectory, Path.Combine(staging, "previous"));
            File.WriteAllText(Path.Combine(staging, "previous", "previous.txt"), "keep-previous");
            Directory.CreateDirectory(instance.InstanceDirectory);
            WriteInstanceSettings(instance.InstanceDirectory, instance);
            File.WriteAllText(Path.Combine(instance.InstanceDirectory, "replacement.txt"), "keep-replacement");
            WriteRestoreMarker(staging, transactionId, instance, "committed");

            await new InstanceBackupService().RecoverPendingRestoresAsync(minecraftDirectory);

            Assert.Equal("keep-replacement", File.ReadAllText(Path.Combine(instance.InstanceDirectory, "replacement.txt")));
            Assert.Equal("keep-previous", File.ReadAllText(Path.Combine(staging, "previous", "previous.txt")));
            Assert.True(File.Exists(Path.Combine(staging, ".launcher-restore-conflict.json")));
        }
        finally
        {
            DeleteDirectoryIfExists(rootDirectory);
        }
    }

    private static GameInstance CreateInstance(string instanceDirectory)
    {
        var instance = new GameInstance
        {

            Name = "Instance A",
            VersionName = Path.GetFileName(instanceDirectory),
            MinecraftVersion = "1.20.1",
            InstanceDirectory = instanceDirectory
        };
        if (Directory.Exists(instanceDirectory))
            WriteInstanceSettings(instanceDirectory, instance);
        InstanceDirectoryOwnership.Observe(instance);
        return instance;
    }

    private static GameInstance CreateRestorableInstance(string rootDirectory)
    {
        var instanceDirectory = Path.Combine(rootDirectory, ".minecraft", "versions", "instance-a");
        Directory.CreateDirectory(instanceDirectory);
        var instance = CreateInstance(instanceDirectory);
        instance.VersionName = "instance-a";
        instance.MinecraftVersion = "1.20.1";
        WriteInstanceSettings(instanceDirectory, instance);
        File.WriteAllText(
            Path.Combine(instanceDirectory, "instance-a.json"),
            """{"id":"instance-a","type":"release"}""");
        InstanceDirectoryOwnership.Observe(instance);
        InstanceDirectoryOwnership.Observe(instance);
        return instance;
    }

    private static string GetMinecraftDirectory(GameInstance instance)
    {
        return Directory.GetParent(Directory.GetParent(instance.InstanceDirectory)!.FullName)!.FullName;
    }

    private static void WriteInstanceSettings(string instanceDirectory, string instanceId)
    {
        WriteInstanceSettings(
            instanceDirectory,
            new GameInstance
            {

                VersionName = Path.GetFileName(instanceDirectory),
                InstanceDirectory = instanceDirectory
            });
    }

    private static void WriteInstanceSettings(string instanceDirectory, GameInstance instance)
    {
        var metadataDirectory = Path.Combine(
            instanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName);
        Directory.CreateDirectory(metadataDirectory);
        var node = JsonSerializer.SerializeToNode(instance)!.AsObject();
        node["Id"] = instance.InstanceDirectory;
        File.WriteAllText(Path.Combine(metadataDirectory, "instance-settings.json"), node.ToJsonString());
    }

    private static void CreateOwnedRestoreCandidate(
        string stagingDirectory,
        GameInstance instance,
        string transactionId,
        string contents)
    {
        var restoredDirectory = Path.Combine(stagingDirectory, "restored");
        Directory.CreateDirectory(restoredDirectory);
        var restored = new GameInstance
        {
            Name = instance.Name,
            VersionName = instance.VersionName,
            MinecraftVersion = instance.MinecraftVersion,
            InstanceDirectory = instance.InstanceDirectory
        };
        WriteInstanceSettings(restoredDirectory, restored);
        File.WriteAllText(
            Path.Combine(restoredDirectory, instance.VersionName + ".json"),
            JsonSerializer.Serialize(new { id = instance.VersionName }));
        File.WriteAllText(Path.Combine(restoredDirectory, "restored.txt"), contents);
        File.WriteAllText(
            Path.Combine(restoredDirectory, "BHL", ".launcher-restore-owner.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                transactionId,
                instanceId = instance.InstanceDirectory
            }));
    }

    private static void WriteRestoreMarker(
        string stagingDirectory,
        string transactionId,
        GameInstance instance,
        string state)
    {
        File.WriteAllText(
            Path.Combine(stagingDirectory, ".launcher-restore-transaction.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                transactionId,
                instanceId = instance.InstanceDirectory,
                versionName = instance.VersionName,
                state,
                createdAtUtc = DateTimeOffset.UtcNow
            }));
    }

    private static async Task<IReadOnlyList<InstanceBackupRecord>> ReadManifestAsync(string backupDirectory)
    {
        await using var stream = File.OpenRead(Path.Combine(backupDirectory, "launcher-backups.json"));
        return await JsonSerializer.DeserializeAsync<IReadOnlyList<InstanceBackupRecord>>(stream)
            ?? [];
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CreateDirectoryReparsePoint(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (IOException)
        {
            // Creating a directory junction does not require the symbolic-link privilege on Windows.
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath }
        }) ?? throw new InvalidOperationException("Failed to start junction creation process.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private static void DeleteDirectoryIfExists(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory, recursive: false);
            return;
        }
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            DeleteDirectoryIfExists(childDirectory);
        foreach (var file in Directory.EnumerateFiles(directory))
            File.Delete(file);
        Directory.Delete(directory, recursive: false);
    }

    private sealed class RecordingUserFileDeletionService : IUserFileDeletionService
    {
        public List<string> Files { get; } = [];
        public Exception? Failure { get; set; }

        public void DeleteFile(string path)
        {
            Files.Add(Path.GetFullPath(path));
            if (Failure is not null)
                throw Failure;
            File.Delete(path);
        }

        public void DeleteDirectory(string path) =>
            throw new InvalidOperationException("Backup deletion must not delete directories.");
    }
}
