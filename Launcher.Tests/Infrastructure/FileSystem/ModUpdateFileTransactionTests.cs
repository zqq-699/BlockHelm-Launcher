/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class ModUpdateFileTransactionTests
{
    [Fact]
    public async Task VersionedUpdatesAccumulateDistinctOldFiles()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var firstPath = Path.Combine(mods, "mod-1.0.0.jar");
        await File.WriteAllTextAsync(firstPath, "one");
        var installer = new WritingInstallationService(mods, "two");
        var transaction = CreateTransaction(installer);
        var firstState = await transaction.CaptureAsync(new LocalMod { FullPath = firstPath, IsEnabled = true });

        var firstResult = await transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(firstState, "mod-2.0.0.jar"));

        Assert.Equal("one", await File.ReadAllTextAsync(firstPath + ".old"));
        Assert.Equal("two", await File.ReadAllTextAsync(firstResult.InstalledPath));

        installer.Content = "three";
        var secondState = await transaction.CaptureAsync(new LocalMod { FullPath = firstResult.InstalledPath, IsEnabled = true });
        var secondResult = await transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(secondState, "mod-3.0.0.jar"));

        Assert.Equal("one", await File.ReadAllTextAsync(firstPath + ".old"));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(mods, "mod-2.0.0.jar.old")));
        Assert.Equal("three", await File.ReadAllTextAsync(secondResult.InstalledPath));
    }

    [Fact]
    public async Task SameFileNameOverwritesPreviousOldBackup()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var source = Path.Combine(mods, "mod.jar");
        await File.WriteAllTextAsync(source, "current");
        await File.WriteAllTextAsync(source + ".old", "previous-old");
        var transaction = CreateTransaction(new WritingInstallationService(mods, "updated"));
        var state = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = true });

        var result = await transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(state, "mod.jar"));

        Assert.Equal("updated", await File.ReadAllTextAsync(result.InstalledPath));
        Assert.Equal("current", await File.ReadAllTextAsync(result.BackupPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(mods), path => path.EndsWith(".previous", StringComparison.Ordinal));
        var updatedState = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = true });
        Assert.NotEqual(state.Sha1, updatedState.Sha1);
    }

    [Fact]
    public async Task DisabledModRemainsDisabledAndBackupDropsDisabledSuffix()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var source = Path.Combine(mods, "mod-1.jar.disabled");
        await File.WriteAllTextAsync(source, "old");
        var transaction = CreateTransaction(new WritingInstallationService(mods, "new"));
        var state = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = false });

        var result = await transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(state, "mod-2.jar"));

        Assert.EndsWith("mod-2.jar.disabled", result.InstalledPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("mod-1.jar.old", result.BackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.InstalledPath));
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public async Task ExistingDifferentTargetStopsBeforeDownload()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var source = Path.Combine(mods, "mod-1.jar");
        var target = Path.Combine(mods, "mod-2.jar");
        await File.WriteAllTextAsync(source, "old");
        await File.WriteAllTextAsync(target, "unrelated");
        var installer = new WritingInstallationService(mods, "new");
        var transaction = CreateTransaction(installer);
        var state = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = true });

        var exception = await Assert.ThrowsAsync<ModUpdateConflictException>(() => transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(state, "mod-2.jar")));

        Assert.Equal(ModUpdateConflictReason.TargetExists, exception.Reason);
        Assert.Equal(0, installer.ExecuteCount);
        Assert.Equal("old", await File.ReadAllTextAsync(source));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task SourceContentChangeWithSameLengthAndTimestampIsDetected()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var source = Path.Combine(mods, "mod.jar");
        await File.WriteAllTextAsync(source, "one");
        var originalTimestamp = File.GetLastWriteTimeUtc(source);
        var installer = new WritingInstallationService(mods, "new");
        var transaction = CreateTransaction(installer);
        var state = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = true });
        await File.WriteAllTextAsync(source, "two");
        File.SetLastWriteTimeUtc(source, originalTimestamp);

        var exception = await Assert.ThrowsAsync<ModUpdateConflictException>(() => transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(state, "mod.jar")));

        Assert.Equal(ModUpdateConflictReason.SourceChanged, exception.Reason);
        Assert.Equal(0, installer.ExecuteCount);
        Assert.Equal("two", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task FailedDownloadLeavesCurrentAndOldFilesUntouched()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var source = Path.Combine(mods, "mod.jar");
        await File.WriteAllTextAsync(source, "current");
        await File.WriteAllTextAsync(source + ".old", "previous-old");
        var transaction = CreateTransaction(new WritingInstallationService(mods, "new") { Fail = true });
        var state = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = true });

        await Assert.ThrowsAsync<IOException>(() => transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(state, "mod.jar")));

        Assert.Equal("current", await File.ReadAllTextAsync(source));
        Assert.Equal("previous-old", await File.ReadAllTextAsync(source + ".old"));
    }

    [Fact]
    public async Task CommitFailureRestoresPreviousOldBackup()
    {
        using var directory = new TemporaryDirectory();
        var mods = Directory.CreateDirectory(Path.Combine(directory.Path, "mods")).FullName;
        var source = Path.Combine(mods, "mod.jar");
        await File.WriteAllTextAsync(source, "current");
        await File.WriteAllTextAsync(source + ".old", "previous-old");
        var transaction = CreateTransaction(new WritingInstallationService(mods, "updated"));
        var state = await transaction.CaptureAsync(new LocalMod { FullPath = source, IsEnabled = true });
        await using var lockStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAnyAsync<IOException>(() => transaction.ApplyAsync(
            CreateInstance(directory.Path),
            CreateCandidate(state, "mod.jar")));

        Assert.Equal("current", await ReadLockedFileAsync(lockStream));
        Assert.Equal("previous-old", await File.ReadAllTextAsync(source + ".old"));
    }

    private static ModUpdateFileTransaction CreateTransaction(IResourceProjectInstallationService installer) =>
        new(new LocalFileFingerprintService(), installer);

    private static async Task<string> ReadLockedFileAsync(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static GameInstance CreateInstance(string root) => new()
    {
        Id = "instance",
        InstanceDirectory = root,
        MinecraftVersion = "1.20.1",
        Loader = LoaderKind.Fabric
    };

    private static ModUpdateCandidate CreateCandidate(ModUpdateLocalFileState state, string fileName) => new(
        state,
        ResourceProjectSource.Modrinth,
        "project",
        "current",
        "1.0.0",
        DateTimeOffset.UtcNow.AddDays(-1),
        new ResourceProjectVersion
        {
            Kind = ResourceProjectKind.Mod,
            VersionId = Guid.NewGuid().ToString("N"),
            VersionNumber = "2.0.0",
            VersionType = "release",
            FileName = fileName,
            PublishedAt = DateTimeOffset.UtcNow
        });

    private sealed class WritingInstallationService(string modsDirectory, string content)
        : IResourceProjectInstallationService
    {
        public string Content { get; set; } = content;
        public bool Fail { get; init; }
        public int ExecuteCount { get; private set; }

        public Task<string> EnsureInstanceContentDirectoryAsync(
            ResourceProjectKind kind,
            GameInstance instance,
            CancellationToken cancellationToken = default) => Task.FromResult(modsDirectory);

        public Task<ResourceProjectInstallationPreparationResult> PrepareAsync(
            ResourceProjectInstallationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceProjectInstallationPreparationResult(
                false,
                request.DestinationPath,
                new ResourceProjectDestinationState(false, 0, 0, string.Empty)));

        public async Task<ResourceProjectInstallationResult> ExecuteAsync(
            ResourceProjectInstallationRequest request,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            if (Fail)
                throw new IOException("Simulated download failure.");
            await File.WriteAllTextAsync(request.DestinationPath!, Content, cancellationToken);
            return new ResourceProjectInstallationResult(request.DestinationPath);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BlockHelm.ModUpdate.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
