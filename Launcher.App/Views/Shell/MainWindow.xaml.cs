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
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Launcher.App.Controls;
using Launcher.App.Diagnostics;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.Views.Account.Dialogs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.Views.Shell;

/// <summary>
/// 承载主窗口级拖放、关闭握手、标题栏交互和无法用 Binding 表达的视觉树协调。
/// </summary>
public partial class MainWindow : Window
{
    // code-behind 只处理窗口/视觉树生命周期，业务决策始终委托给 MainViewModel 和页面 ViewModel。
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    public static readonly DependencyProperty IsMenuExpandedProperty =
        DependencyProperty.Register(nameof(IsMenuExpanded), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    // Keep the existing binding contract while exposing a retained drawing instead of
    // making every local blur surface capture the complete window visual tree.
    public FrameworkElement LauncherPreblurredBackdropSourceElement => LauncherBackgroundVisualSource;

    private readonly NavigationMenuAnimationService navigationMenuService;
    private readonly IAccountDialogService accountDialogService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly LauncherStateSyncService stateSyncService;
    private readonly LauncherShutdownService shutdownService;
    private readonly MainWindowPlacementService windowPlacementService;
    private readonly PageTransitionService pageTransitionService;
    private readonly MainViewModel viewModel;
    private readonly ILogger<MainWindow> logger;
    private IDataObject? cachedThirdPartyAccountDropData;
    private AuthlibInjectorServerDropResult cachedThirdPartyAccountDropResult;
    private bool cachedThirdPartyAccountDropBlocked;
    private DispatcherOperation? pendingDragStateReset;
    private DialogHost[]? cachedDialogHosts;
    private bool isShutdownInProgress;
    private bool isShutdownComplete;

    public MainWindow(
        MainViewModel viewModel,
        IWindowService windowService,
        IAccountDialogService accountDialogService,
        IFloatingMessageService floatingMessageService,
        LauncherStateSyncService stateSyncService,
        LauncherShutdownService shutdownService,
        MainWindowPlacementService windowPlacementService,
        IThemeService themeService,
        ILogger<MainWindow>? logger = null)
    {
        InitializeComponent();
        StateChanged += MainWindow_StateChanged;
        UpdateWindowChromeCornerRadius();
        this.viewModel = viewModel;
        this.accountDialogService = accountDialogService;
        this.floatingMessageService = floatingMessageService;
        this.stateSyncService = stateSyncService;
        this.shutdownService = shutdownService;
        this.windowPlacementService = windowPlacementService;
        this.logger = logger ?? NullLogger<MainWindow>.Instance;
        navigationMenuService = new NavigationMenuAnimationService(MenuColumn);
        pageTransitionService = new PageTransitionService(Dispatcher, ResolvePageRoot, viewModel.CurrentPage);

        DataContext = viewModel;
        windowService.Attach(this);
        var addAccountDialogView = AddAccountDialogHost.DialogContent as AddAccountDialogView
            ?? throw new InvalidOperationException("The add-account dialog content is not initialized.");
        accountDialogService.Attach(
            viewModel.AccountPage,
            AddAccountDialogHost,
            addAccountDialogView,
            DeleteAccountDialogHost,
            RenameAccountDialogHost,
            SkinModelDialogHost,
            SkinManagerDialogHost);

        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        LauncherWindowBackdrop.Attach(this, themeService);
        MaximizedWindowWorkArea.Attach(this);
        NativeCaptionButtons.Hide(this);
        Loaded += MainWindow_Loaded;
        Closing += Window_OnClosing;
        Closed += (_, _) => stateSyncService.Stop();
    }

    public bool IsMenuExpanded
    {
        get => (bool)GetValue(IsMenuExpandedProperty);
        set => SetValue(IsMenuExpandedProperty, value);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel viewModel)
            return;

        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            pageTransitionService.MoveTo(viewModel.CurrentPage);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsMenuExpanded))
        {
            IsMenuExpanded = viewModel.IsMenuExpanded;
            navigationMenuService.AnimateExpanded(IsMenuExpanded);
        }
    }

    private void TitleBarDragArea_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleWindowMaximizedState();
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        DragMove();
        e.Handled = true;
    }

    private void ToggleWindowMaximizedState()
    {
        if (ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
            return;

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximizedState();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowChromeCornerRadius();
    }

    private void UpdateWindowChromeCornerRadius()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
        {
            chrome.CornerRadius = WindowState == WindowState.Maximized
                ? new CornerRadius(0)
                : new CornerRadius(8);
        }
    }

    private FrameworkElement? ResolvePageRoot(string page)
    {
        if (string.Equals(page, "Home", StringComparison.OrdinalIgnoreCase))
            return HomePageView.RootElement;

        if (string.Equals(page, "Account", StringComparison.OrdinalIgnoreCase))
            return AccountPageView.RootElement;

        if (string.Equals(page, "Download", StringComparison.OrdinalIgnoreCase))
            return DownloadPageView.RootElement;

        if (string.Equals(page, "Install", StringComparison.OrdinalIgnoreCase))
            return InstallPageView.RootElement;

        if (string.Equals(page, "GameSettings", StringComparison.OrdinalIgnoreCase))
            return GameSettingsPageView.RootElement;

        if (string.Equals(page, "Multiplayer", StringComparison.OrdinalIgnoreCase))
            return MultiplayerPageView.RootElement;

        if (string.Equals(page, "Resources", StringComparison.OrdinalIgnoreCase))
            return ResourcesPageView.RootElement;

        if (string.Equals(page, "Settings", StringComparison.OrdinalIgnoreCase))
            return SettingsPageView.RootElement;

        return GeneralPageView.RootElement;
    }

    private void PrewarmTransientUi()
    {
        accountDialogService.Prewarm();

        foreach (var comboBox in FindVisualChildren<AnimatedComboBox>(this))
            comboBox.ApplyTemplate();

        // 折叠的页面不会被测量，虚拟化面板要到第一次真正显示才初始化视口。
        // 实测该初始化占用 UI 线程数十到上百毫秒，整段落在第一次切页的动画里。
        // 这里趁空闲把页面提前实例化一次，让用户点进去时已经是热的。
        PrewarmPageContent(GetPrewarmPages(), 0);
    }

    private FrameworkElement[] GetPrewarmPages() =>
    [
        SettingsPageView,
        DownloadPageView,
        GameSettingsPageView,
        ResourcesPageView,
        AccountPageView,
        MultiplayerPageView,
        InstallPageView
    ];

    /// <summary>
    /// 逐个预热页面内容：临时显示、强制布局，再在更低优先级上收回。
    /// 一次只热一个页面，让两次预热之间能处理输入，避免连成一段长阻塞。
    /// </summary>
    private void PrewarmPageContent(IReadOnlyList<FrameworkElement> pages, int index)
    {
        // 只扫一轮。曾经按"内容异步到达、单轮盖不住"的假设扫过四轮，
        // 但实测第二轮起每轮只有 10ms 且一无所获：需要预热的列表是用户导航时
        // 才触发加载的，不是启动后自动到达，多扫几轮永远扫不到。
        if (index >= pages.Count)
            return;

        var page = pages[index];
        // 已经是当前页说明用户先一步点了过去，本来就热了，跳过。
        if (page.Visibility == Visibility.Visible)
        {
            PrewarmPageContent(pages, index + 1);
            return;
        }

        // 预热本身是一次强制布局，撞进动画就是几十毫秒的掉帧——实测出现过 37.7ms。
        if (UiTransitionGate.IsTransitionActive)
        {
            UiTransitionGate.RunWhenIdle(() => PrewarmPageContent(pages, index));
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var visibilityBinding = PageContentPrewarm.Begin(page);

        // 虚拟化面板把首次视口初始化排在 Background 优先级，所以收尾必须排在更低的
        // ContextIdle 上，等它连同随后的布局一起跑完，页面才算真的热了。
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                // 必须在 End 之前量：End 会把页面恢复成折叠，那时 ActualWidth 归零、
                // 所有 Effect 都算"不可见"，量到的尺寸和可见性全是废数据。
                UiPerformanceLog.LogPageSurface(page, page.Name);
                if (!PageContentPrewarm.End(page, visibilityBinding))
                {
                    logger.LogError(
                        "Failed to restore the page visibility binding after prewarming. Page={PageName}",
                        page.Name);
                }

                logger.LogDebug(
                    "Page content prewarmed. Page={PageName} ElapsedMs={ElapsedMs:F1}",
                    page.Name,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                PrewarmPageContent(pages, index + 1);
            },
            DispatcherPriority.ContextIdle);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        // 先显示已 Prime 的界面，再等待完整初始化，避免启动期间窗口长时间空白。
        try
        {
            if (!await viewModel.WaitForUserAgreementDecisionAsync())
                return;

            await viewModel.InitializeCommand.ExecuteAsync(null);
            IsMenuExpanded = viewModel.IsMenuExpanded;
            navigationMenuService.SetExpanded(IsMenuExpanded);
            stateSyncService.Start(() => viewModel.Settings, viewModel.SyncExternalInstanceCatalogAsync);
            _ = Dispatcher.BeginInvoke(PrewarmTransientUi, DispatcherPriority.ContextIdle);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to initialize the main window.");
        }
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        // 第一次 Closing 立即隐藏窗口，再在后台完成有界关闭握手；收尾完成后允许真正关闭。
        if (isShutdownComplete)
            return;

        if (!viewModel.CanCloseWindow())
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        if (isShutdownInProgress)
            return;

        isShutdownInProgress = true;
        var placement = windowPlacementService.Capture(this);
        Hide();
        using var shutdownCancellation = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            await windowPlacementService.SaveAsync(placement, shutdownCancellation.Token);
        }
        catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
        {
            logger.LogWarning("Timed out saving the main window placement during launcher exit.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to save the main window placement during launcher exit.");
        }

        try
        {
            await shutdownService.PrepareForExitAsync(ShutdownTimeout, shutdownCancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unexpected failure while preparing the launcher to exit.");
        }
        finally
        {
            isShutdownInProgress = false;
            isShutdownComplete = true;
            Close();
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
                yield return typedChild;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void Window_OnPreviewDragEnter(object sender, DragEventArgs e)
    {
        CancelPendingDragStateReset();
        HandleDragPreview(e, refreshDialogState: true);
    }

    private void Window_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        CancelPendingDragStateReset();
        HandleDragPreview(e, refreshDialogState: false);
    }

    private void HandleDragPreview(DragEventArgs e, bool refreshDialogState)
    {
        // Preview 路由在页面控件前统一判断当前导航目标，保证全窗口拖放提示一致。
        // 读取拖放数据是跨进程调用，可能以各种方式失败；这里必须兜住：异常一旦从 DragOver
        // 这条高频事件逃逸，全局 DispatcherUnhandledException 只记日志、不标记已处理，进程会直接退出。
        try
        {
            if (HandleThirdPartyAccountDropPreview(e, refreshDialogState))
                return;

            if (HandleDownloadLocalImportPreview(e))
                return;

            if (HandleLocalImportPagePreview(e))
                return;

            HandleFileDropPreview(e);
        }
        catch (Exception exception)
        {
            // 判断不出这次拖放是什么，就退回中立状态并明确拒绝，别让子控件再去踩同一个坑。
            logger.LogWarning(exception, "Failed to evaluate a drag preview over the main window.");
            ResetDragState();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void Window_OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        // WPF 在指针真正离开窗口时走 OleDragLeave，而该回调没有坐标可用，事件里的位置被硬编码成
        // Point(0,0)，任何基于坐标的"是否仍在窗口内"判断都会恒为真，清理因此永远不会执行。
        // 指针在控件之间移动时同样会先收到 DragLeave，但紧跟着就有 DragEnter，
        // 所以这里改为延迟清理：后续拖放事件会撤销它，只有拖放真正结束时才会落地。
        ScheduleDragStateReset();
    }

    private void ScheduleDragStateReset()
    {
        pendingDragStateReset?.Abort();
        pendingDragStateReset = Dispatcher.BeginInvoke(DispatcherPriority.Background, ResetDragState);
    }

    private void CancelPendingDragStateReset()
    {
        pendingDragStateReset?.Abort();
        pendingDragStateReset = null;
    }

    private void ResetDragState()
    {
        pendingDragStateReset = null;
        ClearThirdPartyAccountDropState();
        viewModel.DownloadPage.LocalImportDialog.ClearDropState();
        viewModel.DownloadPage.ClearLocalImportDropState();
        viewModel.GameSettingsPage.ClearImportDropState();
        // 拖放已经结束，无论提示归属于谁都必须收尾。
        floatingMessageService.ClearDragHint();
    }

    private async void Window_OnPreviewDrop(object sender, DragEventArgs e)
    {
        // Drop 后才执行压缩包识别或文件导入，高频 DragOver 阶段只做轻量预览。
        CancelPendingDragStateReset();
        // 松手的瞬间"松开以导入"就已经过时了。提示不再自动消失，若等到下面的
        // 压缩包识别 await 结束才清，整个导入期间它都会挂在屏幕上。
        floatingMessageService.ClearDragHint();
        try
        {
            if (HandleThirdPartyAccountDrop(e))
                return;

            if (HandleDownloadLocalImportDrop(e))
                return;

            if (await HandleLocalImportPageDropAsync(e))
                return;

            var paths = TryGetDroppedPaths(e);
            if (paths is null)
                return;

            e.Handled = true;
            e.Effects = DragDropEffects.None;
            await viewModel.GameSettingsPage.HandleImportDropAsync(paths);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to handle files dropped onto the main window.");
        }
        finally
        {
            // 拖放提示不会自动消失，无论走哪条分支都必须在这里收尾。
            // 若处理过程中已经弹出结果提示，服务会判定当前不是拖放提示而跳过。
            floatingMessageService.ClearDragHint();
        }
    }

    private bool HandleThirdPartyAccountDropPreview(DragEventArgs e, bool refreshDialogState)
    {
        if (refreshDialogState || !ReferenceEquals(cachedThirdPartyAccountDropData, e.Data))
        {
            cachedThirdPartyAccountDropData = e.Data;
            cachedThirdPartyAccountDropResult = AuthlibInjectorServerDropParser.Parse(e.Data);
            cachedThirdPartyAccountDropBlocked =
                cachedThirdPartyAccountDropResult.Status is not AuthlibInjectorServerDropStatus.NotRecognized
                && IsThirdPartyAccountDropBlocked();
        }

        if (cachedThirdPartyAccountDropResult.Status is AuthlibInjectorServerDropStatus.NotRecognized)
        {
            ClearThirdPartyAccountDropHint();
            return false;
        }

        e.Handled = true;
        var canAccept = cachedThirdPartyAccountDropResult.Status is AuthlibInjectorServerDropStatus.Valid
            && !cachedThirdPartyAccountDropBlocked;
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        SetThirdPartyAccountDropHint(cachedThirdPartyAccountDropResult.Status is AuthlibInjectorServerDropStatus.Invalid
            ? Strings.Account_ThirdPartyDropInvalidServer
            : cachedThirdPartyAccountDropBlocked
                ? Strings.Account_ThirdPartyDropDialogBusy
                : Strings.Account_ThirdPartyDropReleaseToAdd);
        return true;
    }

    private bool HandleThirdPartyAccountDrop(DragEventArgs e)
    {
        var result = AuthlibInjectorServerDropParser.Parse(e.Data);
        if (result.Status is AuthlibInjectorServerDropStatus.NotRecognized)
        {
            ClearThirdPartyAccountDropState();
            return false;
        }

        e.Handled = true;
        e.Effects = DragDropEffects.None;

        // 地址无效或被弹窗阻塞时，悬停阶段已把 e.Effects 置为 None，此时 DoDragDrop 派发的是
        // DragLeave 而不是 Drop，用户看到的是禁止光标加悬停提示。所以这里只会收到可接受的拖放，
        // 下面的判断纯属防御：一旦真的走到，说明上述前提不成立，日志就是排查线索。
        if (result.Status is not AuthlibInjectorServerDropStatus.Valid
            || IsThirdPartyAccountDropBlocked())
        {
            logger.LogDebug(
                "Discarded an authlib-injector authentication server drop that the preview stage had already rejected. Status={Status}",
                result.Status);
            ClearThirdPartyAccountDropState();
            return true;
        }

        var authenticationServer = result.AuthenticationServer!;
        logger.LogInformation(
            "Accepted an authlib-injector authentication server drop. AuthenticationServerHost={AuthenticationServerHost}",
            new Uri(authenticationServer).Host);
        e.Effects = DragDropEffects.Copy;
        ClearThirdPartyAccountDropState();
        accountDialogService.ShowThirdPartyAddAccountDialog(authenticationServer);
        return true;
    }

    private bool IsThirdPartyAccountDropBlocked()
    {
        if (viewModel.AccountPage.Dialog.IsAddAccountDialogBusy)
            return true;

        // 与具体宿主无关，先求值一次，不必在每个宿主上重复判断对话框步骤。
        var canReuseAddAccountDialog = CanApplyThirdPartyAccountDropToOpenDialog();
        foreach (var host in GetDialogHosts())
        {
            if (!host.IsOpen)
                continue;
            if (!canReuseAddAccountDialog || !ReferenceEquals(host, AddAccountDialogHost))
                return true;
        }

        return false;
    }

    private DialogHost[] GetDialogHosts()
    {
        // 对话框宿主全部是 MainWindow.xaml 里的静态同级元素，可视化树建好后不再增减。
        // 拖动期间每次 DragEnter 都重新遍历整棵（且已预热所有页面的）可视化树代价过高，缓存一次即可。
        if (cachedDialogHosts is { Length: > 0 })
            return cachedDialogHosts;

        cachedDialogHosts = FindVisualChildren<DialogHost>(this).ToArray();
        return cachedDialogHosts;
    }

    private bool CanApplyThirdPartyAccountDropToOpenDialog() =>
        AddAccountDialogHost.IsOpen
        && viewModel.AccountPage.Dialog.IsAddAccountDialogOpen
        && (viewModel.AccountPage.Dialog.IsAccountTypeStep
            || viewModel.AccountPage.Dialog.IsThirdPartyCredentialsStep);

    // 去重与归属都由浮层服务判断，这里只转发意图。
    // 传入 this 是关键：文件拖放同样会流经本处理器，未命中时不能清掉文件导入自己的提示。
    private void SetThirdPartyAccountDropHint(string message) =>
        floatingMessageService.ShowDragHint(this, message);

    private void ClearThirdPartyAccountDropHint() => floatingMessageService.ClearDragHint(this);

    private void ClearThirdPartyAccountDropState()
    {
        cachedThirdPartyAccountDropData = null;
        cachedThirdPartyAccountDropResult = default;
        cachedThirdPartyAccountDropBlocked = false;
        ClearThirdPartyAccountDropHint();
    }

    private void HandleFileDropPreview(DragEventArgs e)
    {
        var paths = TryGetDroppedPaths(e);
        if (paths is null)
        {
            viewModel.GameSettingsPage.ClearImportDropState();
            return;
        }

        var canAccept = viewModel.GameSettingsPage.UpdateImportDropState(paths);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private bool HandleLocalImportPagePreview(DragEventArgs e)
    {
        if (!IsLocalImportDropPage())
            return false;

        var paths = TryGetDroppedPaths(e);
        var canAccept = false;
        if (paths is null)
            viewModel.DownloadPage.ClearLocalImportDropState();
        else
            canAccept = viewModel.DownloadPage.UpdateLocalImportDropState(paths);

        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return true;
    }

    private async Task<bool> HandleLocalImportPageDropAsync(DragEventArgs e)
    {
        if (!IsLocalImportDropPage())
            return false;

        var paths = TryGetDroppedPaths(e);
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try
        {
            if (paths is not null)
                await viewModel.DownloadPage.HandleLocalImportDropAsync(paths);
        }
        finally
        {
            viewModel.DownloadPage.ClearLocalImportDropState();
        }

        return true;
    }

    private bool HandleDownloadLocalImportPreview(DragEventArgs e)
    {
        if (!viewModel.DownloadPage.LocalImportDialog.IsOpen)
            return false;

        var paths = TryGetDroppedPaths(e);
        if (paths is null)
        {
            viewModel.DownloadPage.LocalImportDialog.ClearDropState();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return true;
        }

        var canAccept = viewModel.DownloadPage.LocalImportDialog.PreviewDroppedFiles(paths);
        e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return true;
    }

    private bool HandleDownloadLocalImportDrop(DragEventArgs e)
    {
        if (!viewModel.DownloadPage.LocalImportDialog.IsOpen)
            return false;

        var paths = TryGetDroppedPaths(e);
        if (paths is not null)
            viewModel.DownloadPage.LocalImportDialog.ApplyDroppedFiles(paths);
        else
            viewModel.DownloadPage.LocalImportDialog.ClearDropState();

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        return true;
    }

    private static string[]? TryGetDroppedPaths(DragEventArgs e)
    {
        // 只接受系统 FileDrop 格式，并复制为字符串快照，避免异步处理继续持有 DragEventArgs。
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;

        return e.Data.GetData(DataFormats.FileDrop) as string[];
    }

    private bool IsLocalImportDropPage()
    {
        return NavigationCatalog.UsesLocalModpackDrop(
            viewModel.CurrentPage,
            viewModel.GameSettingsPage.IsListStep);
    }

}
