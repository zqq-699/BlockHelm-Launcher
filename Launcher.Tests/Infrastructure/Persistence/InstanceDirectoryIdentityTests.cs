/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Tests.Infrastructure.Persistence;

public sealed class InstanceDirectoryIdentityTests : TestTempDirectory
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExternalRenameOrCopyResolvesInstanceIconFromCurrentDirectory(bool legacyAbsolute, bool copy)
    {
        var (settings, _, service) = CreateService();
        var original = WriteVersion(settings.MinecraftDirectory, "A");
        var iconPath = Path.Combine(original, "BHL", "icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        await File.WriteAllBytesAsync(iconPath, [1, 2, 3]);
        var metadata = InstanceDirectoryOwnership.SettingsPath(original);
        if (legacyAbsolute)
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new
                { InstanceDirectory = original, IconSource = new Uri(iconPath).AbsoluteUri }));
        else
        {
            var instance = Assert.Single(await service.GetInstancesAsync());
            instance.IconSource = new Uri(iconPath).AbsoluteUri;
            await service.SaveInstanceAsync(instance);
            using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(metadata));
            Assert.Equal("instance:BHL/icon.png", stored.RootElement.GetProperty("IconSource").GetString());
        }
        var destination = Path.Combine(settings.MinecraftDirectory, "versions", "B");
        if (copy)
        {
            Directory.CreateDirectory(Path.Combine(destination, "BHL"));
            File.Copy(Path.Combine(original, "A.json"), Path.Combine(destination, "A.json"));
            File.Copy(Path.Combine(original, "A.jar"), Path.Combine(destination, "A.jar"));
            File.Copy(iconPath, Path.Combine(destination, "BHL", "icon.png"));
            File.Copy(metadata, InstanceDirectoryOwnership.SettingsPath(destination));
        }
        else Directory.Move(original, destination);
        var targetSettings = InstanceDirectoryOwnership.SettingsPath(destination);
        var before = await File.ReadAllBytesAsync(targetSettings);
        var selected = Assert.Single((await service.GetInstancesAsync()).Where(item => item.Name == "B"));
        Assert.Equal(Path.Combine(destination, "BHL", "icon.png"), new Uri(selected.IconSource!).LocalPath);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(new Uri(selected.IconSource!).LocalPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(targetSettings));
        await service.SaveInstanceAsync(selected);
        using var updated = JsonDocument.Parse(await File.ReadAllTextAsync(targetSettings));
        Assert.Equal("instance:BHL/icon.png", updated.RootElement.GetProperty("IconSource").GetString());
    }

    [Fact]
    public void InstanceIconPathRejectsTraversalAndKeepsExternalImages()
    {
        var directory = Path.Combine(TempRoot, "instance");
        var external = new Uri(Path.Combine(TempRoot, "outside.png")).AbsoluteUri;
        Assert.Null(InstanceIconPaths.Resolve("instance:../outside.png", directory, directory));
        Assert.Equal(external, InstanceIconPaths.Resolve(external, directory, Path.Combine(TempRoot, "other")));
        Assert.Equal("/Assets/icon.png", InstanceIconPaths.ToStorage("/Assets/icon.png", directory));
    }

    [Fact]
    public async Task LegacyIdMigrationKeepsIconAfterExternalRename()
    {
        var root = Path.Combine(TempRoot, "game");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        var original = WriteVersion(root, "A");
        var icon = Path.Combine(original, "BHL", "icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        await File.WriteAllBytesAsync(icon, [8]);
        await File.WriteAllTextAsync(InstanceDirectoryOwnership.SettingsPath(original), JsonSerializer.Serialize(new
            { Id = "legacy", InstanceDirectory = original, IconSource = new Uri(icon).AbsoluteUri }));
        var target = Path.Combine(root, "versions", "B");
        Directory.Move(original, target);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new
            { MinecraftDirectory = root, DefaultInstanceId = "legacy" }));
        var settings = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settings);
        var instance = Assert.Single(await repository.GetAllAsync(root));
        Assert.Equal(Path.Combine(target, "BHL", "icon.png"), new Uri(instance.IconSource!).LocalPath);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(InstanceDirectoryOwnership.SettingsPath(target)));
        Assert.Equal("instance:BHL/icon.png", stored.RootElement.GetProperty("IconSource").GetString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfiguredInstanceHasStableTimestampsAndGlobalMemoryDefault(bool corruptSettings)
    {
        var (settings, _, service) = CreateService();
        settings.DefaultMemoryMb = 6144;
        var directory = WriteVersion(settings.MinecraftDirectory, "Pack");
        if (corruptSettings)
        {
            var path = InstanceDirectoryOwnership.SettingsPath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "invalid json");
        }
        var first = Assert.Single(await service.GetInstancesAsync());
        var second = Assert.Single(await service.GetInstancesAsync());
        Assert.Equal(first.CreatedAt, first.UpdatedAt);
        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
        Assert.Equal(6144, second.MemoryMb);
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("Renamed")]
    public async Task RenamePreservesGeneratedProjectIconAndDirectoryName(string newName)
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "Pack");
        settings.DefaultInstanceDirectory = directory;
        var iconName = $"resource-project-icon-{Guid.NewGuid():N}.png";
        var iconPath = Path.Combine(directory, "BHL", iconName);
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        await File.WriteAllBytesAsync(iconPath, [1, 2, 3]);
        var instance = Assert.Single(await service.GetInstancesAsync());
        instance.IconSource = new Uri(iconPath).AbsoluteUri;
        await service.SaveInstanceAsync(instance);

        await service.RenameInstanceAsync(instance, newName, instance.IconSource);

        var refreshed = Assert.Single(await service.GetInstancesAsync());
        Assert.Equal(newName, refreshed.Name);
        Assert.Equal(newName, Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(settings.MinecraftDirectory, "versions")))));
        Assert.Equal(newName, Path.GetFileName(settings.DefaultInstanceDirectory));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(new Uri(refreshed.IconSource!).LocalPath));
        Assert.Equal(Path.Combine(settings.MinecraftDirectory, "versions", newName, "BHL", iconName), new Uri(refreshed.IconSource!).LocalPath);
    }

    [Fact]
    public async Task InterruptedCaseOnlyRenameResumesFromStaging()
    {
        var (settings, _, _) = CreateService();
        WriteVersion(settings.MinecraftDirectory, "Pack");
        var interrupted = true;
        var repository = new JsonGameInstanceRepository(new TestSettingsService(settings),
            moveDirectoryAsync: (source, destination, _) =>
            {
                if (interrupted && Path.GetFileName(destination) == "pack")
                    throw new IOException("Interrupted final move");
                Directory.Move(source, destination);
                return Task.CompletedTask;
            });
        var instance = Assert.Single(await repository.GetAllAsync());
        await Assert.ThrowsAsync<IOException>(() => repository.RenameVersionAsync(
            settings.MinecraftDirectory, instance, "pack", null, DateTimeOffset.UtcNow));
        interrupted = false;
        await repository.RecoverPendingVersionRenamesAsync(settings.MinecraftDirectory);
        var restored = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("pack", restored.Name);
        Assert.Equal("pack", Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(settings.MinecraftDirectory, "versions")))));
        Assert.Equal("test jar", await File.ReadAllTextAsync(Path.Combine(restored.InstanceDirectory, "pack.jar")));
    }

    [Fact]
    public async Task WatchTargetChangesWhenDirectoryIsReplacedAtTheSamePath()
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        var original = Assert.Single(await service.GetInstancesAsync());
        var unchanged = Assert.Single(await service.GetInstancesAsync());
        var monitor = new Launcher.Infrastructure.FileSystem.InstanceDirectoryMonitor();
        Assert.True(monitor.IsSameWatchTarget(original, unchanged));
        var settingsPath = InstanceDirectoryOwnership.SettingsPath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, """{"Description":"edited"}""");
        var changedSettings = Assert.Single(await service.GetInstancesAsync());
        Assert.True(monitor.IsSameWatchTarget(unchanged, changedSettings));
        Directory.Move(directory, Path.Combine(TempRoot, "old"));
        WriteVersion(settings.MinecraftDirectory, "A");
        var replacement = Assert.Single(await service.GetInstancesAsync());
        Assert.False(monitor.IsSameWatchTarget(original, replacement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationWriteCannotRecreateOrOverwriteAnObsoleteSource(bool replace)
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        var instance = Assert.Single(await service.GetInstancesAsync());
        var ownerProof = InstanceDirectoryOwnership.EnsureCurrent(instance);
        Directory.Move(directory, Path.Combine(TempRoot, "moved"));
        if (replace)
        {
            WriteVersion(settings.MinecraftDirectory, "A");
            var metadata = InstanceDirectoryOwnership.SettingsPath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
            await File.WriteAllTextAsync(metadata, """{"Description":"replacement"}""");
        }
        var store = new GameInstanceSettingsStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var failure = await Record.ExceptionAsync(() => store.PrepareInstallAsync(
            directory, directory, "A", instance, ownerProof, CancellationToken.None));

        Assert.True(failure is IOException or GameInstanceMutationConflictException);
        Assert.Equal(replace, Directory.Exists(directory));
        if (replace)
            Assert.Equal("""{"Description":"replacement"}""", await File.ReadAllTextAsync(InstanceDirectoryOwnership.SettingsPath(directory)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteSourceChangeBeforePreparationNeverCreatesAMarker(bool replace)
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        var instance = Assert.Single(await service.GetInstancesAsync());
        var repository = new JsonGameInstanceRepository(new TestSettingsService(settings),
            logger: null, moveDirectoryAsync: null, stageDeletionMove: null, deleteStagedDirectory: null, deletionGuidFactory: () =>
        {
            Directory.Move(directory, Path.Combine(TempRoot, "moved"));
            if (replace) WriteVersion(settings.MinecraftDirectory, "A");
            return Guid.NewGuid();
        });

        var failure = await Record.ExceptionAsync(() => repository.StageVersionForDeletionAsync(settings.MinecraftDirectory, instance));

        Assert.True(failure is IOException or GameInstanceMutationConflictException);
        Assert.Equal(replace, Directory.Exists(directory));
        Assert.False(File.Exists(PendingInstanceDeletionDirectory.GetMarkerPath(directory)));
        Assert.True(File.Exists(Path.Combine(TempRoot, "moved", "A.json")));
    }

    [Fact]
    public async Task RenamePreparationHoldsSourceUntilItsWritesAreComplete()
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        var instance = Assert.Single(await service.GetInstancesAsync());
        var blocked = false;
        var repository = new JsonGameInstanceRepository(new TestSettingsService(settings),
            logger: null, moveDirectoryAsync: null, deletionGuidFactory: null, stageDeletionMove: null, deleteStagedDirectory: null, renameGuidFactory: () =>
        {
            try { Directory.Move(directory, Path.Combine(TempRoot, "moved")); }
            catch (IOException) { blocked = true; }
            return Guid.NewGuid();
        });

        var failure = await Record.ExceptionAsync(() => repository.RenameVersionAsync(
            settings.MinecraftDirectory, instance, "B", null, DateTimeOffset.UtcNow));

        Assert.True(blocked);
        Assert.Null(failure);
        Assert.True(File.Exists(Path.Combine(settings.MinecraftDirectory, "versions", "B", "B.json")));
    }

    [Theory]
    [InlineData(".launcher-restore-orphan", "extract/Pack", false)]
    [InlineData(".launcher-restore-orphan", "restored", true)]
    [InlineData(".bhl-restore-orphan", "previous", true)]
    [InlineData(".hidden", "", false)]
    public async Task TransactionAndUnsafeDirectoriesNeverBecomeInstancesOrDefaults(string name, string nestedPath, bool hasSettings)
    {
        var (settings, repository, service) = CreateService();
        var current = WriteVersion(settings.MinecraftDirectory, "Pack");
        var staging = Path.Combine(settings.MinecraftDirectory, "versions", name);
        var nested = Path.Combine(staging, nestedPath);
        Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(current, "Pack.json"), Path.Combine(nested, "Pack.json"));
        if (hasSettings)
        {
            var metadata = InstanceDirectoryOwnership.SettingsPath(staging);
            Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
            await File.WriteAllTextAsync(metadata, """{"Name":"Pack"}""");
        }
        settings.DefaultInstanceDirectory = staging;

        Assert.Equal(current, Assert.Single(await repository.GetAllAsync()).InstanceDirectory);
        Assert.Equal(current, Assert.Single(await repository.DiscoverInstalledVersionsAsync(settings.MinecraftDirectory)).Directory);
        Assert.Equal(current, Assert.Single(await service.GetInstancesAsync()).InstanceDirectory);
        Assert.Equal(current, settings.DefaultInstanceDirectory);
        Assert.True(File.Exists(Path.Combine(nested, "Pack.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanDeletionPreparationIsPreservedWithoutBlockingMigration(bool matchesOwner)
    {
        var root = Path.Combine(TempRoot, "game");
        var directory = WriteVersion(root, "A");
        var settingsPath = InstanceDirectoryOwnership.SettingsPath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, """{"Id":"selected","MemoryMb":6144}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "keep");
        var markerPath = PendingInstanceDeletionDirectory.GetMarkerPath(directory);
        var markerBytes = JsonSerializer.SerializeToUtf8Bytes(new PendingInstanceDeletionMarker(
            3, Guid.NewGuid().ToString("N"), "A", matchesOwner ? InstanceDirectoryOwnership.Capture(directory) : "directory-v2:missing", DateTimeOffset.UtcNow));
        await File.WriteAllBytesAsync(markerPath, markerBytes);
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new { MinecraftDirectory = root, DefaultInstanceId = "selected" }));
        var settingsService = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settingsService);

        await repository.RecoverPendingVersionRenamesAsync(root);
        var instance = Assert.Single(await repository.GetAllAsync());

        Assert.Equal(directory, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        Assert.Equal(6144, instance.MemoryMb);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(directory, "keep.txt")));
        Assert.False(File.Exists(markerPath));
        var preserved = Assert.Single(Directory.EnumerateFiles(directory, PendingInstanceDeletionDirectory.MarkerFileName + ".*.orphaned"));
        Assert.Equal(markerBytes, await File.ReadAllBytesAsync(preserved));
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(Path.Combine(data, "settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalRenameOrCopyUsesActualDirectoryAndKeepsSettings(bool copy)
    {
        var (settings, repository, service) = CreateService();
        var original = WriteVersion(settings.MinecraftDirectory, "A");
        var metadata = Path.Combine(original, "BHL", "instance-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new
        {
            Id = "duplicated-legacy-id", Name = "old-name", VersionName = "old-version",
            InstanceDirectory = "old-path", MemoryMb = 6144, Description = "keep me",
            JavaSettingsMode = LaunchSettingsMode.PerInstance, SelectedJavaExecutablePath = @"C:\Java\bin\java.exe"
        }));
        var destination = Path.Combine(settings.MinecraftDirectory, "versions", "B");
        if (copy)
        {
            Directory.CreateDirectory(Path.Combine(destination, "BHL"));
            File.Copy(Path.Combine(original, "A.json"), Path.Combine(destination, "A.json"));
            File.Copy(Path.Combine(original, "A.jar"), Path.Combine(destination, "A.jar"));
            File.Copy(metadata, Path.Combine(destination, "BHL", "instance-settings.json"));
        }
        else Directory.Move(original, destination);

        var before = await File.ReadAllBytesAsync(Path.Combine(destination, "BHL", "instance-settings.json"));
        var instances = await service.GetInstancesAsync();
        Assert.Equal(copy ? 2 : 1, instances.Count);
        var renamed = Assert.Single(instances.Where(i => i.InstanceDirectory == destination));
        Assert.Equal("B", renamed.Name);
        Assert.Equal("B", renamed.VersionName);
        Assert.Equal(6144, renamed.MemoryMb);
        Assert.Equal("keep me", renamed.Description);
        Assert.Equal(@"C:\Java\bin\java.exe", renamed.SelectedJavaExecutablePath);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(destination, "BHL", "instance-settings.json")));
        renamed.MemoryMb = 8192;
        await service.SaveInstanceAsync(renamed);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "BHL", "instance-settings.json")));
        Assert.False(saved.RootElement.TryGetProperty("Id", out _));
        Assert.Equal(8192, Assert.Single((await repository.GetAllAsync()).Where(i => i.InstanceDirectory == destination)).MemoryMb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsMissingOrMalformedDoNotPreventDiscoveryAndExplicitSave(bool malformed)
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "Pack");
        if (malformed)
        {
            Directory.CreateDirectory(Path.Combine(directory, "BHL"));
            await File.WriteAllTextAsync(Path.Combine(directory, "BHL", "instance-settings.json"), "{broken");
        }
        var instance = Assert.Single(await service.GetInstancesAsync());
        instance.Description = "edited";
        await service.SaveInstanceAsync(instance);
        Assert.False(File.Exists(Path.Combine(directory, "Pack.json.json")));
        Assert.Equal("edited", Assert.Single(await service.GetInstancesAsync()).Description);
    }

    [Fact]
    public async Task ExternalSettingsEditRejectsOldSaveAndRefreshAcceptsNewSettings()
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "Pack");
        var stale = Assert.Single(await service.GetInstancesAsync());
        Directory.CreateDirectory(Path.Combine(directory, "BHL"));
        var path = Path.Combine(directory, "BHL", "instance-settings.json");
        await File.WriteAllTextAsync(path, """{"MemoryMb":8192,"Description":"external"}""");
        stale.Description = "stale";
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => service.SaveInstanceAsync(stale));
        var current = Assert.Single(await service.GetInstancesAsync());
        Assert.Equal(8192, current.MemoryMb);
        Assert.Equal("external", current.Description);
    }

    [Fact]
    public async Task UnreadableVersionMetadataFailsRefreshWithoutResettingDefault()
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        settings.DefaultInstanceDirectory = directory;
        Assert.Single(await service.GetInstancesAsync());
        using var locked = new FileStream(Path.Combine(directory, "A.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => service.GetInstancesAsync());
        Assert.Equal(directory, settings.DefaultInstanceDirectory);
    }

    [Fact]
    public async Task BulkSaveCannotRedirectAnObservedSnapshotByChangingItsVersionName()
    {
        var (settings, repository, service) = CreateService();
        WriteVersion(settings.MinecraftDirectory, "A");
        var otherDirectory = WriteVersion(settings.MinecraftDirectory, "B");
        var instances = await service.GetInstancesAsync();
        var other = Assert.Single(instances.Where(instance => instance.VersionName == "B"));
        other.Description = "keep";
        await service.SaveInstanceAsync(other);
        var path = InstanceDirectoryOwnership.SettingsPath(otherDirectory);
        var bytes = await File.ReadAllBytesAsync(path);
        var original = Assert.Single(instances.Where(instance => instance.VersionName == "A"));
        original.VersionName = "B";
        original.Description = "wrong target";
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => repository.SaveAllAsync([original]));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(2, "A")]
    public async Task LegacyDefaultMigratesOnlyOnUniqueMatchAndMigrationIsIdempotent(int matches, string expected)
    {
        var root = Path.Combine(TempRoot, "game");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        foreach (var name in new[] { "A", "B" })
        {
            var directory = WriteVersion(root, name);
            Directory.CreateDirectory(Path.Combine(directory, "BHL"));
            await File.WriteAllTextAsync(Path.Combine(directory, "BHL", "instance-settings.json"),
                JsonSerializer.Serialize(new { Id = name == "B" && matches >= 1 || matches == 2 ? "selected" : "other", MemoryMb = 6144 }));
        }
        var settingsPath = Path.Combine(data, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new { MinecraftDirectory = root, DefaultInstanceId = "selected" }));
        var settingsService = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settingsService);
        await repository.GetAllAsync(root);
        Assert.Equal(Path.Combine(root, "versions", expected), (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
        await repository.GetAllAsync(root);
        Assert.Equal(settingsBytes, await File.ReadAllBytesAsync(settingsPath));
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(settingsPath));
        foreach (var name in new[] { "A", "B" })
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "versions", name, "BHL", "instance-settings.json")));
            Assert.False(json.RootElement.TryGetProperty("Id", out _));
            Assert.Equal(6144, json.RootElement.GetProperty("MemoryMb").GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDeletionCleanupAllowsDefaultSelectionMigration(bool wasQuarantined)
    {
        var root = Path.Combine(TempRoot, "game");
        var selected = WriteVersion(root, "B");
        WriteVersion(root, "A");
        var metadata = InstanceDirectoryOwnership.SettingsPath(selected);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, """{"Id":"selected","MemoryMb":6144}""");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        var settingsPath = Path.Combine(data, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new { MinecraftDirectory = root, DefaultInstanceId = "selected" }));
        const string transactionId = "a83f21c4000000000000000000000000";
        var pending = Path.Combine(root, "versions", ".bhl-delete-pending-Deleted-a83f21c4");
        Directory.CreateDirectory(pending);
        await File.WriteAllTextAsync(Path.Combine(pending, "old.txt"), "old instance");
        await File.WriteAllTextAsync(Path.Combine(pending, wasQuarantined
            ? PendingInstanceDeletionDirectory.AbortedMarkerFileName : PendingInstanceDeletionDirectory.MarkerFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, transactionId, versionName = "Deleted", createdAtUtc = DateTimeOffset.UtcNow }));
        var settingsService = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settingsService, logger: null, moveDirectoryAsync: null,
            deletionGuidFactory: null, stageDeletionMove: null, deleteStagedDirectory: null,
            recycleStagedDirectory: path => Directory.Delete(path, recursive: true));
        await repository.GetAllAsync(root);
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(selected, (await settingsService.LoadAsync()).DefaultInstanceDirectory);

        await repository.CleanupStagedVersionDirectoriesAsync(root);
        await repository.GetAllAsync(root);

        Assert.False(Directory.Exists(pending));
        Assert.Equal(selected, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(6144, Assert.Single((await repository.GetAllAsync(root)).Where(instance => instance.InstanceDirectory == selected)).MemoryMb);
    }

    [Theory]
    [InlineData(".bhl-install-pending-unresolved")]
    [InlineData(".launcher-restore-unresolved")]
    public async Task UnresolvedTransactionAllowsSelectionMigrationAndPreservesLegacyIdUntilResolved(string transactionName)
    {
        var root = Path.Combine(TempRoot, "game");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        WriteVersion(root, "A");
        var selected = WriteVersion(root, "B");
        var metadata = InstanceDirectoryOwnership.SettingsPath(selected);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, """{"Id":"selected","MemoryMb":6144}""");
        var settingsPath = Path.Combine(data, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new { MinecraftDirectory = root, DefaultInstanceId = "selected" }));
        var pending = Path.Combine(root, "versions", transactionName);
        Directory.CreateDirectory(pending);
        var settingsService = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settingsService);
        var instances = await repository.GetAllAsync(root);
        Assert.Equal(selected, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(settingsPath));
        Assert.Contains("selected", await File.ReadAllTextAsync(metadata));
        var instance = Assert.Single(instances.Where(item => item.Name == "B"));
        instance.Description = "saved while transaction remains";
        await repository.UpdateInstanceAsync(root, instance);
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(metadata)))
        {
            Assert.Equal("selected", saved.RootElement.GetProperty("Id").GetString());
            Assert.Equal("saved while transaction remains", saved.RootElement.GetProperty("Description").GetString());
        }
        Assert.True(Directory.Exists(pending));
        Directory.Delete(pending);
        await repository.GetAllAsync(root);
        Assert.Equal(selected, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(settingsPath));
        Assert.DoesNotContain("\"Id\"", await File.ReadAllTextAsync(metadata));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(2, "A")]
    public async Task UnresolvedTransactionUsesStableFallbackWhenLegacyIdIsMissingOrDuplicated(int matches, string expected)
    {
        var root = Path.Combine(TempRoot, "game");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        foreach (var name in new[] { "A", "B" })
        {
            var directory = WriteVersion(root, name);
            var path = InstanceDirectoryOwnership.SettingsPath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Id = matches == 2 ? "selected" : "other" }));
        }
        var pending = Path.Combine(root, "versions", ".bhl-rename-pending-unresolved");
        Directory.CreateDirectory(pending);
        await File.WriteAllTextAsync(Path.Combine(pending, "keep.txt"), "user data");
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new
            { MinecraftDirectory = root, DefaultInstanceId = "selected" }));
        var settingsService = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settingsService);
        await repository.GetAllAsync(root);
        Assert.Equal(Path.Combine(root, "versions", expected), (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        Assert.Equal("user data", await File.ReadAllTextAsync(Path.Combine(pending, "keep.txt")));
    }

    [Fact]
    public async Task UnresolvedTransactionWithNoInstancesClearsLegacyDefault()
    {
        var root = Path.Combine(TempRoot, "game");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(root, "versions", ".bhl-install-pending-unresolved"));
        var path = Path.Combine(data, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { MinecraftDirectory = root, DefaultInstanceId = "selected" }));
        var settings = new JsonSettingsService(data);
        var repository = new JsonGameInstanceRepository(settings);
        Assert.Empty(await repository.GetAllAsync(root));
        Assert.True(string.IsNullOrEmpty((await settings.LoadAsync()).DefaultInstanceDirectory));
        Assert.DoesNotContain("DefaultInstanceId", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LegacyInterruptedRenameIsRecoveredBeforeRetiringItsOwnershipId()
    {
        var (settings, repository, _) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        Directory.CreateDirectory(Path.Combine(directory, "BHL"));
        await File.WriteAllTextAsync(Path.Combine(directory, "BHL", "instance-settings.json"),
            """{"Id":"legacy","VersionName":"A","MemoryMb":6144}""");
        await File.WriteAllTextAsync(Path.Combine(directory, ".bhl-rename-pending.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, transactionId = Guid.NewGuid().ToString("N"), instanceId = "legacy",
            oldName = "A", newName = "B", updatedAtUtc = DateTimeOffset.UtcNow
        }));
        WriteVersion(settings.MinecraftDirectory, "0-other");
        var data = Path.Combine(TempRoot, "data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new
        {
            settings.MinecraftDirectory, DefaultInstanceId = "legacy"
        }));
        var settingsService = new JsonSettingsService(data);
        repository = new JsonGameInstanceRepository(settingsService);
        await repository.RecoverPendingVersionRenamesAsync(settings.MinecraftDirectory);
        var recovered = Assert.Single((await repository.GetAllAsync()).Where(instance => instance.Name == "B"));
        Assert.Equal("B", recovered.VersionName);
        Assert.Equal(6144, recovered.MemoryMb);
        Assert.Equal(recovered.InstanceDirectory, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(Path.Combine(recovered.InstanceDirectory, ".bhl-rename-pending.json")));
    }

    [Fact]
    public async Task InstallRecoveryPreservesSamePathReplacementWithCopiedMarker()
    {
        var (settings, _, _) = CreateService();
        await using var transaction = await new InstanceInstallTransactionService().BeginAsync(settings.MinecraftDirectory, "Pack", "game", false);
        var marker = await File.ReadAllTextAsync(Path.Combine(transaction.PendingDirectory, PendingInstanceInstallDirectory.MarkerFileName));
        await transaction.AbortAsync();
        Directory.CreateDirectory(transaction.PendingDirectory);
        await File.WriteAllTextAsync(Path.Combine(transaction.PendingDirectory, PendingInstanceInstallDirectory.MarkerFileName), marker);
        var keep = Path.Combine(transaction.PendingDirectory, "keep.txt");
        await File.WriteAllTextAsync(keep, "replacement");
        await new InstanceInstallCleanupService(new TestSettingsService(settings)).CleanupPendingAsync();
        Assert.Equal("replacement", await File.ReadAllTextAsync(keep));
    }

    [Fact]
    public async Task RootChangeCannotRedirectRenameToAnUnrelatedSameNamedInstance()
    {
        var (settings, repository, _) = CreateService();
        WriteVersion(settings.MinecraftDirectory, "A");
        var instance = Assert.Single(await repository.GetAllAsync());
        var otherRoot = Path.Combine(TempRoot, "other-game");
        var unrelated = WriteVersion(otherRoot, "A");
        var json = Path.Combine(unrelated, "A.json");
        var before = await File.ReadAllBytesAsync(json);
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() =>
            repository.RenameVersionAsync(otherRoot, instance, "B", null, DateTimeOffset.UtcNow));
        Assert.Equal(before, await File.ReadAllBytesAsync(json));
        Assert.False(File.Exists(Path.Combine(unrelated, ".bhl-rename-pending.json")));
        Assert.False(Directory.Exists(Path.Combine(otherRoot, "versions", "B")));
    }

    [Fact]
    public async Task InternalRenameUpdatesDefaultAndAllowsSubsequentSave()
    {
        var (settings, _, service) = CreateService();
        var original = WriteVersion(settings.MinecraftDirectory, "A");
        settings.DefaultInstanceDirectory = original;
        var snapshot = Assert.Single(await service.GetInstancesAsync());
        var renamed = await service.RenameInstanceAsync(snapshot, "B", null);
        Assert.False(Directory.Exists(original));
        Assert.Equal(Path.Combine(settings.MinecraftDirectory, "versions", "B"), settings.DefaultInstanceDirectory);
        renamed.Description = "after rename";
        await service.SaveInstanceAsync(renamed);
        Assert.Equal("after rename", Assert.Single(await service.GetInstancesAsync()).Description);
    }

    [Fact]
    public async Task AmbiguousVersionJsonCandidatesAreNotGuessed()
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "Pack");
        File.Move(Path.Combine(directory, "Pack.json"), Path.Combine(directory, "A.json"));
        File.Copy(Path.Combine(directory, "A.json"), Path.Combine(directory, "B.json"));
        Assert.Empty(await service.GetInstancesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonMatchingVersionJsonWorksWhenParentDirectoryIsNamedBhl(bool copy)
    {
        var root = Path.Combine(TempRoot, "BHL", ".minecraft");
        var original = WriteVersion(root, "A");
        var destination = Path.Combine(root, "versions", "B");
        if (copy)
        {
            Directory.CreateDirectory(destination);
            File.Copy(Path.Combine(original, "A.json"), Path.Combine(destination, "A.json"));
            File.Copy(Path.Combine(original, "A.jar"), Path.Combine(destination, "A.jar"));
        }
        else Directory.Move(original, destination);
        var launcherSettingsDirectory = Path.Combine(destination, "BHL");
        Directory.CreateDirectory(launcherSettingsDirectory);
        await File.WriteAllTextAsync(Path.Combine(launcherSettingsDirectory, "unrelated.json"), """{"id":"other","type":"release"}""");
        var originalJson = await File.ReadAllBytesAsync(Path.Combine(destination, "A.json"));
        var settingsService = new TestSettingsService(new LauncherSettings { MinecraftDirectory = root });
        var service = new GameInstanceService(settingsService, new JsonGameInstanceRepository(settingsService), []);

        var instance = Assert.Single((await service.GetInstancesAsync()).Where(item => item.Name == "B"));
        Assert.Equal(destination, instance.InstanceDirectory);
        var launcher = VanillaLoaderProvider.CreateLauncher(root, progress: null);
        using var process = await launcher.BuildProcessAsync("B", new CmlLib.Core.ProcessBuilder.MLaunchOption { JavaPath = "java.exe" });
        Assert.Contains(Path.Combine(destination, "A.jar"), process.StartInfo.Arguments);
        Assert.Equal(originalJson, await File.ReadAllBytesAsync(Path.Combine(destination, "A.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedOldSnapshotCannotRenameOrDeleteAReplacement(bool delete)
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        settings.DefaultInstanceDirectory = directory;
        var old = Assert.Single(await service.GetInstancesAsync());
        Directory.Move(directory, Path.Combine(settings.MinecraftDirectory, "versions", "Moved"));
        WriteVersion(settings.MinecraftDirectory, "A");
        if (delete) Assert.False(await service.DeleteInstanceAsync(old));
        else await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => service.RenameInstanceAsync(old, "B", null));
        Assert.True(File.Exists(Path.Combine(directory, "A.jar")));
        Assert.False(Directory.Exists(Path.Combine(settings.MinecraftDirectory, "versions", "B")));
        var current = Assert.Single((await service.GetInstancesAsync()).Where(instance => instance.InstanceDirectory == directory));
        if (delete)
        {
            Assert.True(await service.DeleteInstanceAsync(current));
            Assert.False(Directory.Exists(directory));
            Assert.Equal(Path.Combine(settings.MinecraftDirectory, "versions", "Moved"), settings.DefaultInstanceDirectory);
        }
    }

    [Fact]
    public async Task FailedModpackImportCleanupPreservesAReplacementDirectory()
    {
        var (settings, _, service) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        var imported = Assert.Single(await service.GetInstancesAsync());
        Directory.Move(directory, Path.Combine(settings.MinecraftDirectory, "versions", "Moved"));
        WriteVersion(settings.MinecraftDirectory, "A");
        var cleanup = new ModpackImportCleanupCoordinator(service, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await cleanup.CleanupFailedImportAsync(new ModpackImportSession(null) { ImportedInstance = imported });
        Assert.True(File.Exists(Path.Combine(directory, "A.jar")));
        Assert.True(Directory.Exists(Path.Combine(settings.MinecraftDirectory, "versions", "Moved")));
        Assert.False(File.Exists(PendingInstanceDeletionDirectory.GetMarkerPath(directory)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RenameOfExternallyRenamedInstancePreservesClientAndCanResumeOrRollback(int interruption)
    {
        var (settings, _, _) = CreateService();
        var original = WriteVersion(settings.MinecraftDirectory, "A");
        var copied = Path.Combine(settings.MinecraftDirectory, "versions", "Copy");
        Directory.Move(original, copied);
        var failMove = interruption != 0;
        var repository = new JsonGameInstanceRepository(new TestSettingsService(settings),
            moveDirectoryAsync: (source, destination, _) =>
            {
                if (Path.GetFileName(destination) == "B" && failMove)
                {
                    if (interruption == 2) File.WriteAllText(destination, "keep");
                    throw new IOException("Interrupted final move");
                }
                Directory.Move(source, destination);
                return Task.CompletedTask;
            });
        var instance = Assert.Single(await repository.GetAllAsync());
        var rename = repository.RenameVersionAsync(settings.MinecraftDirectory, instance, "B", null, DateTimeOffset.UtcNow);
        if (interruption == 0) await rename;
        else await Assert.ThrowsAnyAsync<IOException>(async () => await rename);
        failMove = false;
        await repository.RecoverPendingVersionRenamesAsync(settings.MinecraftDirectory);

        var directory = Path.Combine(settings.MinecraftDirectory, "versions", interruption == 2 ? "Copy" : "B");
        var jsonPath = Path.Combine(directory, interruption == 2 ? "A.json" : "B.json");
        Assert.True(File.Exists(jsonPath));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(interruption == 2 ? "A" : "B", json.RootElement.GetProperty("id").GetString());
        Assert.Equal(interruption == 2 ? "A" : "B", json.RootElement.GetProperty("jar").GetString());
        Assert.Equal("test jar", await File.ReadAllTextAsync(Path.ChangeExtension(jsonPath, ".jar")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(settings.MinecraftDirectory, "versions"), ".bhl-rename-pending-*"));
    }

    [Fact]
    public async Task RenameRejectsConflictingClientBeforeMovingTheInstance()
    {
        var (settings, repository, _) = CreateService();
        var directory = WriteVersion(settings.MinecraftDirectory, "A");
        await File.WriteAllTextAsync(Path.Combine(directory, "B.jar"), "user-owned");
        var instance = Assert.Single(await repository.GetAllAsync());
        await Assert.ThrowsAsync<IOException>(() => repository.RenameVersionAsync(settings.MinecraftDirectory, instance, "B", null, DateTimeOffset.UtcNow));
        Assert.Equal("test jar", await File.ReadAllTextAsync(Path.Combine(directory, "A.jar")));
        Assert.Equal("user-owned", await File.ReadAllTextAsync(Path.Combine(directory, "B.jar")));
        Assert.False(File.Exists(PendingInstanceRenameDirectory.GetMarkerPath(directory)));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(settings.MinecraftDirectory, "versions")));
    }

    [Fact]
    public async Task RenamedDirectoryBuildsCommandWithOriginalJarWithoutWritingGameFiles()
    {
        var root = Path.Combine(TempRoot, "game");
        var original = WriteVersion(root, "A");
        var renamed = Path.Combine(root, "versions", "B");
        Directory.Move(original, renamed);
        var json = Path.Combine(renamed, "A.json");
        var bytes = await File.ReadAllBytesAsync(json);
        var launcher = VanillaLoaderProvider.CreateLauncher(root, progress: null);
        using var process = await launcher.BuildProcessAsync("B", new CmlLib.Core.ProcessBuilder.MLaunchOption { JavaPath = "java.exe" });
        Assert.Contains(Path.Combine(renamed, "A.jar"), process.StartInfo.Arguments);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(json));
        Assert.False(File.Exists(Path.Combine(renamed, "B.json")));
    }

    [Fact]
    public void JavaRequirementUsesActualJsonAfterExternalRename()
    {
        var root = Path.Combine(TempRoot, "game");
        var original = WriteVersion(root, "A");
        var directory = Path.Combine(root, "versions", "B");
        Directory.Move(original, directory);
        var path = Path.Combine(directory, "A.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["javaVersion"] = new JsonObject { ["majorVersion"] = 21 };
        File.WriteAllText(path, json.ToJsonString());
        var instance = new GameInstance { InstanceDirectory = directory, VersionName = "B", MinecraftVersion = "1.20.1" };
        Assert.Equal(21, JavaRuntimeSelectionService.ResolveRequiredMajorVersion(instance, new LauncherSettings { MinecraftDirectory = root }));
    }

    private (LauncherSettings Settings, JsonGameInstanceRepository Repository, GameInstanceService Service) CreateService()
    {
        var settings = new LauncherSettings { MinecraftDirectory = Path.Combine(TempRoot, "game") };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        return (settings, repository, new GameInstanceService(settingsService, repository, []));
    }

    private static string WriteVersion(string root, string name)
    {
        var directory = Path.Combine(root, "versions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            id = name, jar = name, mainClass = "example.Main", libraries = Array.Empty<object>(),
            arguments = new { game = Array.Empty<string>(), jvm = new[] { "-cp", "${classpath}" } }
        }));
        File.WriteAllText(Path.Combine(directory, name + ".jar"), "test jar");
        return directory;
    }
}
