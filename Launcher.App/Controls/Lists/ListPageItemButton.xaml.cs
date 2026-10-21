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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.App.Behaviors;

namespace Launcher.App.Controls;

/// <summary>
/// 列表页通用条目容器，统一解析图标、悬停反馈、尾部内容显隐和虚拟化进场动画。
/// </summary>
public partial class ListPageItemButton : UserControl
{
    // 大量依赖属性用于让同一控件服务不同列表；行为属性与纯外观属性分开由模板消费。
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ListPageItemButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ListPageItemButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TrailingTextProperty =
        DependencyProperty.Register(nameof(TrailingText), typeof(string), typeof(ListPageItemButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TrailingContentProperty =
        DependencyProperty.Register(nameof(TrailingContent), typeof(object), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleTrailingContentProperty =
        DependencyProperty.Register(nameof(TitleTrailingContent), typeof(object), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IconSourceProperty =
        DependencyProperty.Register(
            nameof(IconSource),
            typeof(object),
            typeof(ListPageItemButton),
            new PropertyMetadata(null, OnIconSourceChanged));

    private static readonly DependencyPropertyKey ResolvedIconSourcePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedIconSource),
            typeof(ImageSource),
            typeof(ListPageItemButton),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ResolvedIconSourceProperty =
        ResolvedIconSourcePropertyKey.DependencyProperty;

    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.Register(nameof(IconKey), typeof(string), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ListPageItemButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsFirstVisibleProperty =
        DependencyProperty.Register(nameof(IsFirstVisible), typeof(bool), typeof(ListPageItemButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsLastVisibleProperty =
        DependencyProperty.Register(nameof(IsLastVisible), typeof(bool), typeof(ListPageItemButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsPreviousItemHighlightedProperty =
        DependencyProperty.Register(nameof(IsPreviousItemHighlighted), typeof(bool), typeof(ListPageItemButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsPointerOverOptionProperty =
        DependencyProperty.Register(nameof(IsPointerOverOption), typeof(bool), typeof(ListPageItemButton), new PropertyMetadata(false));

    public static readonly DependencyProperty ShouldPlayEnterAnimationProperty =
        DependencyProperty.Register(
            nameof(ShouldPlayEnterAnimation),
            typeof(bool),
            typeof(ListPageItemButton),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnShouldPlayEnterAnimationChanged));

    public static readonly DependencyProperty IsEnterAnimationPendingProperty =
        DependencyProperty.Register(
            nameof(IsEnterAnimationPending),
            typeof(bool),
            typeof(ListPageItemButton),
            new PropertyMetadata(false, OnIsEnterAnimationPendingChanged));

    public static readonly DependencyProperty EnterAnimationIndexProperty =
        DependencyProperty.Register(nameof(EnterAnimationIndex), typeof(int), typeof(ListPageItemButton), new PropertyMetadata(0));

    public static readonly DependencyProperty ItemMarginProperty =
        DependencyProperty.Register(nameof(ItemMargin), typeof(Thickness), typeof(ListPageItemButton), new PropertyMetadata(new Thickness(0, 0, 12, 0)));

    public static readonly DependencyProperty IconColumnWidthProperty =
        DependencyProperty.Register(nameof(IconColumnWidth), typeof(GridLength), typeof(ListPageItemButton), new PropertyMetadata(new GridLength(50)));

    public static readonly DependencyProperty IconWidthProperty =
        DependencyProperty.Register(nameof(IconWidth), typeof(double), typeof(ListPageItemButton), new PropertyMetadata(32d));

    public static readonly DependencyProperty IconHeightProperty =
        DependencyProperty.Register(nameof(IconHeight), typeof(double), typeof(ListPageItemButton), new PropertyMetadata(32d));

    public static readonly DependencyProperty IconMarginProperty =
        DependencyProperty.Register(nameof(IconMargin), typeof(Thickness), typeof(ListPageItemButton), new PropertyMetadata(new Thickness(10, 0, 0, 0)));

    public static readonly DependencyProperty IconScalingModeProperty =
        DependencyProperty.Register(nameof(IconScalingMode), typeof(BitmapScalingMode), typeof(ListPageItemButton), new PropertyMetadata(BitmapScalingMode.HighQuality));

    public static readonly DependencyProperty TextMarginProperty =
        DependencyProperty.Register(nameof(TextMargin), typeof(Thickness), typeof(ListPageItemButton), new PropertyMetadata(new Thickness(5, 0, 0, 0)));

    public static readonly DependencyProperty TrailingMarginProperty =
        DependencyProperty.Register(nameof(TrailingMargin), typeof(Thickness), typeof(ListPageItemButton), new PropertyMetadata(new Thickness(12, 0, 24, 0)));

    public static readonly DependencyProperty TitleFontSizeProperty =
        DependencyProperty.Register(nameof(TitleFontSize), typeof(double), typeof(ListPageItemButton), new PropertyMetadata(15d));

    public static readonly DependencyProperty TitleFontWeightProperty =
        DependencyProperty.Register(nameof(TitleFontWeight), typeof(FontWeight), typeof(ListPageItemButton), new PropertyMetadata(FontWeights.SemiBold));

    public static readonly DependencyProperty TitleForegroundProperty =
        DependencyProperty.Register(nameof(TitleForeground), typeof(Brush), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty SubtitleFontSizeProperty =
        DependencyProperty.Register(nameof(SubtitleFontSize), typeof(double), typeof(ListPageItemButton), new PropertyMetadata(11d));

    public static readonly DependencyProperty SubtitleForegroundProperty =
        DependencyProperty.Register(nameof(SubtitleForeground), typeof(Brush), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IconOpacityProperty =
        DependencyProperty.Register(nameof(IconOpacity), typeof(double), typeof(ListPageItemButton), new PropertyMetadata(1d));

    public static readonly DependencyProperty IconOverlayKeyProperty =
        DependencyProperty.Register(nameof(IconOverlayKey), typeof(string), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IconOverlayForegroundProperty =
        DependencyProperty.Register(nameof(IconOverlayForeground), typeof(Brush), typeof(ListPageItemButton), new PropertyMetadata(null));

    public static readonly DependencyProperty TrailingFontSizeProperty =
        DependencyProperty.Register(nameof(TrailingFontSize), typeof(double), typeof(ListPageItemButton), new PropertyMetadata(12d));

    public static readonly DependencyProperty TrailingForegroundProperty =
        DependencyProperty.Register(nameof(TrailingForeground), typeof(Brush), typeof(ListPageItemButton), new PropertyMetadata(null));

    public ListPageItemButton()
    {
        InitializeComponent();
        // Loaded 时虚拟化容器已获得本轮状态，构造阶段直接检查会误用默认值。
        Loaded += (_, _) => PlayEnterAnimationIfNeeded();
    }

    public Button InnerButton => PART_Button;

    private bool isPreparingEnterAnimation;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string TrailingText
    {
        get => (string)GetValue(TrailingTextProperty);
        set => SetValue(TrailingTextProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    public object? TitleTrailingContent
    {
        get => GetValue(TitleTrailingContentProperty);
        set => SetValue(TitleTrailingContentProperty, value);
    }

    public object? IconSource
    {
        get => GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public ImageSource? ResolvedIconSource
    {
        get => (ImageSource?)GetValue(ResolvedIconSourceProperty);
        private set => SetValue(ResolvedIconSourcePropertyKey, value);
    }

    public string? IconKey
    {
        get => (string?)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool IsFirstVisible
    {
        get => (bool)GetValue(IsFirstVisibleProperty);
        set => SetValue(IsFirstVisibleProperty, value);
    }

    public bool IsLastVisible
    {
        get => (bool)GetValue(IsLastVisibleProperty);
        set => SetValue(IsLastVisibleProperty, value);
    }

    public bool IsPreviousItemHighlighted
    {
        get => (bool)GetValue(IsPreviousItemHighlightedProperty);
        set => SetValue(IsPreviousItemHighlightedProperty, value);
    }

    public bool IsPointerOverOption
    {
        get => (bool)GetValue(IsPointerOverOptionProperty);
        set => SetValue(IsPointerOverOptionProperty, value);
    }

    public bool ShouldPlayEnterAnimation
    {
        get => (bool)GetValue(ShouldPlayEnterAnimationProperty);
        set => SetValue(ShouldPlayEnterAnimationProperty, value);
    }

    public bool IsEnterAnimationPending
    {
        get => (bool)GetValue(IsEnterAnimationPendingProperty);
        set => SetValue(IsEnterAnimationPendingProperty, value);
    }

    public int EnterAnimationIndex
    {
        get => (int)GetValue(EnterAnimationIndexProperty);
        set => SetValue(EnterAnimationIndexProperty, value);
    }

    public Thickness ItemMargin
    {
        get => (Thickness)GetValue(ItemMarginProperty);
        set => SetValue(ItemMarginProperty, value);
    }

    public GridLength IconColumnWidth
    {
        get => (GridLength)GetValue(IconColumnWidthProperty);
        set => SetValue(IconColumnWidthProperty, value);
    }

    public double IconWidth
    {
        get => (double)GetValue(IconWidthProperty);
        set => SetValue(IconWidthProperty, value);
    }

    public double IconHeight
    {
        get => (double)GetValue(IconHeightProperty);
        set => SetValue(IconHeightProperty, value);
    }

    public Thickness IconMargin
    {
        get => (Thickness)GetValue(IconMarginProperty);
        set => SetValue(IconMarginProperty, value);
    }

    public BitmapScalingMode IconScalingMode
    {
        get => (BitmapScalingMode)GetValue(IconScalingModeProperty);
        set => SetValue(IconScalingModeProperty, value);
    }

    public Thickness TextMargin
    {
        get => (Thickness)GetValue(TextMarginProperty);
        set => SetValue(TextMarginProperty, value);
    }

    public Thickness TrailingMargin
    {
        get => (Thickness)GetValue(TrailingMarginProperty);
        set => SetValue(TrailingMarginProperty, value);
    }

    public double TitleFontSize
    {
        get => (double)GetValue(TitleFontSizeProperty);
        set => SetValue(TitleFontSizeProperty, value);
    }

    public FontWeight TitleFontWeight
    {
        get => (FontWeight)GetValue(TitleFontWeightProperty);
        set => SetValue(TitleFontWeightProperty, value);
    }

    public Brush TitleForeground
    {
        get => (Brush)GetValue(TitleForegroundProperty);
        set => SetValue(TitleForegroundProperty, value);
    }

    public double SubtitleFontSize
    {
        get => (double)GetValue(SubtitleFontSizeProperty);
        set => SetValue(SubtitleFontSizeProperty, value);
    }

    public Brush SubtitleForeground
    {
        get => (Brush)GetValue(SubtitleForegroundProperty);
        set => SetValue(SubtitleForegroundProperty, value);
    }

    public double IconOpacity
    {
        get => (double)GetValue(IconOpacityProperty);
        set => SetValue(IconOpacityProperty, value);
    }

    public string? IconOverlayKey
    {
        get => (string?)GetValue(IconOverlayKeyProperty);
        set => SetValue(IconOverlayKeyProperty, value);
    }

    public Brush IconOverlayForeground
    {
        get => (Brush)GetValue(IconOverlayForegroundProperty);
        set => SetValue(IconOverlayForegroundProperty, value);
    }

    public double TrailingFontSize
    {
        get => (double)GetValue(TrailingFontSizeProperty);
        set => SetValue(TrailingFontSizeProperty, value);
    }

    public Brush TrailingForeground
    {
        get => (Brush)GetValue(TrailingForegroundProperty);
        set => SetValue(TrailingForegroundProperty, value);
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        // 悬停与进场动画是两套生命周期；这里只切换交互装饰和可操作的尾部内容。
        IsPointerOverOption = true;
        OptionHoverBehavior.SetIsExternalActive(PART_Button, true);

        if (TrailingContent is null)
            return;

        AnimateTrailingVisibility(0, 1, TimeSpan.FromMilliseconds(140));
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        IsPointerOverOption = false;
        OptionHoverBehavior.SetIsExternalActive(PART_Button, false);

        if (TrailingContent is null)
            return;

        AnimateTrailingVisibility(1, 0, TimeSpan.FromMilliseconds(180));
    }

    private void PlayEnterAnimationIfNeeded()
    {
        // Recycling 会复用控件，是否播放由数据身份对应的 VirtualizedListItemState 决定。
        if (!ShouldPlayEnterAnimation)
        {
            if (IsEnterAnimationPending)
            {
                HoldEntrancePendingVisual();
                return;
            }

            ResetVisual();
            return;
        }

        // 延迟设置上限，长列表只为首批可见项形成阶梯，不让后续条目等待过久。
        var delay = TimeSpan.FromMilliseconds(Math.Min(EnterAnimationIndex, 12) * 30);
        var duration = TimeSpan.FromMilliseconds(330);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var scaleTransform = new ScaleTransform(0.96, 0.96);
        var translateTransform = new TranslateTransform(0, 14);
        AnimatedRoot.BeginAnimation(OpacityProperty, null);
        AnimatedRoot.RenderTransform = new TransformGroup
        {
            Children =
            {
                scaleTransform,
                translateTransform
            }
        };

        AnimatedRoot.Opacity = 0;

        // 开播前消费一次性标记；更新源时会触发依赖属性回调，抑制标记防止其立即重置视觉。
        isPreparingEnterAnimation = true;
        try
        {
            var shouldPlayEnterAnimationBinding = BindingOperations.GetBindingExpression(this, ShouldPlayEnterAnimationProperty);
            if (shouldPlayEnterAnimationBinding?.ResolvedSource is VirtualizedListItemState state
                && string.Equals(
                    shouldPlayEnterAnimationBinding.ResolvedSourcePropertyName,
                    nameof(VirtualizedListItemState.ShouldPlayEnterAnimation),
                    StringComparison.Ordinal))
            {
                state.ShouldPlayEnterAnimation = false;
            }
            else
            {
                SetCurrentValue(ShouldPlayEnterAnimationProperty, false);
                shouldPlayEnterAnimationBinding?.UpdateSource();
            }

            ClearValue(IsEnterAnimationPendingProperty);
        }
        finally
        {
            isPreparingEnterAnimation = false;
        }

        // 让初始透明度先提交一帧，再开始动画，否则 WPF 可能直接呈现终值。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => BeginEnterAnimation(
                scaleTransform,
                translateTransform,
                delay,
                duration,
                easing));
    }

    private static void OnIconSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 模板只绑定 ImageSource；兼容资源对象和路径的解析集中在加载器中完成。
        if (d is ListPageItemButton button)
            button.ResolvedIconSource = IconSourceImageLoader.TryLoad(e.NewValue);
    }

    private static void OnShouldPlayEnterAnimationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ListPageItemButton { IsLoaded: true } button && e.NewValue is true)
            button.PlayEnterAnimationIfNeeded();
    }

    private static void OnIsEnterAnimationPendingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListPageItemButton button)
            return;

        if (e.NewValue is true)
        {
            button.HoldEntrancePendingVisual();
            return;
        }

        if (!button.isPreparingEnterAnimation && !button.ShouldPlayEnterAnimation && button.IsLoaded)
            button.ResetVisual();
    }

    private void HoldEntrancePendingVisual()
    {
        // 容器已经生成但尚未轮到动画时保持隐藏，避免滚动或刷新时闪现一帧。
        AnimatedRoot.BeginAnimation(OpacityProperty, null);
        AnimatedRoot.Opacity = 0;
        AnimatedRoot.RenderTransform = null;
        TrailingContentPresenter.BeginAnimation(OpacityProperty, null);
    }

    private void ResetVisual()
    {
        // 容器回收后恢复无动画稳定值，不能把旧条目的 Transform 带给新数据项。
        AnimatedRoot.BeginAnimation(OpacityProperty, null);
        AnimatedRoot.Opacity = 1;
        AnimatedRoot.RenderTransform = null;
        TrailingContentPresenter.BeginAnimation(OpacityProperty, null);
    }

    private void BeginEnterAnimation(
        ScaleTransform scaleTransform,
        TranslateTransform translateTransform,
        TimeSpan delay,
        TimeSpan duration,
        IEasingFunction easing)
    {
        // 延迟期间容器可能已被回收或卸载，此时不再向离开视觉树的对象挂动画时钟。
        if (!AnimatedRoot.IsLoaded)
            return;

        AnimatedRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            });

        scaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, 1, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            });

        scaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            });

        translateTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing
            });
    }

    private void AnimateTrailingVisibility(double trailingTextOpacity, double trailingContentOpacity, TimeSpan duration)
    {
        // 文本提示与任意操作内容反向淡入淡出，布局始终保留以避免悬停时宽度跳变。
        TrailingTextBlock.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(trailingTextOpacity, duration));
        TrailingContentPresenter.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(trailingContentOpacity, duration));
    }
}
