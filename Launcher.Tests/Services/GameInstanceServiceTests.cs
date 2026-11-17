/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Tests.Services;

public sealed class GameInstanceServiceTests : TestTempDirectory
{
    [Fact]
    public async Task SelectingInstanceDoesNotScanUnrelatedVersionsAndRejectsRemovedTarget()
    {
        var settings = CreateSettings();
        var settingsService = new TestSettingsService(settings);
        await CreateVersionAsync(settings.MinecraftDirectory, "selected", type: "release");
        await CreateVersionAsync(settings.MinecraftDirectory, "unrelated", type: "release");
        var selected = Path.Combine(settings.MinecraftDirectory, "versions", "selected");
        var unrelatedJson = Path.Combine(settings.MinecraftDirectory, "versions", "unrelated", "unrelated.json");
        var repository = new JsonGameInstanceRepository(settingsService);
        var service = new GameInstanceService(settingsService, repository, []);
        await using (var held = new FileStream(unrelatedJson, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(await service.SetDefaultInstanceAsync(selected));
            Assert.Equal(selected, settings.DefaultInstanceDirectory);
        }
        Directory.Move(selected, Path.Combine(TempRoot, "moved"));
        Assert.False(await service.SetDefaultInstanceAsync(selected));
        Assert.False(Directory.Exists(selected));
    }

    [Fact]
    public async Task CatalogReadCannotObserveATemporarySwapAndResetDefault()
    {
        var settings = CreateSettings("selected");
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        await CreateVersionAsync(settings.MinecraftDirectory, "selected", type: "release");
        await CreateVersionAsync(settings.MinecraftDirectory, "0-other", type: "release");
        var selected = CreateStoredInstance("selected");
        selected.MemoryMb = 6144;
        await repository.SaveAllAsync(settings.MinecraftDirectory, [selected]);
        var blocked = false;
        var loads = 0;
        settingsService.BeforeLoad = () =>
        {
            if (Interlocked.Increment(ref loads) != 2) return;
            using var mutationLock = CrossProcessVersionLock.TryAcquire(CrossProcessVersionLock.GetMutationPath(settings.MinecraftDirectory));
            if (mutationLock is null) { blocked = true; return; }
            Directory.Move(selected.InstanceDirectory, Path.Combine(TempRoot, "active-swap"));
        };
        var service = new GameInstanceService(settingsService, repository, []);

        var instances = await service.GetInstancesAsync();

        Assert.Equal(selected.InstanceDirectory, settings.DefaultInstanceDirectory);
        Assert.True(blocked);
        Assert.Equal(6144, Assert.Single(instances.Where(instance => instance.VersionName == "selected")).MemoryMb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryChangeDuringDiscoveryRejectsTheMixedSnapshot(bool replace)
    {
        var settings = CreateSettings("selected");
        var settingsService = new TestSettingsService(settings);
        var directory = Path.Combine(settings.MinecraftDirectory, "versions", "selected");
        await CreateVersionAsync(settings.MinecraftDirectory, "selected", type: "release");
        var changed = false;
        var logger = new DiscoveryCallbackLogger(() =>
        {
            if (changed) return;
            changed = true;
            Directory.Move(directory, Path.Combine(TempRoot, "moved"));
            if (replace)
            {
                Directory.CreateDirectory(directory);
                File.Copy(Path.Combine(TempRoot, "moved", "selected.json"), Path.Combine(directory, "selected.json"));
                File.WriteAllText(Path.Combine(directory, "keep.txt"), "replacement");
            }
        });
        var service = new GameInstanceService(settingsService, new JsonGameInstanceRepository(settingsService, logger), []);

        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => service.GetInstancesAsync());

        Assert.True(changed);
        Assert.Equal(directory, settings.DefaultInstanceDirectory);
        Assert.Equal(replace, Directory.Exists(directory));
        if (replace) Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(directory, "keep.txt")));
    }

