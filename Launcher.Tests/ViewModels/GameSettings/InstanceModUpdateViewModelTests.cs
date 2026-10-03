/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class InstanceModUpdateViewModelTests
{
    [Fact]
    public void UpdateAvailabilityRequiresRecognizedProjectAndIdleState()
    {
        var item = new ModManagementModItemViewModel(new LocalMod
        {
            Name = "Example",
            FileName = "example-1.jar",
            FullPath = Path.Combine("instance", "mods", "example-1.jar"),
            IsEnabled = true
        });

        Assert.False(item.CanUpdate);

        item.ProjectReference = new ResourceProjectReference(
            ResourceProjectKind.Mod,
            ResourceProjectSource.Modrinth,
            "example");

        Assert.True(item.CanUpdate);

        item.IsUpdateBusy = true;

        Assert.False(item.CanUpdate);
    }

    [Fact]
    public async Task UnrecognizedModDoesNotStartUpdateCheck()
    {
        using var context = await CreateContextAsync(recognized: false);

        var item = Assert.Single(context.ViewModel.Mods);
        Assert.False(item.CanUpdate);

        await context.ViewModel.RequestModUpdateCommand.ExecuteAsync(item);

        Assert.Equal(0, context.UpdateService.CheckCalls);
        Assert.Empty(context.Tasks.Tasks);
    }

    [Fact]
    public async Task CancelingConfirmationDoesNotCreateDownloadTask()
    {
        using var context = await CreateContextAsync();
        context.ViewModel.ModUpdateConfirmationRequested += request => request.Resolve(false);

        await context.ViewModel.RequestModUpdateCommand.ExecuteAsync(Assert.Single(context.ViewModel.Mods));

        Assert.Empty(context.Tasks.Tasks);
        Assert.Equal(1, context.UpdateService.CheckCalls);
        Assert.Equal(0, context.UpdateService.UpdateCalls);
        Assert.False(Assert.Single(context.ViewModel.Mods).IsUpdateBusy);
    }

    [Fact]
    public async Task ConfirmedUpdateRunsInsideDownloadTask()
    {
        using var context = await CreateContextAsync();
        context.ViewModel.ModUpdateConfirmationRequested += request => request.Resolve(true);

        await context.ViewModel.RequestModUpdateCommand.ExecuteAsync(Assert.Single(context.ViewModel.Mods));

        var task = Assert.Single(context.Tasks.Tasks);
        Assert.Equal(DownloadTaskState.Completed, task.State);
        Assert.Equal(1, context.UpdateService.UpdateCalls);
        Assert.False(Assert.Single(context.ViewModel.Mods).IsUpdateBusy);
    }

    [Fact]
    public async Task BulkUpdateChecksAllInstalledModsRegardlessOfCurrentFilterAndUsesOneTask()
    {
        var mods = new[]
        {
            CreateMod("First", "first.jar", enabled: true),
            CreateMod("Second", "second.jar", enabled: false)
        };
        using var context = await CreateContextAsync(mods: mods);
        context.ViewModel.ModSearchQuery = "no-visible-results";
        Assert.Empty(context.ViewModel.Mods);
        context.ViewModel.ModBulkUpdateConfirmationRequested += ConfirmBulkFlow;

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, context.UpdateService.CheckManyCalls);
        Assert.Equal(2, context.UpdateService.LastCheckedCount);
        Assert.Equal(1, context.UpdateService.UpdateManyCalls);
        Assert.Equal(2, context.UpdateService.LastUpdatedCount);
        Assert.Equal(DownloadTaskState.Completed, Assert.Single(context.Tasks.Tasks).State);
        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task CancelingInitialBulkRiskConfirmationDoesNotStartChecking()
    {
        using var context = await CreateContextAsync();
        context.ViewModel.ModBulkUpdateConfirmationRequested += request => request.Cancel();

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, context.UpdateService.CheckManyCalls);
        Assert.Equal(0, context.UpdateService.UpdateManyCalls);
        Assert.Empty(context.Tasks.Tasks);
        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task CancelingImmediatelyAfterRiskConfirmationDoesNotStartChecking()
    {
        using var context = await CreateContextAsync();
        context.ViewModel.ModBulkUpdateConfirmationRequested += request =>
        {
            request.Confirm();
            request.Cancel();
        };

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, context.UpdateService.CheckManyCalls);
        Assert.Empty(context.Tasks.Tasks);
        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task BulkCheckWithNoCandidatesShowsSummaryWithoutUpdating()
    {
        using var context = await CreateContextAsync();
        context.UpdateService.BatchCheckStatus = ModUpdateCheckStatus.UpToDate;
        var confirmationCount = 0;
        var summaryShown = false;
        context.ViewModel.ModBulkUpdateConfirmationRequested += request =>
        {
            confirmationCount++;
            request.Changed += () => summaryShown |= request.Stage is ModBulkUpdateConfirmationStage.Summary;
            ConfirmBulkFlow(request);
        };

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, confirmationCount);
        Assert.True(summaryShown);
        Assert.Equal(0, context.UpdateService.UpdateManyCalls);
        Assert.Equal(DownloadTaskState.Completed, Assert.Single(context.Tasks.Tasks).State);
    }

    [Fact]
    public async Task BulkUpdateContinuesWithCandidatesWhenAnotherModCannotBeChecked()
    {
        var mods = new[]
        {
            CreateMod("Good", "good.jar", enabled: true),
            CreateMod("Unavailable", "unavailable.jar", enabled: true)
        };
        using var context = await CreateContextAsync(mods: mods);
        context.UpdateService.BatchResultFactory = mod => mod.FileName == "unavailable.jar"
            ? new ModUpdateCheckResult(ModUpdateCheckStatus.Unavailable)
            : context.UpdateService.CreateAvailableResult(mod);
        context.ViewModel.ModBulkUpdateConfirmationRequested += ConfirmBulkFlow;

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, context.UpdateService.LastUpdatedCount);
        var task = Assert.Single(context.Tasks.Tasks);
        Assert.Equal(DownloadTaskState.Failed, task.State);
        Assert.Contains("unavailable.jar", task.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkConfirmationDisablesSingleAndBulkUpdateCommands()
    {
        using var context = await CreateContextAsync();
        ModBulkUpdateConfirmationRequest? pending = null;
        context.ViewModel.ModBulkUpdateConfirmationRequested += request => pending = request;

        context.ViewModel.UpdateAllModsCommand.Execute(null);

        Assert.NotNull(pending);
        Assert.True(context.ViewModel.IsBulkUpdateBusy);
        Assert.False(context.ViewModel.CanUpdateAllMods);
        Assert.False(Assert.Single(context.ViewModel.Mods).CanUpdate);

        pending.Cancel();
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task CancelingDuringBulkCheckCancelsAndRemovesAggregateTask()
    {
        using var context = await CreateContextAsync();
        context.UpdateService.BlockBatchCheck = true;
        ModBulkUpdateConfirmationRequest? pending = null;
        context.ViewModel.ModBulkUpdateConfirmationRequested += request => pending = request;

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        Assert.NotNull(pending);
        pending.Confirm();
        await context.UpdateService.BatchCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ModBulkUpdateConfirmationStage.Checking, pending.Stage);
        Assert.Single(context.Tasks.Tasks);
        pending.Cancel();
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(context.Tasks.Tasks);
        Assert.Equal(0, context.UpdateService.UpdateManyCalls);
        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task CancelingAsBulkCheckCompletesDoesNotWaitForHiddenSummary()
    {
        using var context = await CreateContextAsync();
        context.UpdateService.BlockBatchCheck = true;
        context.UpdateService.ReturnBatchCheckAfterCancellation = true;
        ModBulkUpdateConfirmationRequest? pending = null;
        context.ViewModel.ModBulkUpdateConfirmationRequested += request => pending = request;

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        Assert.NotNull(pending);
        pending.Confirm();
        await context.UpdateService.BatchCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pending.Cancel();
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(context.Tasks.Tasks);
        Assert.Equal(0, context.UpdateService.UpdateManyCalls);
        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task CheckFailureAfterCancellationDoesNotWaitForHiddenFailureDialog()
    {
        using var context = await CreateContextAsync();
        context.UpdateService.BlockBatchCheck = true;
        context.UpdateService.FailBatchCheckAfterCancellation = true;
        ModBulkUpdateConfirmationRequest? pending = null;
        context.ViewModel.ModBulkUpdateConfirmationRequested += request => pending = request;

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        Assert.NotNull(pending);
        pending.Confirm();
        await context.UpdateService.BatchCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pending.Cancel();
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(context.Tasks.Tasks);
        Assert.Equal(0, context.UpdateService.UpdateManyCalls);
        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    [Fact]
    public async Task BulkCheckFailureWaitsForAcknowledgementBeforeClosing()
    {
        using var context = await CreateContextAsync();
        context.UpdateService.FailBatchCheck = true;
        ModBulkUpdateConfirmationRequest? pending = null;
        var failureShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.ViewModel.ModBulkUpdateConfirmationRequested += request =>
        {
            pending = request;
            request.Changed += () =>
            {
                if (request.Stage is ModBulkUpdateConfirmationStage.Failed)
                    failureShown.TrySetResult();
            };
            request.Confirm();
        };

        context.ViewModel.UpdateAllModsCommand.Execute(null);
        await failureShown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(pending);
        Assert.Equal(ModBulkUpdateConfirmationStage.Failed, pending.Stage);
        Assert.True(context.ViewModel.IsBulkUpdateBusy);
        Assert.Equal(DownloadTaskState.Failed, Assert.Single(context.Tasks.Tasks).State);

        pending.Confirm();
        await context.Tasks.WaitForTrackedBackgroundTasksAsync(TimeSpan.FromSeconds(5));

        Assert.False(context.ViewModel.IsBulkUpdateBusy);
    }

    private static void ConfirmBulkFlow(ModBulkUpdateConfirmationRequest request)
    {
        void ConfirmSummary()
        {
            if (request.Stage is not ModBulkUpdateConfirmationStage.Summary)
                return;

            request.Changed -= ConfirmSummary;
            request.Confirm();
        }

        request.Changed += ConfirmSummary;
        request.Confirm();
    }

    private static async Task<TestContext> CreateContextAsync(
        bool recognized = true,
        IReadOnlyList<LocalMod>? mods = null)
    {
        mods ??= [CreateMod("Example", "example-1.jar", enabled: true, recognized)];
        var modService = new StubModService(mods);
        var status = new StubStatusService();
        var updateService = new StubUpdateService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var localMods = new LocalModsViewModel(modService, status, new NoOpDirectoryMonitor());
        var viewModel = new InstanceModManagementSettingsViewModel(
            null!,
            localMods,
            status,
            null!,
            null!,
            null!,
            new StubFloatingMessageService(),
            modUpdateService: updateService,
            downloadTasksPage: tasks);
        var instance = new GameInstance
        {
            Id = "instance",
            InstanceDirectory = "instance",
            MinecraftVersion = "1.20.1",
            Loader = LoaderKind.Fabric
        };
        await viewModel.SetSelectedInstanceAsync(instance);
        await viewModel.OnSectionActivatedAsync();
        return new TestContext(viewModel, localMods, updateService, tasks);
    }

    private static LocalMod CreateMod(
        string name,
        string fileName,
        bool enabled,
        bool recognized = true) => new()
    {
        Name = name,
        FileName = fileName,
        FullPath = Path.Combine("instance", "mods", fileName),
        IsEnabled = enabled,
        ProjectReference = recognized
            ? new ResourceProjectReference(
                ResourceProjectKind.Mod,
                ResourceProjectSource.Modrinth,
                name.ToLowerInvariant())
            : null
    };

    private sealed record TestContext(
        InstanceModManagementSettingsViewModel ViewModel,
        LocalModsViewModel LocalMods,
        StubUpdateService UpdateService,
        DownloadTasksPageViewModel Tasks) : IDisposable
    {
        public void Dispose() => LocalMods.Dispose();
    }

    private sealed class StubUpdateService : IModUpdateService
    {
        public int CheckCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int CheckManyCalls { get; private set; }
        public int UpdateManyCalls { get; private set; }
        public int LastCheckedCount { get; private set; }
        public int LastUpdatedCount { get; private set; }
        public ModUpdateCheckStatus BatchCheckStatus { get; set; } = ModUpdateCheckStatus.UpdateAvailable;
        public Func<LocalMod, ModUpdateCheckResult>? BatchResultFactory { get; set; }
        public bool BlockBatchCheck { get; set; }

        public bool ReturnBatchCheckAfterCancellation { get; set; }

        public bool FailBatchCheckAfterCancellation { get; set; }

        public bool FailBatchCheck { get; set; }
        public TaskCompletionSource<bool> BatchCheckStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModUpdateCheckResult> CheckAsync(
            GameInstance instance,
            LocalMod localMod,
            CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return Task.FromResult(CreateCheckResult(localMod));
        }

        private ModUpdateCheckResult CreateCheckResult(LocalMod localMod)
        {
            if (BatchCheckStatus is not ModUpdateCheckStatus.UpdateAvailable)
                return new ModUpdateCheckResult(BatchCheckStatus, SuccessfulProviderCount: 2, RecognizedProviderCount: 2);

            var state = new ModUpdateLocalFileState(localMod.FullPath, "sha1", 123, 10, 20, localMod.IsEnabled);
            var candidate = new ModUpdateCandidate(
                state,
                ResourceProjectSource.Modrinth,
                "project",
                "current",
                "1.0.0",
                DateTimeOffset.UtcNow.AddDays(-1),
                new ResourceProjectVersion
                {
                    VersionId = "target",
                    VersionNumber = "2.0.0",
                    VersionType = "release",
                    FileName = Path.GetFileNameWithoutExtension(localMod.FileName) + "-2.jar",
                    PublishedAt = DateTimeOffset.UtcNow
                });
            return new ModUpdateCheckResult(
                ModUpdateCheckStatus.UpdateAvailable,
                candidate,
                2,
                2);
        }

        public ModUpdateCheckResult CreateAvailableResult(LocalMod localMod) => CreateCheckResult(localMod);

        public Task<ModUpdateResult> UpdateAsync(
            GameInstance instance,
            ModUpdateCandidate candidate,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            return Task.FromResult(new ModUpdateResult("example-2.jar", "example-1.jar.old"));
        }

        public async Task<ModUpdateBatchCheckResult> CheckManyAsync(
            GameInstance instance,
            IReadOnlyList<LocalMod> mods,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CheckManyCalls++;
            LastCheckedCount = mods.Count;
            if (FailBatchCheck)
                throw new InvalidOperationException("Batch check failed.");
            if (BlockBatchCheck)
            {
                BatchCheckStarted.TrySetResult(true);
                if (ReturnBatchCheckAfterCancellation)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                }
                else if (FailBatchCheckAfterCancellation)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException("Batch check failed after cancellation.");
                    }
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }

            var items = new List<ModUpdateBatchCheckItem>(mods.Count);
            foreach (var item in mods)
            {
                var result = BatchResultFactory?.Invoke(item) ?? CreateCheckResult(item);
                items.Add(new ModUpdateBatchCheckItem(item.FullPath, result));
            }

            return new ModUpdateBatchCheckResult(items, 2);
        }

        public async Task<ModUpdateBatchResult> UpdateManyAsync(
            GameInstance instance,
            IReadOnlyList<ModUpdateCandidate> candidates,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpdateManyCalls++;
            LastUpdatedCount = candidates.Count;
            var items = new List<ModUpdateBatchItemResult>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var result = await UpdateAsync(instance, candidate, progress, cancellationToken);
                items.Add(new ModUpdateBatchItemResult(candidate, result));
            }

            return new ModUpdateBatchResult(items);
        }
    }

    private sealed class StubModService(IReadOnlyList<LocalMod> mods) : IModService
    {
        public Task<IReadOnlyList<LocalMod>> GetModsAsync(GameInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(mods);

        public Task<LocalMod> ImportAsync(GameInstance instance, string sourceJarPath, bool overwriteExisting = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetEnabledAsync(LocalMod mod, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(LocalMod mod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubStatusService : IStatusService
    {
        public event Action<string>? MessageReported;

        public void Report(string message) => MessageReported?.Invoke(message);
    }

    private sealed class StubFloatingMessageService : IFloatingMessageService
    {
        public event Action<FloatingMessageRequest>? MessageRequested;

        public void Show(string message) => MessageRequested?.Invoke(new FloatingMessageRequest(message));

        public void ShowDragHint(object source, string message)
        {
        }

        public void ClearDragHint(object source)
        {
        }

        public void ClearDragHint()
        {
        }
    }

    private sealed class NoOpDirectoryMonitor : IInstanceDirectoryMonitor
    {
        public IInstanceDirectoryWatch Watch(GameInstance instance, InstanceDirectoryKind directoryKind) =>
            new NoOpDirectoryWatch();
    }

    private sealed class NoOpDirectoryWatch : IInstanceDirectoryWatch
    {
        public event EventHandler<InstanceDirectoryChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }
}
