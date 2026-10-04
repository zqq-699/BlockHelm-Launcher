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

using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.App.Controls;
using Launcher.App.Diagnostics;
using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>
/// 合成帧来源。抽成接缝只为一件事：让测试能复现"窗口不出帧"。
/// 那正是预热兜底存在的理由，而单元测试环境里 <see cref="CompositionTarget.Rendering"/>
/// 照常触发，无法自然复现，兜底逻辑会一直处于没人验证的状态。
/// </summary>
internal interface ICompositionFrameSource
{
    void Subscribe(EventHandler handler);

    void Unsubscribe(EventHandler handler);
}

internal sealed class CompositionTargetFrameSource : ICompositionFrameSource
{
    internal static CompositionTargetFrameSource Instance { get; } = new();

    public void Subscribe(EventHandler handler) => CompositionTarget.Rendering += handler;

    public void Unsubscribe(EventHandler handler) => CompositionTarget.Rendering -= handler;
}

public sealed class PageTransitionService
{
    internal const double TransitionOffset = 22;

    internal static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(240);

    /// <summary>
    /// 主菜单页面的默认顺序。直接取自 <see cref="NavigationCatalog.PageOrder"/>，
    /// 免得这里的副本和侧边栏实际顺序悄悄分家——一旦漏页，缺的那页在
    /// <see cref="IndexOfPage"/> 里拿到 -1，进出方向就都会退化成默认的自下而上。
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultPageOrder = NavigationCatalog.PageOrder;

    /// <summary>
    /// 预热阶段使用的透明度。必须严格大于 0：完全透明的子树会被渲染层剔除，
    /// 首次栅格化就会推迟到淡入动画把透明度抬离 0 的那一帧，正是要避免的情况。
    /// 取 8 位色深下的最小一档 alpha：既不为零，合成结果也只有 1/255，肉眼不可见。
    /// </summary>
    internal const double WarmupOpacity = 1d / 255d;

    /// <summary>
    /// 每一段等待的合成帧数。启动动画前一共等两段：第一段让新页面完成布局与首次栅格化，
    /// 装上位图缓存后再等第二段，让页面被画进缓存纹理。等待期间页面处于 WarmupOpacity，不可见。
    /// 在 60Hz 显示器上两段合计约 33ms，这是动画启动延迟与首帧卡顿之间的取舍。
    /// </summary>
    private const int WarmupCompositionFrames = 1;

    /// <summary>
    /// 单段预热等待合成帧的上限。窗口被最小化或完全遮挡时 WPF 会停止出帧，
    /// <see cref="CompositionTarget.Rendering"/> 也就不再触发，预热会永远等不到回调。
    /// 到点直接放行：此时预热已经没有意义，但动画必须照常起步。
    /// </summary>
    private static readonly TimeSpan CompositionWaitTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 一次过渡从发起到收尾的上限。不出帧时动画时钟也可能不推进，Completed 就永远不来，
    /// 页面会停在几乎透明的 <see cref="WarmupOpacity"/>，闸门也一直不 Exit——
    /// 后者会让所有可延后的工作都多等一个 <see cref="UiTransitionGate.MaximumDeferral"/>。
    /// 预热两段兜底加动画时长约 740ms，取 2 秒留足余量。
    /// </summary>
    private static readonly TimeSpan TransitionWatchdogTimeout = TimeSpan.FromSeconds(2);

    private readonly Dispatcher dispatcher;
    private readonly Func<string, FrameworkElement?> resolvePageRoot;
    private readonly Func<IReadOnlyList<string>> resolvePageOrder;
    private readonly TransitionRenderCacheFactory renderCacheFactory;
    private readonly ICompositionFrameSource compositionFrames;
    private readonly TimeSpan compositionWaitTimeoutValue;
    private readonly TimeSpan transitionWatchdogTimeoutValue;
    private string? currentPage;
    private int transitionToken;
    private IDisposable? blurRefreshLease;
    private TransitionRenderCacheScope? renderCacheScope;
    private UiInteractionScope? interactionScope;
    private FrameworkElement? activeTarget;
    private EventHandler? pendingCompositionWait;
    private DispatcherTimer? compositionWaitTimeout;
    private DispatcherTimer? transitionWatchdog;
    private long warmupStartedAtTimestamp;
    private DispatcherBusyProbe? warmupBusyProbe;
    private bool hasEnteredTransitionGate;

