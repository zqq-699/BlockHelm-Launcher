/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.Windows.Threading;

namespace Launcher.Tests.Helpers;

internal static class DispatcherTestWait
{
    public static void Until(Func<bool> condition)
    {
        if (condition())
            return;

        var timeout = TimeSpan.FromSeconds(15);
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        timer.Tick += (_, _) =>
        {
            if (condition() || elapsed.Elapsed >= timeout)
                frame.Continue = false;
        };
        try
        {
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }

        Assert.True(condition(), "The dispatcher did not reach the expected state.");
    }
}
