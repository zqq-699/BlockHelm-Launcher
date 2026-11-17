/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Services;
using Launcher.App.ViewModels.Home;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Home;

public sealed class HomeLaunchGameListViewModelTests
{
    [Fact]
    public void SameCatalogRevisionIsCompleteNoOp()
    {
        var viewModel = CreateViewModel();
        var instance = CreateInstance("instance-a", "Instance A");
        Assert.True(viewModel.ApplyInstanceCatalog([instance], 1));
        var item = Assert.Single(viewModel.LaunchInstances);
        var collectionChanges = 0;
        var itemNotifications = 0;
        viewModel.LaunchInstances.CollectionChanged += (_, _) => collectionChanges++;
        item.PropertyChanged += (_, _) => itemNotifications++;

        var changed = viewModel.ApplyInstanceCatalog(
            [CreateInstance("instance-a", "Changed but stale")],
            1);

        Assert.False(changed);
        Assert.Same(item, Assert.Single(viewModel.LaunchInstances));
        Assert.Equal("Instance A", item.Name);
        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, itemNotifications);
    }

    [Fact]
    public void NewCatalogRevisionReusesItemsAndNotifiesOnlyChangedItem()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyInstanceCatalog(
            [
                CreateInstance("instance-a", "Before"),
                CreateInstance("instance-b", "Unchanged")
            ],
            1);
        var first = viewModel.LaunchInstances[0];
        var second = viewModel.LaunchInstances[1];
        var firstNotifications = new List<string?>();
        var secondNotifications = new List<string?>();
        first.PropertyChanged += (_, args) => firstNotifications.Add(args.PropertyName);
        second.PropertyChanged += (_, args) => secondNotifications.Add(args.PropertyName);

        var changed = viewModel.ApplyInstanceCatalog(
            [
                CreateInstance("instance-a", "After"),
                CreateInstance("instance-b", "Unchanged")
            ],
            2);

        Assert.True(changed);
        Assert.Same(first, viewModel.LaunchInstances[0]);
        Assert.Same(second, viewModel.LaunchInstances[1]);
        Assert.Equal("After", first.Name);
        Assert.Contains(nameof(HomeLaunchInstanceItem.Name), firstNotifications);
        Assert.Empty(secondNotifications);
    }

    [Fact]
    public void CatalogUsesVersionTypeFromLocalInstanceMetadata()
    {
        var viewModel = CreateViewModel();
        var instance = CreateInstance("snapshot-instance", "Snapshot");
        instance.VersionType = "snapshot";

        viewModel.ApplyInstanceCatalog([instance], 1);

        Assert.Equal("snapshot", Assert.Single(viewModel.LaunchInstances).VersionType);
    }

    private static HomeLaunchGameListViewModel CreateViewModel()
    {
        return new HomeLaunchGameListViewModel(
            new StubStatusService(),
            _ => Task.FromResult(true));
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

    private sealed class StubStatusService : IStatusService
    {
        public event Action<string>? MessageReported;

        public void Report(string message) => MessageReported?.Invoke(message);
    }
}