    private sealed class DiscoveryCallbackLogger(Action callback) : ILogger<JsonGameInstanceRepository>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties && properties.Any(property => property.Key == "VersionCount"))
                callback();
        }
    }

    [Fact]
    public async Task CreateInstanceCreatesIsolatedDirectories()
    {
        var (settings, repository, service, provider) = CreateService();

        var instance = await service.CreateInstanceAsync(
            "1.20.1", LoaderKind.Vanilla, null, "Test Instance", null);

        Assert.Equal("Test Instance", provider.LastIsolatedVersionName);
        Assert.Equal(Path.Combine(settings.MinecraftDirectory, "versions", "Test Instance"), instance.InstanceDirectory);
        Assert.True(Directory.Exists(Path.Combine(instance.InstanceDirectory, "mods")));
        Assert.True(Directory.Exists(Path.Combine(instance.InstanceDirectory, "config")));
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public async Task CreateInstanceRejectsPathLikeName()
    {
        var (settings, repository, service, provider) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateInstanceAsync("1.20.1", LoaderKind.Vanilla, null, @"..\Pack", null));

        Assert.Null(provider.LastIsolatedVersionName);
        Assert.Empty(await repository.GetAllAsync());
        Assert.False(Directory.Exists(Path.Combine(settings.MinecraftDirectory, "Pack")));
    }

    [Fact]
    public async Task CreateCancellationRemovesNewVersionDirectory()
    {
        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeLoaderProvider
        {
            WriteJsonBeforeWaiting = true,
            PartialVersionName = "1.20.1",
            WaitBeforeInstall = wait.Task
        };
        var (settings, repository, service, _) = CreateService(provider);
        using var cancellation = new CancellationTokenSource();

        var create = service.CreateInstanceAsync(
            "1.20.1", LoaderKind.Vanilla, null, "Custom Name", null,
            cancellationToken: cancellation.Token);
        await provider.InstallStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        Assert.False(Directory.Exists(Path.Combine(settings.MinecraftDirectory, "versions", "1.20.1")));
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task DeleteInstancePreservesOriginalStateWhenStagingMoveFails()
    {
        var settings = CreateSettings("first");
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: () => Guid.ParseExact("a83f21c4000000000000000000000000", "N"),
            stageDeletionMove: (_, _) => throw new IOException("source is locked"),
            deleteStagedDirectory: null);
        var service = new GameInstanceService(settingsService, repository, [new FakeLoaderProvider()]);
        await CreateVersionAsync(settings.MinecraftDirectory, "first");
        await repository.SaveAllAsync([CreateStoredInstance("first")]);

        await Assert.ThrowsAsync<IOException>(() => service.DeleteInstanceAsync(Path.Combine(settings.MinecraftDirectory, "versions", "first")));

        Assert.True(Directory.Exists(Path.Combine(settings.MinecraftDirectory, "versions", "first")));
        Assert.Single(await repository.GetAllAsync());
        Assert.Equal(Path.Combine(settings.MinecraftDirectory, "versions", "first"), (await settingsService.LoadAsync()).DefaultInstanceDirectory);
    }

    [Fact]
    public async Task DeleteInstanceRemainsCommittedWhenPhysicalCleanupFails()
    {
        var settings = CreateSettings("first");
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: () => Guid.ParseExact("a83f21c4000000000000000000000000", "N"),
            stageDeletionMove: Directory.Move,
            deleteStagedDirectory: (_, _) => throw new IOException("directory is still in use"),
            recycleStagedDirectory: _ => throw new IOException("recycle bin unavailable"));
        var service = new GameInstanceService(settingsService, repository, [new FakeLoaderProvider()]);
        await CreateVersionAsync(settings.MinecraftDirectory, "first");
        await repository.SaveAllAsync([CreateStoredInstance("first")]);

        Assert.True(await service.DeleteInstanceAsync(Path.Combine(settings.MinecraftDirectory, "versions", "first")));

        var versionsDirectory = Path.Combine(settings.MinecraftDirectory, "versions");
        Assert.False(Directory.Exists(Path.Combine(versionsDirectory, "first")));
        var pendingDirectory = Path.Combine(versionsDirectory, ".bhl-delete-pending-first-a83f21c4");
        Assert.True(Directory.Exists(pendingDirectory));
        Assert.True(File.Exists(Path.Combine(pendingDirectory, ".bhl-delete-pending-.json")));
        Assert.Empty(await repository.GetAllAsync());
        Assert.Empty(await service.GetInstancesAsync());
        Assert.Equal(string.Empty, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
    }

    [Fact]
    public async Task DeleteInstanceRejectsConcurrentDuplicateRequest()
    {
        var settings = CreateSettings();
        var settingsService = new TestSettingsService(settings);
        var moveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowMove = new ManualResetEventSlim();
        var moveCount = 0;
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: () => Guid.NewGuid(),
            stageDeletionMove: (source, destination) =>
            {
                Interlocked.Increment(ref moveCount);
                moveStarted.TrySetResult();
                if (!allowMove.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("test did not release staged deletion move");
                Directory.Move(source, destination);
            },
            deleteStagedDirectory: null,
            recycleStagedDirectory: path => Directory.Delete(path, recursive: true));
        var service = new GameInstanceService(settingsService, repository, [new FakeLoaderProvider()]);
        await CreateVersionAsync(settings.MinecraftDirectory, "first");
        await repository.SaveAllAsync([CreateStoredInstance("first")]);

        var firstDelete = service.DeleteInstanceAsync(Path.Combine(settings.MinecraftDirectory, "versions", "first"));
        await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateResult = await service.DeleteInstanceAsync(Path.Combine(settings.MinecraftDirectory, "versions", "first"));
        allowMove.Set();

        Assert.False(duplicateResult);
        Assert.True(await firstDelete);
        Assert.Equal(1, moveCount);
    }

    private (LauncherSettings Settings, JsonGameInstanceRepository Repository, GameInstanceService Service, FakeLoaderProvider Provider)
        CreateService(FakeLoaderProvider? provider = null, string? defaultInstanceId = null)
    {
        var settings = CreateSettings(defaultInstanceId);
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(
            settingsService,
            logger: null,
            moveDirectoryAsync: null,
            deletionGuidFactory: null,
            stageDeletionMove: null,
            deleteStagedDirectory: null,
            recycleStagedDirectory: path => Directory.Delete(path, recursive: true));
        provider ??= new FakeLoaderProvider();
        var installer = new ModpackGameInstaller([provider]);
        var transactionService = new InstanceInstallTransactionService();
        return (
            settings,
            repository,
            new GameInstanceService(
                settingsService,
                repository,
                [provider],
                modpackGameInstaller: installer,
                installTransactionService: transactionService),
            provider);
    }

    private LauncherSettings CreateSettings(string? defaultInstanceId = null) => new()
    {
        DataDirectory = TempRoot,
        MinecraftDirectory = Path.Combine(TempRoot, ".minecraft"),
        DefaultInstanceDirectory = defaultInstanceId is null ? string.Empty : Path.Combine(TempRoot, ".minecraft", "versions", defaultInstanceId)
    };

    private static async Task CreateVersionAsync(
        string minecraftDirectory,
        string versionName,
        string? type = null,
        string? inheritsFrom = null,
        string? library = null,
        bool writeJar = false)
    {
        var directory = Path.Combine(minecraftDirectory, "versions", versionName);
        Directory.CreateDirectory(directory);
        var json = new JsonObject { ["id"] = versionName, ["jar"] = versionName };
        if (type is not null) json["type"] = type;
        if (inheritsFrom is not null) json["inheritsFrom"] = inheritsFrom;
        if (library is not null)
            json["libraries"] = new JsonArray(new JsonObject { ["name"] = library });
        await File.WriteAllTextAsync(Path.Combine(directory, $"{versionName}.json"), json.ToJsonString());
        if (writeJar)
            await File.WriteAllTextAsync(Path.Combine(directory, $"{versionName}.jar"), "fake jar");
    }

    private GameInstance CreateStoredInstance(string name) => new()
    {

        Name = name,
        MinecraftVersion = "1.20.1",
        VersionName = name,
        InstanceDirectory = Path.Combine(TempRoot, ".minecraft", "versions", name)
    };
}
