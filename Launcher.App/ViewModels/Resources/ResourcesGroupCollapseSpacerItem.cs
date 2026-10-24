/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.App.ViewModels.Resources;

/// <summary>子项淡出移除后保留的临时布局高度，用于完成下方内容的收定位移。</summary>
public sealed class ResourcesGroupCollapseSpacerItem(double height, TimeSpan duration)
{
    public double Height { get; } = height;

    public TimeSpan Duration { get; } = duration;
}