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
using System.Text.Json;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Launcher.Tests.Helpers;

namespace Launcher.Tests.Infrastructure.Persistence;

public sealed class JsonGameInstanceRepositoryTests : TestTempDirectory
{
    private static readonly string InstanceMetadataDirectoryName = LauncherApplicationIdentity.StorageDirectoryName;

    [Fact]
    public async Task SaveAllAsyncWritesInstanceSettingsIntoVersionLauncherDirectory()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        var versionDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "demo-pack");
        repository.CreateInstanceDirectories(versionDirectory);

        await repository.SaveAllAsync(
        [
            new GameInstance
            {

                Name = "Demo Pack",
                MinecraftVersion = "1.20.1",
                VersionName = "demo-pack",
                Description = "stored beside the instance",
                InstanceDirectory = versionDirectory,
                BackupDirectory = Path.Combine(TempRoot, "backups", "demo-pack"),
                MemorySettingsMode = MemorySettingsMode.Auto,
                MemoryMb = 5120,
                LaunchSettingsMode = LaunchSettingsMode.PerInstance,
                AutoJoinServerAddress = "play.example.com:25565",
                JavaSettingsMode = LaunchSettingsMode.PerInstance,
                JavaSelectionMode = JavaSelectionMode.Manual,
                SelectedJavaExecutablePath = @"C:\Java\jdk-21\bin\java.exe"
            }
        ]);

        var settingsPath = Path.Combine(versionDirectory, InstanceMetadataDirectoryName, "instance-settings.json");
        Assert.True(File.Exists(settingsPath));
        Assert.False(File.Exists(Path.Combine(settings.DataDirectory, "instances.json")));

        var savedJson = await File.ReadAllTextAsync(settingsPath);
        using var document = JsonDocument.Parse(savedJson);
        Assert.Equal("demo-pack", document.RootElement.GetProperty("Name").GetString());
        Assert.False(document.RootElement.TryGetProperty("Id", out _));
        Assert.Equal("stored beside the instance", document.RootElement.GetProperty("Description").GetString());
        Assert.Equal(Path.Combine(TempRoot, "backups", "demo-pack"), document.RootElement.GetProperty("BackupDirectory").GetString());
        Assert.Equal((int)MemorySettingsMode.Auto, document.RootElement.GetProperty("MemorySettingsMode").GetInt32());
        Assert.Equal(5120, document.RootElement.GetProperty("MemoryMb").GetInt32());
        Assert.Equal((int)LaunchSettingsMode.PerInstance, document.RootElement.GetProperty("LaunchSettingsMode").GetInt32());
        Assert.Equal("play.example.com:25565", document.RootElement.GetProperty("AutoJoinServerAddress").GetString());
        Assert.Equal((int)LaunchSettingsMode.PerInstance, document.RootElement.GetProperty("JavaSettingsMode").GetInt32());
        Assert.Equal((int)JavaSelectionMode.Manual, document.RootElement.GetProperty("JavaSelectionMode").GetInt32());
        Assert.Equal(@"C:\Java\jdk-21\bin\java.exe", document.RootElement.GetProperty("SelectedJavaExecutablePath").GetString());

        var reloaded = Assert.Single(await repository.GetAllAsync());
        Assert.Equal(LaunchSettingsMode.PerInstance, reloaded.LaunchSettingsMode);
        Assert.Equal("play.example.com:25565", reloaded.AutoJoinServerAddress);
        Assert.Equal(LaunchSettingsMode.PerInstance, reloaded.JavaSettingsMode);
    }

    [Fact]
    public async Task SaveAllAsyncWithStaleSnapshotDoesNotDeleteInstanceSavedByAnotherRepository()
    {
        var (settings, settingsService) = CreateSettings();
        var staleRepository = new JsonGameInstanceRepository(settingsService);
        var currentRepository = new JsonGameInstanceRepository(settingsService);
        var existingDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "Existing");
        staleRepository.CreateInstanceDirectories(existingDirectory);
        await staleRepository.SaveAllAsync([CreateInstance("existing", "Existing", existingDirectory)]);
        var staleSnapshot = await staleRepository.GetAllAsync();

        var newDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "NewlyInstalled");
        currentRepository.CreateInstanceDirectories(newDirectory);
        var newlyInstalled = CreateInstance("newly-installed", "NewlyInstalled", newDirectory);
        await currentRepository.SaveAllAsync([.. staleSnapshot, newlyInstalled]);

        await staleRepository.SaveAllAsync(staleSnapshot);

        var newSettingsPath = Path.Combine(
            newDirectory,
            InstanceMetadataDirectoryName,
            "instance-settings.json");
        Assert.True(File.Exists(newSettingsPath));
        var reloaded = await new JsonGameInstanceRepository(settingsService).GetAllAsync();
        Assert.Contains(reloaded, instance => instance.InstanceDirectory == newDirectory);
    }

    [Fact]
    public async Task SaveAllAsyncDoesNotOverwriteSameNameReplacementInstance()
    {
        var (settings, settingsService) = CreateSettings();
        var staleRepository = new JsonGameInstanceRepository(settingsService);
        var currentRepository = new JsonGameInstanceRepository(settingsService);
        var directory = await CreateStoredInstanceAsync(staleRepository, settings.MinecraftDirectory, "Test");
        var staleInstance = Assert.Single(await staleRepository.GetAllAsync());
        Directory.Delete(directory, recursive: true);
        CreateVersionDirectory(settings.MinecraftDirectory, "Test");
        var replacement = CreateInstance("replacement", "Test", directory);
        replacement.Description = "keep replacement";
        await currentRepository.SaveAllAsync([replacement]);

        staleInstance.Description = "stale overwrite";
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => staleRepository.SaveAllAsync([staleInstance]));

        var reloaded = Assert.Single(await currentRepository.GetAllAsync());
        Assert.Equal(directory, reloaded.InstanceDirectory);
        Assert.Equal("keep replacement", reloaded.Description);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"C:\Pack")]
    public void GetVersionDirectoryRejectsUnsafeVersionName(string versionName)
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);

        Assert.Throws<ArgumentException>(() => repository.GetVersionDirectory(settings.MinecraftDirectory, versionName));
    }

    [Theory]
    [InlineData(false)]
    public async Task RenameVersionAsyncRollsBackWhenDestinationAppearsDuringFinalMove(bool destinationIsDirectory)
    {
        var (settings, settingsService) = CreateSettings();
        var moveAttempts = 0;
        var repository = new JsonGameInstanceRepository(
            settingsService,
            moveDirectoryAsync: (source, destination, _) =>
            {
                moveAttempts++;
                if (string.Equals(Path.GetFileName(destination), "New Pack", StringComparison.OrdinalIgnoreCase))
                {
                    if (destinationIsDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
                    }
                    else
                    {
                        File.WriteAllText(destination, "keep");
                    }

                    throw new IOException("destination won the race");
                }

                Directory.Move(source, destination);
                return Task.CompletedTask;
            });
        CreateVersionDirectory(settings.MinecraftDirectory, "Old Pack");
        var oldDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "Old Pack");
        Directory.CreateDirectory(Path.Combine(oldDirectory, "Old Pack-natives"));
        var instance = new GameInstance
        {

            Name = "Old Pack",
            MinecraftVersion = "1.20.1",
            VersionName = "Old Pack",
            InstanceDirectory = oldDirectory
        };
        await repository.SaveAllAsync([instance]);

        await Assert.ThrowsAsync<InstanceInstallNameConflictException>(() => repository.RenameVersionAsync(
            settings.MinecraftDirectory,
            instance,
            "New Pack",
            null,
            DateTimeOffset.UtcNow));

        Assert.Equal(3, moveAttempts);
        Assert.True(File.Exists(Path.Combine(oldDirectory, "Old Pack.json")));
        Assert.True(File.Exists(Path.Combine(oldDirectory, "Old Pack.jar")));
        Assert.True(Directory.Exists(Path.Combine(oldDirectory, "Old Pack-natives")));
        Assert.False(File.Exists(Path.Combine(oldDirectory, ".bhl-rename-pending.json")));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(settings.MinecraftDirectory, "versions"),
            ".bhl-rename-pending-*"));
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(oldDirectory, "Old Pack.json"))))
        {
            Assert.Equal("Old Pack", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("Old Pack", document.RootElement.GetProperty("jar").GetString());
        }
        Assert.Equal("Old Pack", Assert.Single(await repository.GetAllAsync()).VersionName);

        var destination = Path.Combine(settings.MinecraftDirectory, "versions", "New Pack");
        if (destinationIsDirectory)
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
        else
            Assert.Equal("keep", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task RenameDoesNotRollbackDifferentInstanceInjectedAfterFinalMove()
    {
        var (settings, settingsService) = CreateSettings();
        var versionsDirectory = Path.Combine(settings.MinecraftDirectory, "versions");
        var displacedOriginal = Path.Combine(versionsDirectory, "displaced-original");
        var moveAttempts = 0;
        var repository = new JsonGameInstanceRepository(
            settingsService,
            moveDirectoryAsync: (source, destination, _) =>
            {
                moveAttempts++;
                Directory.Move(source, destination);
                if (string.Equals(Path.GetFileName(destination), "New Pack", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(destination, displacedOriginal);
                    CreateVersionDirectory(settings.MinecraftDirectory, "New Pack");
                    WriteInstanceSettings(destination, "replacement");
                    File.Copy(
                        Path.Combine(displacedOriginal, ".bhl-rename-pending.json"),
                        Path.Combine(destination, ".bhl-rename-pending.json"));
                    File.WriteAllText(Path.Combine(destination, "replacement.txt"), "keep");
                }

                return Task.CompletedTask;
            });
        var oldDirectory = await CreateStoredInstanceAsync(repository, settings.MinecraftDirectory, "Old Pack");
        var instance = Assert.Single(await repository.GetAllAsync());

        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => repository.RenameVersionAsync(
            settings.MinecraftDirectory,
            instance,
            "New Pack",
            null,
            DateTimeOffset.UtcNow));

        var replacementDirectory = Path.Combine(versionsDirectory, "New Pack");
        Assert.Equal(2, moveAttempts);
        Assert.False(Directory.Exists(oldDirectory));
        Assert.True(Directory.Exists(displacedOriginal));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(replacementDirectory, "replacement.txt")));
        Assert.True(File.Exists(Path.Combine(replacementDirectory, ".bhl-rename-aborted.json")));
        Assert.False(File.Exists(Path.Combine(replacementDirectory, ".bhl-rename-pending.json")));
    }

    [Fact]
    public async Task StageVersionForDeletionRejectsSameNameReplacementBeforeWritingMarkerOrMoving()
    {
        var (settings, settingsService) = CreateSettings();
        var moveAttempts = 0;
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: null,
            stageDeletionMove: (_, _) => moveAttempts++,
            deleteStagedDirectory: null);
        var directory = await CreateStoredInstanceAsync(repository, settings.MinecraftDirectory, "Test");
        Directory.Delete(directory, recursive: true);
        CreateVersionDirectory(settings.MinecraftDirectory, "Test");
        await repository.SaveAllAsync([CreateInstance("replacement", "Test", directory)]);
        await File.WriteAllTextAsync(Path.Combine(directory, "replacement.txt"), "keep");

        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() =>
            repository.StageVersionForDeletionAsync(settings.MinecraftDirectory, "Test", "test"));

        Assert.Equal(0, moveAttempts);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(directory, "replacement.txt")));
        Assert.False(File.Exists(Path.Combine(directory, ".bhl-delete-pending-.json")));
    }

    [Fact]
    public async Task SchemaTwoDeletionMarkerWithDifferentInstanceIdIsNeverCleaned()
    {
        var (settings, settingsService) = CreateSettings();
        var stagedDirectory = Path.Combine(
            settings.MinecraftDirectory,
            "versions",
            ".bhl-delete-pending-Test-a83f21c4");
        Directory.CreateDirectory(stagedDirectory);
        WriteInstanceSettings(stagedDirectory, "replacement");
        await File.WriteAllTextAsync(
            Path.Combine(stagedDirectory, ".bhl-delete-pending-.json"),
            """{"schemaVersion":2,"transactionId":"a83f21c4000000000000000000000000","versionName":"Test","instanceId":"expected","createdAtUtc":"2026-07-13T00:00:00Z"}""");
        var recycleAttempts = 0;
        var deleteAttempts = 0;
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: null,
            stageDeletionMove: null,
            deleteStagedDirectory: (_, _) => deleteAttempts++,
            recycleStagedDirectory: _ => recycleAttempts++);

        await repository.CleanupStagedVersionDirectoriesAsync(settings.MinecraftDirectory);
        await repository.CleanupStagedVersionDirectoriesAsync(settings.MinecraftDirectory);

        Assert.True(Directory.Exists(stagedDirectory));
        Assert.True(File.Exists(Path.Combine(stagedDirectory, ".bhl-delete-aborted.json")));
        Assert.Equal(0, recycleAttempts);
        Assert.Equal(0, deleteAttempts);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    public async Task TryDeleteStagedVersionDirectoryUsesRecycleBinBeforePermanentDeletion(int schemaVersion, bool recycleFails, bool wasQuarantined)
    {
        var (settings, settingsService) = CreateSettings();
        var stagedDirectory = Path.Combine(
            settings.MinecraftDirectory,
            "versions",
            ".bhl-delete-pending-Test-a83f21c4");
        Directory.CreateDirectory(stagedDirectory);
        WriteDeletionMarker(stagedDirectory, "Test", "a83f21c4000000000000000000000000", schemaVersion, wasQuarantined);
        var recycleAttempts = 0;
        var permanentDeleteAttempts = 0;
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: null,
            stageDeletionMove: null,
            deleteStagedDirectory: (path, recursive) =>
            {
                permanentDeleteAttempts++;
                Directory.Delete(path, recursive);
            },
            recycleStagedDirectory: path =>
            {
                recycleAttempts++;
                if (recycleFails) throw new IOException("Recycle bin is unavailable.");
                Directory.Delete(path, recursive: true);
            });

        Assert.True(await repository.TryDeleteStagedVersionDirectoryAsync(
            settings.MinecraftDirectory,
            stagedDirectory));

        Assert.Equal(1, recycleAttempts);
        Assert.Equal(recycleFails ? 1 : 0, permanentDeleteAttempts);
        Assert.False(Directory.Exists(stagedDirectory));
    }

    [Fact]
    public async Task QuarantinedLegacyDeletionWithMismatchedDirectoryIsNotResumed()
    {
        var (settings, settingsService) = CreateSettings();
        var stagedDirectory = Path.Combine(settings.MinecraftDirectory, "versions", ".bhl-delete-pending-Test-a83f21c4");
        Directory.CreateDirectory(stagedDirectory);
        WriteDeletionMarker(stagedDirectory, "Other", "a83f21c4000000000000000000000000", 1, true);
        var repository = new JsonGameInstanceRepository(settingsService);

        Assert.False(await repository.TryDeleteStagedVersionDirectoryAsync(settings.MinecraftDirectory, stagedDirectory));
        Assert.True(Directory.Exists(stagedDirectory));
        Assert.True(File.Exists(Path.Combine(stagedDirectory, PendingInstanceDeletionDirectory.AbortedMarkerFileName)));
    }

    private static void WriteDeletionMarker(string directory, string versionName, string transactionId, int schemaVersion = 3, bool wasQuarantined = false)
    {
        File.WriteAllText(
            Path.Combine(directory, wasQuarantined ? PendingInstanceDeletionDirectory.AbortedMarkerFileName : PendingInstanceDeletionDirectory.MarkerFileName),
            JsonSerializer.Serialize(new { schemaVersion, transactionId, versionName, ownerProof = schemaVersion >= 2 ? InstanceDirectoryOwnership.Capture(directory) : null, createdAtUtc = DateTimeOffset.UtcNow }));
    }

    private static async Task<string> CreateStoredInstanceAsync(
        JsonGameInstanceRepository repository,
        string minecraftDirectory,
        string versionName)
    {
        CreateVersionDirectory(minecraftDirectory, versionName);
        var directory = Path.Combine(minecraftDirectory, "versions", versionName);
        await repository.SaveAllAsync(
        [
            new GameInstance
            {

                Name = versionName,
                MinecraftVersion = "1.20.1",
                VersionName = versionName,
                InstanceDirectory = directory
            }
        ]);
        return directory;
    }

    private static GameInstance CreateInstance(string id, string versionName, string instanceDirectory)
    {
        return new GameInstance
        {

            Name = versionName,
            MinecraftVersion = "1.20.1",
            VersionName = versionName,
            InstanceDirectory = instanceDirectory
        };
    }

    private static void WriteInstanceSettings(string instanceDirectory, string instanceId)
    {
        var metadataDirectory = Path.Combine(instanceDirectory, InstanceMetadataDirectoryName);
        Directory.CreateDirectory(metadataDirectory);
        File.WriteAllText(
            Path.Combine(metadataDirectory, "instance-settings.json"),
            JsonSerializer.Serialize(new GameInstance
            {

                Name = Path.GetFileName(instanceDirectory),
                MinecraftVersion = "1.20.1",
                VersionName = Path.GetFileName(instanceDirectory),
                InstanceDirectory = instanceDirectory
            }));
    }

    private (LauncherSettings Settings, TestSettingsService SettingsService) CreateSettings()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        return (settings, new TestSettingsService(settings));
    }

    private static void CreateVersionDirectory(string minecraftDirectory, string versionName)
    {
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, $"{versionName}.json"),
            $$"""
            {
              "id": "{{versionName}}",
              "jar": "{{versionName}}"
            }
            """);
        File.WriteAllText(Path.Combine(versionDirectory, $"{versionName}.jar"), "fake jar");
    }
}
