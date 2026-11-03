/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Reflection;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Tests.Fakes;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class GameSettingsDetailsWatcherLifecycleTests : TestTempDirectory
{
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

    private static GameSettingsDetailsViewModel CreateDetails(
        RecordingDirectoryMonitor monitor,
        ILocalSaveService? saveService = null,
        IGameInstanceService? instanceService = null,
        IModService? modService = null,
        ILocalResourcePackService? resourcePackService = null,
        ILocalShaderPackService? shaderPackService = null)
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
            Stub<IInstanceBackupService>(),
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
            Id = "instance",
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

    private sealed class RecordingDirectoryMonitor : IInstanceDirectoryMonitor
    {
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

        public Task DeleteAsync(LocalSave save, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
