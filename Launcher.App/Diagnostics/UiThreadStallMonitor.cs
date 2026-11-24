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

using System.Diagnostics;
using System.Windows.Threading;
using Serilog;

namespace Launcher.App.Diagnostics;

/// <summary>
/// 以固定节拍在 UI 线程排队一个低优先级回调，用回调迟到的时间衡量界面线程被阻塞的时长。
/// 动画和滚动的卡顿大多来自 UI 线程长时间不可用，这条信号可以把“渲染慢”和“线程被占住”区分开。
/// </summary>
internal sealed class UiThreadStallMonitor : IDisposable
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartupInputProbeInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StartupInputSampleDuration = TimeSpan.FromSeconds(3);

    // 一帧的抖动不算卡顿；超过这个迟到量用户才会察觉到动画停顿。
    internal const double StallThresholdMs = 200d;

    private readonly DispatcherTimer timer;
    private DispatcherTimer? startupInputTimer;
    private long lastTickTimestamp;
    private long stallCount;
    private double worstStallMs;
    private bool isDisposed;

    internal UiThreadStallMonitor(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = ProbeInterval
        };
        timer.Tick += Timer_Tick;
    }

    internal long StallCount => Interlocked.Read(ref stallCount);

    internal double WorstStallMs => worstStallMs;

    internal void Start()
    {
        // 埋点未开启时连探测定时器都不要启动：
        // 它的回调会周期性唤醒 Dispatcher，而这些唤醒对普通运行没有任何价值。
        if (isDisposed || timer.IsEnabled || !UiPerformanceLog.IsEnabled)
            return;

        lastTickTimestamp = Stopwatch.GetTimestamp();
        timer.Start();
    }

    internal void SampleStartupInputAfterContentRendered(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Window window)
            window.ContentRendered -= SampleStartupInputAfterContentRendered;
        if (isDisposed || !UiPerformanceLog.IsEnabled)
            return;

        var startedAt = Stopwatch.GetTimestamp();
        var previousTick = startedAt;
        var worstDelayMs = 0d;
        var samples = 0;
        var inputTimer = new DispatcherTimer(DispatcherPriority.Input, timer.Dispatcher)
        {
            Interval = StartupInputProbeInterval
        };
        inputTimer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var delayMs = CalculateStallMs(
                Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds,
                StartupInputProbeInterval.TotalMilliseconds);
            previousTick = now;
            worstDelayMs = Math.Max(worstDelayMs, delayMs);
            samples++;
            var duration = Stopwatch.GetElapsedTime(startedAt, now);
            if (duration < StartupInputSampleDuration)
                return;

            inputTimer.Stop();
            startupInputTimer = null;
            if (UiPerformanceLog.IsEnabled)
            {
                Log.Debug(
                    "Startup input dispatcher sampled. DurationMs={DurationMs:F0} ProbeIntervalMs={ProbeIntervalMs:F0} "
                    + "Samples={Samples} WorstDelayMs={WorstDelayMs:F1}",
                    duration.TotalMilliseconds,
                    StartupInputProbeInterval.TotalMilliseconds,
                    samples,
                    worstDelayMs);
            }
        };
        startupInputTimer = inputTimer;
        inputTimer.Start();
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;
        startupInputTimer?.Stop();
        startupInputTimer = null;
        timer.Stop();
        timer.Tick -= Timer_Tick;
    }

    internal static double CalculateStallMs(double elapsedMs, double probeIntervalMs) =>
        Math.Max(0d, elapsedMs - probeIntervalMs);

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(lastTickTimestamp).TotalMilliseconds;
        lastTickTimestamp = Stopwatch.GetTimestamp();

        // 计时器始终运行，但只有开启诊断日志时才写入，避免普通运行产生噪声。
        if (!UiPerformanceLog.IsEnabled)
            return;

        var stallMs = CalculateStallMs(elapsedMs, ProbeInterval.TotalMilliseconds);
        if (stallMs < StallThresholdMs)
            return;

        Interlocked.Increment(ref stallCount);
        if (stallMs > worstStallMs)
            worstStallMs = stallMs;

        Log.Warning(
            "UI thread stalled; animations and scrolling were blocked. StallMs={StallMs:F0} "
            + "ProbeIntervalMs={ProbeIntervalMs:F0} StallCount={StallCount} WorstStallMs={WorstStallMs:F0}",
            stallMs,
            ProbeInterval.TotalMilliseconds,
            Interlocked.Read(ref stallCount),
            worstStallMs);
    }
}
