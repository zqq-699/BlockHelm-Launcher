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

using Launcher.App.ViewModels.Resources;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Resources;

public sealed class ResourcesProjectVersionsViewModelTests
{
    [Fact]
    public async Task MultipleCompatibilityGroupsStartCollapsedAndCanBeExpanded()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromResult(CreateGroupedVersionsResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);

        await viewModel.RefreshAsync();

        var headers = viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().ToList();
        Assert.Equal(2, headers.Count);
        Assert.All(headers, header => Assert.False(header.IsExpanded));
        Assert.Empty(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.False(viewModel.CanAutoLoadMoreVersions);

        var notified = false;
        var entranceWasPreparedBeforeInsertion = false;
        viewModel.GroupEntranceRequested += children =>
        {
            entranceWasPreparedBeforeInsertion = children.OfType<ResourcesModVersionItemViewModel>()
                .All(child => child.IsGroupEntrancePending)
                && !viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Any();
        };
        headers[0].PropertyChanged += (_, args) =>
            notified |= args.PropertyName == nameof(ResourcesModVersionListHeaderItem.IsExpanded);
        headers[0].ToggleExpandedCommand.Execute(null);

        var expandedHeader = Assert.Single(
            viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>(),
            header => header.Title == headers[0].Title);
        Assert.Same(headers[0], expandedHeader);
        Assert.Same(headers[1], viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().Last());
        Assert.True(notified);
        Assert.True(entranceWasPreparedBeforeInsertion);
        Assert.True(expandedHeader.IsExpanded);
        Assert.Single(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.True(viewModel.CanAutoLoadMoreVersions);

        expandedHeader.ToggleExpandedCommand.Execute(null);
        Assert.Same(expandedHeader, viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().First());
        Assert.Empty(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
    }

    [Fact]
    public async Task CollapseWaitsForReverseEntranceBeforeRemovingChildren()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromResult(CreateGroupedVersionsResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);
        await viewModel.RefreshAsync();

        var header = viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().First();
        header.ToggleExpandedCommand.Execute(null);
        Assert.Single(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());

        Action? completeCollapse = null;
        viewModel.GroupCollapseRequested += (_, complete) => completeCollapse = complete;
        header.ToggleExpandedCommand.Execute(null);

        // 真实行控件先播放退出动画，动画结束后才移除。
        Assert.Single(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.NotNull(completeCollapse);
        completeCollapse!();
        Assert.Empty(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.Equal(52, Assert.Single(viewModel.ListItems.OfType<ResourcesGroupCollapseSpacerItem>()).Height);

        await Task.Delay(400);
        Assert.Empty(viewModel.ListItems.OfType<ResourcesGroupCollapseSpacerItem>());

        header.ToggleExpandedCommand.Execute(null);
        Assert.Single(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
    }

    [Fact]
    public async Task LargeGroupKeepsAllChildrenUntilTheExitAnimationCompletes()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromResult(CreateLargeGroupedVersionsResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);
        await viewModel.RefreshAsync();

        var header = viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().First();
        var queuedChildren = 0;
        viewModel.GroupEntranceRequested += children =>
        {
            queuedChildren = children.Count;
            Assert.All(children.OfType<ResourcesModVersionItemViewModel>(), child =>
                Assert.True(child.IsGroupEntrancePending));
        };
        header.ToggleExpandedCommand.Execute(null);
        Assert.Equal(25, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());
        Assert.Equal(25, queuedChildren);

        Action? completeCollapse = null;
        viewModel.GroupCollapseRequested += (_, complete) => completeCollapse = complete;
        header.ToggleExpandedCommand.Execute(null);
        Assert.Equal(25, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());
        Assert.NotNull(completeCollapse);
        completeCollapse!();
        Assert.Empty(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.Equal(1040, Assert.Single(viewModel.ListItems.OfType<ResourcesGroupCollapseSpacerItem>()).Height);

        await Task.Delay(400);
        Assert.Empty(viewModel.ListItems.OfType<ResourcesGroupCollapseSpacerItem>());

        header.ToggleExpandedCommand.Execute(null);
        Assert.Equal(25, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());
    }

    [Fact]
    public async Task LargeGroupStagesNormalRowsBeforeAddingOffscreenChildren()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromResult(CreateLargeGroupedVersionsResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);
        await viewModel.RefreshAsync();
        viewModel.IsGroupExpansionStagingEnabled = true;

        var queuedChildren = 0;
        viewModel.GroupEntranceRequested += children => queuedChildren = children.Count;
        var header = viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().First();
        header.ToggleExpandedCommand.Execute(null);

        var firstRows = viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().ToList();
        Assert.Equal(20, firstRows.Count);
        Assert.Equal(20, queuedChildren);
        Assert.All(firstRows, row => Assert.True(row.IsGroupEntrancePending));

        await Task.Delay(700);
        var allRows = viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().ToList();
        Assert.Equal(25, allRows.Count);
        Assert.Same(firstRows[0], allRows[0]);

        Action? completeCollapse = null;
        viewModel.GroupCollapseRequested += (_, complete) => completeCollapse = complete;
        header.ToggleExpandedCommand.Execute(null);
        Assert.Equal(25, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());
        completeCollapse!();
        Assert.Empty(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.Equal(1040, Assert.Single(viewModel.ListItems.OfType<ResourcesGroupCollapseSpacerItem>()).Height);
    }

    [Fact]
    public async Task LargeGroupStagesOnlyEnoughRowsToMoveFollowingContentPastViewport()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromResult(CreateLargeGroupedVersionsResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);
        await viewModel.RefreshAsync();
        viewModel.IsGroupExpansionStagingEnabled = true;
        viewModel.GroupExpansionViewportHeight = 220;

        var header = viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>().First();
        header.ToggleExpandedCommand.Execute(null);

        Assert.Equal(7, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());
        await Task.Delay(600);
        Assert.Equal(25, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());

        Action? completeCollapse = null;
        viewModel.GroupCollapseRequested += (_, complete) => completeCollapse = complete;
        header.ToggleExpandedCommand.Execute(null);
        Assert.Equal(25, viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>().Count());
        completeCollapse!();
        Assert.Empty(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
        Assert.Equal(364, Assert.Single(viewModel.ListItems.OfType<ResourcesGroupCollapseSpacerItem>()).Height);
    }

    [Fact]
    public async Task InstanceFilteredSingleCompatibilityGroupStartsExpanded()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromResult(CreateGroupedVersionsResult()));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareProject(viewModel);
        viewModel.SelectedTarget = ResourcesModInstallTargetItemViewModel.FromInstance(new GameInstance
        {
            Id = "fabric-instance",
            MinecraftVersion = "1.21.3",
            Loader = LoaderKind.Fabric
        });

        await viewModel.RefreshAsync();

        var header = Assert.Single(viewModel.ListItems.OfType<ResourcesModVersionListHeaderItem>());
        Assert.True(header.IsExpanded);
        Assert.True(viewModel.CanAutoLoadMoreVersions);
        Assert.Equal("1.21.3-fabric", header.Title);
        Assert.Single(viewModel.ListItems.OfType<ResourcesModVersionItemViewModel>());
    }

    [Fact]
    public async Task FailedVersionListCanBeRefreshed()
    {
        var retryResult = new TaskCompletionSource<ResourceProjectVersionsResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SequencedResourceCatalogService(
            () => Task.FromException<ResourceProjectVersionsResult>(new HttpRequestException("offline")),
            () => retryResult.Task);
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanShowLoadErrorState);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));

