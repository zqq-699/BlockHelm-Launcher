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

using Launcher.App.Services;
using Launcher.App.Resources;
using Launcher.App.Utilities;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.ViewModels.Download;

public sealed class DownloadViewModelTests
{
    private const string MinecraftDirectory = @"C:\Games\.minecraft";

    [Fact]
    public async Task VersionListLoadsAndFiltersItsOwnCatalog()
    {
        using var viewModel = new DownloadVersionListViewModel(
            new StubGameVersionService(
            [
                new MinecraftVersionInfo("1.21", "release", false),
                new MinecraftVersionInfo("25w01a", "snapshot", false)
            ]),
            ImmediateUiDispatcher.Instance);

        await viewModel.EnsureVersionsLoadedAsync();

        var release = Assert.Single(viewModel.VisibleVersions);
        Assert.Equal("1.21", release.Name);
        Assert.Equal("release", viewModel.SelectedVersionCategory?.Id);
    }

    [Fact]
    public async Task PendingVersionLoadShowsLoadingAndConcurrentEnsuresShareRequest()
    {
        var result = new TaskCompletionSource<IReadOnlyList<MinecraftVersionInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SequencedGameVersionService(() => result.Task);
        using var viewModel = new DownloadVersionListViewModel(
            service,
            ImmediateUiDispatcher.Instance);

        var firstLoad = viewModel.EnsureVersionsLoadedAsync();
        var secondLoad = viewModel.EnsureVersionsLoadedAsync();

        Assert.Equal(1, service.CallCount);
        Assert.True(viewModel.IsLoadingVersions);
        Assert.False(viewModel.HasVersionLoadError);

        result.SetResult([new MinecraftVersionInfo("1.21.8", "release", false)]);
        await Task.WhenAll(firstLoad, secondLoad);

        Assert.False(viewModel.IsLoadingVersions);
        Assert.Equal("1.21.8", Assert.Single(viewModel.VisibleVersions).Name);
    }

    [Fact]
    public async Task FailedVersionListCanBeRefreshed()
    {
        var retryResult = new TaskCompletionSource<IReadOnlyList<MinecraftVersionInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SequencedGameVersionService(
            () => Task.FromException<IReadOnlyList<MinecraftVersionInfo>>(new HttpRequestException("offline")),
            () => retryResult.Task);
        using var viewModel = new DownloadVersionListViewModel(
            service,
            ImmediateUiDispatcher.Instance);

        await viewModel.EnsureVersionsLoadedAsync();

        Assert.False(viewModel.IsLoadingVersions);
        Assert.True(viewModel.HasVersionLoadError);
        Assert.True(viewModel.RefreshVersionsCommand.CanExecute(null));

        var refresh = viewModel.RefreshVersionsCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CallCount);
        Assert.True(viewModel.IsLoadingVersions);
        Assert.False(viewModel.HasVersionLoadError);
        Assert.False(viewModel.RefreshVersionsCommand.CanExecute(null));

        retryResult.SetResult([new MinecraftVersionInfo("1.21.8", "release", false)]);
        await refresh;

