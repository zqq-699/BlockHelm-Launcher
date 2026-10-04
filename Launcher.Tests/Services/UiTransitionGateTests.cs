/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Launcher.App.Services;

namespace Launcher.Tests.Services;

// 闸门是进程级静态状态，而过渡相关的测试会通过 PageTransitionService 一起碰它，
// 因此必须和它们串行跑，否则彼此的 Enter/Exit 会互相干扰。
[Collection(TransitionRenderingTestCollection.Name)]
public sealed class UiTransitionGateTests
{
    /// <summary>过渡进行中，可延后的工作必须一件都不执行——那正是掉帧的来源。</summary>
    [Fact]
    public void WorkIsHeldBackWhileATransitionIsActive()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            var ran = 0;
            UiTransitionGate.Enter();

            UiTransitionGate.RunWhenIdle(() => ran++);
            Pump();

            Assert.Equal(0, ran);
            Assert.Equal(1, UiTransitionGate.PendingCount);

            UiTransitionGate.Exit();
            Pump();

            Assert.Equal(1, ran);
            Assert.Equal(0, UiTransitionGate.PendingCount);
        });
    }

    /// <summary>页面切换与页内分区切换会重叠，因此闸门必须按计数配对，不能一进一出就放行。</summary>
    [Fact]
    public void NestedTransitionsOnlyReleaseAfterTheOutermostOneEnds()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            var ran = 0;
            UiTransitionGate.Enter();
            UiTransitionGate.Enter();
            UiTransitionGate.RunWhenIdle(() => ran++);

            UiTransitionGate.Exit();
            Pump();
            Assert.Equal(0, ran);

            UiTransitionGate.Exit();
            Pump();
            Assert.Equal(1, ran);
        });
    }

    /// <summary>没有过渡时不得推迟，否则普通路径会平白多等一轮调度。</summary>
    [Fact]
    public void WorkRunsPromptlyWhenNoTransitionIsActive()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            var ran = 0;

            UiTransitionGate.RunWhenIdle(() => ran++);
            Pump();

            Assert.Equal(1, ran);
        });
    }

    /// <summary>Exit 多于 Enter 时不能把计数压成负数，否则后续过渡将永远拦不住。</summary>
    [Fact]
    public void UnbalancedExitDoesNotBreakLaterTransitions()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            UiTransitionGate.Exit();
            UiTransitionGate.Exit();

            var ran = 0;
            UiTransitionGate.Enter();
            UiTransitionGate.RunWhenIdle(() => ran++);
            Pump();

            Assert.Equal(0, ran);
            UiTransitionGate.Exit();
            Pump();
            Assert.Equal(1, ran);
        });
    }

    /// <summary>
    /// 连续快切时，上一次过渡放出来的工作不能砸进下一次动画——实测出现过 20.6ms 的掉帧。
    /// </summary>
    [Fact]
    public void ReleasedWorkIsHeldAgainIfANewTransitionAlreadyStarted()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            var ran = 0;
            UiTransitionGate.Enter();
            UiTransitionGate.RunWhenIdle(() => ran++);

            // 第一次过渡结束，但排队的活还没轮到执行时，下一次过渡就开始了。
            UiTransitionGate.Exit();
            UiTransitionGate.Enter();
            Pump();

            Assert.Equal(0, ran);

            UiTransitionGate.Exit();
            Pump();

            Assert.Equal(1, ran);
        });
    }

    /// <summary>
    /// 有 await 契约的加载路径靠它推迟数据落地：过渡期间不得放行，结束后必须放行。
    /// </summary>
    [Fact]
    public void WaitForIdleCompletesOnlyAfterTheTransitionEnds()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            UiTransitionGate.Enter();

            var wait = UiTransitionGate.WaitForIdleAsync();
            Pump();
            Assert.False(wait.IsCompleted);

            UiTransitionGate.Exit();
            PumpUntil(wait);

            Assert.True(wait.IsCompleted);
        });
    }

    /// <summary>
    /// 等待返回后续体是排队执行的，轮到它时新的过渡可能已经开始。
    /// 不重新判断就会把数据落地又送回动画里——实测 92ms 的视口初始化正是这样漏回去的。
    /// </summary>
    [Fact]
    public void WaitForIdleKeepsWaitingWhenANewTransitionStartsBeforeItResumes()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            UiTransitionGate.Enter();
            var wait = UiTransitionGate.WaitForIdleAsync();

            // 第一次过渡结束，但在续体轮到执行之前，下一次过渡就开始了。
            UiTransitionGate.Exit();
            UiTransitionGate.Enter();
            Pump();

            Assert.False(wait.IsCompleted);

            UiTransitionGate.Exit();
            PumpUntil(wait);

            Assert.True(wait.IsCompleted);
        });
    }

    /// <summary>没有过渡时必须立刻返回，否则每次加载都平白多等一轮调度。</summary>
    [Fact]
    public void WaitForIdleReturnsImmediatelyWhenNoTransitionIsActive()
    {
        RunOnStaThread(() =>
        {
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher);
            Assert.True(UiTransitionGate.WaitForIdleAsync().IsCompleted);
        });
    }

    /// <summary>
    /// 连续切页时不能把工作无限期饿死。掉几帧是体验问题，工作永远不执行是正确性问题——
    /// 被推迟的里面有启动失败上报、保存完成通知这类不能丢的东西。
    /// </summary>
    [Fact]
    public void WorkIsReleasedOnceItsDeadlinePassesEvenWhileTransitionsContinue()
    {
        RunOnStaThread(() =>
        {
            var now = DateTime.UtcNow;
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher, () => now);
            var ran = 0;
            UiTransitionGate.Enter();
            UiTransitionGate.RunWhenIdle(() => ran++);

            UiTransitionGate.Exit();
            UiTransitionGate.Enter();
            Pump();
            now += UiTransitionGate.MaximumDeferral;
            UiTransitionGate.ReleaseExpiredActionsForTesting();
            Pump();

            Assert.Equal(1, ran);
        });
    }

    /// <summary>
    /// 排队的工作原本只有 Exit 会去唤醒。窗口最小化后 WPF 停止出帧，动画走不完，
    /// Exit 就永远不来——此时截止时间必须仍然生效，否则队列里的工作会被永久丢弃。
    /// </summary>
    [Fact]
    public void WorkIsReleasedOnceItsDeadlinePassesEvenIfTheTransitionNeverEnds()
    {
        RunOnStaThread(() =>
        {
            var now = DateTime.UtcNow;
            UiTransitionGate.ResetForTesting(Dispatcher.CurrentDispatcher, () => now);
            var ran = 0;
            UiTransitionGate.Enter();
            UiTransitionGate.RunWhenIdle(() => ran++);
            Pump();
            Assert.Equal(0, ran);

            // 刻意一次 Exit 都不调用，只推进测试时钟并触发一次巡检。
            now += UiTransitionGate.MaximumDeferral;
            UiTransitionGate.ReleaseExpiredActionsForTesting();
            Pump();

            Assert.Equal(1, ran);
            Assert.Equal(0, UiTransitionGate.PendingCount);
            Assert.True(UiTransitionGate.IsTransitionActive);

            UiTransitionGate.ResetForTesting();
        });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(1))
        {
            Pump();
            Thread.Yield();
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
