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

using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.Fakes;

internal sealed class FakeGameInstanceService : IGameInstanceService
{
    private readonly object syncRoot = new();
    public List<GameInstance> CreatedInstances { get; } = [];
    public Exception? CreateException { get; init; }
    public Exception? RenameException { get; init; }
    public Exception? SaveException { get; set; }
    public bool ReturnNewInstanceOnRename { get; init; }
    public Action? RenameCallback { get; set; }
    public Action? DeleteCallback { get; set; }
    public Func<string, CancellationToken, Task<bool>>? DeleteHandler { get; set; }
    public TaskCompletionSource<bool> CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? WaitBeforeCreate { get; init; }
    public TaskCompletionSource<bool> GetInstancesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? WaitBeforeGetInstances { get; set; }
    public Func<int, CancellationToken, Task<IReadOnlyList<GameInstance>>>? GetInstancesHandler { get; set; }
    public Task? WaitBeforeSave { get; set; }
    public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> SaveCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? LastMinecraftVersion { get; private set; }
    public LoaderKind LastLoader { get; private set; }
    public string? LastLoaderVersion { get; private set; }
    public string? LastName { get; private set; }
    public DownloadSourcePreference LastDownloadSourcePreference { get; private set; } = LauncherDefaults.DefaultDownloadSourcePreference;
    public int LastDownloadSpeedLimitMbPerSecond { get; private set; }
    public bool LastInstallFabricApi { get; private set; } = true;
    public string? LastFabricApiVersionId { get; private set; }
    public string? LastQuiltStandardLibraryVersionId { get; private set; }
    public string? LastDefaultInstanceDirectory { get; private set; }
    public string? LastDeletedInstanceId { get; private set; }
    public string? LastRenamedInstanceId { get; private set; }
    public string? LastRenamedName { get; private set; }
    public string? LastRenamedIconSource { get; private set; }
    public GameInstance? LastSavedInstance { get; private set; }
    public int CreateCallCount { get; private set; }
    public int GetInstancesCallCount { get; private set; }
    public int GetStoredInstancesCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public int SaveCallCount { get; private set; }
    public LauncherProgress? InitialProgress { get; init; }