        Assert.False(viewModel.IsLoadingVersions);
        Assert.False(viewModel.HasVersionLoadError);
        Assert.Equal("1.21.8", Assert.Single(viewModel.VisibleVersions).Name);
    }

    [Fact]
    public async Task FailedRefreshRemainsRetryable()
    {
        var service = new SequencedGameVersionService(
            () => Task.FromException<IReadOnlyList<MinecraftVersionInfo>>(new HttpRequestException("offline")),
            () => Task.FromException<IReadOnlyList<MinecraftVersionInfo>>(new HttpRequestException("still offline")));
        using var viewModel = new DownloadVersionListViewModel(
            service,
            ImmediateUiDispatcher.Instance);

        await viewModel.EnsureVersionsLoadedAsync();
        await viewModel.RefreshVersionsCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CallCount);
        Assert.False(viewModel.IsLoadingVersions);
        Assert.True(viewModel.HasVersionLoadError);
        Assert.True(viewModel.RefreshVersionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task PageReentryRetriesFailedVersionListLoad()
    {
        var service = new SequencedGameVersionService(
            () => Task.FromException<IReadOnlyList<MinecraftVersionInfo>>(new HttpRequestException("offline")),
            () => Task.FromResult<IReadOnlyList<MinecraftVersionInfo>>(
                [new MinecraftVersionInfo("1.21.8", "release", false)]));
        using var viewModel = new DownloadVersionListViewModel(
            service,
            ImmediateUiDispatcher.Instance);

        await viewModel.EnsureVersionsLoadedAsync();
        await viewModel.EnsureVersionsLoadedAsync();

        Assert.Equal(2, service.CallCount);
        Assert.False(viewModel.HasVersionLoadError);
        Assert.Equal("1.21.8", Assert.Single(viewModel.VisibleVersions).Name);
    }

    [Fact]
    public async Task CancelingDownloadTaskCancelsInstallationWithoutFailureMessage()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeGameInstanceService { WaitBeforeCreate = release.Task };
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromSeconds(1));
        var viewModel = CreateInstallViewModel(service, tasks, new DownloadInstanceNameTracker());
        var installation = viewModel.InstallAsync(CreateInstallRequest("cancel-me"));
        await service.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        tasks.CancelTask(Assert.Single(tasks.Tasks));
        await installation;

        Assert.False(viewModel.IsInstalling);
        Assert.False(viewModel.HasInstallError);
        Assert.Empty(service.CreatedInstances);
    }

    [Fact]
    public async Task FailedAssetsUseTheAssetErrorOnTheInstallPageAndTask()
    {
        var service = new FakeGameInstanceService
        {
            CreateException = new MinecraftAssetInstallationException(new HttpRequestException("offline"))
        };
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromSeconds(1));
        var viewModel = CreateInstallViewModel(service, tasks, new DownloadInstanceNameTracker());

        await viewModel.InstallAsync(CreateInstallRequest("asset-failure") with { Loader = LoaderKind.Forge });

        Assert.Equal(Strings.Status_MinecraftAssetInstallFailed, viewModel.InstallError);
        var task = Assert.Single(tasks.Tasks);
        Assert.True(task.IsFailed);
        Assert.Equal(Strings.Status_MinecraftAssetInstallFailed, task.StatusMessage);
        Assert.Empty(service.CreatedInstances);
    }

    [Fact]
    public async Task CompletedInstallPublishesVersionTypeForInstanceCategoryProjection()
    {
        var service = new FakeGameInstanceService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromSeconds(1));
        var viewModel = CreateInstallViewModel(service, tasks, new DownloadInstanceNameTracker());
        GameInstance? installed = null;
        viewModel.InstanceInstalled += (_, instance) => installed = instance;

        await viewModel.InstallAsync(CreateInstallRequest("new-release"));

        Assert.NotNull(installed);
        Assert.Equal("release", installed.VersionType);
    }

    [Fact]
    public async Task InstallRequestRetainsSelectedMinecraftVersionType()
    {
        var service = new FakeGameInstanceService();
        using var viewModel = new DownloadInstanceOptionsViewModel(
            service,
            [],
            new DownloadInstanceNameTracker());
        await viewModel.PrepareAsync(
            new DownloadMinecraftVersionItem(
                new MinecraftVersionInfo("25w01a", "snapshot", false)));

        var request = viewModel.CreateInstallRequest();

        Assert.NotNull(request);
        Assert.Equal("snapshot", request.MinecraftVersionType);
    }

    [Fact]
    public async Task ExistingRawVersionDirectoryShowsDuplicateNameBeforeInstall()
    {
        var service = new FakeGameInstanceService();
        var availability = new StubInstanceInstallNameAvailabilityService
        {
            Result = InstanceInstallNameAvailability.Occupied
        };
        using var viewModel = new DownloadInstanceOptionsViewModel(
            service,
            [],
            new DownloadInstanceNameTracker(),
            availability);
        viewModel.ApplyMinecraftDirectory(MinecraftDirectory);

        await viewModel.PrepareAsync(
            new DownloadMinecraftVersionItem(
                new MinecraftVersionInfo("1.20.1", "release", false)));

        Assert.Equal(Strings.Status_DuplicateInstanceName, viewModel.InstanceNameDuplicateMessage);
        Assert.False(viewModel.CanInstall);
        Assert.Equal("1.20.1", Assert.Single(availability.CheckedNames));
        Assert.Equal(MinecraftDirectory, Assert.Single(availability.CheckedDirectories));
    }

    [Fact]
    public async Task RefreshNameAvailabilityRechecksAfterMinecraftDirectoryChanges()
    {
        var service = new FakeGameInstanceService();
        var availability = new StubInstanceInstallNameAvailabilityService();
        using var viewModel = new DownloadInstanceOptionsViewModel(
            service,
            [],
            new DownloadInstanceNameTracker(),
            availability);
        viewModel.ApplyMinecraftDirectory(MinecraftDirectory);
        await viewModel.PrepareAsync(
            new DownloadMinecraftVersionItem(
                new MinecraftVersionInfo("1.20.1", "release", false)));
        Assert.Empty(viewModel.InstanceNameDuplicateMessage);

        availability.Result = InstanceInstallNameAvailability.Occupied;
        await viewModel.RefreshNameAvailabilityAsync();

        Assert.Equal(Strings.Status_DuplicateInstanceName, viewModel.InstanceNameDuplicateMessage);
        Assert.False(viewModel.CanInstall);
        Assert.Equal(2, availability.CheckedNames.Count);
    }

    [Fact]
    public async Task StaleNameAvailabilityResultCannotOverrideTheLatestName()
    {
        var staleResult = new TaskCompletionSource<InstanceInstallNameAvailability>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCheckStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var availability = new SequencedInstanceInstallNameAvailabilityService(
            (_, _) => Task.FromResult(InstanceInstallNameAvailability.Available),
            async (_, _) =>
            {
                staleCheckStarted.TrySetResult();
                return await staleResult.Task;
            },
            (_, _) => Task.FromResult(InstanceInstallNameAvailability.Available));
        using var viewModel = new DownloadInstanceOptionsViewModel(
            new FakeGameInstanceService(),
            [],
            new DownloadInstanceNameTracker(),
            availability);
        viewModel.ApplyMinecraftDirectory(MinecraftDirectory);
        await viewModel.PrepareAsync(
            new DownloadMinecraftVersionItem(
                new MinecraftVersionInfo("1.20.1", "release", false)));

        viewModel.InstanceName = "occupied";
        var staleRefresh = viewModel.RefreshNameAvailabilityAsync();
        await staleCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.InstanceName = "free";
        await viewModel.RefreshNameAvailabilityAsync();
        staleResult.TrySetResult(InstanceInstallNameAvailability.Occupied);
        await staleRefresh;

        Assert.Equal("free", viewModel.InstanceName);
        Assert.Empty(viewModel.InstanceNameDuplicateMessage);
        Assert.True(viewModel.CanInstall);
    }

    private static DownloadInstallViewModel CreateInstallViewModel(
        FakeGameInstanceService service,
        DownloadTasksPageViewModel tasks,
        DownloadInstanceNameTracker tracker)
    {
        return new DownloadInstallViewModel(
            service,
            tasks,
            tracker,
            ImmediateUiDispatcher.Instance,
            new RecordingFloatingMessageService(),
            NullLogger<DownloadInstallViewModel>.Instance);
    }

    private static DownloadInstallRequest CreateInstallRequest(string instanceName)
    {
        return new DownloadInstallRequest(
            "1.20.1",
            "release",
            instanceName,
            LoaderKind.Vanilla,
            null,
            null,
            null,
            "Vanilla",
            LauncherDefaults.DefaultDownloadSourcePreference,
            0);
    }

    private sealed class StubGameVersionService(IReadOnlyList<MinecraftVersionInfo> versions) : IGameVersionService
    {
        public Task<IReadOnlyList<MinecraftVersionInfo>> GetVersionsAsync(
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            CancellationToken cancellationToken = default,
            int downloadSpeedLimitMbPerSecond = 0)
        {
            return Task.FromResult(versions);
        }
    }

    private sealed class SequencedGameVersionService(
        params Func<Task<IReadOnlyList<MinecraftVersionInfo>>>[] responses) : IGameVersionService
    {
        private readonly Queue<Func<Task<IReadOnlyList<MinecraftVersionInfo>>>> responses = new(responses);

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<MinecraftVersionInfo>> GetVersionsAsync(
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            CancellationToken cancellationToken = default,
            int downloadSpeedLimitMbPerSecond = 0)
        {
            CallCount++;
            return responses.Dequeue()();
        }
    }

    private sealed class StubInstanceInstallNameAvailabilityService
        : IInstanceInstallNameAvailabilityService
    {
        public InstanceInstallNameAvailability Result { get; set; } =
            InstanceInstallNameAvailability.Available;

        public List<string> CheckedNames { get; } = [];

        public List<string> CheckedDirectories { get; } = [];

        public Task<InstanceInstallNameAvailability> CheckAsync(
            string minecraftDirectory,
            string instanceName,
            CancellationToken cancellationToken = default)
        {
            CheckedDirectories.Add(minecraftDirectory);
            CheckedNames.Add(instanceName);
            return Task.FromResult(Result);
        }
    }

    private sealed class SequencedInstanceInstallNameAvailabilityService(
        params Func<string, CancellationToken, Task<InstanceInstallNameAvailability>>[] responses)
        : IInstanceInstallNameAvailabilityService
    {
        private readonly Queue<Func<string, CancellationToken, Task<InstanceInstallNameAvailability>>> responses =
            new(responses);

        public Task<InstanceInstallNameAvailability> CheckAsync(
            string minecraftDirectory,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            responses.Dequeue()(instanceName, cancellationToken);
    }

    private sealed class RecordingFloatingMessageService : IFloatingMessageService
    {
        public event Action<FloatingMessageRequest>? MessageRequested;

        public void Show(string message)
        {
            MessageRequested?.Invoke(new FloatingMessageRequest(message));
        }

        public void ShowDragHint(object source, string message)
        {
            MessageRequested?.Invoke(new FloatingMessageRequest(message, AutoHide: false));
        }

        public void ClearDragHint(object source) => ClearDragHint();

        public void ClearDragHint()
        {
            MessageRequested?.Invoke(new FloatingMessageRequest(string.Empty));
        }
    }
}
