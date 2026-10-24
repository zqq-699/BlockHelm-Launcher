/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Behaviors;

namespace Launcher.Tests.Behaviors;

public sealed class ListEntranceAnimationDefaultsTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(6, 180)]
    [InlineData(12, 360)]
    [InlineData(13, 360)]
    [InlineData(1000, 360)]
    public void StaggerDelayStopsAtConfiguredMaximum(int index, int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            ListEntranceAnimationDefaults.GetStaggerDelay(index));
    }

    [Fact]
    public void SharedEntranceKeepsTheExistingScaleOffsetAndDuration()
    {
        Assert.Equal(0.96, ListEntranceAnimationDefaults.StartScale);
        Assert.Equal(14, ListEntranceAnimationDefaults.OffsetY);
        Assert.Equal(TimeSpan.FromMilliseconds(330), ListEntranceAnimationDefaults.Duration);
    }
}
