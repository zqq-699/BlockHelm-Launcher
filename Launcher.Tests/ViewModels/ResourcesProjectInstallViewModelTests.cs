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
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.Resources;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.ViewModels;

public sealed class ResourcesProjectInstallViewModelTests
{
    [Fact]
    public async Task IntegrityFailureUsesLocalizedMessageInsteadOfGenericFailure()
    {
        var installation = new RecordingInstallationService { IntegrityFailure = true };
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var statuses = new List<string>();
        var viewModel = CreateViewModel(installation, tasks, statuses.Add);

        await viewModel.InstallAsync(
            CreateVersionItem(ResourceProjectKind.ResourcePack),
            ResourcesModInstallTargetItemViewModel.CreateLocalDownload(),
            null);

        var task = Assert.Single(tasks.Tasks);
        Assert.Equal(DownloadTaskState.Failed, task.State);
        Assert.Equal(Strings.Status_ResourceProjectIntegrityFailed, task.StatusMessage);
        Assert.Contains(Strings.Status_ResourceProjectIntegrityFailed, statuses);
        Assert.DoesNotContain("download failed", statuses);
    }

    [Fact]
    public async Task NewModpackInstallsRunConcurrentlyAndShowFeedbackForEachTask()
    {
        var installation = new ControllableInstallationService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var messages = new RecordingFloatingMessageService();
        var viewModel = CreateViewModel(installation, tasks, _ => { }, messages);
        var target = ResourcesModInstallTargetItemViewModel.CreateNewInstanceInstall("new instance");

        var first = viewModel.InstallAsync(CreateVersionItem(ResourceProjectKind.Modpack, "first"), target, null);
        var second = viewModel.InstallAsync(CreateVersionItem(ResourceProjectKind.Modpack, "second"), target, null);
        await installation.WaitForExecutionCountAsync(2);

        Assert.True(viewModel.IsInstalling);
        Assert.Equal(2, tasks.Tasks.Count);
        Assert.All(tasks.Tasks, task => Assert.Equal(DownloadTaskState.Running, task.State));
        Assert.Equal(2, messages.Messages.Count(message => message == "downloading"));

        installation.Complete("first", CreateSuccessfulModpackResult("first-instance"));
        await first;
        installation.Fail("second", new InvalidOperationException("failure"));
        await second;

        Assert.False(viewModel.IsInstalling);
        Assert.Equal(DownloadTaskState.Completed, tasks.Tasks.Single(task => task.Title == "Version first").State);
        Assert.Equal(DownloadTaskState.Failed, tasks.Tasks.Single(task => task.Title == "Version second").State);
    }

    [Theory]
    [InlineData(false)]
    public async Task OrdinaryResourceRequestsRunConcurrentlyAndCreateVisibleTasks(bool localDownload)
    {
        var installation = new ControllableInstallationService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var viewModel = CreateViewModel(installation, tasks, _ => { });
        var target = localDownload
            ? ResourcesModInstallTargetItemViewModel.CreateLocalDownload("save as")
            : ResourcesModInstallTargetItemViewModel.FromInstance(new GameInstance
            {
                Id = "instance",
                Name = "Instance",
                InstanceDirectory = "instance"
            });
        var kind = localDownload ? ResourceProjectKind.ResourcePack : ResourceProjectKind.Mod;

        var first = viewModel.InstallAsync(CreateVersionItem(kind, "first"), target, null);
        var second = viewModel.InstallAsync(CreateVersionItem(kind, "second"), target, null);
        await installation.WaitForExecutionCountAsync(2);

        Assert.True(viewModel.IsInstalling);
        Assert.Equal(2, tasks.Tasks.Count);
        Assert.All(tasks.Tasks, task => Assert.Equal(DownloadTaskState.Running, task.State));

        installation.Complete("first", new ResourceProjectInstallationResult());
        await first;
        installation.Complete("second", new ResourceProjectInstallationResult());
        await second;

        Assert.False(viewModel.IsInstalling);
        Assert.All(tasks.Tasks, task => Assert.Equal(DownloadTaskState.Completed, task.State));
    }

