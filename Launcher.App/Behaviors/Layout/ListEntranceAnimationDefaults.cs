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

namespace Launcher.App.Behaviors;

/// <summary>
/// 列表项入场动画的时序与几何来源。
/// </summary>
internal static class ListEntranceAnimationDefaults
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(330);

    /// <summary>插入/移除分组后，下方已实现项补偿布局位移的动画时长。</summary>
    public static readonly TimeSpan LayoutShiftDuration = TimeSpan.FromMilliseconds(220);

    public static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(30);

    // 长列表只为首批可见项形成阶梯，后续条目不再继续增加延迟。
    public const int MaxStaggerIndex = 12;

    public const double StartScale = 0.96;

    public const double OffsetY = 14;

    public static TimeSpan GetStaggerDelay(int index)
    {
        return StaggerStep * Math.Clamp(index, 0, MaxStaggerIndex);
    }
}
