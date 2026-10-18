/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.Json;
using Launcher.Infrastructure;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class JavaRuntimeDiscoveryServiceTests : TestTempDirectory
{
    [Fact]
    public void LocalDriveSearchFindsJavaUnderArbitraryDirectoriesOnEachRoot()
    {
        var firstDrive = Path.Combine(TempRoot, "first");
        var secondDrive = Path.Combine(TempRoot, "second");
        var firstJava = Path.Combine(firstDrive, "Tools", "JDK 8", "bin", "java.exe");
        var secondJava = Path.Combine(secondDrive, "Games", "Runtime", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(firstJava)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondJava)!);
        File.WriteAllText(firstJava, string.Empty);
        File.WriteAllText(secondJava, string.Empty);
        Directory.CreateDirectory(Path.Combine(firstDrive, "Unrelated", "nested"));

        var candidates = JavaRuntimeDiscoveryService.CollectLocalDriveCandidates([firstDrive, secondDrive]);

        Assert.Equal(
            new[] { firstJava, secondJava },
            candidates.Select(candidate => candidate.ExecutablePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DefaultDiscoveryRefreshesConfiguredAndRecordedCandidatesWithoutScanningLocalDrives()
    {
        var launcherDirectory = Path.Combine(TempRoot, "launcher-default");
        var configuredJava = Path.Combine(TempRoot, "configured", "bin", "java.exe");
        var recordedJava = Path.Combine(TempRoot, "recorded", "bin", "java.exe");
        var manualJava = Path.Combine(TempRoot, "manual-default", "bin", "java.exe");
        foreach (var executablePath in new[] { configuredJava, recordedJava, manualJava })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            File.WriteAllText(executablePath, string.Empty);
        }

        var catalogPath = Path.Combine(launcherDirectory, "BHL", "java-runtimes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            ExecutablePaths = new[] { manualJava },
            DiscoveredExecutablePaths = new[] { recordedJava },
            FullScanCompleted = true
        }));

        var configuredSearchCount = 0;
        var localDriveSearchCount = 0;
        var service = new JavaRuntimeDiscoveryService(
            new LauncherPathProvider(launcherDirectory),
            null,
            _ =>
            {
                configuredSearchCount++;
                return
                [
                    new JavaRuntimeCandidate(configuredJava, "JAVA_HOME", configuredJava)
                ];
            },
            () =>
            {
                localDriveSearchCount++;
                return [];
            });

        var firstCandidates = await service.CollectDefaultCandidatesAsync(null, CancellationToken.None);
        var secondCandidates = await service.CollectDefaultCandidatesAsync(null, CancellationToken.None);

        Assert.Equal(2, configuredSearchCount);
        Assert.Equal(0, localDriveSearchCount);
        Assert.Equal(
            new[] { configuredJava, manualJava, recordedJava }.Order(StringComparer.OrdinalIgnoreCase),
            firstCandidates.Select(candidate => candidate.ExecutablePath)
                .Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            firstCandidates.Select(candidate => candidate.ExecutablePath)
                .Order(StringComparer.OrdinalIgnoreCase),
            secondCandidates.Select(candidate => candidate.ExecutablePath)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnlyExplicitSearchScansLocalDrivesAndRecordsCompletion()
    {
        var launcherDirectory = Path.Combine(TempRoot, "launcher");
        var manualJava = Path.Combine(TempRoot, "manual", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(manualJava)!);
        File.WriteAllText(manualJava, string.Empty);
        var catalogPath = Path.Combine(launcherDirectory, "BHL", "java-runtimes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            ExecutablePaths = new[] { manualJava }
        }));

        var configuredSearchCount = 0;
        var localDriveSearchCount = 0;
        JavaRuntimeDiscoveryService CreateService() => new(
            new LauncherPathProvider(launcherDirectory),
            null,
            _ =>
            {
                configuredSearchCount++;
                return [];
            },
            () =>
            {
                localDriveSearchCount++;
                return [];
            });

        var firstService = CreateService();
        await firstService.DiscoverAsync(null);
        Assert.Equal(1, configuredSearchCount);
        Assert.Equal(0, localDriveSearchCount);

        var nextLaunchService = CreateService();
        await nextLaunchService.DiscoverAsync(null);
        Assert.Equal(2, configuredSearchCount);
        Assert.Equal(0, localDriveSearchCount);

        await nextLaunchService.SearchAllAsync(null);
        Assert.Equal(3, configuredSearchCount);
        Assert.Equal(1, localDriveSearchCount);

        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        Assert.True(catalog.RootElement.GetProperty("FullScanCompleted").GetBoolean());
        Assert.Contains(manualJava, catalog.RootElement.GetProperty("ExecutablePaths")
            .EnumerateArray().Select(element => element.GetString()));
    }

    [Fact]
    public async Task RecordingAutomaticDiscoveryPreservesManualImports()
    {
        var launcherDirectory = Path.Combine(TempRoot, "launcher");
        var catalogPath = Path.Combine(launcherDirectory, "BHL", "java-runtimes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            ExecutablePaths = new[] { @"C:\Manual\bin\java.exe" }
        }));
        var service = new JavaRuntimeDiscoveryService(new LauncherPathProvider(launcherDirectory));

        await service.RememberDiscoveredExecutablesAsync(
            [@"D:\JDK\bin\java.exe", @"E:\Tools\Java\bin\java.exe"], CancellationToken.None);

        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        Assert.True(catalog.RootElement.GetProperty("FullScanCompleted").GetBoolean());
        Assert.Equal(2, catalog.RootElement.GetProperty("DiscoveredExecutablePaths").GetArrayLength());
        Assert.Equal(@"C:\Manual\bin\java.exe",
            catalog.RootElement.GetProperty("ExecutablePaths")[0].GetString());
    }
}
