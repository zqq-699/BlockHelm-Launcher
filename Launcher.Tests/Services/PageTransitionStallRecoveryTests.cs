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
using Launcher.App.Services;

namespace Launcher.Tests.Services;

/// <summary>
/// 窗口被最小化或完全遮挡时 WPF 会停止出帧：CompositionTarget.Rendering 不再触发，
/// 动画时钟也可能不推进。过渡必须仍然收得了尾，否则页面会停在几乎不可见的预热透明度，
/// 过渡闸门也一直不 Exit，所有可延后的工作都要被多拖一个 MaximumDeferral。
/// 不显示窗口就能复现这个场景——没有渲染目标，合成帧一帧都不会来。
/// </summary>
[Collection(TransitionRenderingTestCollection.Name)]
public sealed class PageTransitionStallRecoveryTests
{
    [Fact]
    public void TransitionForceCompletesWhenNoCompositionFrameEverArrives()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            var target = new Border { Width = 320d, Height = 200d };
            var host = new Grid();
            host.Children.Add(target);
            var service = new PageTransitionService(
                Dispatcher.CurrentDispatcher,
                page => page == "Settings" ? target : null,
                "Home",
                ["Home", "Settings"],
                TransitionRenderCacheScope.TryAcquire,
                new SilentCompositionFrameSource(),
                compositionWaitTimeout: TimeSpan.FromMilliseconds(10),
                transitionWatchdogTimeout: TimeSpan.FromMilliseconds(75));

            service.MoveTo("Settings");

            // 预热阶段页面几乎全透明；卡在这里就是用户看到的"页面不见了"。
            Assert.Equal(PageTransitionService.WarmupOpacity, target.Opacity);
            Assert.True(UiTransitionGate.IsTransitionActive);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (UiTransitionGate.IsTransitionActive && DateTime.UtcNow < deadline)
                PumpDispatcher(DispatcherPriority.ContextIdle);

            Assert.False(UiTransitionGate.IsTransitionActive);
            Assert.Equal(1d, target.Opacity);
            Assert.Equal(0d, Assert.IsType<TranslateTransform>(target.RenderTransform).Y);

            UiTransitionGate.ResetForTesting();
        });
    }

    /// <summary>一帧都不来的合成源，等价于窗口被最小化或完全遮挡。</summary>
    private sealed class SilentCompositionFrameSource : ICompositionFrameSource
    {
        public void Subscribe(EventHandler handler)
        {
        }

        public void Unsubscribe(EventHandler handler)
        {
        }
    }

    private static void PumpDispatcher(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
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
