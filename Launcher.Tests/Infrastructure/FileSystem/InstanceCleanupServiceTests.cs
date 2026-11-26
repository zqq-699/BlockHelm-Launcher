/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Persistence;
using Launcher.Infrastructure.Minecraft;
using Launcher.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class InstanceCleanupServiceTests : TestTempDirectory
{
    [Fact]
    public async Task CleansOnlySelectedPreviewedFilesAndProtectsUserData()
    {
        var instance = CreateInstance();
        foreach (var path in new[] { "logs/latest.log", "logs/archive/a.log.gz", "crash-reports/crash-2026-11-26_10.30.00-client.txt", "hs_err_pid1.log", "BHL/logs/launch-output-00000000000000000000000000000001.log", "BHL/logs/launch-diagnostics-20261126-103000-123-00000000000000000000000000000001.log", "BHL/logs/launch-output-personal.log", "BHL/logs/launch-diagnostics-notes.log", "BHL/logs/other.log", "saves/world/level.dat", "WailaErrorOutput.txt", "personal.log", "hs_err_pidnotes.log", "logs/photos.png", "logs/backup.zip", "crash-reports/notes.txt", "crash-reports/crash-notes.txt", "crash-reports/world.zip" })
            Write(instance, path, "original");
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        Assert.Equal(6, preview.Entries.Count);
        Write(instance, "logs/latest.log", "changed after preview");
        Write(instance, "logs/new.log", "new after preview");
        var result = await service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet());
        Assert.Equal(5, result.Deleted);
        Assert.Equal(1, result.Skipped);
        foreach (var path in new[] { "logs/latest.log", "logs/new.log", "BHL/logs/other.log", "BHL/logs/launch-output-personal.log", "BHL/logs/launch-diagnostics-notes.log", "saves/world/level.dat", "WailaErrorOutput.txt", "personal.log", "hs_err_pidnotes.log", "logs/photos.png", "logs/backup.zip", "crash-reports/notes.txt", "crash-reports/crash-notes.txt", "crash-reports/world.zip" })
            Assert.True(File.Exists(Path.Combine(instance.InstanceDirectory, path)), path);
    }

    [Fact]
    public async Task ReplacedInstanceIsNotCleaned()
    {
        var instance = CreateInstance();
        Write(instance, "logs/latest.log", "original");
        InstanceDirectoryOwnership.Observe(instance);
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        Directory.Move(instance.InstanceDirectory, instance.InstanceDirectory + "-old");
        Write(instance, "logs/latest.log", "replacement");
        await Assert.ThrowsAnyAsync<Exception>(() => service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet()));
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(instance.InstanceDirectory, "logs/latest.log")));
    }

    [Fact]
    public async Task ScanAndCleanAreBlockedByActiveLaunch()
    {
        var instance = CreateInstance();
        Write(instance, "logs/latest.log", "original");
        var activity = new InstanceActivityCoordinator();
        var service = CreateService(activity);
        var preview = await service.ScanAsync(instance);
        using (activity.EnterLaunch(instance.InstanceDirectory))
        {
            await Assert.ThrowsAsync<InstanceCleanupBusyException>(() => service.ScanAsync(instance));
            await Assert.ThrowsAsync<InstanceCleanupBusyException>(() => service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet()));
        }
        using (activity.EnterCleanup(instance.InstanceDirectory))
            Assert.Throws<InvalidOperationException>(() => activity.EnterLaunch(instance.InstanceDirectory));
    }

    [Fact]
    public async Task LaunchCoordinatorKeepsInstanceBusyUntilSessionExits()
    {
        var instance = CreateInstance();
        var activity = new InstanceActivityCoordinator();
        var exit = new TaskCompletionSource<LaunchExitResult>();
        var launch = new CoordinatedLaunchService(new FakeLaunchService(exit.Task), activity);
        await launch.LaunchAsync(instance, new LauncherAccount { Id = "test", DisplayName = "test" }, new LauncherSettings(), null);
        Assert.Throws<InstanceCleanupBusyException>(() => activity.EnterCleanup(instance.InstanceDirectory));
        exit.SetResult(LaunchExitResult.Success);
        using var cleanup = activity.EnterCleanup(instance.InstanceDirectory);
    }

    [Fact]
    public async Task SelectedCategoriesDoNotDeleteOtherCategories()
    {
        var instance = CreateInstance();
        Write(instance, "logs/latest.log", "log");
        Write(instance, "crash-reports/crash-2026-11-26_10.30.00-client.txt", "crash");
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        await service.CleanAsync(preview, new HashSet<InstanceCleanupCategory> { InstanceCleanupCategory.GameLogs });
        Assert.False(File.Exists(Path.Combine(instance.InstanceDirectory, "logs/latest.log")));
        Assert.True(File.Exists(Path.Combine(instance.InstanceDirectory, "crash-reports/crash-2026-11-26_10.30.00-client.txt")));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CleanAsync(preview, new HashSet<InstanceCleanupCategory> { InstanceCleanupCategory.CrashReports }));
        Assert.True(File.Exists(Path.Combine(instance.InstanceDirectory, "crash-reports/crash-2026-11-26_10.30.00-client.txt")));
    }

    [Theory]
    [InlineData("crash-2026-11-26_10.30.00-client.txt", true)]
    [InlineData("crash-2026-11-26_10.30.00-server.txt", true)]
    [InlineData("crash-2026-11-26_10.30.00-fml.txt", true)]
    [InlineData("crash-notes.txt", false)]
    [InlineData("crash-2026-99-26_10.30.00-client.txt", false)]
    public async Task RecognizesOnlyStandardMinecraftCrashReports(string name, bool expected)
    {
        var instance = CreateInstance();
        Write(instance, Path.Combine("crash-reports", name), "content");
        var preview = await CreateService().ScanAsync(instance);
        Assert.Equal(expected, preview.Entries.Count == 1);
    }

    [Fact]
    public async Task RefusesUnobservedOrUnrelatedInstanceDirectory()
    {
        var root = Path.Combine(TempRoot, "documents");
        Directory.CreateDirectory(root);
        var instance = new GameInstance { InstanceDirectory = root, VersionName = "documents" };
        Write(instance, "important.log", "preserve");
        var service = CreateService();
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => service.ScanAsync(instance));
        InstanceDirectoryOwnership.Observe(instance);
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => service.ScanAsync(instance));
        Assert.True(File.Exists(Path.Combine(root, "important.log")));
    }

    [Theory]
    [InlineData(".bhl-staging")]
    [InlineData(".launcher-restore-orphan")]
    public async Task RefusesTransactionDirectoriesEvenIfObserved(string directoryName)
    {
        var root = Path.Combine(TempRoot, ".minecraft", "versions", directoryName);
        Directory.CreateDirectory(root);
        var instance = new GameInstance { InstanceDirectory = root, VersionName = directoryName };
        Write(instance, "logs/latest.log", "preserve");
        InstanceDirectoryOwnership.Observe(instance);
        await Assert.ThrowsAsync<GameInstanceMutationConflictException>(() => CreateService().ScanAsync(instance));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "logs/latest.log")));
    }

    [Fact]
    public async Task SkipsOpenWritersAndFilesReplacedWithSameSizeAndTimestamp()
    {
        var instance = CreateInstance();
        Write(instance, "logs/writing.log", "original");
        Write(instance, "logs/replaced.log", "original");
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        var path = Path.Combine(instance.InstanceDirectory, "logs/replaced.log");
        var lastWrite = File.GetLastWriteTimeUtc(path);
        File.Move(path, path + ".old");
        File.WriteAllText(path, "personal");
        File.SetLastWriteTimeUtc(path, lastWrite);
        using var writer = new FileStream(Path.Combine(instance.InstanceDirectory, "logs/writing.log"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var result = await service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet());
        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, result.Skipped);
        Assert.Equal("personal", File.ReadAllText(path));
        Assert.True(File.Exists(writer.Name));
    }

    [Fact]
    public async Task TamperedOrForgedPreviewCannotRedirectDeletion()
    {
        var instance = CreateInstance();
        Write(instance, "logs/latest.log", "original");
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        Write(instance, "hs_err_pid777.log", "private");
        var target = Path.Combine(instance.InstanceDirectory, "hs_err_pid777.log");
        var snapshot = WindowsDirectoryHandleMover.CaptureCleanupFile(target)!.Value;
        var forged = new InstanceCleanupEntry(InstanceCleanupCategory.RootLogs, "hs_err_pid777.log", snapshot.Length, snapshot.LastWriteTimeUtcTicks, snapshot.Identity);
        ((InstanceCleanupEntry[])preview.Entries)[0] = forged;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CleanAsync(preview with { Entries = [forged] }, Enum.GetValues<InstanceCleanupCategory>().ToHashSet()));
        var result = await service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet());
        Assert.Equal(1, result.Deleted);
        Assert.Equal("private", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(instance.InstanceDirectory, "logs/latest.log")));
    }

    [Fact]
    public async Task ReplacedLogDirectoryLinkCannotRedirectCleanup()
    {
        var instance = CreateInstance();
        Write(instance, "logs/latest.log", "original");
        var outside = Path.Combine(TempRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "latest.log"), "preserve");
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        var logs = Path.Combine(instance.InstanceDirectory, "logs");
        Directory.Move(logs, logs + "-old");
        CreateJunction(logs, outside);
        var result = await service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet());
        Assert.Equal(1, result.Skipped);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(outside, "latest.log")));
    }

    [Fact]
    public async Task ScanSkipsLinkedDirectoriesEvenWhenTheyContainMatchingLogs()
    {
        var instance = CreateInstance();
        Write(instance, "logs/latest.log", "original");
        var outside = Path.Combine(TempRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.log"), "preserve");
        CreateJunction(Path.Combine(instance.InstanceDirectory, "logs", "external"), outside);
        var service = CreateService();
        var preview = await service.ScanAsync(instance);
        Assert.Single(preview.Entries);
        var result = await service.CleanAsync(preview, Enum.GetValues<InstanceCleanupCategory>().ToHashSet());
        Assert.Equal(1, result.Deleted);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(outside, "private.log")));
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private GameInstance CreateInstance()
    {
        var root = Path.Combine(TempRoot, ".minecraft", "versions", "instance");
        Directory.CreateDirectory(root);
        var instance = new GameInstance { InstanceDirectory = root, VersionName = "instance" };
        InstanceDirectoryOwnership.Observe(instance);
        return instance;
    }

    private static void Write(GameInstance instance, string relative, string value)
    {
        var path = Path.Combine(instance.InstanceDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static InstanceCleanupService CreateService(InstanceActivityCoordinator? activity = null) =>
        new(activity ?? new InstanceActivityCoordinator(), NullLogger<InstanceCleanupService>.Instance);

    private sealed class FakeLaunchService(Task<LaunchExitResult> exit) : ILaunchService
    {
        public Task<GameLaunchSession> LaunchAsync(GameInstance instance, LauncherAccount account, LauncherSettings settings,
            IProgress<LauncherProgress>? progress, LaunchRequestOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameLaunchSession(instance.InstanceDirectory, "test", exit));
    }
}
