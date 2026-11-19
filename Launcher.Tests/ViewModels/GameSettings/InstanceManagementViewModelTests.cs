/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Services;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class InstanceManagementViewModelTests : TestTempDirectory
{
    [Fact]
    public async Task RootChangeDuringRefreshDiscardsOldResultAndRefreshesNewRoot()
    {
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root-a")
        };
        var backupService = new RecordingBackupService();
        var instanceService = new RootSwitchingInstanceService(backupService);
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(settings),
            instanceService,
            new NullStatusService(),
            backupService);

        var firstRefresh = viewModel.InitializeAsync(settings);
        await instanceService.FirstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.MinecraftDirectory = Path.Combine(TempRoot, "root-b");
        var rootChangeRefresh = viewModel.RefreshInstancesAsync();
        instanceService.ReleaseFirstRefresh.TrySetResult();

        await Task.WhenAll(firstRefresh, rootChangeRefresh);

        Assert.Equal(2, instanceService.RefreshCount);
        Assert.Equal("root-b", Assert.Single(viewModel.Instances).InstanceDirectory);
        Assert.Equal(
            [Path.GetFullPath(Path.Combine(TempRoot, "root-a")), Path.GetFullPath(Path.Combine(TempRoot, "root-b"))],
            backupService.RecoveredDirectories);
    }

    [Fact]
    public async Task NewRefreshForSameRootDiscardsSnapshotStartedBeforeDeletion()
    {
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root")
        };
        var instanceService = new RootSwitchingInstanceService();
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(settings),
            instanceService,
            new NullStatusService());

        var earlierRefresh = viewModel.InitializeAsync(settings);
        await instanceService.FirstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var postDeleteRefresh = viewModel.RefreshInstancesAsync();
        instanceService.ReleaseFirstRefresh.TrySetResult();
        await Task.WhenAll(earlierRefresh, postDeleteRefresh);

        Assert.Equal(2, instanceService.RefreshCount);
        Assert.Equal("root-b", Assert.Single(viewModel.Instances).InstanceDirectory);
    }

    [Fact]
    public async Task UnchangedCatalogPublishesFreshSnapshotAndNewGeneration()
    {
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root")
        };
        var first = CreateInstance("instance-a", "Instance A");
        var service = new SequenceInstanceService(
            [first],
            [CreateInstance("instance-a", "Instance A")]);
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(settings),
            service,
            new NullStatusService());

        await viewModel.InitializeAsync(settings);
        var original = Assert.Single(viewModel.Instances);
        var revision = viewModel.CatalogRevision;
        var collectionChanges = 0;
        viewModel.Instances.CollectionChanged += (_, _) => collectionChanges++;

        await viewModel.RefreshInstancesAsync();

        Assert.NotSame(original, Assert.Single(viewModel.Instances));
        Assert.True(viewModel.CatalogRevision > revision);
        Assert.Equal(1, collectionChanges);
    }

    [Fact]
    public async Task CatalogFieldChangeReplacesSnapshotAndAdvancesRevision()
    {
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root")
        };
        var service = new SequenceInstanceService(
            [CreateInstance("instance-a", "Before")],
            [CreateInstance("instance-a", "After")]);
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(settings),
            service,
            new NullStatusService());

        await viewModel.InitializeAsync(settings);
        var original = Assert.Single(viewModel.Instances);
        var revision = viewModel.CatalogRevision;
        var collectionChanges = 0;
        viewModel.Instances.CollectionChanged += (_, _) => collectionChanges++;

        await viewModel.RefreshInstancesAsync();

        Assert.NotSame(original, Assert.Single(viewModel.Instances));
        Assert.Equal("After", Assert.Single(viewModel.Instances).Name);
        Assert.True(viewModel.CatalogRevision > revision);
        Assert.True(collectionChanges > 0);
    }

    [Fact]
    public async Task NonCatalogSettingChangePublishesNewGeneration()
    {
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root")
        };
        var first = CreateInstance("instance-a", "Instance A");
        first.MemoryMb = 4096;
        var second = CreateInstance("instance-a", "Instance A");
        second.MemoryMb = 8192;
        var service = new SequenceInstanceService([first], [second]);
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(settings),
            service,
            new NullStatusService());

        await viewModel.InitializeAsync(settings);
        var original = Assert.Single(viewModel.Instances);
        var revision = viewModel.CatalogRevision;

        await viewModel.RefreshInstancesAsync();

        Assert.NotSame(original, Assert.Single(viewModel.Instances));
        Assert.Equal(8192, Assert.Single(viewModel.Instances).MemoryMb);
        Assert.True(viewModel.CatalogRevision > revision);
    }

    [Fact]
    public async Task CreatedInstanceRetainsSelectedMinecraftVersionType()
    {
        var service = new FakeGameInstanceService();
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(new LauncherSettings()),
            service,
            new NullStatusService());

        var instance = await viewModel.CreateInstanceAsync(
            new MinecraftVersionInfo("25w01a", "snapshot", false),
            LoaderKind.Vanilla,
            loaderVersion: null,
            progress: null);

        Assert.NotNull(instance);
        Assert.Equal("snapshot", instance.VersionType);
    }

    [Fact]
    public async Task LocalInstallAppliedAfterRunningStaleRefreshCannotBeRemovedByIt()
    {
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root")
        };
        var existing = CreateInstance("instance-a", "Instance A");
        var installed = CreateInstance("instance-b", "Instance B");
        var releaseStaleRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeGameInstanceService();
        service.CreatedInstances.Add(existing);
        service.GetInstancesHandler = async (_, cancellationToken) =>
        {
            await releaseStaleRefresh.Task.WaitAsync(cancellationToken);
            return [existing];
        };
        var viewModel = new InstanceManagementViewModel(
            new TestSettingsService(settings),
            service,
            new NullStatusService());
        await viewModel.PrimeInstancesAsync(settings);

        var staleRefresh = viewModel.RefreshInstancesAsync();
        await service.GetInstancesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var applyInstalled = viewModel.ApplyUpdatedInstanceAsync(installed);

        Assert.False(applyInstalled.IsCompleted);
        releaseStaleRefresh.TrySetResult();
        await Task.WhenAll(staleRefresh, applyInstalled);

        Assert.Equal([existing.InstanceDirectory, installed.InstanceDirectory], viewModel.Instances.Select(instance => instance.InstanceDirectory));
    }

    [Fact]
    public async Task MissingDefaultRemainsSelectedInSettingsUntilItCanBeReadAgain()
    {
        var missing = CreateInstance("instance-a", "Instance A");
        var available = CreateInstance("instance-b", "Instance B");
        var settings = new LauncherSettings
        {
            MinecraftDirectory = Path.Combine(TempRoot, "root"),
            DefaultInstanceDirectory = missing.InstanceDirectory
        };
        var service = new SequenceInstanceService([available], [missing, available]);
        var viewModel = new InstanceManagementViewModel(new TestSettingsService(settings), service, new NullStatusService());

        await viewModel.InitializeAsync(settings);
        Assert.Equal(missing.InstanceDirectory, settings.DefaultInstanceDirectory);
        Assert.Equal(available.InstanceDirectory, viewModel.SelectedInstance?.InstanceDirectory);

        await viewModel.RefreshInstancesAsync();
        Assert.Equal(missing.InstanceDirectory, settings.DefaultInstanceDirectory);
        Assert.Equal(missing.InstanceDirectory, viewModel.SelectedInstance?.InstanceDirectory);
    }

    private static GameInstance CreateInstance(string id, string name)
    {
        return new GameInstance
        {

            Name = name,
            MinecraftVersion = "1.21.4",
            VersionName = "1.21.4",
            VersionType = "release",
            InstanceDirectory = Path.Combine("versions", id),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private sealed class SequenceInstanceService(
        IReadOnlyList<GameInstance> first,
        IReadOnlyList<GameInstance> second) : IGameInstanceService
    {
        private int callCount;

        public Task<IReadOnlyList<GameInstance>> GetStoredInstancesAsync(
            LauncherSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameInstance>>([]);

        public Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Interlocked.Increment(ref callCount) == 1 ? first : second);
        }

        public Task<GameInstance?> GetDefaultInstanceAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GameInstance> CreateInstanceAsync(
            string minecraftVersion,
            LoaderKind loader,
            string? loaderVersion,
            string? name,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0,
            bool installFabricApi = true,
            string? fabricApiVersionId = null,
            string? quiltStandardLibraryVersionId = null) => throw new NotSupportedException();

        public Task SaveInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GameInstance> RenameInstanceAsync(
            string instanceId,
            string? newName,
            string? newIconSource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SetDefaultInstanceAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteInstanceAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RootSwitchingInstanceService : IGameInstanceService
    {
        private readonly RecordingBackupService? backupService;
        private int refreshCount;

        public RootSwitchingInstanceService(RecordingBackupService? backupService = null)
        {
            this.backupService = backupService;
        }

        public int RefreshCount => Volatile.Read(ref refreshCount);
        public TaskCompletionSource FirstRefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<GameInstance>> GetStoredInstancesAsync(
            LauncherSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameInstance>>([]);

        public async Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref refreshCount);
            Assert.True(
                backupService is null || backupService.RecoveredDirectories.Count >= call,
                "Pending backup restores must be recovered before scanning an instance root.");
            if (call == 1)
            {
                FirstRefreshStarted.TrySetResult();
                await ReleaseFirstRefresh.Task.WaitAsync(cancellationToken);
                return [new GameInstance { InstanceDirectory = "root-a", Name = "A" }];
            }
            return [new GameInstance { InstanceDirectory = "root-b", Name = "B" }];
        }

        public Task<GameInstance?> GetDefaultInstanceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GameInstance?>(null);

        public Task<GameInstance> CreateInstanceAsync(
            string minecraftVersion,
            LoaderKind loader,
            string? loaderVersion,
            string? name,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0,
            bool installFabricApi = true,
            string? fabricApiVersionId = null,
            string? quiltStandardLibraryVersionId = null) => throw new NotSupportedException();

        public Task SaveInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GameInstance> RenameInstanceAsync(
            string instanceId,
            string? newName,
            string? newIconSource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SetDefaultInstanceAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteInstanceAsync(string instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NullStatusService : IStatusService
    {
        public event Action<string>? MessageReported;

        public void Report(string message) => MessageReported?.Invoke(message);
    }

    private sealed class RecordingBackupService : IInstanceBackupService
    {
        public List<string> RecoveredDirectories { get; } = [];

        public Task RecoverPendingRestoresAsync(
            string minecraftDirectory,
            CancellationToken cancellationToken = default)
        {
            RecoveredDirectories.Add(Path.GetFullPath(minecraftDirectory));
            return Task.CompletedTask;
        }

        public Task<string> EnsureBackupDirectoryAsync(
            string backupDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountBackupEntriesAsync(
            string backupDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InstanceBackupRecord>> GetBackupsAsync(
            string backupDirectory,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InstanceBackupRecord> CreateBackupAsync(
            GameInstance instance,
            string backupDirectory,
            string backupName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteBackupAsync(
            string backupDirectory,
            string backupFullPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RestoreBackupAsync(
            GameInstance instance,
            string backupDirectory,
            string backupFullPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