    public PageTransitionService(
        Dispatcher dispatcher,
        Func<string, FrameworkElement?> resolvePageRoot,
        string? initialPage)
        : this(dispatcher, resolvePageRoot, initialPage, (IReadOnlyList<string>?)null)
    {
    }

    public PageTransitionService(
        Dispatcher dispatcher,
        Func<string, FrameworkElement?> resolvePageRoot,
        string? initialPage,
        IReadOnlyList<string>? pageOrder)
        : this(dispatcher, resolvePageRoot, initialPage, pageOrder, TransitionRenderCacheScope.TryAcquire)
    {
    }

    /// <summary>
    /// 顺序在运行时会变的调用方走这里：每次过渡都重新取一遍。
    /// 账户列表就是这样——条目随增删改序，构造时的固定快照会让方向停在那一刻。
    /// </summary>
    public static PageTransitionService CreateWithDynamicOrder(
        Dispatcher dispatcher,
        Func<string, FrameworkElement?> resolvePageRoot,
        string? initialPage,
        Func<IReadOnlyList<string>> resolvePageOrder) =>
        new(
            dispatcher,
            resolvePageRoot,
            initialPage,
            resolvePageOrder,
            TransitionRenderCacheScope.TryAcquire,
            compositionFrames: null);

    internal PageTransitionService(
        Dispatcher dispatcher,
        Func<string, FrameworkElement?> resolvePageRoot,
        string? initialPage,
        IReadOnlyList<string>? pageOrder,
        TransitionRenderCacheFactory renderCacheFactory,
        ICompositionFrameSource? compositionFrames = null,
        TimeSpan? compositionWaitTimeout = null,
        TimeSpan? transitionWatchdogTimeout = null)
        : this(
            dispatcher,
            resolvePageRoot,
            initialPage,
            () => pageOrder is { Count: > 0 } ? pageOrder : DefaultPageOrder,
            renderCacheFactory,
            compositionFrames,
            compositionWaitTimeout,
            transitionWatchdogTimeout)
    {
    }

    private PageTransitionService(
        Dispatcher dispatcher,
        Func<string, FrameworkElement?> resolvePageRoot,
        string? initialPage,
        Func<IReadOnlyList<string>> resolvePageOrder,
        TransitionRenderCacheFactory renderCacheFactory,
        ICompositionFrameSource? compositionFrames,
        TimeSpan? compositionWaitTimeout = null,
        TimeSpan? transitionWatchdogTimeout = null)
    {
        this.dispatcher = dispatcher;
        UiTransitionGate.AttachDispatcher(dispatcher);
        this.resolvePageRoot = resolvePageRoot;
        this.resolvePageOrder = resolvePageOrder;
        this.renderCacheFactory = renderCacheFactory;
        this.compositionFrames = compositionFrames ?? CompositionTargetFrameSource.Instance;
        compositionWaitTimeoutValue = compositionWaitTimeout ?? CompositionWaitTimeout;
        transitionWatchdogTimeoutValue = transitionWatchdogTimeout ?? TransitionWatchdogTimeout;
        if (compositionWaitTimeoutValue <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(compositionWaitTimeout));
        if (transitionWatchdogTimeoutValue <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(transitionWatchdogTimeout));
        currentPage = initialPage;
    }

