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

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.App.Controls;
using Launcher.App.Utilities;

namespace Launcher.App.Behaviors;

/// <summary>
/// 为虚拟化列表的已实现容器计算相邻项状态，并在容器分批生成时协调一次性入场动画。
/// </summary>
public static class VirtualizedListItemStateBehavior
{
    // 容器可能跨多个布局周期生成，有限次数轮询兼顾首屏完整动画和计时器生命周期。
    private const int EntranceAnimationPassCount = 8;
    private static readonly TimeSpan EntranceAnimationPassInterval = TimeSpan.FromMilliseconds(45);

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(VirtualizedListItemStateBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty EntranceAnimationTokenProperty =
        DependencyProperty.RegisterAttached(
            "EntranceAnimationToken",
            typeof(int),
            typeof(VirtualizedListItemStateBehavior),
            new PropertyMetadata(0, OnEntranceAnimationTokenChanged));

    public static readonly DependencyProperty ScrollResetTokenProperty =
        DependencyProperty.RegisterAttached(
            "ScrollResetToken",
            typeof(int),
            typeof(VirtualizedListItemStateBehavior),
            new PropertyMetadata(0, OnScrollResetTokenChanged));

    public static readonly DependencyProperty ContentTopOffsetProperty =
        DependencyProperty.RegisterAttached(
            "ContentTopOffset",
            typeof(double),
            typeof(VirtualizedListItemStateBehavior),
            new PropertyMetadata(0d));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static int GetEntranceAnimationToken(DependencyObject element) => (int)element.GetValue(EntranceAnimationTokenProperty);

    public static void SetEntranceAnimationToken(DependencyObject element, int value) => element.SetValue(EntranceAnimationTokenProperty, value);

    public static int GetScrollResetToken(DependencyObject element) => (int)element.GetValue(ScrollResetTokenProperty);

    public static void SetScrollResetToken(DependencyObject element, int value) => element.SetValue(ScrollResetTokenProperty, value);

    public static double GetContentTopOffset(DependencyObject element) => (double)element.GetValue(ContentTopOffsetProperty);

    public static void SetContentTopOffset(DependencyObject element, double value) => element.SetValue(ContentTopOffsetProperty, value);

    public static void Refresh(DependencyObject element)
    {
        if (element is ListBox listBox && GetOptionalState(listBox) is { } state)
            state.QueueRenderedItemStateRefresh();
    }

    public static void QueueItemEntranceAnimation(ListBox listBox, IReadOnlyList<object> items)
    {
        if (GetIsEnabled(listBox) && items.Count > 0)
            GetState(listBox).QueueItemEntranceAnimation(items);
    }

    /// <summary>
    /// 在列表数据插入/移除前调用：记录已实现项的旧布局位置，等布局完成后把差值作为
    /// RenderTransform 起点动画到 0，避免下方分组瞬间跳到新位置。
    /// </summary>
    public static void QueueLayoutShiftAnimation(ListBox listBox)
    {
        if (GetIsEnabled(listBox))
            GetState(listBox).QueueLayoutShiftAnimation();
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ListBox listBox)
            return;

        if ((bool)e.NewValue)
        {
            GetState(listBox).Attach(listBox);
            return;
        }

        if (GetOptionalState(listBox) is { } state)
            state.Detach();
    }

    private static void OnEntranceAnimationTokenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ListBox listBox || !GetIsEnabled(listBox))
            return;

        var token = (int)e.NewValue;
        if (token > 0)
            GetState(listBox).QueueEntranceAnimation(token);
    }

    private static void OnScrollResetTokenChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ListBox listBox)
            return;

        var token = (int)e.NewValue;
        if (token > 0)
            GetState(listBox).QueueScrollReset(token);
    }

    private static ListBoxState GetState(ListBox listBox)
    {
        if (listBox.GetValue(StateProperty) is ListBoxState state)
            return state;

        state = new ListBoxState();
        listBox.SetValue(StateProperty, state);
        return state;
    }

    private static ListBoxState? GetOptionalState(ListBox listBox)
    {
        return listBox.GetValue(StateProperty) as ListBoxState;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(ListBoxState),
            typeof(VirtualizedListItemStateBehavior),
            new PropertyMetadata(null));

    private sealed class ListBoxState
    {
        // 按数据对象引用记录已播放项，因为 ListBoxItem 会被 Recycling 模式反复复用。
        private readonly HashSet<object> animatedEntranceItems = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> targetedEntranceItems = new(ReferenceEqualityComparer.Instance);
        private readonly List<LayoutShift> activeLayoutShifts = [];
        private readonly DispatcherTimer entranceAnimationTimer;
        private Dictionary<object, LayoutShiftSnapshot>? pendingLayoutShiftTops;
        private ListBox? listBox;
        private ScrollViewer? scrollViewer;
        private ItemsPresenter? itemsPresenter;
        // 刷新和动画标志把多个 WPF 事件合并为串行 Dispatcher 工作，避免重入遍历视觉树。
        private bool isStateRefreshQueued;
        private bool isGlobalEntrancePending;
        private bool isTargetedItemPruneQueued;
        private bool isTargetedEntranceCompletionQueued;
        private bool isLayoutShiftQueued;
        private int targetedEntranceGeneration;
        private int completedTargetedEntranceGeneration;
        private int entranceAnimationPassesRemaining;
        private int observedEntranceAnimationToken;
        private int observedScrollResetToken;
        private bool isScrollResetPending;
        private object? hoveredItem;

        public ListBoxState()
        {
            entranceAnimationTimer = new DispatcherTimer(DispatcherPriority.Loaded)
            {
                Interval = EntranceAnimationPassInterval
            };
            entranceAnimationTimer.Tick += EntranceAnimationTimer_OnTick;
        }

        public void Attach(ListBox target)
        {
            if (ReferenceEquals(listBox, target))
                return;

            // 先解除旧控件全部事件，即使状态对象因模板复用被错误附加也不会形成双重订阅。
            Detach();
            listBox = target;
            target.Loaded += ListBox_OnLoaded;
            target.Unloaded += ListBox_OnUnloaded;
            target.SelectionChanged += ListBox_OnSelectionChanged;
            target.ItemContainerGenerator.StatusChanged += ItemContainerGenerator_OnStatusChanged;
            target.ItemContainerGenerator.ItemsChanged += ItemContainerGenerator_OnItemsChanged;
            AttachScrollViewer();
            QueueScrollReset(GetScrollResetToken(target));
            TryApplyPendingScrollReset();
            QueueRenderedItemStateRefresh();
        }

        public void Detach()
        {
            // Timer 和 ScrollViewer 不是 ListBox 自身事件，必须显式释放以免控件被引用。
            entranceAnimationTimer.Stop();
            ReleaseTargetedEntranceItems();
            CancelLayoutShifts();
            pendingLayoutShiftTops = null;
            DetachScrollViewer();

            if (listBox is null)
                return;

            listBox.Loaded -= ListBox_OnLoaded;
            listBox.Unloaded -= ListBox_OnUnloaded;
            listBox.SelectionChanged -= ListBox_OnSelectionChanged;
            listBox.ItemContainerGenerator.StatusChanged -= ItemContainerGenerator_OnStatusChanged;
            listBox.ItemContainerGenerator.ItemsChanged -= ItemContainerGenerator_OnItemsChanged;
            listBox = null;
            hoveredItem = null;
            animatedEntranceItems.Clear();
        }

        /// <summary>
        /// 为新的动画令牌启动跨布局周期扫描，并清除上一轮已播放项集合。
        /// </summary>
        public void QueueEntranceAnimation(int token)
        {
            if (token == observedEntranceAnimationToken)
                return;

            // 虚拟化容器会跨多个布局周期生成，因此保留多个短轮询周期来捕获首屏容器。
            observedEntranceAnimationToken = token;
            ReleaseTargetedEntranceItems();
            isGlobalEntrancePending = true;
            entranceAnimationPassesRemaining = EntranceAnimationPassCount;
            animatedEntranceItems.Clear();
            entranceAnimationTimer.Start();
            MarkEntrancePendingContainers();
            QueueRenderedItemStateRefresh();
        }

        public void QueueItemEntranceAnimation(IReadOnlyList<object> items)
        {
            targetedEntranceGeneration++;
            for (var index = 0; index < items.Count; index++)
            {
                targetedEntranceItems[items[index]] = index;
                animatedEntranceItems.Remove(items[index]);
            }

            MarkEntrancePendingContainers();
            QueueRenderedItemStateRefresh();
            QueueTargetedEntranceCompletion();
        }

        /// <summary>
        /// 记录当前已实现项位置，等本次数据变更完成布局后把布局差值动画回 0。
        /// 由调用方在 <c>ListItems</c> 插入/移除之前触发。
        /// </summary>
        public void QueueLayoutShiftAnimation()
        {
            if (listBox is null)
                return;

            // 上一轮补偿仍在播放时先回到布局位置，避免把动画偏移重复计入采样。
            CancelLayoutShifts();
            var tops = CaptureContainerTops();
            pendingLayoutShiftTops = tops.Count == 0 ? null : tops;
            if (pendingLayoutShiftTops is null || isLayoutShiftQueued)
                return;

            isLayoutShiftQueued = true;
            listBox.Dispatcher.BeginInvoke(() =>
            {
                isLayoutShiftQueued = false;
                ApplyLayoutShift();
            }, DispatcherPriority.Loaded);
        }

        private Dictionary<object, LayoutShiftSnapshot> CaptureContainerTops()
        {
            var tops = new Dictionary<object, LayoutShiftSnapshot>(ReferenceEqualityComparer.Instance);
            if (listBox is null)
                return tops;

            foreach (var container in FindRealizedContainers(listBox))
            {
                if (container.DataContext is not { } item)
                    continue;

                var top = GetContainerTop(container);
                if (!double.IsNaN(top))
                    tops[item] = new LayoutShiftSnapshot(top, IsContainerInUsableViewport(container));
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
            foreach (var container in FindRealizedContainers(listBox))
            {
                if (container.DataContext is not { } item
                    || !previousTops.TryGetValue(item, out var previous))
                {
                    continue;
                }

                var currentTop = GetContainerTop(container);
                if (double.IsNaN(currentTop))
                    continue;

                var isVisibleAfter = IsContainerInUsableViewport(container);
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

        private void CancelLayoutShifts()
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

        /// <summary>
        /// 只在新的上下文令牌发布时重置滚动；控件临时卸载后使用同一令牌重新挂载不会改变原位置。
        /// </summary>
        public void QueueScrollReset(int token)
        {
            if (token <= 0 || token == observedScrollResetToken)
                return;

            observedScrollResetToken = token;
            isScrollResetPending = true;
            TryApplyPendingScrollReset();
        }

        private void ListBox_OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachScrollViewer();
            TryApplyPendingScrollReset();
            QueueRenderedItemStateRefresh();

            if (listBox is { } target)
                QueueEntranceAnimation(GetEntranceAnimationToken(target));

            QueueTargetedEntranceCompletion();
        }

        private void ListBox_OnUnloaded(object sender, RoutedEventArgs e)
        {
            entranceAnimationTimer.Stop();
            ReleaseTargetedEntranceItems();
            CancelLayoutShifts();
            pendingLayoutShiftTops = null;
            isGlobalEntrancePending = false;
            DetachScrollViewer();
        }

        private void ListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QueueRenderedItemStateRefresh();
        }

        private void ItemContainerGenerator_OnStatusChanged(object? sender, EventArgs e)
        {
            if (listBox?.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            {
                // 新容器先进入等待状态，再在同一轮判断哪些位于可视区域并真正播放。
                MarkEntrancePendingContainers();
                PreparePendingEntranceAnimationStates();
                QueueTargetedEntranceCompletion();
            }

            QueueRenderedItemStateRefresh();
        }

        private void ItemContainerGenerator_OnItemsChanged(object? sender, ItemsChangedEventArgs e)
        {
            // 容器即将复用或重建，旧容器的补偿位移不能带到新数据项上。
            CancelLayoutShifts();

            if (targetedEntranceItems.Count == 0 || isTargetedItemPruneQueued || listBox is null)
                return;

            isTargetedItemPruneQueued = true;
            listBox.Dispatcher.BeginInvoke(() =>
            {
                isTargetedItemPruneQueued = false;
                if (listBox is null || targetedEntranceItems.Count == 0)
                    return;

                var currentItems = new HashSet<object>(listBox.Items.Cast<object>(), ReferenceEqualityComparer.Instance);
                foreach (var item in targetedEntranceItems.Keys.Where(item => !currentItems.Contains(item)).ToArray())
                {
                    if (item is IListEntrancePendingItem pendingItem)
                        pendingItem.IsGroupEntrancePending = false;
                    targetedEntranceItems.Remove(item);
                    animatedEntranceItems.Remove(item);
                }
            }, DispatcherPriority.Loaded);
        }

        private void QueueTargetedEntranceCompletion()
        {
            if (targetedEntranceItems.Count == 0 || isTargetedEntranceCompletionQueued || listBox is null
                || targetedEntranceGeneration == completedTargetedEntranceGeneration)
                return;

            isTargetedEntranceCompletionQueued = true;
            listBox.Dispatcher.BeginInvoke(() =>
            {
                isTargetedEntranceCompletionQueued = false;
                CompleteTargetedEntranceAfterInitialLayout();
            }, DispatcherPriority.ContextIdle);
        }

        private void CompleteTargetedEntranceAfterInitialLayout()
        {
            if (listBox is not { IsLoaded: true, IsVisible: true } target
                || targetedEntranceItems.Count == 0
                || target.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
                return;

            // 插入大量子项时先完成当前视口布局；仅这一批可见容器参加本次展开动画。
            completedTargetedEntranceGeneration = targetedEntranceGeneration;
            target.UpdateLayout();
            MarkEntrancePendingContainers();
            PreparePendingEntranceAnimationStates();
            var initialViewportItems = new HashSet<object>(
                FindRealizedContainers(target)
                    .Where(IsContainerInUsableViewport)
                    .Select(container => container.DataContext)
                    .OfType<object>(),
                ReferenceEqualityComparer.Instance);
            foreach (var item in targetedEntranceItems.Keys.Where(item => !initialViewportItems.Contains(item)).ToArray())
            {
                if (item is IListEntrancePendingItem pendingItem)
                    pendingItem.IsGroupEntrancePending = false;
                targetedEntranceItems.Remove(item);
                animatedEntranceItems.Remove(item);
            }

            QueueRenderedItemStateRefresh();
        }

        private void AttachScrollViewer()
        {
            if (listBox is null)
                return;

            // ScrollViewer 位于控件模板内部，模板未应用前无法订阅滚动事件。
            listBox.ApplyTemplate();
            var nextScrollViewer = VisualTreeSearch.FindDescendant<ScrollViewer>(listBox, _ => true);
            if (!ReferenceEquals(scrollViewer, nextScrollViewer))
            {
                DetachScrollViewer();
                scrollViewer = nextScrollViewer;
                if (scrollViewer is not null)
                    scrollViewer.ScrollChanged += ScrollViewer_OnScrollChanged;
            }

            // ItemsPresenter 的模板可能晚于 ListBox 模板生成，每次挂载都重新查找。
            itemsPresenter = scrollViewer is null
                ? null
                : VisualTreeSearch.FindDescendant<ItemsPresenter>(listBox, _ => true);
        }

        private void TryApplyPendingScrollReset()
        {
            if (!isScrollResetPending || listBox is null)
                return;

            AttachScrollViewer();
            if (scrollViewer is null)
                return;

            isScrollResetPending = false;
            SmoothScrollBehavior.CancelAnimation(scrollViewer);
            scrollViewer.ScrollToVerticalOffset(0);
            QueueRenderedItemStateRefresh();
        }

        private void DetachScrollViewer()
        {
            if (scrollViewer is null)
                return;

            scrollViewer.ScrollChanged -= ScrollViewer_OnScrollChanged;
            scrollViewer = null;
            itemsPresenter = null;
        }

        private void ScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 用户滚动时继续保留补偿位移会让行粘在旧位置，直接结束布局补偿动画。
            if (e.VerticalChange != 0)
                CancelLayoutShifts();

            QueueRenderedItemStateRefresh();
        }

        /// <summary>
        /// 合并容器生成、滚动和选择事件，在布局稳定后刷新一次已实现项状态。
        /// </summary>
        public void QueueRenderedItemStateRefresh()
        {
            if (isStateRefreshQueued || listBox is null)
                return;

            // 滚动、选择和容器生成可能在同一帧连续触发，只需在 Loaded 优先级统一计算一次。
            isStateRefreshQueued = true;
            listBox.Dispatcher.BeginInvoke(
                () =>
                {
                    isStateRefreshQueued = false;
                    RefreshRenderedItemState();
                    PlayPendingEntranceAnimations();
                },
                DispatcherPriority.Loaded);
        }

        private void EntranceAnimationTimer_OnTick(object? sender, EventArgs e)
        {
            if (!isGlobalEntrancePending)
            {
                entranceAnimationTimer.Stop();
                return;
            }

            // 每一轮都重新扫描，因为虚拟化面板可能刚刚补生成下一批首屏容器。
            QueueRenderedItemStateRefresh();
            entranceAnimationPassesRemaining--;
            if (entranceAnimationPassesRemaining > 0)
                return;

            isGlobalEntrancePending = false;
            if (targetedEntranceItems.Count == 0)
                ClearEntrancePendingStates();
            else
                QueueRenderedItemStateRefresh();
            entranceAnimationTimer.Stop();
        }

        /// <summary>
        /// 只遍历当前已实现容器，计算首尾项、相邻高亮和动画等待状态。
        /// </summary>
        private void RefreshRenderedItemState()
        {
            if (listBox is null)
                return;

            var containers = FindRealizedContainers(listBox);
            // 排序后的相邻关系只针对已实现容器，用于圆角/分隔线视觉而非完整数据集合。
            for (var i = 0; i < containers.Count; i++)
            {
                var container = containers[i];
                var state = EnsureItemState(container);
                UpdateEntrancePendingState(container, state);
                state.IsFirstVisible = i == 0;
                state.IsLastVisible = i == containers.Count - 1;
                state.IsPreviousItemHighlighted = i > 0 && IsHighlighted(containers[i - 1].DataContext);

                if (VisualTreeSearch.FindDescendant<ListPageItemButton>(container, _ => true) is { } button)
                    HookItemButton(button);
            }
        }

        private void PlayPendingEntranceAnimations()
        {
            if (listBox is null || (!isGlobalEntrancePending && targetedEntranceItems.Count == 0))
                return;

            if (listBox.Items.Count == 0)
            {
                isGlobalEntrancePending = false;
                ClearEntrancePendingStates();
                ReleaseTargetedEntranceItems();
                entranceAnimationTimer.Stop();
                return;
            }

            PreparePendingEntranceAnimationStates();
        }

        /// <summary>
        /// 为进入有效视口且尚未播放的数据项分配稳定的入场序号。
        /// </summary>
        private void PreparePendingEntranceAnimationStates()
        {
            if (listBox is null || (!isGlobalEntrancePending && targetedEntranceItems.Count == 0))
                return;

            var containers = FindRealizedContainers(listBox);
            foreach (var container in containers)
            {
                var index = listBox.ItemContainerGenerator.IndexFromContainer(container);
                if (index < 0)
                    continue;

                // 按数据项身份记录播放状态，而不是按可回收容器记录，防止滚动后同一项重复入场。
                if (container.DataContext is not { } item)
                    continue;

                var isTargeted = targetedEntranceItems.TryGetValue(item, out var targetedIndex);
                if (!isTargeted && !isGlobalEntrancePending)
                    continue;

                if (animatedEntranceItems.Contains(item))
                {
                    if (!isTargeted || item is not IListEntrancePendingItem { IsGroupEntrancePending: true }
                        || EnsureItemState(container).ShouldPlayEnterAnimation)
                        continue;

                    // 上一容器在播放前被回收，允许新容器接过尚未消费的入场请求。
                    animatedEntranceItems.Remove(item);
                }

                if (!IsContainerInUsableViewport(container))
                    continue;

                var state = EnsureItemState(container);
                state.IsEntranceAnimationPending = true;
                state.EnterAnimationIndex = isTargeted ? targetedIndex : index;
                state.ShouldPlayEnterAnimation = true;
                animatedEntranceItems.Add(item);
                if (isTargeted && item is IListEntrancePendingItem { IsGroupEntrancePending: false })
                    targetedEntranceItems.Remove(item);
            }
        }

        private void MarkEntrancePendingContainers()
        {
            if (listBox is null || (!isGlobalEntrancePending && targetedEntranceItems.Count == 0))
                return;

            foreach (var container in FindRealizedContainers(listBox))
                UpdateEntrancePendingState(container, EnsureItemState(container));
        }

        private void UpdateEntrancePendingState(ListBoxItem container, VirtualizedListItemState state)
        {
            state.IsEntranceAnimationPending = ShouldHoldForEntranceAnimation(container);
        }

        private bool ShouldHoldForEntranceAnimation(ListBoxItem container)
        {
            if (listBox is null || (!isGlobalEntrancePending && targetedEntranceItems.Count == 0))
                return false;

            if (container.DataContext is not { } item || animatedEntranceItems.Contains(item))
                return false;

            if (!isGlobalEntrancePending && !targetedEntranceItems.ContainsKey(item))
                return false;

            // 未映射到数据索引的临时容器不参与动画，防止得到无效 stagger 序号。
            var index = listBox.ItemContainerGenerator.IndexFromContainer(container);
            return index >= 0 && IsContainerInUsableViewport(container);
        }

        private void ClearEntrancePendingStates()
        {
            if (listBox is null)
                return;

            foreach (var container in FindRealizedContainers(listBox))
                EnsureItemState(container).IsEntranceAnimationPending = false;
        }

        private void ReleaseTargetedEntranceItems()
        {
            foreach (var item in targetedEntranceItems.Keys)
                if (item is IListEntrancePendingItem pendingItem)
                    pendingItem.IsGroupEntrancePending = false;

            targetedEntranceItems.Clear();
        }

        /// <summary>
        /// 判断容器是否位于扣除顶部遮挡区域后的有效可视范围内。
        /// </summary>
        private bool IsContainerInUsableViewport(ListBoxItem container)
        {
            if (scrollViewer is null || scrollViewer.ActualHeight <= 0 || listBox is null)
                return true;

            try
            {
                var bounds = container
                    .TransformToAncestor(scrollViewer)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                var visibleTop = GetContentTopOffset(listBox);
                var visibleBottom = Math.Max(visibleTop, scrollViewer.ActualHeight);
                return bounds.Bottom > visibleTop && bounds.Top < visibleBottom;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static IReadOnlyList<ListBoxItem> FindRealizedContainers(ListBox listBox)
        {
            // 虚拟化面板不会为未显示项创建容器，因此遍历视觉树的成本与视口大小相关。
            var containers = new List<ListBoxItem>();
            AddRealizedContainers(listBox, containers);
            containers.Sort((left, right) =>
                listBox.ItemContainerGenerator.IndexFromContainer(left)
                    .CompareTo(listBox.ItemContainerGenerator.IndexFromContainer(right)));
            return containers;
        }

        private static void AddRealizedContainers(DependencyObject root, ICollection<ListBoxItem> containers)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ListBoxItem item)
                    containers.Add(item);

                AddRealizedContainers(child, containers);
            }
        }

        private static VirtualizedListItemState EnsureItemState(ListBoxItem container)
        {
            // 状态挂在容器 Tag 上，回收容器时会被下一次刷新完整覆盖，不依赖旧数据项。
            if (container.Tag is VirtualizedListItemState state)
                return state;

            state = new VirtualizedListItemState();
            container.Tag = state;
            return state;
        }

        private void HookItemButton(ListPageItemButton button)
        {
            // 先移除再添加使重复视觉树扫描保持幂等，不会累计 MouseEnter/Leave 订阅。
            button.MouseEnter -= ItemButton_OnMouseEnter;
            button.MouseLeave -= ItemButton_OnMouseLeave;
            button.MouseEnter += ItemButton_OnMouseEnter;
            button.MouseLeave += ItemButton_OnMouseLeave;
        }

        private void ItemButton_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: { } item })
                hoveredItem = item;

            QueueRenderedItemStateRefresh();
        }

        private void ItemButton_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            hoveredItem = null;
            QueueRenderedItemStateRefresh();
        }

        private bool IsHighlighted(object? item)
        {
            return item is not null
                && (ReferenceEquals(item, hoveredItem) || ReferenceEquals(item, listBox?.SelectedItem));
        }
    }
}