    [Theory]
    [InlineData(false)]
    public async Task DuplicateResourceRequestForSameTargetReusesRunningTask(bool localDownload)
    {
        var installation = new ControllableInstallationService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var messages = new RecordingFloatingMessageService();
        var viewModel = CreateViewModel(installation, tasks, _ => { }, messages);
        var target = localDownload
            ? ResourcesModInstallTargetItemViewModel.CreateLocalDownload("save as")
            : ResourcesModInstallTargetItemViewModel.FromInstance(new GameInstance
            {
                Id = "instance",
                Name = "Instance",
                InstanceDirectory = "instance"
            });
        var item = CreateVersionItem(
            localDownload ? ResourceProjectKind.ResourcePack : ResourceProjectKind.Mod,
            "same");

        var first = viewModel.InstallAsync(item, target, null);
        await installation.WaitForExecutionCountAsync(1);
        await viewModel.InstallAsync(item, target, null);

        Assert.Equal(1, installation.ExecutionCount);
        Assert.Single(tasks.Tasks);
        Assert.Contains(Strings.Status_ResourceProjectDownloadAlreadyRunning, messages.Messages);

        installation.Complete("same", new ResourceProjectInstallationResult());
        await first;
    }

    [Fact]
    public async Task SameResourceCanInstallIntoDifferentInstancesConcurrently()
    {
        var installation = new ControllableInstallationService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var viewModel = CreateViewModel(installation, tasks, _ => { });
        var item = CreateVersionItem(ResourceProjectKind.Mod, "same");
        var firstTarget = ResourcesModInstallTargetItemViewModel.FromInstance(new GameInstance
        {
            Id = "first-instance",
            Name = "First",
            InstanceDirectory = "first"
        });
        var secondTarget = ResourcesModInstallTargetItemViewModel.FromInstance(new GameInstance
        {
            Id = "second-instance",
            Name = "Second",
            InstanceDirectory = "second"
        });

        var first = viewModel.InstallAsync(item, firstTarget, null);
        var second = viewModel.InstallAsync(item, secondTarget, null);
        await installation.WaitForExecutionCountAsync(2);

        Assert.Equal(2, tasks.Tasks.Count);
        installation.Complete("same", new ResourceProjectInstallationResult());
        await Task.WhenAny(first, second);
        installation.Complete("same", new ResourceProjectInstallationResult());
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task LocalDownloadUsesExactSaveDialogDestination()
    {
        var destination = Path.Combine("chosen", "renamed.jar");
        var picker = new StubFilePickerService { ResourceDestination = destination };
        var installation = new RecordingInstallationService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var viewModel = CreateViewModel(
            installation,
            tasks,
            _ => { },
            filePickerService: picker);

        await viewModel.InstallAsync(
            CreateVersionItem(ResourceProjectKind.Mod),
            ResourcesModInstallTargetItemViewModel.CreateLocalDownload(),
            null);

        var request = Assert.Single(installation.ExecutedRequests);
        Assert.Equal(Path.GetFullPath(destination), request.DestinationPath);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(destination)), request.TargetDirectory);
        Assert.NotNull(request.ExpectedDestinationState);
        Assert.Equal("mod.jar", picker.LastDefaultFileName);
    }

    [Fact]
    public async Task ExistingInstanceRejectsDestinationOutsideItsContentDirectory()
    {
        var picker = new StubFilePickerService
        {
            ResourceDestination = Path.Combine(Path.GetTempPath(), "outside", "mod.jar")
        };
        var installation = new RecordingInstallationService();
        var tasks = new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1));
        var statuses = new List<string>();
        var viewModel = CreateViewModel(
            installation,
            tasks,
            statuses.Add,
            filePickerService: picker);
        var instance = new GameInstance
        {
            Id = "instance",
            InstanceDirectory = Path.Combine(Path.GetTempPath(), "instance")
        };

        await viewModel.InstallAsync(
            CreateVersionItem(ResourceProjectKind.Mod),
            ResourcesModInstallTargetItemViewModel.FromInstance(instance),
            null);

        Assert.Empty(installation.ExecutedRequests);
        Assert.Empty(tasks.Tasks);
        Assert.Contains(Strings.Status_ResourceProjectInstanceDestinationInvalid, statuses);
        Assert.Equal(Path.Combine(instance.InstanceDirectory, "mods"), picker.LastInitialDirectory);
    }

    [Theory]
    [InlineData(ResourceProjectKind.Mod, "mods")]
    public async Task ExistingInstanceEnsuresCorrespondingContentDirectoryBeforeContinuing(
        ResourceProjectKind kind,
        string directoryName)
    {
        var instance = new GameInstance
        {
            Id = "instance",
            InstanceDirectory = Path.Combine(Path.GetTempPath(), $"resource-instance-{Guid.NewGuid():N}")
        };
        var ensuredDirectory = Path.Combine(instance.InstanceDirectory, directoryName);
        var installation = new RecordingInstallationService
        {
            EnsuredDirectory = ensuredDirectory
        };
        var picker = new StubFilePickerService
        {
            ResourceDestination = Path.Combine(ensuredDirectory, "selected-file")
        };
        var viewModel = CreateViewModel(
            installation,
            new DownloadTasksPageViewModel(TimeSpan.FromMinutes(1)),
            _ => { },
            filePickerService: picker,
            kind: kind);

        await viewModel.InstallAsync(
            CreateVersionItem(kind),
            ResourcesModInstallTargetItemViewModel.FromInstance(instance),
            null);

        Assert.Equal(kind, installation.EnsuredKind);
        Assert.Same(instance, installation.EnsuredInstance);
        if (kind is ResourceProjectKind.World)
            Assert.Null(picker.LastInitialDirectory);
        else
            Assert.Equal(ensuredDirectory, picker.LastInitialDirectory);
        Assert.Single(installation.ExecutedRequests);
    }

    private static ResourcesProjectInstallViewModel CreateViewModel(
        IResourceProjectInstallationService installation,
        DownloadTasksPageViewModel tasks,
        Action<string> reportStatus,
        IFloatingMessageService? floatingMessageService = null,
        IFilePickerService? filePickerService = null,
        ResourceProjectKind kind = ResourceProjectKind.Mod)
    {
        var options = CreateOptions(kind);
        return new ResourcesProjectInstallViewModel(
            options,
            installation,
            new ResourcesRequiredDependencyPlanner(null, options, null, reportStatus),
            filePickerService ?? new StubFilePickerService(),
            floatingMessageService,
            tasks,
            ImmediateUiDispatcher.Instance,
            NullLogger<ResourcesProjectInstallViewModel>.Instance,
            reportStatus);
    }

    private static ResourcesModVersionItemViewModel CreateVersionItem(
        ResourceProjectKind kind,
        string versionId = "version") => new(
        new ResourceProjectVersion
        {
            Kind = kind,
            VersionId = versionId,
            Name = $"Version {versionId}",
            FileName = kind is ResourceProjectKind.Mod ? "mod.jar" : "pack.zip"
        },
        null);

    private static ResourceProjectInstallationResult CreateSuccessfulModpackResult(string instanceId) => new(
        ModpackImportResult: ModpackImportResult.Success(new GameInstance
        {
            Id = instanceId,
            Name = instanceId,
            InstanceDirectory = instanceId
        }));

    private static ResourcesOnlineProjectPageOptions CreateOptions(
        ResourceProjectKind kind = ResourceProjectKind.Mod) => new(
        Kind: kind,
        Title: "title",
        FallbackIconKey: "icon",
        ShowsLoaderFilters: true,
        AllVersionsText: "all versions",
        AllLoadersText: "all loaders",
        ProjectsLoadingText: "loading",
        ProjectsEmptyText: "empty",
        ProjectsLoadErrorText: "error",
        ProjectsLoadingMoreText: "loading more",
        ProjectsNoMoreText: "no more",
        ProjectsLoadMoreErrorText: "more error",
        CurseForgeMissingApiKeyText: "missing key",
        DetailsInfoSectionText: "details",
        InstallTargetSectionText: "targets",
        InstallTargetLocalText: "local",
        InstallTargetsLoadingText: "targets loading",
        InstallTargetsLoadErrorText: "targets error",
        VersionsLoadingText: "versions loading",
        VersionsEmptyText: "versions empty",
        VersionsEmptyLocalText: "local empty",
        VersionsFilterEmptyText: "filter empty",
        VersionsLoadErrorText: "versions error",
        VersionsLoadingMoreText: "versions more",
        VersionsNoMoreText: "versions no more",
        VersionsLoadMoreErrorText: "versions more error",
        VersionsAllTitleText: "all",
        DownloadDirectoryPickerTitle: "folder",
        DownloadingText: "downloading",
        DownloadingFormat: "downloading {0}",
        DownloadedFormat: "downloaded {0}",
        DownloadFailedText: "download failed",
        InstalledFormat: "installed {0}",
        InstallFailedText: "install failed",
        FileExistsMessageFormat: "exists {0}",
        TypeOptions: []);

    private sealed class RecordingInstallationService : IResourceProjectInstallationService
    {
        public bool TargetExists { get; init; }
        public bool WaitForCancellation { get; init; }
        public bool IntegrityFailure { get; init; }
        public string? TargetPath { get; init; }
        public string? EnsuredDirectory { get; init; }
        public ResourceProjectKind? EnsuredKind { get; private set; }
        public GameInstance? EnsuredInstance { get; private set; }
        public TaskCompletionSource<bool> ExecuteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ResourceProjectInstallationRequest> ExecutedRequests { get; } = [];

        public Task<string> EnsureInstanceContentDirectoryAsync(
            ResourceProjectKind kind,
            GameInstance instance,
            CancellationToken cancellationToken = default)
        {
            EnsuredKind = kind;
            EnsuredInstance = instance;
            return Task.FromResult(
                EnsuredDirectory
                ?? Path.Combine(instance.InstanceDirectory, ResolveContentDirectoryName(kind)));
        }

        public Task<ResourceProjectInstallationPreparationResult> PrepareAsync(
            ResourceProjectInstallationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceProjectInstallationPreparationResult(
                TargetExists,
                TargetPath,
                request.DestinationPath is null
                    ? null
                    : new ResourceProjectDestinationState(false, 0, 0, string.Empty)));

        public async Task<ResourceProjectInstallationResult> ExecuteAsync(
            ResourceProjectInstallationRequest request,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExecutedRequests.Add(request);
            ExecuteStarted.TrySetResult(true);
            if (IntegrityFailure)
            {
                throw new ResourceProjectIntegrityException(
                    request.Version.VersionId,
                    ResourceProjectIntegrityFailureReason.HashMismatch,
                    ResourceFileHashAlgorithm.Sha512);
            }
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ResourceProjectInstallationResult();
        }
    }

    private sealed class ControllableInstallationService : IResourceProjectInstallationService
    {
        private readonly object syncRoot = new();
        private readonly Dictionary<string, List<TaskCompletionSource<ResourceProjectInstallationResult>>> executions = [];
        private TaskCompletionSource<bool> executionCountChanged = CreateSignal();

        public int ExecutionCount
        {
            get
            {
                lock (syncRoot)
                    return executions.Values.Sum(values => values.Count);
            }
        }

        public Task<string> EnsureInstanceContentDirectoryAsync(
            ResourceProjectKind kind,
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(instance.InstanceDirectory, ResolveContentDirectoryName(kind)));

        public Task<ResourceProjectInstallationPreparationResult> PrepareAsync(
            ResourceProjectInstallationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceProjectInstallationPreparationResult(false));

        public Task<ResourceProjectInstallationResult> ExecuteAsync(
            ResourceProjectInstallationRequest request,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<ResourceProjectInstallationResult> completion;
            lock (syncRoot)
            {
                completion = new TaskCompletionSource<ResourceProjectInstallationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!executions.TryGetValue(request.Version.VersionId, out var versionExecutions))
                {
                    versionExecutions = [];
                    executions.Add(request.Version.VersionId, versionExecutions);
                }
                versionExecutions.Add(completion);
                executionCountChanged.TrySetResult(true);
                executionCountChanged = CreateSignal();
            }
            return completion.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForExecutionCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (true)
            {
                Task signal;
                lock (syncRoot)
                {
                    if (executions.Values.Sum(values => values.Count) >= count)
                        return;
                    signal = executionCountChanged.Task;
                }
                await signal.WaitAsync(timeout.Token);
            }
        }

        public void Complete(string versionId, ResourceProjectInstallationResult result)
        {
            lock (syncRoot)
                executions[versionId].First(execution => !execution.Task.IsCompleted).TrySetResult(result);
        }

        public void Fail(string versionId, Exception exception)
        {
            lock (syncRoot)
                executions[versionId].First(execution => !execution.Task.IsCompleted).TrySetException(exception);
        }

        public bool IsCompleted(string versionId)
        {
            lock (syncRoot)
                return executions[versionId].All(execution => execution.Task.IsCompleted);
        }

        private static TaskCompletionSource<bool> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string ResolveContentDirectoryName(ResourceProjectKind kind) =>
        kind switch
        {
            ResourceProjectKind.Mod => "mods",
            ResourceProjectKind.ResourcePack => "resourcepacks",
            ResourceProjectKind.ShaderPack => "shaderpacks",
            ResourceProjectKind.World => "saves",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private sealed class RecordingFloatingMessageService : IFloatingMessageService
    {
        public event Action<FloatingMessageRequest>? MessageRequested;

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
            MessageRequested?.Invoke(new FloatingMessageRequest(message));
        }

        public void ShowDragHint(object source, string message)
        {
            Messages.Add(message);
            MessageRequested?.Invoke(new FloatingMessageRequest(message, AutoHide: false));
        }

        public void ClearDragHint(object source) => ClearDragHint();

        public void ClearDragHint()
        {
            MessageRequested?.Invoke(new FloatingMessageRequest(string.Empty));
        }
    }

    private sealed class StubFilePickerService(string? folder = "target") : IFilePickerService
    {
        public string? ResourceDestination { get; init; }
        public string? LastDefaultFileName { get; private set; }
        public string? LastInitialDirectory { get; private set; }

        public string? PickMinecraftSkin() => null;
        public string? PickJavaExecutable() => null;
        public string? PickLocalImportFile() => null;
        public string? PickModFile() => null;
        public string? PickSaveArchive() => null;
        public string? PickResourcePackArchive() => null;
        public string? PickShaderPackArchive() => null;
        public string? PickModpackExportArchive(string defaultFileName, ModpackExportKind kind) => null;
        public string? PickLaunchDiagnosticExportArchive(string instanceName) => null;
        public string? PickCustomDownloadDestination(string defaultFileName) => null;
        public string? PickResourceProjectDestination(
            string title,
            string defaultFileName,
            string? initialDirectory = null)
        {
            LastDefaultFileName = defaultFileName;
            LastInitialDirectory = initialDirectory;
            return ResourceDestination
                ?? Path.Combine(initialDirectory ?? folder ?? string.Empty, defaultFileName);
        }
        public string? PickFolder(string title, string? initialDirectory = null) => folder;
    }
}
