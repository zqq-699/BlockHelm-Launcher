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

using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Tests.Infrastructure.Mods;

public sealed class ModServiceTests : TestTempDirectory
{
    [Theory]
    [InlineData("sodium")]
    [InlineData("unlisted-mod-id")]
    public async Task GetModsAsyncWaitsForOnlineProjectLocalization(string modId)
    {
        var instanceDirectory = Directory.CreateDirectory(
            Path.Combine(TempRoot, "instances", "localized-name")).FullName;
        var modsDirectory = Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods")).FullName;
        var jarPath = Path.Combine(modsDirectory, "sodium.jar");
        using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                $$"""{"id":"{{modId}}","name":"Sodium","version":"1.0.0"}"""));
        }
        var service = CreateService();

        var mod = Assert.Single(await service.GetModsAsync(new GameInstance
        {
            Id = "localized-name",
            InstanceDirectory = instanceDirectory
        }));

        Assert.Equal(modId, mod.ModId);
        Assert.Equal("Sodium", mod.Name);
        Assert.Null(mod.LocalizedName);
    }

    [Fact]
    public async Task GetModsAsyncDoesNotIdentifyModByFileNameAlone()
    {
        var instanceDirectory = Directory.CreateDirectory(
            Path.Combine(TempRoot, "instances", "filename-only")).FullName;
        var modsDirectory = Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods")).FullName;
        using (ZipFile.Open(Path.Combine(modsDirectory, "sodium.jar"), ZipArchiveMode.Create))
        {
        }
        var service = CreateService();

        var mod = Assert.Single(await service.GetModsAsync(new GameInstance
        {
            Id = "filename-only",
            InstanceDirectory = instanceDirectory
        }));

        Assert.Equal("sodium", mod.Name);
        Assert.Null(mod.LocalizedName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetEnabledAsyncRejectsExistingTargetWithoutChangingEitherFile(bool enabled)
    {
        var modsDirectory = Directory.CreateDirectory(Path.Combine(TempRoot, "instances", "conflict", "mods")).FullName;
        var enabledPath = Path.Combine(modsDirectory, "example.jar");
        var disabledPath = Path.Combine(modsDirectory, "example.jar.disabled");
        await File.WriteAllTextAsync(enabledPath, "enabled-content");
        await File.WriteAllTextAsync(disabledPath, "disabled-content");
        var sourcePath = enabled ? disabledPath : enabledPath;
        var targetPath = enabled ? enabledPath : disabledPath;
        var mod = CreateLocalMod(sourcePath, isEnabled: !enabled);
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ModEnabledStateConflictException>(
            () => service.SetEnabledAsync(mod, enabled));

        Assert.Equal(targetPath, exception.TargetPath);
        Assert.Equal("enabled-content", await File.ReadAllTextAsync(enabledPath));
        Assert.Equal("disabled-content", await File.ReadAllTextAsync(disabledPath));
    }

    [Fact]
    public async Task SetEnabledAsyncRejectsExistingTargetDirectoryWithoutChangingSource()
    {
        var modsDirectory = Directory.CreateDirectory(Path.Combine(TempRoot, "instances", "directory-conflict", "mods")).FullName;
        var sourcePath = Path.Combine(modsDirectory, "example.jar.disabled");
        var targetPath = Path.Combine(modsDirectory, "example.jar");
        await File.WriteAllTextAsync(sourcePath, "source-content");
        Directory.CreateDirectory(targetPath);
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ModEnabledStateConflictException>(
            () => service.SetEnabledAsync(CreateLocalMod(sourcePath, isEnabled: false), enabled: true));

        Assert.Equal(targetPath, exception.TargetPath);
        Assert.Equal("source-content", await File.ReadAllTextAsync(sourcePath));
        Assert.True(Directory.Exists(targetPath));
    }

    [Fact]
    public async Task MoveFileWithoutOverwriteMapsTargetCreatedAfterPrecheckToConflict()
    {
        var modsDirectory = Directory.CreateDirectory(Path.Combine(TempRoot, "instances", "race-conflict", "mods")).FullName;
        var sourcePath = Path.Combine(modsDirectory, "example.jar.disabled");
        var targetPath = Path.Combine(modsDirectory, "example.jar");
        await File.WriteAllTextAsync(sourcePath, "source-content");
        await File.WriteAllTextAsync(targetPath, "target-content");

        var exception = Assert.Throws<ModEnabledStateConflictException>(
            () => ModService.MoveFileWithoutOverwrite(sourcePath, targetPath));

        Assert.Equal(targetPath, exception.TargetPath);
        Assert.Equal("source-content", await File.ReadAllTextAsync(sourcePath));
        Assert.Equal("target-content", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task SetEnabledAsyncMovesExistingSnapshotWithoutReplacingItem()
    {
        var modsDirectory = Directory.CreateDirectory(
            Path.Combine(TempRoot, "instances", "toggle-snapshot", "mods")).FullName;
        var enabledPath = Path.Combine(modsDirectory, "example.jar");
        await File.WriteAllTextAsync(enabledPath, "content");
        var instance = new GameInstance
        {
            InstanceDirectory = Directory.GetParent(modsDirectory)!.FullName
        };
        var service = CreateService();
        var loaded = Assert.Single(await service.GetModsAsync(instance));

        await service.SetEnabledAsync(loaded, enabled: false);
        var afterToggle = Assert.Single(await service.GetModsAsync(instance));

        Assert.Same(loaded, afterToggle);
        Assert.False(afterToggle.IsEnabled);
        Assert.Equal("example.jar.disabled", afterToggle.FileName);
        Assert.False(File.Exists(enabledPath));
        Assert.True(File.Exists(Path.Combine(modsDirectory, "example.jar.disabled")));
    }

    [Fact]
    public async Task SetEnabledAsyncRemainsConsistentAfterSnapshotEviction()
    {
        var instances = new List<GameInstance>();
        for (var index = 0; index < 5; index++)
        {
            var instanceDirectory = Directory.CreateDirectory(
                Path.Combine(TempRoot, "instances", "toggle-after-eviction", $"instance-{index}")).FullName;
            var modsDirectory = Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods")).FullName;
            var jarPath = Path.Combine(modsDirectory, $"mod-{index}.jar");
            using (ZipFile.Open(jarPath, ZipArchiveMode.Create))
            {
            }
            instances.Add(new GameInstance { Id = $"instance-{index}", InstanceDirectory = instanceDirectory });
        }

        var service = CreateService();
        var first = Assert.Single(await service.GetModsAsync(instances[0]));
        for (var index = 1; index < instances.Count; index++)
            Assert.Single(await service.GetModsAsync(instances[index]));

        await service.SetEnabledAsync(first, enabled: false);
        var reloaded = Assert.Single(await service.GetModsAsync(instances[0]));

        Assert.NotSame(first, reloaded);
        Assert.False(first.IsEnabled);
        Assert.False(reloaded.IsEnabled);
        Assert.Equal("mod-0.jar.disabled", reloaded.FileName);
        Assert.True(File.Exists(Path.Combine(instances[0].InstanceDirectory, "mods", "mod-0.jar.disabled")));
    }

    [Fact]
    public async Task ConcurrentInstanceLoadsRemainConsistentAcrossEviction()
    {
        var instances = new List<GameInstance>();
        for (var index = 0; index < 8; index++)
        {
            var instanceDirectory = Directory.CreateDirectory(
                Path.Combine(TempRoot, "instances", "concurrent-lru", $"instance-{index}")).FullName;
            var modsDirectory = Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods")).FullName;
            using (ZipFile.Open(Path.Combine(modsDirectory, $"mod-{index}.jar"), ZipArchiveMode.Create))
            {
            }
            instances.Add(new GameInstance { Id = $"instance-{index}", InstanceDirectory = instanceDirectory });
        }

        var service = CreateService();
        var results = await Task.WhenAll(instances.Select(instance => service.GetModsAsync(instance)));

        Assert.All(results, result => Assert.Single(result));
        for (var index = 0; index < instances.Count; index++)
        {
            var reloaded = Assert.Single(await service.GetModsAsync(instances[index]));
            Assert.Equal($"mod-{index}.jar", reloaded.FileName);
        }
    }

    [Fact]
    public async Task RemoteIconFileAliasIsStableAcrossEnabledStateRename()
    {
        var modsDirectory = Directory.CreateDirectory(
            Path.Combine(TempRoot, "instances", "icon-alias", "mods")).FullName;
        var enabledPath = Path.Combine(modsDirectory, "example.jar");
        var disabledPath = enabledPath + ".disabled";
        await File.WriteAllTextAsync(enabledPath, "content");
        var originalWriteTime = File.GetLastWriteTimeUtc(enabledPath);
        var enabledAlias = LocalModIconEnrichmentService.TryCreateFileAlias(enabledPath);

        File.Move(enabledPath, disabledPath);
        File.SetLastWriteTimeUtc(disabledPath, originalWriteTime);
        var disabledAlias = LocalModIconEnrichmentService.TryCreateFileAlias(disabledPath);

        Assert.NotNull(enabledAlias);
        Assert.Equal(enabledAlias, disabledAlias);
    }

    private ModService CreateService()
    {
        return new ModService(new LauncherPathProvider(TempRoot));
    }

    private static LocalMod CreateLocalMod(string fullPath, bool isEnabled) => new()
    {
        Name = "Example",
        FileName = Path.GetFileName(fullPath),
        FullPath = fullPath,
        IsEnabled = isEnabled
    };

    private static (string EntryName, byte[] Content) TextEntry(string entryName, string content)
    {
        return (entryName, Encoding.UTF8.GetBytes(content));
    }

}