/// <summary>允许虚拟化列表在动画扫描结束后显示未进入视口的数据项。</summary>
public interface IListEntrancePendingItem
{
    bool IsGroupEntrancePending { get; set; }
}

public sealed class VirtualizedListItemState : INotifyPropertyChanged
{
    private bool isFirstVisible;
    private bool isLastVisible;
    private bool isPreviousItemHighlighted;
    private bool shouldPlayEnterAnimation;
    private bool isEntranceAnimationPending;
    private int enterAnimationIndex;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsFirstVisible
    {
        get => isFirstVisible;
        set => SetProperty(ref isFirstVisible, value);
    }

    public bool IsLastVisible
    {
        get => isLastVisible;
        set => SetProperty(ref isLastVisible, value);
    }

    public bool IsPreviousItemHighlighted
    {
        get => isPreviousItemHighlighted;
        set => SetProperty(ref isPreviousItemHighlighted, value);
    }

    public bool ShouldPlayEnterAnimation
    {
        get => shouldPlayEnterAnimation;
        set
        {
            if (!SetProperty(ref shouldPlayEnterAnimation, value))
                return;

            if (!value)
                IsEntranceAnimationPending = false;
        }
    }

    public bool IsEntranceAnimationPending
    {
        get => isEntranceAnimationPending;
        set => SetProperty(ref isEntranceAnimationPending, value);
    }

    public int EnterAnimationIndex
    {
        get => enterAnimationIndex;
        set => SetProperty(ref enterAnimationIndex, value);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