    public void MoveTo(string newPage)
    {
        if (string.Equals(currentPage, newPage, StringComparison.OrdinalIgnoreCase))
            return;

        CancelActiveTransition(requestFinalRefresh: true);
        var oldPage = currentPage;
        currentPage = newPage;
        var startOffset = GetTransitionStartOffset(oldPage, newPage);

        var target = resolvePageRoot(newPage);
        if (target is null)
            return;

        var token = ++transitionToken;
        EnterTransitionGate();
        StartTransitionWatchdog();
        PreparePageForTransition(target, startOffset);
        activeTarget = target;
        target.Unloaded += ActiveTarget_Unloaded;
        warmupStartedAtTimestamp = Stopwatch.GetTimestamp();
        // 预热窗口发生在交互采样开启之前，其中的 UI 线程占用不会被 UiInteractionScope 统计到。
        // 单独挂一个探针把这段补上，否则无法判断预热是在等渲染线程还是 UI 线程自己在忙。
        ReleaseWarmupBusyProbe();
        if (UiPerformanceLog.IsEnabled)
            warmupBusyProbe = DispatcherBusyProbe.TryAttach(dispatcher);

        // 新页面此前是折叠的，从未被绘制过。等一个 dispatcher 轮次并不能保证渲染线程
        // 已经把它栅格化，动画一起步就会撞上这次整页栅格化——实测长帧正是集中在这里。
        // 因此改为等真实的合成帧：先栅格化一次，再装缓存，再等一帧让缓存被填满，最后才开始动画。
        WaitForCompositionFrames(
            WarmupCompositionFrames,
            () =>
            {
                if (!IsTransitionCurrent(newPage, token))
                    return;

                PrepareRenderPathForTransition(newPage, target);
                WaitForCompositionFrames(
                    WarmupCompositionFrames,
                    () => AnimatePage(newPage, target, startOffset, token));
            });
    }

