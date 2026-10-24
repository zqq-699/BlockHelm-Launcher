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
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launcher.App.Behaviors;

/// <summary>
/// 让非 ListPageItemButton 的列表内容复用相同的虚拟化列表入场动画。
/// </summary>
public static class ListPageEntranceAnimationBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ListPageEntranceAnimationBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty ItemStateProperty =
        DependencyProperty.RegisterAttached(
            "ItemState",
            typeof(VirtualizedListItemState),
            typeof(ListPageEntranceAnimationBehavior),
            new PropertyMetadata(null, OnItemStateChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(AnimationState),
            typeof(ListPageEntranceAnimationBehavior),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static VirtualizedListItemState? GetItemState(DependencyObject element) =>
        (VirtualizedListItemState?)element.GetValue(ItemStateProperty);

    public static void SetItemState(DependencyObject element, VirtualizedListItemState? value) =>
        element.SetValue(ItemStateProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
            return;

        element.Loaded -= Element_OnLoaded;
        element.Unloaded -= Element_OnUnloaded;
        GetState(element).DetachItemState();

        if (e.NewValue is true)
        {
            element.Loaded += Element_OnLoaded;
            element.Unloaded += Element_OnUnloaded;
            AttachItemState(element);
            UpdateVisual(element);
            return;
        }

        ResetVisual(element);
        element.ClearValue(StateProperty);
    }

    private static void OnItemStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element || !GetIsEnabled(element))
            return;

        var state = GetState(element);
        state.AnimationGeneration++;
        state.IsAnimating = false;
        ResetVisual(element);
        AttachItemState(element);
        UpdateVisual(element);
    }

    private static void AttachItemState(FrameworkElement element)
    {
        var state = GetState(element);
        state.DetachItemState();
        if (GetItemState(element) is not { } itemState)
            return;

        state.ItemState = itemState;
        state.ItemState.PropertyChanged += state.ItemStatePropertyChanged;
    }

    private static void Element_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            AttachItemState(element);
            UpdateVisual(element);
        }
    }

    private static void Element_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        var state = GetState(element);
        state.AnimationGeneration++;
        state.IsAnimating = false;
        state.DetachItemState();
        ResetVisual(element);
    }

    private static void UpdateVisual(FrameworkElement element)
    {
        var state = GetState(element);
        if (state.IsPreparing)
            return;

        if (state.ItemState is not { } itemState)
        {
            ResetVisual(element);
            return;
        }

        if (!element.IsLoaded && itemState.ShouldPlayEnterAnimation)
        {
            HoldPendingVisual(element);
            return;
        }

        // 消费播放标记会清除等待标记；播放中的后续状态刷新不能取消动画。
        if (state.IsAnimating && !itemState.ShouldPlayEnterAnimation)
            return;

        if (!itemState.ShouldPlayEnterAnimation)
        {
            if (itemState.IsEntranceAnimationPending)
                HoldPendingVisual(element);
            else
                ResetVisual(element);
            return;
        }

        Play(element, state, itemState);
    }

    private static void Play(
        FrameworkElement element,
        AnimationState state,
        VirtualizedListItemState itemState)
    {
        var delay = ListEntranceAnimationDefaults.GetStaggerDelay(itemState.EnterAnimationIndex);
        var duration = ListEntranceAnimationDefaults.Duration;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleTransform = new ScaleTransform(
            ListEntranceAnimationDefaults.StartScale,
            ListEntranceAnimationDefaults.StartScale);
        var translateTransform = new TranslateTransform(0, ListEntranceAnimationDefaults.OffsetY);

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.RenderTransform = new TransformGroup
        {
            Children =
            {
                scaleTransform,
                translateTransform
            }
        };
        element.Opacity = 0;

        state.IsAnimating = true;
        var generation = ++state.AnimationGeneration;

        // 直接消费共享状态，避免双向绑定回写在动画开始前重置视觉。
        state.IsPreparing = true;
        try
        {
            itemState.ShouldPlayEnterAnimation = false;
        }
        finally
        {
            state.IsPreparing = false;
        }

        element.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => BeginAnimation(element, state, generation, scaleTransform, translateTransform, delay, duration, easing));
    }

    private static void BeginAnimation(
        FrameworkElement element,
        AnimationState state,
        int generation,
        ScaleTransform scaleTransform,
        TranslateTransform translateTransform,
        TimeSpan delay,
        TimeSpan duration,
        IEasingFunction easing)
    {
        if (!element.IsLoaded || generation != state.AnimationGeneration)
            return;

        var fade = new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            if (generation != state.AnimationGeneration)
                return;

            state.IsAnimating = false;
            ResetVisual(element);
        };
        element.BeginAnimation(
            UIElement.OpacityProperty,
            fade);
        scaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(ListEntranceAnimationDefaults.StartScale, 1, duration) { BeginTime = delay, EasingFunction = easing });
        scaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(ListEntranceAnimationDefaults.StartScale, 1, duration) { BeginTime = delay, EasingFunction = easing });
        translateTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(ListEntranceAnimationDefaults.OffsetY, 0, duration) { BeginTime = delay, EasingFunction = easing });
    }

    private static void HoldPendingVisual(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
        element.RenderTransform = null;
    }

    private static void ResetVisual(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.RenderTransform = null;
    }

    private static AnimationState GetState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is AnimationState state)
            return state;

        state = new AnimationState(element);
        element.SetValue(StateProperty, state);
        return state;
    }

    private sealed class AnimationState
    {
        private readonly FrameworkElement element;

        public AnimationState(FrameworkElement element)
        {
            this.element = element;
        }

        public VirtualizedListItemState? ItemState { get; set; }

        public bool IsPreparing { get; set; }

        public bool IsAnimating { get; set; }

        public int AnimationGeneration { get; set; }

        public void ItemStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateVisual(element);
        }

        public void DetachItemState()
        {
            if (ItemState is not null)
                ItemState.PropertyChanged -= ItemStatePropertyChanged;
            ItemState = null;
        }
    }
}
