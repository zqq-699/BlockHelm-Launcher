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
using Launcher.App.ViewModels.GameSettings;
using Launcher.Domain.Models;
using Launcher.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class InstanceSettingsPersistenceCoordinatorTests
{
    [Fact]
    public async Task ChangedSnapshotReportsInterruptedEditsAndRejectsStaleEditorRequests()
    {
        var instance = CreateInstance("stored");
        var service = new FakeGameInstanceService();
        var status = new RecordingStatusService();
        using var coordinator = CreateCoordinator(service, status);
        coordinator.SetInstance(instance);
        coordinator.Schedule("description", instance, target => ApplyDescription(target, "draft"),
            () => { }, TimeSpan.FromMinutes(1));

        coordinator.SetInstance(new GameInstance { InstanceDirectory = instance.InstanceDirectory, Description = "external" });

        Assert.True(status.Message.Task.IsCompletedSuccessfully);
        Assert.Equal(Strings.Status_InstanceSettingsSaveFailed, await status.Message.Task);
        Assert.False(coordinator.HasPendingSave("description"));
        coordinator.Schedule("description", instance, target => ApplyDescription(target, "stale"), () => { });
        Assert.Equal(0, service.SaveCallCount);
        Assert.Equal("stored", instance.Description);
    }

    [Fact]
    public async Task QueuedSaveNotificationDoesNotRebindAnObsoleteSnapshotAfterRefresh()
    {
        var instance = CreateInstance("old");
        var dispatcher = new QueuedUiDispatcher();
        using var coordinator = new InstanceSettingsPersistenceCoordinator(new FakeGameInstanceService(),
            new RecordingStatusService(), dispatcher, NullLogger.Instance);
        var notified = false;
        coordinator.InstanceSaved += _ => notified = true;
        coordinator.SetInstance(instance);
        coordinator.Schedule("description", instance, target => ApplyDescription(target, "saved"), () => { });
        var notification = await dispatcher.Posted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.SetInstance(new GameInstance { InstanceDirectory = instance.InstanceDirectory, Description = "external" });
        notification();
        Assert.False(notified);
    }

    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        public TaskCompletionSource<Action> Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasAccess => true;
        public void Post(Action action) => Posted.TrySetResult(action);
        public void PostAfterTransition(Action action) => Post(action);
        public Task PostAfterTransitionAsync(Action action) { Post(action); return Task.CompletedTask; }
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Func<Task> action) => action();
    }

    [Fact]
    public async Task RapidChangesInSameAreaPersistOnlyLatestMutation()
    {
        var instance = CreateInstance("first");
        var instanceService = new FakeGameInstanceService();
        using var coordinator = CreateCoordinator(instanceService, new RecordingStatusService());
        coordinator.SetInstance(instance);

        coordinator.Schedule(
            "description",
            instance,
            target => ApplyDescription(target, "older"),
            () => { },
            TimeSpan.FromMilliseconds(80));
        coordinator.Schedule(
            "description",
            instance,
            target => ApplyDescription(target, "latest"),
            () => { },
            TimeSpan.FromMilliseconds(80));

        await instanceService.SaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, instanceService.SaveCallCount);
        Assert.Equal("latest", instance.Description);
    }

    [Fact]
    public async Task SaveNotificationForSameInstanceDoesNotCancelNewerPendingMutation()
    {
        var instance = CreateInstance("original");
        var saveGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var instanceService = new FakeGameInstanceService
        {
            WaitBeforeSave = saveGate.Task
        };
        using var coordinator = CreateCoordinator(instanceService, new RecordingStatusService());
        var twoSavesCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedCount = 0;
        coordinator.InstanceSaved += savedInstance =>
        {
            coordinator.SetInstance(savedInstance);
            if (Interlocked.Increment(ref savedCount) == 2)
                twoSavesCompleted.TrySetResult(true);
        };
        coordinator.SetInstance(instance);
        coordinator.Schedule(
            "first-area",
            instance,
            target => ApplyDescription(target, "first"),
            () => { });

        await instanceService.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.Schedule(
            "second-area",
            instance,
            target => ApplyDescription(target, "latest"),
            () => { },
            TimeSpan.FromMilliseconds(50));
        saveGate.TrySetResult(true);

        await twoSavesCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, instanceService.SaveCallCount);
        Assert.Equal("latest", instance.Description);
    }

    private static InstanceSettingsPersistenceCoordinator CreateCoordinator(
        FakeGameInstanceService instanceService,
        RecordingStatusService statusService)
    {
        return new InstanceSettingsPersistenceCoordinator(
            instanceService,
            statusService,
            ImmediateUiDispatcher.Instance,
            NullLogger.Instance);
    }

    private static GameInstance CreateInstance(string description)
    {
        return new GameInstance
        {

            Description = description,
            InstanceDirectory = Path.Combine(Path.GetTempPath(), "launcher-tests", Guid.NewGuid().ToString("N"))
        };
    }

    private static Action? ApplyDescription(GameInstance instance, string description)
    {
        var original = instance.Description;
        if (string.Equals(original, description, StringComparison.Ordinal))
            return null;
        instance.Description = description;
        return () => instance.Description = original;
    }

    private sealed class RecordingStatusService : IStatusService
    {
        public TaskCompletionSource<string> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string>? MessageReported;

        public void Report(string message)
        {
            Message.TrySetResult(message);
            MessageReported?.Invoke(message);
        }
    }
}