        var refresh = viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CallCount);
        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.CanShowLoadErrorState);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));

        retryResult.SetResult(CreateVersionsResult());
        await refresh;

        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.CanShowLoadErrorState);
        Assert.Equal("sodium-1", Assert.Single(viewModel.SourceVersions).VersionId);
    }

    [Fact]
    public async Task FailedVersionRefreshRemainsRetryable()
    {
        var service = new SequencedResourceCatalogService(
            () => Task.FromException<ResourceProjectVersionsResult>(new HttpRequestException("offline")),
            () => Task.FromException<ResourceProjectVersionsResult>(new HttpRequestException("still offline")));
        using var page = new ResourcesPageViewModel(service).ModPage;
        var viewModel = page.Versions;
        PrepareLocalDownloadTarget(viewModel);

        await viewModel.RefreshAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, service.CallCount);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.CanShowLoadErrorState);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    private static void PrepareLocalDownloadTarget(ResourcesProjectVersionsViewModel viewModel)
    {
        PrepareProject(viewModel);
        viewModel.SelectedTarget = Assert.Single(viewModel.InstallTargets, target => target.IsLocalDownload);
    }

    private static void PrepareProject(ResourcesProjectVersionsViewModel viewModel)
    {
        viewModel.SetProject(new ResourcesModProjectItemViewModel(new ResourceProject
        {
            Kind = ResourceProjectKind.Mod,
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "sodium",
            Title = "Sodium"
        }));
    }

    private static ResourceProjectVersionsResult CreateGroupedVersionsResult()
    {
        return new ResourceProjectVersionsResult
        {
            Versions =
            [
                new ResourceProjectVersion
                {
                    Kind = ResourceProjectKind.Mod,
                    VersionId = "sodium-fabric",
                    Name = "Sodium Fabric",
                    VersionNumber = "1.0",
                    FileName = "sodium-fabric.jar",
                    GameVersions = ["1.21.3"],
                    Loaders = ["fabric"]
                },
                new ResourceProjectVersion
                {
                    Kind = ResourceProjectKind.Mod,
                    VersionId = "sodium-neoforge",
                    Name = "Sodium NeoForge",
                    VersionNumber = "1.0",
                    FileName = "sodium-neoforge.jar",
                    GameVersions = ["1.21.4"],
                    Loaders = ["neoforge"]
                }
            ]
        };
    }

    private static ResourceProjectVersionsResult CreateLargeGroupedVersionsResult()
    {
        var versions = new List<ResourceProjectVersion>();
        for (var index = 0; index < 25; index++)
        {
            versions.Add(new ResourceProjectVersion
            {
                Kind = ResourceProjectKind.Mod,
                VersionId = $"sodium-fabric-{index}",
                Name = $"Sodium Fabric {index}",
                VersionNumber = $"1.{index}",
                FileName = $"sodium-fabric-{index}.jar",
                GameVersions = ["1.21.3"],
                Loaders = ["fabric"]
            });
        }

        versions.Add(new ResourceProjectVersion
        {
            Kind = ResourceProjectKind.Mod,
            VersionId = "sodium-neoforge",
            Name = "Sodium NeoForge",
            VersionNumber = "1.0",
            FileName = "sodium-neoforge.jar",
            GameVersions = ["1.21.6"],
            Loaders = ["neoforge"]
        });

        return new ResourceProjectVersionsResult { Versions = versions };
    }

    private static ResourceProjectVersionsResult CreateVersionsResult()
    {
        return new ResourceProjectVersionsResult
        {
            Versions =
            [
                new ResourceProjectVersion
                {
                    Kind = ResourceProjectKind.Mod,
                    VersionId = "sodium-1",
                    Name = "Sodium 1.0",
                    VersionNumber = "1.0",
                    FileName = "sodium.jar"
                }
            ]
        };
    }

    private sealed class SequencedResourceCatalogService(
        params Func<Task<ResourceProjectVersionsResult>>[] responses) : IResourceCatalogService
    {
        private readonly Queue<Func<Task<ResourceProjectVersionsResult>>> responses = new(responses);

        public int CallCount { get; private set; }

        public Task<ResourceCatalogSearchResult> SearchModsAsync(
            ResourceCatalogSearchRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(
            ResourceProjectVersionsRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return responses.Dequeue()();
        }

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