    private bool IsTransitionCurrent(string page, int token) =>
        token == transitionToken
        && string.Equals(currentPage, page, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 等待指定数量的合成帧后执行回调。订阅期间 WPF 会保持连续渲染，
    /// 因此只用于过渡起步这种短暂等待，并且新的过渡必须取消尚未触发的等待。
    /// 合成帧迟迟不来时由 <see cref="CompositionWaitTimeout"/> 兜底放行。
    /// </summary>
    private void WaitForCompositionFrames(int frameCount, Action continuation)
    {
        CancelPendingCompositionWait();
        var remaining = Math.Max(frameCount, 1);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (--remaining > 0)
                return;

            if (TryCompleteCompositionWait(handler!))
                continuation();
        };

        pendingCompositionWait = handler;
        compositionFrames.Subscribe(handler);

        var timeout = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = compositionWaitTimeoutValue
        };
        timeout.Tick += (_, _) =>
        {
            if (!TryCompleteCompositionWait(handler))
                return;

            // 走到这里说明窗口大概率不在出帧。预热已经没有意义，但动画必须照常起步，
            // 否则页面会停在几乎不可见的预热状态，闸门也不会 Exit。
            Serilog.Log.Debug(
                "Page transition warm-up timed out waiting for a composition frame. TimeoutMs={TimeoutMs}",
                compositionWaitTimeoutValue.TotalMilliseconds);
            continuation();
        };
        compositionWaitTimeout = timeout;
        timeout.Start();
    }

    /// <summary>
    /// 认领本次等待并拆掉两路触发源。合成帧与兜底定时器只能有一方跑赢，
    /// 因此以 <see cref="pendingCompositionWait"/> 作为唯一裁决。
    /// </summary>
    /// <returns>调用方是否该继续执行续体；等待已被新的过渡取代时返回 false。</returns>
    private bool TryCompleteCompositionWait(EventHandler handler)
    {
        compositionFrames.Unsubscribe(handler);
        if (!ReferenceEquals(pendingCompositionWait, handler))
            return false;

        pendingCompositionWait = null;
        StopCompositionWaitTimeout();
        return true;
    }

    private void StopCompositionWaitTimeout()
    {
        compositionWaitTimeout?.Stop();
        compositionWaitTimeout = null;
    }

    /// <summary>
    /// 过渡收尾的兜底。不出帧时动画的 Completed 可能永远不来，收尾里的
    /// 恢复透明度、释放渲染缓存和 Exit 闸门就都不会发生。
    /// </summary>
    private void StartTransitionWatchdog()
    {
        StopTransitionWatchdog();
        var watchdog = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = transitionWatchdogTimeoutValue
        };
        watchdog.Tick += (_, _) =>
        {
            Serilog.Log.Debug(
                "Page transition did not finish in time and was force-completed. Page={Page} TimeoutMs={TimeoutMs}",
                currentPage,
                transitionWatchdogTimeoutValue.TotalMilliseconds);
            // 与正常收尾同一条路径：恢复透明度与位移，释放渲染资源，并配对 Exit 闸门。
            transitionToken++;
            CancelActiveTransition(requestFinalRefresh: true);
        };
        transitionWatchdog = watchdog;
        watchdog.Start();
    }

    private void StopTransitionWatchdog()
    {
        transitionWatchdog?.Stop();
        transitionWatchdog = null;
    }

    private void ReleaseWarmupBusyProbe()
    {
        warmupBusyProbe?.Dispose();
        warmupBusyProbe = null;
    }

    private void CancelPendingCompositionWait()
    {
        StopCompositionWaitTimeout();
        if (pendingCompositionWait is null)
            return;

        compositionFrames.Unsubscribe(pendingCompositionWait);
        pendingCompositionWait = null;
    }

    public void SyncTo(string? page)
    {
        transitionToken++;
        ExitTransitionGate();
        CancelPendingCompositionWait();
        CancelActiveTransition(requestFinalRefresh: true);
        currentPage = page;
    }

    private double GetTransitionStartOffset(string? oldPage, string newPage)
    {
        if (string.IsNullOrWhiteSpace(oldPage))
            return TransitionOffset;

        // 顺序可能每次都不一样，取一份用到底，避免两次查询落在不同的快照上。
        var order = resolvePageOrder() ?? DefaultPageOrder;
        var oldIndex = IndexOfPage(order, oldPage);
        var newIndex = IndexOfPage(order, newPage);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return TransitionOffset;

        return newIndex > oldIndex ? TransitionOffset : -TransitionOffset;
    }

    private static int IndexOfPage(IReadOnlyList<string> order, string page)
    {
        for (var index = 0; index < order.Count; index++)
        {
            if (string.Equals(order[index], page, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static TranslateTransform EnsureTranslateTransform(FrameworkElement target)
    {
        if (target.RenderTransform is TranslateTransform transform)
            return transform;

        transform = new TranslateTransform();
        target.RenderTransform = transform;
        return transform;
    }

    private static void PreparePageForTransition(FrameworkElement target, double startOffset)
    {
        target.BeginAnimation(UIElement.OpacityProperty, null);
        target.Opacity = WarmupOpacity;

        var transform = EnsureTranslateTransform(target);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = startOffset;
    }

    /// <summary>
    /// 选定渲染路径。必须在动画开始前完成，好让接下来的那一帧把页面画进位图缓存，
    /// 而不是把整页栅格化推到动画的头几帧里。
    /// </summary>
    private void PrepareRenderPathForTransition(string page, FrameworkElement target)
    {
        if (BackdropBlurRefreshCoordinator.HasActiveImageBackdropBlur(target))
        {
            blurRefreshLease = BackdropBlurRefreshCoordinator.BeginContinuousRefresh(target);
            return;
        }

        renderCacheScope = renderCacheFactory($"Page:{page}", [target]);
        if (TransitionRenderCacheScope.RequiresContinuousRefreshFallback(
                renderCacheScope.FallbackReason))
        {
            blurRefreshLease = BackdropBlurRefreshCoordinator.BeginContinuousRefresh(target);
        }
    }

    private void AnimatePage(string page, FrameworkElement target, double startOffset, int token)
    {
        if (!IsTransitionCurrent(page, token))
            return;

        var transform = EnsureTranslateTransform(target);
        target.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        target.Opacity = WarmupOpacity;
        transform.Y = startOffset;

        // 采样必须覆盖整段动画，因此在渲染路径确定之后、动画开始之前打开。
        interactionScope = UiPerformanceLog.BeginInteraction("PageTransition", page, target);
        interactionScope.RenderPath = UiRenderPaths.Resolve(
            renderCacheScope?.IsActive is true,
            blurRefreshLease is not null);
        // 过渡期间被反复合成的就是整个页面，因此表面积直接对应每帧的填充率成本。
        interactionScope.SurfaceWidth = target.ActualWidth;
        interactionScope.SurfaceHeight = target.ActualHeight;
        interactionScope.HasAncestorBitmapCache = renderCacheScope?.IsActive is true;
        // 预热被排除在出帧统计之外，单独记录，避免把开销从动画里挪走却看不出挪到了哪。
        interactionScope.WarmupMs = warmupStartedAtTimestamp == 0L
            ? 0d
            : Stopwatch.GetElapsedTime(warmupStartedAtTimestamp).TotalMilliseconds;
        warmupStartedAtTimestamp = 0L;
        if (warmupBusyProbe is not null)
        {
            interactionScope.WarmupBusyMs = warmupBusyProbe.TotalBusyMs;
            interactionScope.WarmupWorstOperationMs = warmupBusyProbe.WorstOperationMs;
            interactionScope.WarmupWorstOperation = warmupBusyProbe.WorstOperationDetail;
            ReleaseWarmupBusyProbe();
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeAnimation = new DoubleAnimation
        {
            From = WarmupOpacity,
            To = 1,
            Duration = TransitionDuration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        fadeAnimation.Completed += (_, _) =>
        {
            if (token == transitionToken && string.Equals(currentPage, page, StringComparison.OrdinalIgnoreCase))
                target.Opacity = 1;
        };

        var slideAnimation = new DoubleAnimation
        {
            From = startOffset,
            To = 0,
            Duration = TransitionDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        slideAnimation.Completed += (_, _) =>
        {
            if (token == transitionToken && string.Equals(currentPage, page, StringComparison.OrdinalIgnoreCase))
            {
                CompleteTransition(target, transform);
            }
        };

        target.BeginAnimation(UIElement.OpacityProperty, fadeAnimation, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(TranslateTransform.YProperty, slideAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ReleaseBlurRefreshLease()
    {
        blurRefreshLease?.Dispose();
        blurRefreshLease = null;
    }

    private void ReleaseInteractionScope()
    {
        interactionScope?.Dispose();
        interactionScope = null;
    }

    private void CompleteTransition(FrameworkElement target, TranslateTransform transform)
    {
        target.BeginAnimation(UIElement.OpacityProperty, null);
        target.Opacity = 1;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 0;
        ReleaseTransitionResources(target, requestFinalRefresh: true);
    }

    private void EnterTransitionGate()
    {
        if (hasEnteredTransitionGate)
            return;

        hasEnteredTransitionGate = true;
        UiTransitionGate.Enter();
    }

    /// <summary>闸门必须与 Enter 严格配对，否则被推迟的工作会永远排不出去。</summary>
    private void ExitTransitionGate()
    {
        if (!hasEnteredTransitionGate)
            return;

        hasEnteredTransitionGate = false;
        UiTransitionGate.Exit();
    }

    private void CancelActiveTransition(bool requestFinalRefresh)
    {
        StopTransitionWatchdog();
        CancelPendingCompositionWait();
        ReleaseWarmupBusyProbe();
        if (activeTarget is not { } target)
        {
            ExitTransitionGate();
            ReleaseInteractionScope();
            ReleaseBlurRefreshLease();
            renderCacheScope?.Dispose();
            renderCacheScope = null;
            return;
        }

        target.BeginAnimation(UIElement.OpacityProperty, null);
        target.Opacity = 1;
        var transform = EnsureTranslateTransform(target);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 0;
        ReleaseTransitionResources(target, requestFinalRefresh);
    }

    private void ReleaseTransitionResources(FrameworkElement target, bool requestFinalRefresh)
    {
        StopTransitionWatchdog();
        ExitTransitionGate();
        target.Unloaded -= ActiveTarget_Unloaded;
        ReleaseInteractionScope();
        ReleaseBlurRefreshLease();
        var usedRenderCache = renderCacheScope?.IsActive is true;
        renderCacheScope?.Dispose();
        renderCacheScope = null;
        activeTarget = null;

        if (requestFinalRefresh && usedRenderCache)
            BackdropBlurRefreshCoordinator.RequestScopeRefresh(target);
    }

    private void ActiveTarget_Unloaded(object sender, RoutedEventArgs e)
    {
        transitionToken++;
        CancelActiveTransition(requestFinalRefresh: false);
    }
}
