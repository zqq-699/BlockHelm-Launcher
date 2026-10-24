/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Launcher.App.Behaviors;

namespace Launcher.Tests.Behaviors;

public sealed class ListPageEntranceAnimationBehaviorTests
{
    [Fact]
    public void SharedItemStateStartsScaleTranslateAndFadeAnimationWithoutResettingIt()
    {
        RunOnStaThread(() =>
        {
            var button = new Button
            {
                Width = 240,
                Height = 32,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var itemState = new VirtualizedListItemState
            {
                IsEntranceAnimationPending = true,
                EnterAnimationIndex = 0
            };
            ListPageEntranceAnimationBehavior.SetIsEnabled(button, true);
            ListPageEntranceAnimationBehavior.SetItemState(button, itemState);
            itemState.ShouldPlayEnterAnimation = true;
            Assert.True(itemState.ShouldPlayEnterAnimation);

            var window = new Window
            {
                Width = 320,
                Height = 120,
                Content = button,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0
            };

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.ApplicationIdle);

                var transforms = Assert.IsType<TransformGroup>(button.RenderTransform);
                var scale = Assert.IsType<ScaleTransform>(transforms.Children[0]);
                var translate = Assert.IsType<TranslateTransform>(transforms.Children[1]);
                // 列表刷新会再次发布等待状态；它不能中断已经开始的淡入与缩放。
                itemState.IsEntranceAnimationPending = true;
                itemState.IsEntranceAnimationPending = false;
                Assert.True(button.HasAnimatedProperties);
                Assert.True(scale.HasAnimatedProperties);
                Assert.True(translate.HasAnimatedProperties);
                Assert.Same(transforms, button.RenderTransform);
                Assert.True(button.Opacity < 1);
                Assert.False(itemState.ShouldPlayEnterAnimation);
                Assert.False(itemState.IsEntranceAnimationPending);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void PumpDispatcher(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            priority,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
