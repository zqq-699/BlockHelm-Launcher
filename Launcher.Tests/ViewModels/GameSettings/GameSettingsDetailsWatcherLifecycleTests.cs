/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Reflection;
using System.Text.Json;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Tests.Fakes;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class GameSettingsDetailsWatcherLifecycleTests : TestTempDirectory
{
    [Fact]
    public async Task SuccessfulRestorePublishesTheNewInstanceSnapshot()
    {
        var original = CreateInstanceItem(Path.Combine(TempRoot, "instance"));
        original.Instance.BackupDirectory = Path.Combine(TempRoot, "backups");
        var refreshed = CreateInstanceItem(original.Instance.InstanceDirectory).Instance;
        refreshed.BackupDirectory = original.Instance.BackupDirectory;
        var instanceService = new FakeGameInstanceService();
        instanceService.CreatedInstances.Add(refreshed);
        var backups = new RecordingBackupService();
        using var details = CreateDetails(new RecordingDirectoryMonitor(), instanceService: instanceService, backupService: backups);
        GameInstance? published = null;
        details.InstanceSettingsSaved += instance => published = instance;
        details.SetSelectedInstance(original);
        await details.Backup.OnSectionActivatedAsync();
        var backup = Assert.Single(details.Backup.VisibleBackups);

        details.Backup.RequestRestoreBackupCommand.Execute(backup);
        await details.Backup.ConfirmRestoreBackupCommand.ExecuteAsync(null);

        Assert.True(backups.Restored);
        Assert.Same(refreshed, published);
    }

    [Fact]
    public async Task ReplacedDirectorySnapshotRearmsTheContentWatcher()
    {
        var monitor = new RecordingDirectoryMonitor { RebindOnNewSnapshot = true };
        using var details = CreateDetails(monitor);
        var item = CreateInstanceItem(Path.Combine(TempRoot, "same"));
        details.SetSelectedInstance(item);
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection("saves"));
        await details.SaveManagement.OnSectionActivatedAsync();
        Assert.Equal(1, monitor.WatchStartCount);

        item.Update(CreateInstanceItem(item.Instance.InstanceDirectory).Instance, "release");
        details.SetSelectedInstance(item);
        await details.SaveManagement.OnSectionActivatedAsync();

        Assert.Equal(2, monitor.WatchStartCount);
        Assert.False(monitor.StartedBeforePreviousWatchWasDisposed);
        details.SetPageActive(false);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("launch")]
    [InlineData("java")]
    public async Task UnchangedRefreshPreservesEditorAndPendingSave(string area)
    {
        var (service, instance) = await CreateEditableInstanceAsync();
        using var details = CreateDetails(new RecordingDirectoryMonitor(), instanceService: service);
        var item = new GameSettingsInstanceItem(instance, "release");
        details.SetSelectedInstance(item);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        details.InstanceSettingsSaved += _ => saved.TrySetResult();
        void AssertEditor()
        {
            if (area == "description") Assert.Equal("draft", details.General.DescriptionText);
            else if (area == "launch") Assert.Equal("--draft", details.Launch.LaunchGameArguments);
            else Assert.Equal(LaunchSettingsMode.UseGlobal, details.Java.SelectedInstanceJavaSettingsModeOption!.Mode);
        }
        if (area == "description") details.General.DescriptionText = "draft";
        else if (area == "launch") details.Launch.LaunchGameArguments = "--draft";
        else details.Java.SelectedInstanceJavaSettingsModeOption = details.Java.LaunchSettingsModeOptions[0];

        // Even rebinding the exact same item must not refill an editor with unsaved disk values.
        details.SetSelectedInstance(item);
        AssertEditor();
        if (area == "description")
            await File.WriteAllTextAsync(Path.Combine(instance.InstanceDirectory, "Pack.json"),
                """{"id":"Pack","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
        var refreshed = Assert.Single(await service.GetInstancesAsync());
        Assert.NotSame(instance, refreshed);
        item.Update(refreshed, "release");
        details.SetSelectedInstance(item);
        AssertEditor();
        if (area == "description")
        {
            Assert.Equal(LoaderKind.Fabric, instance.Loader);
            Assert.Equal("0.16.9", instance.LoaderVersion);
        }
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = JsonSerializer.Deserialize<GameInstance>(await File.ReadAllTextAsync(
            InstanceDirectoryOwnership.SettingsPath(instance.InstanceDirectory)))!;
        if (area == "description") Assert.Equal("draft", persisted.Description);
        else if (area == "launch") Assert.Equal("--draft", persisted.GameArguments);
        else Assert.Equal(LaunchSettingsMode.UseGlobal, persisted.JavaSettingsMode);
    }

    [Fact]
    public async Task ReadResultFromBeforeALocalSaveDoesNotCancelNewerDraft()
    {
        var (service, instance) = await CreateEditableInstanceAsync();
        using var details = CreateDetails(new RecordingDirectoryMonitor(), instanceService: service);
        var item = new GameSettingsInstanceItem(instance, "release");
        details.SetSelectedInstance(item);
        var olderRead = Assert.Single(await service.GetInstancesAsync());
        var firstSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        details.InstanceSettingsSaved += _ =>
        {
            if (Interlocked.Increment(ref count) == 1) firstSaved.TrySetResult();
            else secondSaved.TrySetResult();
        };
        details.Launch.LaunchGameArguments = "--saved";
        await firstSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        details.General.DescriptionText = "newer draft";

        item.Update(olderRead, "release");
        details.SetSelectedInstance(item);

        Assert.Equal("newer draft", details.General.DescriptionText);
        Assert.Equal("--saved", details.Launch.LaunchGameArguments);
        await secondSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var persisted = JsonSerializer.Deserialize<GameInstance>(await File.ReadAllTextAsync(
            InstanceDirectoryOwnership.SettingsPath(instance.InstanceDirectory)))!;
        Assert.Equal("newer draft", persisted.Description);
        Assert.Equal("--saved", persisted.GameArguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSettingsOrReplacedDirectoryStillRejectsOldEditorSnapshot(bool replaceDirectory)
    {
        var (service, instance) = await CreateEditableInstanceAsync();
        using var details = CreateDetails(new RecordingDirectoryMonitor(), instanceService: service);
        var item = new GameSettingsInstanceItem(instance, "release");
        details.SetSelectedInstance(item);
        details.General.DescriptionText = "draft";
        var directory = instance.InstanceDirectory;
        var metadataPath = InstanceDirectoryOwnership.SettingsPath(directory);
        if (replaceDirectory)
        {
            Directory.Move(directory, directory + "-moved");
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            File.Copy(InstanceDirectoryOwnership.SettingsPath(directory + "-moved"), metadataPath);
            File.Copy(Path.Combine(directory + "-moved", "Pack.json"), Path.Combine(directory, "Pack.json"));
        }
        else
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
            json["Description"] = "external";
            await File.WriteAllTextAsync(metadataPath, json.ToJsonString());
        }
        var refreshed = Assert.Single((await service.GetInstancesAsync()).Where(candidate => candidate.InstanceDirectory == directory));
        Assert.NotSame(instance, refreshed);
        item.Update(refreshed, "release");
        details.SetSelectedInstance(item);

        Assert.Equal(replaceDirectory ? "stored" : "external", details.General.DescriptionText);
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => service.SaveInstanceAsync(instance));
        Assert.Equal(replaceDirectory ? "stored" : "external", JsonSerializer.Deserialize<GameInstance>(await File.ReadAllTextAsync(metadataPath))!.Description);
    }

    private async Task<(GameInstanceService Service, GameInstance Instance)> CreateEditableInstanceAsync()
    {
        var settings = new LauncherSettings { MinecraftDirectory = Path.Combine(TempRoot, "game") };
        var directory = Path.Combine(settings.MinecraftDirectory, "versions", "Pack");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "Pack.json"), """{"id":"Pack","type":"release","mainClass":"example.Main","libraries":[]}""");
        var settingsService = new TestSettingsService(settings);
        var service = new GameInstanceService(settingsService, new JsonGameInstanceRepository(settingsService), []);
        var instance = Assert.Single(await service.GetInstancesAsync());
        instance.Description = "stored";
        instance.LaunchSettingsMode = LaunchSettingsMode.PerInstance;
        instance.JavaSettingsMode = LaunchSettingsMode.PerInstance;
        await service.SaveInstanceAsync(instance);
        return (service, instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupDirectorySaveAndDelayedDescriptionShareCurrentSnapshot(bool descriptionFirst)
    {
        var (service, instance) = await CreateEditableInstanceAsync();
        var refreshed = Assert.Single(await service.GetInstancesAsync());
        using var persistence = new InstanceSettingsPersistenceCoordinator(service, Stub<IStatusService>(),
            ImmediateUiDispatcher.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        persistence.SetInstance(instance);
        var descriptionSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        persistence.InstanceSaved += saved =>
        {
            if (saved.Description == "draft") descriptionSaved.TrySetResult();
        };
        persistence.Schedule("description", instance, target =>
        {
            var old = target.Description;
            target.Description = "draft";
            return () => target.Description = old;
        }, () => { }, descriptionFirst ? TimeSpan.Zero : TimeSpan.FromMilliseconds(100));

        if (descriptionFirst)
            await descriptionSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var backupDirectory = Path.Combine(TempRoot, "backups");
        Assert.True(await persistence.SaveAsync(refreshed, target =>
        {
            var old = target.BackupDirectory;
            target.BackupDirectory = backupDirectory;
            return () => target.BackupDirectory = old;
        }));
        await descriptionSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stored = JsonSerializer.Deserialize<GameInstance>(
            await File.ReadAllTextAsync(InstanceDirectoryOwnership.SettingsPath(instance.InstanceDirectory)))!;
        Assert.Equal("draft", stored.Description);
        Assert.Equal(backupDirectory, stored.BackupDirectory);
    }

    [Fact]
    public async Task StaleEditorMutationCannotMergeIntoExternallyChangedSettings()
    {
        var (service, instance) = await CreateEditableInstanceAsync();
        var path = InstanceDirectoryOwnership.SettingsPath(instance.InstanceDirectory);
        var original = JsonSerializer.Deserialize<GameInstance>(await File.ReadAllTextAsync(path))!;
        original.Description = "external";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original));
        var current = Assert.Single(await service.GetInstancesAsync());
        using var persistence = new InstanceSettingsPersistenceCoordinator(service, Stub<IStatusService>(),
            ImmediateUiDispatcher.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        persistence.SetInstance(current);
        var applied = false;

        persistence.Schedule("description", instance, target =>
        {
            applied = true;
            target.Description = "stale";
            return () => { };
        }, () => { });

        Assert.False(applied);
        Assert.Equal("external", current.Description);
        Assert.Equal("external", JsonSerializer.Deserialize<GameInstance>(await File.ReadAllTextAsync(path))!.Description);
    }

    [Fact]
    public void JavaSectionUsesFullViewportAndRestoresScrollableHostWhenLeaving()
    {
        using var details = CreateDetails(new RecordingDirectoryMonitor());

        details.SetSelectedSection(CreateSection("java"));
        Assert.Same(details.Java, details.NonRetainedFullViewportSectionViewModel);
        Assert.Null(details.ScrollSectionViewModel);

        details.SetSelectedSection(CreateSection("general"));
        Assert.Same(details.General, details.ScrollSectionViewModel);
        Assert.Null(details.NonRetainedFullViewportSectionViewModel);
    }

    [Theory]
    [InlineData("mod_management", InstanceDirectoryKind.Mods)]
    public async Task ResourceSectionUsesExpectedWatcherLifecycle(string sectionId, InstanceDirectoryKind expectedKind)
    {
        var monitor = new RecordingDirectoryMonitor();
        using var details = CreateDetails(monitor);
        details.SetSelectedInstance(CreateInstanceItem());
        details.SetPageActive(true);

        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();

        Assert.Equal([expectedKind], monitor.ActiveKinds);
        Assert.False(monitor.StartedBeforePreviousWatchWasDisposed);

        details.SetSelectedSection(CreateSection("general"));
        Assert.Equal([expectedKind], monitor.ActiveKinds);
        Assert.True(GetHasLoaded(details, expectedKind));
        var entranceAnimationToken = GetEntranceAnimationToken(details, expectedKind);

        details.SetSelectedSection(CreateSection(sectionId));
        var silentRefresh = details.CurrentSectionViewModel!.OnSectionActivatedAsync();
        Assert.True(GetHasLoaded(details, expectedKind));
        Assert.False(GetIsLoading(details, expectedKind));
        Assert.Equal(entranceAnimationToken, GetEntranceAnimationToken(details, expectedKind));
        await silentRefresh;
        Assert.Equal(entranceAnimationToken, GetEntranceAnimationToken(details, expectedKind));
        details.SetPageActive(false);
    }

    [Theory]
    [InlineData("saves", InstanceDirectoryKind.Saves)]
    public async Task RepeatedLocalContentActivationWithoutChangesDoesNotReloadInventory(
        string sectionId,
        InstanceDirectoryKind kind)
    {
        var monitor = new RecordingDirectoryMonitor();
        var services = new RecordingLocalContentServices();
        using var details = CreateDetails(
            monitor,
            saveService: services,
            resourcePackService: services,
            shaderPackService: services);
        details.SetSelectedInstance(CreateInstanceItem());
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();
        var entranceAnimationToken = GetEntranceAnimationToken(details, kind);
        var item = GetFirstVisibleItem(details, kind);

        details.SetSelectedSection(CreateSection("general"));
        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();
        details.SetSelectedSection(CreateSection("general"));
        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();

        Assert.Equal(1, services.GetCallCount(kind));
        Assert.Same(item, GetFirstVisibleItem(details, kind));
        Assert.Equal(entranceAnimationToken, GetEntranceAnimationToken(details, kind));
        Assert.Equal([kind], monitor.ActiveKinds);
        details.SetPageActive(false);
    }

    [Theory]
    [InlineData("saves", InstanceDirectoryKind.Saves, "saves", "world")]
    public async Task HiddenLocalContentChangesAreCoalescedUntilNextActivation(
        string sectionId,
        InstanceDirectoryKind kind,
        string directoryName,
        string itemName)
    {
        var monitor = new RecordingDirectoryMonitor();
        var services = new RecordingLocalContentServices();
        using var details = CreateDetails(
            monitor,
            saveService: services,
            resourcePackService: services,
            shaderPackService: services);
        details.SetSelectedInstance(CreateInstanceItem());
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();
        details.SetSelectedSection(CreateSection("general"));

        var change = new InstanceDirectoryChangedEventArgs(
            "Changed",
            Path.Combine(TempRoot, directoryName, itemName));
        monitor.Raise(kind, change);
        monitor.Raise(kind, change);

        Assert.Equal(1, services.GetCallCount(kind));

        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();

        Assert.Equal(2, services.GetCallCount(kind));
        details.SetPageActive(false);
    }

    [Theory]
    [InlineData("saves", InstanceDirectoryKind.Saves)]
    public async Task HiddenLocalContentWatcherErrorRebuildsObservationAndDefersInventoryCheck(
        string sectionId,
        InstanceDirectoryKind kind)
    {
        var monitor = new RecordingDirectoryMonitor();
        var services = new RecordingLocalContentServices();
        using var details = CreateDetails(
            monitor,
            saveService: services,
            resourcePackService: services,
            shaderPackService: services);
        details.SetSelectedInstance(CreateInstanceItem());
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();
        details.SetSelectedSection(CreateSection("general"));

        monitor.Raise(
            kind,
            new InstanceDirectoryChangedEventArgs(
                "Error",
                Path.Combine(TempRoot, kind.ToString())));

        Assert.Equal(1, services.GetCallCount(kind));
        Assert.Equal(2, monitor.WatchStartCount);
        Assert.Equal([kind], monitor.ActiveKinds);

        details.SetSelectedSection(CreateSection(sectionId));
        await details.CurrentSectionViewModel!.OnSectionActivatedAsync();

        Assert.Equal(2, services.GetCallCount(kind));
        details.SetPageActive(false);
    }

    [Fact]
    public async Task SwitchingInstanceReleasesAllOldLocalContentWatchersAndArmsOnlyVisibleSection()
    {
        var monitor = new RecordingDirectoryMonitor();
        var services = new RecordingLocalContentServices();
        using var details = CreateDetails(
            monitor,
            saveService: services,
            resourcePackService: services,
            shaderPackService: services);
        details.SetSelectedInstance(CreateInstanceItem(Path.Combine(TempRoot, "first")));
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection("saves"));
        await details.SaveManagement.OnSectionActivatedAsync();
        details.SetSelectedSection(CreateSection("resource_packs"));
        await details.ResourcePackManagement.OnSectionActivatedAsync();
        Assert.Equal(
            [InstanceDirectoryKind.Saves, InstanceDirectoryKind.ResourcePacks],
            monitor.ActiveKinds);

        details.SetSelectedInstance(CreateInstanceItem(Path.Combine(TempRoot, "second")));
        await details.ResourcePackManagement.OnSectionActivatedAsync();

        Assert.Equal([InstanceDirectoryKind.ResourcePacks], monitor.ActiveKinds);
        Assert.All(
            monitor.ActiveDirectories,
            directory => Assert.Equal(Path.Combine(TempRoot, "second"), directory));
        details.SetPageActive(false);
    }

    [Fact]
    public async Task LeavingInstanceDetailsReleasesAllArmedWatchers()
    {
        var monitor = new RecordingDirectoryMonitor();
        var services = new RecordingLocalContentServices();
        using var details = CreateDetails(
            monitor,
            saveService: services,
            resourcePackService: services,
            shaderPackService: services);
        details.SetSelectedInstance(CreateInstanceItem());
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection("saves"));
        await details.SaveManagement.OnSectionActivatedAsync();
        details.SetSelectedSection(CreateSection("resource_packs"));
        await details.ResourcePackManagement.OnSectionActivatedAsync();
        Assert.Equal(
            [InstanceDirectoryKind.Saves, InstanceDirectoryKind.ResourcePacks],
            monitor.ActiveKinds);

        details.SetPageActive(false, releaseLocalContentObservation: true);

        Assert.Empty(monitor.ActiveKinds);
        details.SetPageActive(true);
        await details.ResourcePackManagement.OnSectionActivatedAsync();
        Assert.Equal([InstanceDirectoryKind.ResourcePacks], monitor.ActiveKinds);
        Assert.Equal(1, services.GetCallCount(InstanceDirectoryKind.Saves));
        Assert.Equal(2, services.GetCallCount(InstanceDirectoryKind.ResourcePacks));
        details.SetPageActive(false, releaseLocalContentObservation: true);
    }

    [Fact]
    public async Task OldInstanceInventoryResultCannotReplaceNewInstanceContent()
    {
        var monitor = new RecordingDirectoryMonitor();
        var saveService = new BlockingInstanceSwitchSaveService();
        using var details = CreateDetails(monitor, saveService: saveService);
        details.SetSelectedInstance(CreateInstanceItem(Path.Combine(TempRoot, "first")));
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection("saves"));
        await saveService.FirstCallStarted.Task;

        details.SetSelectedInstance(CreateInstanceItem(Path.Combine(TempRoot, "second")));
        await saveService.SecondCallCompleted.Task;
        saveService.ReleaseFirstCall.TrySetResult();
        await saveService.FirstCallCompleted.Task;

        var item = Assert.Single(details.SaveManagement.Saves);
        Assert.Equal("Second", item.Title);
        Assert.Equal(Path.Combine(TempRoot, "second", "saves", "second"), item.FullPath);
        details.SetPageActive(false);
    }

    [Fact]
    public async Task SameDirectorySnapshotRebindDoesNotPublishOldInventory()
    {
        var service = new BlockingInstanceSwitchSaveService();
        using var details = CreateDetails(new RecordingDirectoryMonitor(), saveService: service);
        var item = CreateInstanceItem(Path.Combine(TempRoot, "same"));
        details.SetSelectedInstance(item);
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection("saves"));
        await service.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        item.Update(CreateInstanceItem(item.Instance.InstanceDirectory).Instance, "release");
        details.SetSelectedInstance(item);
        await service.SecondCallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ReleaseFirstCall.TrySetResult();
        await service.FirstCallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Second", Assert.Single(details.SaveManagement.Saves).Title);
        details.SetPageActive(false);
    }

    [Fact]
    public async Task FailedSilentRefreshKeepsCachedContent()
    {
        var monitor = new RecordingDirectoryMonitor();
        var saveService = new RecordingSaveService
        {
            Items = [CreateSave("Cached", Path.Combine(TempRoot, "cached"))]
        };
        using var details = CreateDetails(monitor, saveService: saveService);
        details.SetSelectedInstance(CreateInstanceItem());
        details.SetPageActive(true);
        details.SetSelectedSection(CreateSection("saves"));
        await details.SaveManagement.OnSectionActivatedAsync();
        var cachedItem = Assert.Single(details.SaveManagement.Saves);

        details.SetSelectedSection(CreateSection("general"));
        saveService.FailSecondCall = true;
        monitor.Raise(
            InstanceDirectoryKind.Saves,
            new InstanceDirectoryChangedEventArgs(
                "Changed",
                Path.Combine(TempRoot, "saves", "cached")));
        details.SetSelectedSection(CreateSection("saves"));
        await details.SaveManagement.OnSectionActivatedAsync();

        Assert.True(details.SaveManagement.HasLoadedSaves);
        Assert.False(details.SaveManagement.IsLoadingSaves);
        Assert.Same(cachedItem, Assert.Single(details.SaveManagement.Saves));
        details.SetPageActive(false);
    }

    [Fact]
    public async Task CommittedDeletionDoesNotRestartOldInstanceWatcher()
    {
        var monitor = new RecordingDirectoryMonitor();
        var instanceDirectory = Directory.CreateDirectory(Path.Combine(TempRoot, "deleted-instance")).FullName;
        var instance = CreateInstanceItem(instanceDirectory);
        var instanceService = new FakeGameInstanceService();
        instanceService.CreatedInstances.Add(instance.Instance);
        instanceService.DeleteCallback = () => Directory.Delete(instanceDirectory, recursive: true);
        using var details = CreateDetails(monitor, instanceService: instanceService);
        details.SetSelectedInstance(instance);
        details.SetSelectedSection(CreateSection("mod_management"));
        details.SetPageActive(true);
        Assert.Equal(1, monitor.WatchStartCount);
        var dialogs = new GameSettingsDialogsViewModel(instanceService, Stub<IStatusService>(), details);
        dialogs.OpenDeleteInstance(instance);

        await dialogs.ConfirmDeleteInstanceDialogCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(instanceDirectory));
        Assert.Empty(monitor.ActiveKinds);
        Assert.Equal(1, monitor.WatchStartCount);
        Assert.Null(details.SelectedInstance);
        details.SetPageActive(false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentDeleteConfirmationCannotResolvePathsAgainstARefreshedInstance(bool replaceInstance)
    {
        var path = Path.Combine(TempRoot, "game", "saves", "world");
        var saves = new RecordingSaveService { Items = [CreateSave("World", path)] };
        using var details = CreateDetails(new RecordingDirectoryMonitor(), saveService: saves);
        var instancePath = Path.Combine(TempRoot, "game");
        var item = CreateInstanceItem(instancePath);
        details.SetSelectedInstance(item);
        details.SetSelectedSection(CreateSection("saves"));
        await details.SaveManagement.OnSectionActivatedAsync();
        var dialogs = new GameSettingsDialogsViewModel(Stub<IGameInstanceService>(), Stub<IStatusService>(), details);
        dialogs.OpenDeleteSaves(new SaveDeleteRequest([path], ["World"]));
        if (replaceInstance)
            details.SetSelectedInstance(CreateInstanceItem(instancePath));
        else
        {
            saves.Items = [CreateSave("Replacement", path)];
            details.SaveManagement.OnSelectedInstanceChanged(item.Instance);
        }
        await details.SaveManagement.OnSectionActivatedAsync();
        await dialogs.ConfirmDeleteContentDialogCommand.ExecuteAsync(null);
        Assert.Equal(0, saves.DeleteCallCount);
        Assert.Single(details.SaveManagement.Saves);
    }

    private static GameSettingsDetailsViewModel CreateDetails(
        RecordingDirectoryMonitor monitor,
        ILocalSaveService? saveService = null,
        IGameInstanceService? instanceService = null,
        IModService? modService = null,
        ILocalResourcePackService? resourcePackService = null,
        ILocalShaderPackService? shaderPackService = null,
        IInstanceBackupService? backupService = null)
    {
        var statusService = Stub<IStatusService>();
        var resolvedModService = modService ?? Stub<IModService>();
        var launchSettingsModService = Stub<IModService>();
        var localMods = new LocalModsViewModel(resolvedModService, statusService, monitor);
        var localSaves = new LocalSavesViewModel(saveService ?? Stub<ILocalSaveService>(), statusService, monitor);
        var localResourcePacks = new LocalResourcePacksViewModel(
            resourcePackService ?? Stub<ILocalResourcePackService>(),
            statusService,
            monitor);
        var localShaderPacks = new LocalShaderPacksViewModel(
            shaderPackService ?? Stub<ILocalShaderPackService>(),
            statusService,
            monitor);

        return new GameSettingsDetailsViewModel(
            null!,
            instanceService ?? Stub<IGameInstanceService>(),
            statusService,
            Stub<IInstanceFolderService>(),
            Stub<ISystemMemoryService>(),
            launchSettingsModService,
            backupService ?? Stub<IInstanceBackupService>(),
            new DownloadTasksPageViewModel(),
            localMods,
            localSaves,
            localResourcePacks,
            localShaderPacks,
            Stub<IJavaRuntimeDiscoveryService>(),
            Stub<IFilePickerService>(),
            Stub<IInstanceContentImportPathValidator>(),
            Stub<IFloatingMessageService>(),
            ImmediateUiDispatcher.Instance);
    }

    private static GameSettingsInstanceItem CreateInstanceItem(string? instanceDirectory = null) => new(
        new GameInstance
        {

            Name = "Instance",
            VersionName = "1.21",
            MinecraftVersion = "1.21",
            Loader = LoaderKind.Fabric,
            InstanceDirectory = instanceDirectory
                ?? Path.Combine(Path.GetTempPath(), "launcher-tests", "watcher-lifecycle")
        },
        "release");

    private static GameSettingsDetailSectionItem CreateSection(string id) => new(id, id, string.Empty);

    private static bool GetHasLoaded(GameSettingsDetailsViewModel details, InstanceDirectoryKind kind) => kind switch
    {
        InstanceDirectoryKind.Mods => details.ModManagement.HasLoadedMods,
        InstanceDirectoryKind.Saves => details.SaveManagement.HasLoadedSaves,
        InstanceDirectoryKind.ResourcePacks => details.ResourcePackManagement.HasLoadedResourcePacks,
        InstanceDirectoryKind.ShaderPacks => details.ShaderPackManagement.HasLoadedShaderPacks,
        _ => false
    };

    private static bool GetIsLoading(GameSettingsDetailsViewModel details, InstanceDirectoryKind kind) => kind switch
    {
        InstanceDirectoryKind.Mods => details.ModManagement.IsLoadingMods,
        InstanceDirectoryKind.Saves => details.SaveManagement.IsLoadingSaves,
        InstanceDirectoryKind.ResourcePacks => details.ResourcePackManagement.IsLoadingResourcePacks,
        InstanceDirectoryKind.ShaderPacks => details.ShaderPackManagement.IsLoadingShaderPacks,
        _ => false
    };

    private static int GetEntranceAnimationToken(GameSettingsDetailsViewModel details, InstanceDirectoryKind kind) => kind switch
    {
        InstanceDirectoryKind.Mods => details.ModManagement.ListEntranceAnimationToken,
        InstanceDirectoryKind.Saves => details.SaveManagement.ListEntranceAnimationToken,
        InstanceDirectoryKind.ResourcePacks => details.ResourcePackManagement.ListEntranceAnimationToken,
        InstanceDirectoryKind.ShaderPacks => details.ShaderPackManagement.ListEntranceAnimationToken,
        _ => 0
    };

    private static object GetFirstVisibleItem(GameSettingsDetailsViewModel details, InstanceDirectoryKind kind) => kind switch
    {
        InstanceDirectoryKind.Saves => Assert.Single(details.SaveManagement.Saves),
        InstanceDirectoryKind.ResourcePacks => Assert.Single(details.ResourcePackManagement.ResourcePacks),
        InstanceDirectoryKind.ShaderPacks => Assert.Single(details.ShaderPackManagement.ShaderPacks),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static LocalSave CreateSave(string name, string fullPath) => new()
    {
        Name = name,
        DirectoryName = Path.GetFileName(fullPath),
        FullPath = fullPath
    };

    private static LocalMod CreateMod(string fileName) => new()
    {
        Name = Path.GetFileNameWithoutExtension(fileName),
        FileName = fileName,
        FullPath = Path.Combine("instance", "mods", fileName),
        IsEnabled = true
    };

    private static T Stub<T>() where T : class => DispatchProxy.Create<T, DefaultInterfaceProxy>();

    public class DefaultInterfaceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void))
                return null;
            if (returnType == typeof(Task))
                return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [CreateDefaultResult(resultType)]);
            }

            return CreateDefaultResult(returnType);
        }

        private static object? CreateDefaultResult(Type resultType)
        {
            if (resultType.IsGenericType
                && resultType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                return Array.CreateInstance(resultType.GetGenericArguments()[0], 0);
            }

            return resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
        }
    }

    private sealed class RecordingBackupService : IInstanceBackupService
    {
        private readonly InstanceBackupRecord backup = new() { Name = "Backup", FileName = "backup.zip", FullPath = "backup.zip" };
        public bool Restored { get; private set; }
        public Task<string> EnsureBackupDirectoryAsync(string directory, CancellationToken cancellationToken = default) => Task.FromResult(directory);
        public Task<int> CountBackupEntriesAsync(string directory, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<IReadOnlyList<InstanceBackupRecord>> GetBackupsAsync(string directory, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstanceBackupRecord>>([backup]);
        public Task<InstanceBackupRecord> CreateBackupAsync(GameInstance instance, string directory, string name, CancellationToken cancellationToken = default) => Task.FromResult(backup);
        public Task DeleteBackupAsync(string directory, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestoreBackupAsync(GameInstance instance, string directory, string path, CancellationToken cancellationToken = default) { Restored = true; return Task.CompletedTask; }
        public Task RecoverPendingRestoresAsync(string minecraftDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingDirectoryMonitor : IInstanceDirectoryMonitor
    {
        public bool RebindOnNewSnapshot { get; init; }
        public bool IsSameWatchTarget(GameInstance previous, GameInstance current) =>
            string.Equals(previous.InstanceDirectory, current.InstanceDirectory, StringComparison.OrdinalIgnoreCase)
            && (!RebindOnNewSnapshot || ReferenceEquals(previous, current));
        private readonly List<RecordingWatch> activeWatches = [];

        public IReadOnlyList<InstanceDirectoryKind> ActiveKinds => activeWatches
            .Where(watch => !watch.IsDisposed)
            .Select(watch => watch.Kind)
            .ToList();

        public bool StartedBeforePreviousWatchWasDisposed { get; private set; }
        public int WatchStartCount { get; private set; }
        public IReadOnlyList<string> WatchedDirectories => activeWatches.Select(watch => watch.Directory).ToList();
        public IReadOnlyList<string> ActiveDirectories => activeWatches
            .Where(watch => !watch.IsDisposed)
            .Select(watch => watch.Directory)
            .ToList();

        public IInstanceDirectoryWatch Watch(GameInstance instance, InstanceDirectoryKind directoryKind)
        {
            if (activeWatches.Any(watch => !watch.IsDisposed))
                StartedBeforePreviousWatchWasDisposed = true;
            WatchStartCount++;
            var watch = new RecordingWatch(directoryKind, instance.InstanceDirectory);
            activeWatches.Add(watch);
            return watch;
        }

        public void Raise(InstanceDirectoryKind kind, InstanceDirectoryChangedEventArgs args)
        {
            activeWatches.Last(watch => !watch.IsDisposed && watch.Kind == kind).Raise(args);
        }
    }

    private sealed class RecordingSaveService : ILocalSaveService
    {
        private readonly TaskCompletionSource firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<LocalSave> Items { get; set; } = [];
        public int CallCount { get; private set; }
        public Task? WaitBeforeFirstCall { get; set; }
        public Task? WaitBeforeSecondCall { get; set; }
        public bool FailSecondCall { get; set; }

        public async Task<IReadOnlyList<LocalSave>> GetSavesAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount >= 1)
                firstCall.TrySetResult();
            if (CallCount >= 2)
                secondCall.TrySetResult();
            if (CallCount == 1 && WaitBeforeFirstCall is not null)
                await WaitBeforeFirstCall;
            if (CallCount == 2 && WaitBeforeSecondCall is not null)
                await WaitBeforeSecondCall;
            if (CallCount == 2 && FailSecondCall)
                throw new IOException("Controlled silent refresh failure.");
            return Items;
        }

        public Task WaitForCallAsync(int callCount) => (callCount <= 1 ? firstCall : secondCall).Task;

        public Task<LocalSaveImportResult> ImportFromArchiveAsync(
            GameInstance instance,
            string archivePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public int DeleteCallCount { get; private set; }
        public Task DeleteAsync(LocalSave save, CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(IEnumerable<LocalSave> saves, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLocalContentServices :
        ILocalSaveService,
        ILocalResourcePackService,
        ILocalShaderPackService
    {
        private readonly IReadOnlyList<LocalSave> saves =
        [
            CreateSave("World", Path.Combine("instance", "saves", "world"))
        ];
        private readonly IReadOnlyList<LocalResourcePack> resourcePacks =
        [
            new()
            {
                Name = "Pack",
                FileName = "pack.zip",
                FullPath = Path.Combine("instance", "resourcepacks", "pack.zip")
            }
        ];
        private readonly IReadOnlyList<LocalShaderPack> shaderPacks =
        [
            new()
            {
                Name = "Shader",
                FileName = "shader.zip",
                FullPath = Path.Combine("instance", "shaderpacks", "shader.zip")
            }
        ];
        private int saveCallCount;
        private int resourcePackCallCount;
        private int shaderPackCallCount;

        public int GetCallCount(InstanceDirectoryKind kind) => kind switch
        {
            InstanceDirectoryKind.Saves => saveCallCount,
            InstanceDirectoryKind.ResourcePacks => resourcePackCallCount,
            InstanceDirectoryKind.ShaderPacks => shaderPackCallCount,
            _ => 0
        };

        public Task<IReadOnlyList<LocalSave>> GetSavesAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            saveCallCount++;
            return Task.FromResult(saves);
        }

        public Task<IReadOnlyList<LocalResourcePack>> GetResourcePacksAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            resourcePackCallCount++;
            return Task.FromResult(resourcePacks);
        }

        public Task<IReadOnlyList<LocalShaderPack>> GetShaderPacksAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            shaderPackCallCount++;
            return Task.FromResult(shaderPacks);
        }

        public Task<LocalSaveImportResult> ImportFromArchiveAsync(
            GameInstance instance,
            string archivePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LocalResourcePackImportResult> ImportAsync(
            GameInstance instance,
            string archivePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        Task<LocalShaderPackImportResult> ILocalShaderPackService.ImportAsync(
            GameInstance instance,
            string archivePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(LocalSave save, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(IEnumerable<LocalSave> saves, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(LocalResourcePack resourcePack, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            IEnumerable<LocalResourcePack> resourcePacks,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(LocalShaderPack shaderPack, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            IEnumerable<LocalShaderPack> shaderPacks,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingInstanceSwitchSaveService : ILocalSaveService
    {
        private int callCount;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCallCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondCallCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<LocalSave>> GetSavesAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                FirstCallStarted.TrySetResult();
                await ReleaseFirstCall.Task;
                FirstCallCompleted.TrySetResult();
                return
                [
                    CreateSave("First", Path.Combine(instance.InstanceDirectory, "saves", "first"))
                ];
            }

            SecondCallCompleted.TrySetResult();
            return
            [
                CreateSave("Second", Path.Combine(instance.InstanceDirectory, "saves", "second"))
            ];
        }

        public Task<LocalSaveImportResult> ImportFromArchiveAsync(
            GameInstance instance,
            string archivePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(LocalSave save, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(IEnumerable<LocalSave> saves, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWatch(InstanceDirectoryKind kind, string directory) : IInstanceDirectoryWatch
    {
        public InstanceDirectoryKind Kind { get; } = kind;
        public string Directory { get; } = directory;
        public bool IsDisposed { get; private set; }
        public event EventHandler<InstanceDirectoryChangedEventArgs>? Changed;

        public void Raise(InstanceDirectoryChangedEventArgs args) => Changed?.Invoke(this, args);

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class RecordingModService(IReadOnlyList<LocalMod> items) : IModService
    {
        public int GetModsCallCount { get; private set; }

        public Task<IReadOnlyList<LocalMod>> GetModsAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            GetModsCallCount++;
            return Task.FromResult(items);
        }

        public Task<LocalMod> ImportAsync(
            GameInstance instance,
            string sourceJarPath,
            bool overwriteExisting = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetEnabledAsync(
            LocalMod mod,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(LocalMod mod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
