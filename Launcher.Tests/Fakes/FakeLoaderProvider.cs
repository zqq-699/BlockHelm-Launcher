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

internal sealed class FakeLoaderProvider : ILoaderProvider
{
    private int installCallCount;

    public LoaderKind Kind { get; init; } = LoaderKind.Vanilla;
    public bool IsImplemented { get; init; } = true;
    public IReadOnlyList<LoaderVersionInfo> LoaderVersions { get; init; } = [new LoaderVersionInfo("fake")];
    public Exception? GetLoaderVersionsException { get; init; }
    public Task? WaitBeforeGetLoaderVersions { get; init; }
    public int GetLoaderVersionsCallCount { get; private set; }
    public string? LastMinecraftVersion { get; private set; }
    public string? LastGameDirectory { get; private set; }
    public string? LastIsolatedVersionName { get; private set; }
    public string? LastLoaderVersion { get; private set; }
    public DownloadSourcePreference LastDownloadSourcePreference { get; private set; } = LauncherDefaults.DefaultDownloadSourcePreference;
    public int LastDownloadSpeedLimitMbPerSecond { get; private set; }
    public TaskCompletionSource<bool> InstallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? WaitBeforeInstall { get; init; }
    public bool WriteJsonBeforeWaiting { get; init; }
    public string? PartialVersionName { get; init; }
    public int InstallCallCount => installCallCount;
    public bool SawLibrariesDirectoryDuringInstall { get; private set; }
    public bool SawAssetObjectsDirectoryDuringInstall { get; private set; }

    public Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0,
        bool interactive = false)
    {
        GetLoaderVersionsCallCount++;
        LastMinecraftVersion = minecraftVersion;
        LastDownloadSourcePreference = downloadSourcePreference;
        LastDownloadSpeedLimitMbPerSecond = downloadSpeedLimitMbPerSecond;
        if (GetLoaderVersionsException is not null)
            return Task.FromException<IReadOnlyList<LoaderVersionInfo>>(GetLoaderVersionsException);

        return GetLoaderVersionsAsyncCore(cancellationToken);
    }

    private async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsyncCore(CancellationToken cancellationToken)
    {
        if (WaitBeforeGetLoaderVersions is not null)
            await WaitBeforeGetLoaderVersions.WaitAsync(cancellationToken);

        return LoaderVersions;
    }

    public async Task<string> InstallAsync(
        string minecraftVersion,
        string gameDirectory,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        LastGameDirectory = gameDirectory;
        LastIsolatedVersionName = isolatedVersionName;
        LastMinecraftVersion = minecraftVersion;
        LastLoaderVersion = loaderVersion;
        LastDownloadSourcePreference = downloadSourcePreference;
        LastDownloadSpeedLimitMbPerSecond = downloadSpeedLimitMbPerSecond;
        SawLibrariesDirectoryDuringInstall = Directory.Exists(Path.Combine(gameDirectory, "libraries"));
        SawAssetObjectsDirectoryDuringInstall = Directory.Exists(Path.Combine(gameDirectory, "assets", "objects"));
        Interlocked.Increment(ref installCallCount);
        InstallStarted.TrySetResult(true);

        if (WriteJsonBeforeWaiting)
        {
            var partialVersionName = string.IsNullOrWhiteSpace(PartialVersionName)
                ? isolatedVersionName
                : PartialVersionName;
            var partialVersionDirectory = Path.Combine(gameDirectory, "versions", partialVersionName);
            Directory.CreateDirectory(partialVersionDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(partialVersionDirectory, $"{partialVersionName}.json"),
                $$"""
                {
                  "id": "{{partialVersionName}}",
                  "jar": "{{partialVersionName}}"
                }
                """,
                cancellationToken);
        }

        if (WaitBeforeInstall is not null)
            await WaitBeforeInstall.WaitAsync(cancellationToken);

        var versionDirectory = Path.Combine(gameDirectory, "versions", isolatedVersionName);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, $"{isolatedVersionName}.json"),
            $$"""
            {
              "id": "{{isolatedVersionName}}",
              "jar": "{{isolatedVersionName}}"
            }
            """,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, $"{isolatedVersionName}.jar"),
            "fake jar",
            cancellationToken);
        return isolatedVersionName;
    }
}

