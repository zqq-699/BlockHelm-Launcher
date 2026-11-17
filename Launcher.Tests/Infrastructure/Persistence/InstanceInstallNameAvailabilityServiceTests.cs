/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Tests.Infrastructure.Persistence;

public sealed class InstanceInstallNameAvailabilityServiceTests : TestTempDirectory
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingDirectoryOrFileOccupiesTheInstallName(bool createDirectory)
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var target = Path.Combine(minecraftDirectory, "versions", "occupied");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (createDirectory)
            Directory.CreateDirectory(target);
        else
            await File.WriteAllTextAsync(target, "occupied");
        var service = CreateService(new GameInstallCoordinator());

        var result = await service.CheckAsync(minecraftDirectory, "occupied");

        Assert.Equal(InstanceInstallNameAvailability.Occupied, result);
    }

    [Fact]
    public async Task PendingInstallTransactionOccupiesItsLogicalName()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var transactionService = new InstanceInstallTransactionService();
        await using var transaction = await transactionService.BeginAsync(
            minecraftDirectory,
            "pending",
            "game",
            initializeDefaultIfEmpty: false);
        var service = CreateService(new GameInstallCoordinator());

        var result = await service.CheckAsync(minecraftDirectory, "pending");

        Assert.Equal(InstanceInstallNameAvailability.Occupied, result);
    }

    [Fact]
    public async Task InProcessInstallLeaseOccupiesItsLogicalName()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var coordinator = new GameInstallCoordinator();
        await using var lease = await coordinator.AcquireInstallAsync(
            minecraftDirectory,
            "installing",
            progress: null);
        var service = CreateService(coordinator);

        var result = await service.CheckAsync(minecraftDirectory, "installing");

        Assert.Equal(InstanceInstallNameAvailability.Occupied, result);
    }

    [Fact]
    public async Task CheckAnswersForWhicheverDirectoryTheCallerSupplies()
    {
        var firstDirectory = Path.Combine(TempRoot, "first", ".minecraft");
        var secondDirectory = Path.Combine(TempRoot, "second", ".minecraft");
        Directory.CreateDirectory(Path.Combine(secondDirectory, "versions", "target"));
        var service = CreateService(new GameInstallCoordinator());

        // 目录由调用方给出，服务不再自行读取 settings.json，因此切换目录只是换一个参数。
        Assert.Equal(
            InstanceInstallNameAvailability.Available,
            await service.CheckAsync(firstDirectory, "target"));
        Assert.Equal(
            InstanceInstallNameAvailability.Occupied,
            await service.CheckAsync(secondDirectory, "target"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingDirectoryReportsUnknownInsteadOfGuessing(string minecraftDirectory)
    {
        var service = CreateService(new GameInstallCoordinator());

        Assert.Equal(
            InstanceInstallNameAvailability.Unknown,
            await service.CheckAsync(minecraftDirectory, "target"));
    }

    /// <summary>
    /// 占用检查会枚举整个 versions 目录并读取标记文件，调用方是逐字符校验实例名的界面代码，
    /// 在调用线程上跑完就会阻塞 UI 线程。历史上正是因为方法体里没有任何 await
    /// 而退化成同步执行过一次，因此这里直接钉住"不得在调用线程上做这件事"。
    /// </summary>
    [Fact]
    public async Task ProbingDoesNotRunOnTheCallingThread()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        Directory.CreateDirectory(Path.Combine(minecraftDirectory, "versions"));
        var coordinator = new ThreadRecordingInstallCoordinator();
        var service = CreateService(coordinator);

        // 从专用线程发起：专用线程不属于线程池，Task.Run 必然换到另一条线程上，
        // 因此"线程号相同"只可能意味着探测是在调用线程上同步跑完的。
        // 若改用线程池线程发起，Task.Run 有可能复用调用者刚释放的那条，判定会失真。
        var callerThreadId = 0;
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            _ = ObserveProbeAsync();
        });
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "CheckAsync did not return to the caller in time.");
        var failure = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(failure);
        Assert.NotNull(coordinator.ObservedThreadId);
        Assert.NotEqual(callerThreadId, coordinator.ObservedThreadId);

        async Task ObserveProbeAsync()
        {
            try
            {
                await service.CheckAsync(minecraftDirectory, "target");
                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetResult(exception);
            }
        }
    }

    private static InstanceInstallNameAvailabilityService CreateService(
        IGameInstallCoordinator coordinator) => new(coordinator);

    /// <summary>记录探测实际发生在哪个线程上。</summary>
    private sealed class ThreadRecordingInstallCoordinator : IGameInstallCoordinator
    {
        internal int? ObservedThreadId { get; private set; }

        public ValueTask<IAsyncDisposable> AcquireInstallAsync(
            string minecraftDirectory,
            string versionName,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool IsInstallingVersion(string minecraftDirectory, string versionName)
        {
            ObservedThreadId = Environment.CurrentManagedThreadId;
            return false;
        }
    }
}
