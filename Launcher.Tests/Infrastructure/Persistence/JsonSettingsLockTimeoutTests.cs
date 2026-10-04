/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Tests.Infrastructure.Persistence;

/// <summary>
/// 跨进程锁曾经无限期重试：网络盘上一个不再释放的 .lock 会把设置写入永远挂住，
/// 表现为点击后无响应（issue #38）。
/// </summary>
public sealed class JsonSettingsLockTimeoutTests : TestTempDirectory
{
    // 回归时 UpdateAsync 会永远等下去。用一个外层看门狗把"挂死"变成"失败"，
    // 否则整个测试套件会被一个回归拖住。看门狗刻意不用 TimeoutException，
    // 免得和被测的超时行为混为一谈。
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task UpdateGivesUpInsteadOfWaitingForeverOnAHeldLock()
    {
        var service = CreateService();
        await service.LoadAsync();

        using var holder = HoldLock();
        var elapsed = Stopwatch.StartNew();
        var exception = await BoundedAsync(() => service.UpdateAsync(settings => settings.IsMenuExpanded = true));
        elapsed.Stop();

        Assert.IsType<TimeoutException>(exception);
        Assert.Contains("settings.json.lock", exception!.Message, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < Watchdog, $"Gave up only after {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task AFailedAttemptDoesNotPoisonLaterWrites()
    {
        var service = CreateService();
        await service.LoadAsync();

        using (HoldLock())
        {
            await BoundedAsync(() => service.UpdateAsync(settings => settings.IsMenuExpanded = true));
        }

        var updated = await service.UpdateAsync(settings => settings.IsMenuExpanded = true);

        Assert.True(updated.IsMenuExpanded);
        Assert.True((await service.LoadAsync()).IsMenuExpanded);
    }

    private FileStream HoldLock() => new(
        Path.Combine(TempRoot, "settings.json.lock"),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);

    private JsonSettingsService CreateService() =>
        new(
            dataDirectory: TempRoot,
            logger: null,
            crossProcessLockTimeout: LockTimeout,
            crossProcessLockRetryDelay: RetryDelay);

    private static async Task<Exception?> BoundedAsync(Func<Task> action)
    {
        var attempt = Record.ExceptionAsync(action);
        var finished = await Task.WhenAny(attempt, Task.Delay(Watchdog));
        Assert.True(ReferenceEquals(finished, attempt), "The settings write never gave up on the held lock.");
        return await attempt;
    }
}
