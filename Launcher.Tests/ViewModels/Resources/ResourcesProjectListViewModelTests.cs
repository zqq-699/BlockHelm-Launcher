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
using Launcher.App.ViewModels.Resources;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Resources;

public sealed class ResourcesProjectListViewModelTests
{
    [Fact]
    public async Task FailedProjectListCanBeRefreshed()
    {
        var retryResult = new TaskCompletionSource<ResourceCatalogSearchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SequencedResourceCatalogService(
            () => Task.FromException<ResourceCatalogSearchResult>(new HttpRequestException("offline")),
            () => retryResult.Task);
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.ProjectList;

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanShowLoadErrorState);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));

        var refresh = viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CallCount);
        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.CanShowLoadErrorState);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));

        retryResult.SetResult(CreateSearchResult());
        await refresh;

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.CanShowLoadErrorState);
        Assert.Equal("sodium", Assert.Single(viewModel.VisibleProjects).Project.ProjectId);
    }

    [Fact]
    public async Task FailedRefreshRemainsRetryable()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromException<ResourceCatalogSearchResult>(new HttpRequestException("offline")),
            () => Task.FromException<ResourceCatalogSearchResult>(new HttpRequestException("still offline")));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.ProjectList;

        await viewModel.RefreshAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CallCount);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanShowLoadErrorState);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task PageReentryRetriesFailedProjectListLoad()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromException<ResourceCatalogSearchResult>(new HttpRequestException("offline")),
            () => Task.FromResult(CreateSearchResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.ProjectList;

        await viewModel.RefreshAsync();
        viewModel.BeginEnsureLoaded();

        Assert.Equal(2, service.CallCount);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.CanShowLoadErrorState);
        Assert.Equal("sodium", Assert.Single(viewModel.VisibleProjects).Project.ProjectId);
    }

    [Fact]
    public void ProjectDetailsDeepLinkDefersProjectListLoadUntilReturningToList()
    {
        var service = new SequencedResourceCatalogService(() => Task.FromResult(CreateSearchResult()));
        var resources = new ResourcesPageViewModel(service);
        var reference = new ResourceProjectReference(
            ResourceProjectKind.Mod,
            ResourceProjectSource.Modrinth,
            "sodium");
        var project = new ResourceProject
        {
            Kind = ResourceProjectKind.Mod,
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "sodium",
            Title = "Sodium"
        };

        Assert.True(resources.BeginLoadProjectDetails(reference));
        Assert.True(resources.ModPage.IsProjectDetailsLoading);
        resources.ShowProjectDetails(reference, project);
        resources.BeginEnsureCurrentSectionLoaded();

        Assert.Equal("mods", resources.SelectedSection?.Id);
        Assert.Same(resources.ModPage, resources.CurrentOnlineProjectPage);
        Assert.Equal(ResourcesModPageStep.ProjectDetails, resources.ModPage.CurrentStep);
        Assert.Same(project, resources.ModPage.Details.CurrentProject?.Project);
        Assert.Equal(0, service.CallCount);

        resources.ModPage.BackToProjectList();

        Assert.Equal(ResourcesModPageStep.ProjectList, resources.ModPage.CurrentStep);
        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public void ProjectDetailsLoadingIgnoresCompletionAfterUserReturnsToList()
    {
        var resources = new ResourcesPageViewModel();
        var reference = new ResourceProjectReference(
            ResourceProjectKind.Mod,
            ResourceProjectSource.Modrinth,
            "sodium");
        var project = new ResourceProject
        {
            Kind = ResourceProjectKind.Mod,
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "sodium",
            Title = "Sodium"
        };

        Assert.True(resources.BeginLoadProjectDetails(reference));
        Assert.True(resources.ModPage.IsProjectDetailsLoading);
        Assert.Equal(ResourcesModPageStep.ProjectDetails, resources.ModPage.CurrentStep);

        resources.ModPage.BackToProjectList();

        Assert.False(resources.ModPage.IsProjectDetailsLoading);
        Assert.Equal(ResourcesModPageStep.ProjectList, resources.ModPage.CurrentStep);
        Assert.False(resources.ShowProjectDetails(reference, project));
        Assert.Null(resources.ModPage.Details.CurrentProject);
    }

    /// <summary>
    /// 结果落地和缩略图回填都是排队执行的，且大结果集是分批加入集合的：缩略图返回时，
    /// 后面几批还没进 VisibleProjects。曾经要求"已在集合中"才回填，这些图标会被静默丢弃，
    /// 直到用户重新加载才出现。
    /// </summary>
    [Fact]
    public async Task ThumbnailsResolvedBeforeTheirItemsEnterTheListAreStillApplied()
    {
        const int projectCount = 20;
        var dispatcher = new QueuedUiDispatcher();
        var service = new ThumbnailingResourceCatalogService(projectCount);
        using var page = new ResourcesPageViewModel(service, uiDispatcher: dispatcher).ModPage;
        var viewModel = page.ProjectList;

        // 结果落地本身也排在队列里，因此先不 await，由测试推动队列。
        var refresh = viewModel.RefreshAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!refresh.IsCompleted || viewModel.VisibleProjects.Count < projectCount)
               && DateTime.UtcNow < deadline)
        {
            dispatcher.Flush();
            await Task.Yield();
        }
        dispatcher.Flush();
        await refresh;

        Assert.Equal(projectCount, viewModel.VisibleProjects.Count);
        Assert.All(viewModel.VisibleProjects, item => Assert.False(string.IsNullOrEmpty(item.IconSource)));
    }

    /// <summary>
    /// 结果落地要让位给切页动画，是排队执行的。但排队不能改变异步方法的完成语义：
    /// 任务完成必须代表列表已经更新，否则 await 之后读到的还是旧状态。
    /// </summary>
    [Fact]
    public async Task RefreshTaskCompletesOnlyAfterTheListHasBeenUpdated()
    {
        var dispatcher = new QueuedUiDispatcher();
        var service = new SequencedResourceCatalogService(() => Task.FromResult(CreateSearchResult()));
        using var page = new ResourcesPageViewModel(service, uiDispatcher: dispatcher).ModPage;
        var viewModel = page.ProjectList;

        var refresh = viewModel.RefreshAsync();

        // 结果还排在队列里，任务此刻绝不能已经完成。
        Assert.False(refresh.IsCompleted);
        Assert.True(viewModel.IsLoading);
        Assert.Empty(viewModel.VisibleProjects);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!refresh.IsCompleted && DateTime.UtcNow < deadline)
        {
            dispatcher.Flush();
            await Task.Yield();
        }
        await refresh;

        Assert.False(viewModel.IsLoading);
        Assert.Equal("sodium", Assert.Single(viewModel.VisibleProjects).Project.ProjectId);
    }

    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly List<Action> queued = [];

        public bool HasAccess => true;

        public void Post(Action action) => action();

        // 攒起来而不是立刻执行，让测试能自己决定"结果落地"和"缩略图回填"谁先跑。
        public void PostAfterTransition(Action action) => queued.Add(action);

        public Task PostAfterTransitionAsync(Action action)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queued.Add(() =>
            {
                try
                {
                    action();
                    completed.TrySetResult();
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
            });
            return completed.Task;
        }

        public void Invoke(Action action) => action();

        public Task InvokeAsync(Func<Task> action) => action();

        public void Flush()
        {
            lock (queued)
            {
                var pending = queued.ToArray();
                queued.Clear();
                foreach (var action in pending)
                    action();
            }
        }
    }

    private sealed class ThumbnailingResourceCatalogService(int projectCount)
        : IResourceCatalogService, IResourceThumbnailService
    {
        public Task<ResourceCatalogSearchResult> SearchModsAsync(
            ResourceCatalogSearchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceCatalogSearchResult
            {
                Projects = Enumerable.Range(0, projectCount)
                    .Select(index => new ResourceProject
                    {
                        Kind = ResourceProjectKind.Mod,
                        Source = ResourceProjectSource.Modrinth,
                        ProjectId = $"project-{index}",
                        Title = $"Project {index}",
                        IconUrl = $"https://example.invalid/{index}.png"
                    })
                    .ToList()
            });

        // 没有缓存：图标只能走异步那条路回填，正是本用例要覆盖的路径。
        public string? TryGetCachedThumbnailSource(ResourceProject project) => null;

        public Task<string?> GetOrCreateThumbnailSourceAsync(
            ResourceProject project,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>($"icon://{project.ProjectId}");

        public Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(
            ResourceProjectVersionsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> InstallProjectVersionAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DownloadProjectVersionAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ProjectVersionDownloadExistsAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ProjectVersionInstallExistsAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static ResourceCatalogSearchResult CreateSearchResult()
    {
        return new ResourceCatalogSearchResult
        {
            Projects =
            [
                new ResourceProject
                {
                    Kind = ResourceProjectKind.Mod,
                    Source = ResourceProjectSource.Modrinth,
                    ProjectId = "sodium",
                    Title = "Sodium"
                }
            ]
        };
    }

    private sealed class SequencedResourceCatalogService(
        params Func<Task<ResourceCatalogSearchResult>>[] responses) : IResourceCatalogService
    {
        private readonly Queue<Func<Task<ResourceCatalogSearchResult>>> responses = new(responses);

        public int CallCount { get; private set; }

        public Task<ResourceCatalogSearchResult> SearchModsAsync(
            ResourceCatalogSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return responses.Dequeue()();
        }

        public Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(
            ResourceProjectVersionsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> InstallProjectVersionAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DownloadProjectVersionAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ProjectVersionDownloadExistsAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ProjectVersionInstallExistsAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