    public Task<IReadOnlyList<GameInstance>> GetStoredInstancesAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            GetStoredInstancesCallCount++;
            return Task.FromResult<IReadOnlyList<GameInstance>>(CreatedInstances.ToList());
        }
    }

    public async Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
    {
        int call;
        lock (syncRoot)
        {
            GetInstancesCallCount++;
            call = GetInstancesCallCount;
        }

        GetInstancesStarted.TrySetResult(true);
        if (GetInstancesHandler is not null)
            return await GetInstancesHandler(call, cancellationToken);

        if (WaitBeforeGetInstances is not null)
            await WaitBeforeGetInstances.WaitAsync(cancellationToken);

        lock (syncRoot)
        {
            return CreatedInstances.ToList();
        }
    }

    public Task<GameInstance?> GetDefaultInstanceAsync(CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            return Task.FromResult<GameInstance?>(CreatedInstances.FirstOrDefault());
        }
    }

    public async Task<GameInstance> CreateInstanceAsync(
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
        string? quiltStandardLibraryVersionId = null)
    {
        LastMinecraftVersion = minecraftVersion;
        LastLoader = loader;
        LastLoaderVersion = loaderVersion;
        LastName = name;
        LastDownloadSourcePreference = downloadSourcePreference;
        LastDownloadSpeedLimitMbPerSecond = downloadSpeedLimitMbPerSecond;
        LastInstallFabricApi = installFabricApi;
        LastFabricApiVersionId = fabricApiVersionId;
        LastQuiltStandardLibraryVersionId = quiltStandardLibraryVersionId;
        progress?.Report(InitialProgress ?? new LauncherProgress(InstallProgressStages.Preparing, string.Empty, 25));
        CreateStarted.TrySetResult(true);
        lock (syncRoot)
        {
            CreateCallCount++;
        }

        if (WaitBeforeCreate is not null)
            await WaitBeforeCreate.WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (CreateException is not null)
            throw CreateException;

        var instance = new GameInstance
        {
            Name = string.IsNullOrWhiteSpace(name) ? minecraftVersion : name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            VersionName = minecraftVersion,
            InstanceDirectory = Path.Combine(Path.GetTempPath(), "launcher-tests", Guid.NewGuid().ToString("N"))
        };
        lock (syncRoot)
        {
            CreatedInstances.Add(instance);
        }
        return instance;
    }

    public async Task SaveInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        SaveStarted.TrySetResult(true);
        if (WaitBeforeSave is not null)
            await WaitBeforeSave.WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (SaveException is not null)
            throw SaveException;

        lock (syncRoot)
        {
            SaveCallCount++;
            LastSavedInstance = instance;

            var index = CreatedInstances.FindIndex(existing => existing.InstanceDirectory == instance.InstanceDirectory);
            if (index >= 0)
                CreatedInstances[index] = instance;
            else
                CreatedInstances.Add(instance);
        }
        SaveCompleted.TrySetResult(true);
    }

    public Task<GameInstance> RenameInstanceAsync(
        string instanceId,
        string? newName,
        string? newIconSource,
        CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            LastRenamedInstanceId = instanceId;
            LastRenamedName = newName;
            LastRenamedIconSource = newIconSource;
            RenameCallback?.Invoke();

            if (RenameException is not null)
                throw RenameException;

            var index = CreatedInstances.FindIndex(existing => existing.InstanceDirectory == instanceId);
            var instance = CreatedInstances[index];
            var updated = ReturnNewInstanceOnRename ? CloneGameInstance(instance) : instance;
            updated.Name = string.IsNullOrWhiteSpace(newName) ? updated.Name : newName.Trim();
            updated.VersionName = string.IsNullOrWhiteSpace(newName) ? updated.VersionName : newName.Trim();
            updated.IconSource = string.IsNullOrWhiteSpace(newIconSource) ? null : newIconSource.Trim();
            updated.InstanceDirectory = Path.Combine(Path.GetDirectoryName(updated.InstanceDirectory) ?? Path.GetTempPath(), updated.VersionName);
            CreatedInstances[index] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<bool> SetDefaultInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            LastDefaultInstanceDirectory = instanceId;
            return Task.FromResult(CreatedInstances.Any(instance => instance.InstanceDirectory == instanceId));
        }
    }

    public async Task<bool> DeleteInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        Func<string, CancellationToken, Task<bool>>? handler;
        lock (syncRoot)
        {
            DeleteCallback?.Invoke();
            LastDeletedInstanceId = instanceId;
            DeleteCallCount++;
            handler = DeleteHandler;
            if (handler is null)
                return CreatedInstances.RemoveAll(instance => instance.InstanceDirectory == instanceId) > 0;
        }

        var deleted = await handler(instanceId, cancellationToken);
        if (!deleted)
            return false;

        lock (syncRoot)
            return CreatedInstances.RemoveAll(instance => instance.InstanceDirectory == instanceId) > 0;
    }

    private static GameInstance CloneGameInstance(GameInstance instance)
    {
        return new GameInstance
        {
            Name = instance.Name,
            MinecraftVersion = instance.MinecraftVersion,
            Loader = instance.Loader,
            LoaderVersion = instance.LoaderVersion,
            VersionName = instance.VersionName,
            VersionType = instance.VersionType,
            Description = instance.Description,
            IconSource = instance.IconSource,
            InstanceDirectory = instance.InstanceDirectory,
            BackupDirectory = instance.BackupDirectory,
            MemorySettingsMode = instance.MemorySettingsMode,
            MemoryMb = instance.MemoryMb,
            WindowWidth = instance.WindowWidth,
            WindowHeight = instance.WindowHeight,
            PreLaunchCommand = instance.PreLaunchCommand,
            WaitForPreLaunchCommand = instance.WaitForPreLaunchCommand,
            PostExitCommand = instance.PostExitCommand,
            JvmArguments = instance.JvmArguments,
            GameArguments = instance.GameArguments,
            LaunchSettingsMode = instance.LaunchSettingsMode,
            JavaSettingsMode = instance.JavaSettingsMode,
            JavaSelectionMode = instance.JavaSelectionMode,
            SelectedJavaExecutablePath = instance.SelectedJavaExecutablePath,
            CheckFilesBeforeLaunch = instance.CheckFilesBeforeLaunch,
            AutoRepairMissingFiles = instance.AutoRepairMissingFiles,
            MinimizeLauncherAfterLaunch = instance.MinimizeLauncherAfterLaunch,
            LaunchFullScreen = instance.LaunchFullScreen,
            AutoJoinServerAddress = instance.AutoJoinServerAddress,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = instance.UpdatedAt
        };
    }
}
