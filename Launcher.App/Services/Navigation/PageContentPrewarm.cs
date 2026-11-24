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

using System.Windows;

namespace Launcher.App.Services;

/// <summary>
/// 折叠的页面不会被测量，虚拟化面板要到第一次真正显示才初始化视口，
/// 这笔开销会整段落进第一次切页的动画里。本类型负责让页面在空闲时先完成一次布局。
/// </summary>
internal static class PageContentPrewarm
{
    /// <summary>
    /// 将折叠页面临时设为 Hidden，让 WPF 在下一轮正常布局中测量页面。
    /// </summary>
    internal static void Begin(FrameworkElement page)
    {
        ArgumentNullException.ThrowIfNull(page);

        // 保留原绑定，预热期间用户切到该页时可以立即显示。
        page.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
    }

    /// <summary>
    /// 清除预热设置的当前值，重新按原有值源计算可见性。
    /// </summary>
    internal static void End(FrameworkElement page)
    {
        ArgumentNullException.ThrowIfNull(page);

        page.InvalidateProperty(UIElement.VisibilityProperty);
    }
}
