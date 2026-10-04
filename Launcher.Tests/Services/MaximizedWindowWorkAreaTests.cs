/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Services;

namespace Launcher.Tests.Services;

public sealed class MaximizedWindowWorkAreaTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1040, 0, 0, 1920, 1040)]
    [InlineData(-1920, 0, 0, 1080, -1880, 0, 0, 1080, 40, 0, 1880, 1080)]
    [InlineData(1920, -1080, 3840, 0, 1920, -1040, 3840, 0, 0, 40, 1920, 1040)]
    public void CalculateBoundsUsesTheMonitorRelativeWorkArea(
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var bounds = MaximizedWindowWorkArea.CalculateBounds(
            new NativeMonitorBounds(monitorLeft, monitorTop, monitorRight, monitorBottom),
            new NativeMonitorBounds(workLeft, workTop, workRight, workBottom));

        Assert.Equal(
            new MaximizedWindowBounds(expectedX, expectedY, expectedWidth, expectedHeight),
            bounds);
    }

    [Theory]
    [InlineData(900, 600, 1, 1, 112, 112, 900, 600)]
    [InlineData(900, 600, 1.25, 1.25, 112, 112, 1125, 750)]
    [InlineData(900, 600, 1.5, 1.5, 112, 112, 1350, 900)]
    [InlineData(40, 30, 1, 1, 120, 80, 120, 80)]
    public void CalculateMinimumTrackSizeUsesDpiAndPreservesExistingMinimum(
        double minimumWidth,
        double minimumHeight,
        double dpiScaleX,
        double dpiScaleY,
        int existingWidth,
        int existingHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var size = MaximizedWindowWorkArea.CalculateMinimumTrackSize(
            minimumWidth,
            minimumHeight,
            dpiScaleX,
            dpiScaleY,
            new NativeWindowSize(existingWidth, existingHeight));

        Assert.Equal(new NativeWindowSize(expectedWidth, expectedHeight), size);
    }
}
