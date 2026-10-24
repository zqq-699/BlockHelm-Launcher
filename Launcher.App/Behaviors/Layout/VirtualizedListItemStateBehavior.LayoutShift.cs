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
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launcher.App.Behaviors;

public static partial class VirtualizedListItemStateBehavior
{
    private sealed partial class ListBoxState
    {
        // 布局补偿与入场扫描各自管理状态，事件入口仍由 ListBoxState 统一调度。
        private sealed class LayoutShiftState
        {
            private readonly ListBoxState owner;
            private readonly List<LayoutShift> activeLayoutShifts = [];
            private Dictionary<object, LayoutShiftSnapshot>? pendingLayoutShiftTops;
            private bool isLayoutShiftQueued;
            private int layoutShiftGeneration;
            private ItemsPresenter? itemsPresenter;
            private ListBox? listBox => owner.listBox;
            private ScrollViewer? scrollViewer => owner.scrollViewer;

            public LayoutShiftState(ListBoxState owner) => this.owner = owner;

            public ItemsPresenter? ItemsPresenter
            {
                set => itemsPresenter = value;
            }

            public void ClearPending()
            {
                pendingLayoutShiftTops = null;
                isLayoutShiftQueued = false;
                layoutShiftGeneration++;
            }

            /// <summary>
            /// 记录当前已实现项位置，等本次数据变更完成布局后把布局差值动画回 0。
            /// 由调用方在 <c>ListItems</c> 插入/移除之前触发。
            /// </summary>
            public void QueueLayoutShiftAnimation()
            {
                if (listBox is not { } target)
                    return;

                // 上一轮补偿仍在播放时先回到布局位置，避免把动画偏移重复计入采样。
                CancelLayoutShifts();
                var tops = CaptureContainerTops();
                pendingLayoutShiftTops = tops.Count == 0 ? null : tops;
                if (pendingLayoutShiftTops is null || isLayoutShiftQueued)
                    return;

                isLayoutShiftQueued = true;
                var generation = layoutShiftGeneration;
                target.Dispatcher.BeginInvoke(() =>
                {
                    if (generation != layoutShiftGeneration || !ReferenceEquals(listBox, target))
                        return;

                    isLayoutShiftQueued = false;
                    // 先完成新布局并立即写入补偿位移，确保最终位置不会被渲染出一帧。
                    target.UpdateLayout();
                    ApplyLayoutShift();
                }, DispatcherPriority.DataBind);
            }

            private Dictionary<object, LayoutShiftSnapshot> CaptureContainerTops()
            {
                var tops = new Dictionary<object, LayoutShiftSnapshot>(ReferenceEqualityComparer.Instance);
                if (listBox is null)
                    return tops;

                foreach (var container in ListBoxState.FindRealizedContainers(listBox))
                {
                    if (container.DataContext is not { } item)
                        continue;

                    var top = GetContainerTop(container);
                    if (!double.IsNaN(top))
                        tops[item] = new LayoutShiftSnapshot(top, owner.IsContainerInUsableViewport(container));
                }

                return tops;
            }

            private void ApplyLayoutShift()
            {
                var previousTops = pendingLayoutShiftTops;
                pendingLayoutShiftTops = null;
                if (previousTops is null || listBox is null)
                    return;

                var duration = ListEntranceAnimationDefaults.LayoutShiftDuration;
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                var viewportTravel = GetLayoutShiftViewportTravel();
                foreach (var container in ListBoxState.FindRealizedContainers(listBox))
                {
                    if (container.DataContext is not { } item
                        || !previousTops.TryGetValue(item, out var previous))
                    {
                        continue;
                    }

                    var currentTop = GetContainerTop(container);
                    if (double.IsNaN(currentTop))
                        continue;

                    var isVisibleAfter = owner.IsContainerInUsableViewport(container);
                    // 子项多时下方内容会直接被推出视口；视口外的位移不再处理。
                    if (!previous.WasVisible && !isVisibleAfter)
                        continue;

                    var delta = previous.Top - currentTop;
                    if (Math.Abs(delta) < 0.5)
                        continue;

                    var start = delta;
                    var end = 0d;
                    if (viewportTravel > 0)
                    {
                        if (previous.WasVisible && !isVisibleAfter)
                        {
                            // 离开视口：只播放视口内的可见段，剩余位移直接交给布局。
                            end = delta > 0
                                ? delta - Math.Min(delta, viewportTravel)
                                : delta + Math.Min(-delta, viewportTravel);
                        }
                        else if (!previous.WasVisible && isVisibleAfter)
                        {
                            // 进入视口：从最近的视口边缘开始，而不是从很远的旧位置。
                            start = delta > 0
                                ? Math.Min(delta, viewportTravel)
                                : Math.Max(delta, -viewportTravel);
                        }
                    }

                    var translate = new TranslateTransform(0, start);
                    container.RenderTransform = translate;
                    var animation = new DoubleAnimation(start, end, duration)
                    {
                        EasingFunction = easing
                    };
                    animation.Completed += (_, _) =>
                    {
                        activeLayoutShifts.RemoveAll(shift => ReferenceEquals(shift.Transform, translate));
                        if (ReferenceEquals(container.RenderTransform, translate))
                            container.RenderTransform = null;
                    };
                    translate.BeginAnimation(TranslateTransform.YProperty, animation);
                    activeLayoutShifts.Add(new LayoutShift(container, translate));
                }
            }

            private double GetLayoutShiftViewportTravel()
            {
                return scrollViewer is { ViewportHeight: > 0 } scroller
                    ? scroller.ViewportHeight + 48d
                    : 0d;
            }

            private double GetContainerTop(ListBoxItem container)
            {
                // 相对 ItemsPresenter 采样，滚动位置变化不会污染布局差值。
                var ancestor = (Visual?)itemsPresenter ?? scrollViewer;
                if (ancestor is null)
                    return double.NaN;

                try
                {
                    return container.TransformToAncestor(ancestor).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                    return double.NaN;
                }
            }

            public void CancelLayoutShifts()
            {
                foreach (var shift in activeLayoutShifts)
                {
                    if (ReferenceEquals(shift.Container.RenderTransform, shift.Transform))
                        shift.Container.RenderTransform = null;
                }

                activeLayoutShifts.Clear();
            }

            private readonly record struct LayoutShift(ListBoxItem Container, TranslateTransform Transform);

            private readonly record struct LayoutShiftSnapshot(double Top, bool WasVisible);
        }
    }
}
