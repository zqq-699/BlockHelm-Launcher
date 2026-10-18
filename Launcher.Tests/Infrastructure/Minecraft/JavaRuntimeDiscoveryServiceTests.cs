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
    public async Task FirstSearchIsRecordedAndOnlyExplicitSearchRepeatsIt()
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
        JavaRuntimeDiscoveryService CreateService() => new(
            new LauncherPathProvider(launcherDirectory),
            null,
            _ =>
            {
                configuredSearchCount++;
                return [];
            },
            () => []);

        var firstService = CreateService();
        await firstService.DiscoverAsync(null);
        Assert.Equal(1, configuredSearchCount);

        var nextLaunchService = CreateService();
        await nextLaunchService.DiscoverAsync(null);
        Assert.Equal(1, configuredSearchCount);

        await nextLaunchService.SearchAllAsync(null);
        Assert.Equal(2, configuredSearchCount);

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
