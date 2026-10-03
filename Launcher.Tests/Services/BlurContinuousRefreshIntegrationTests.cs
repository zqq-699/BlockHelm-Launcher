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
using Launcher.App.Controls;
using Launcher.App.Services;

namespace Launcher.Tests.Services;

[Collection(TransitionRenderingTestCollection.Name)]
public sealed class BlurContinuousRefreshIntegrationTests
{
    private static readonly TransitionRenderCacheCapabilities SupportedCapabilities = new(
        RenderingTier: 2,
        MaximumTextureSize: new Size(8192d, 8192d),
        MaximumEstimatedBytes: TransitionRenderCacheCapabilities.DefaultMaximumEstimatedBytes);

    [Fact]
    public void CachedPageTransitionSkipsContinuousRefreshAndRestoresCacheOnSync()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture();
            try
            {
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);

                var service = new PageTransitionService(
                    Dispatcher.CurrentDispatcher,
                    page => string.Equals(page, "Settings", StringComparison.Ordinal)
                        ? fixture.Scope
                        : null,
                    "Home",
                    ["Home", "Settings"],
                    AcquireSupportedCache);

                service.MoveTo("Settings");
                PumpDispatcher(DispatcherPriority.Render);

                var coordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                Assert.False(coordinator.IsContinuousRenderingActive);
                Assert.IsType<BitmapCache>(fixture.Scope.CacheMode);

                service.SyncTo("Settings");

                Assert.False(coordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);
                Assert.Equal(1, coordinator.PendingCount);
                Assert.Equal(1d, fixture.Scope.Opacity);
                Assert.Equal(0d, Assert.IsType<TranslateTransform>(fixture.Scope.RenderTransform).Y);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void CachedSlidingTransitionSkipsContinuousRefreshAndRestoresBothCachesOnSync()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture(includeSecondaryLayer: true);
            try
            {
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);

                var coordinator = new SlidingContentTransitionCoordinator(
                    fixture.Root,
                    fixture.Root,
                    fixture.Scope,
                    fixture.SecondaryScope!,
                    secondaryFloatingElements: null,
                    useSlideTransition: true,
                    useScaleTransition: false,
                    transitionScale: SlidingContentTransitionCoordinator.DefaultTransitionScale,
                    renderCacheFactory: AcquireSupportedCache);
                coordinator.Sync(showSecondaryLayer: false);
                coordinator.AnimateTo(showSecondaryLayer: true);

                var refreshCoordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                Assert.False(refreshCoordinator.IsContinuousRenderingActive);
                Assert.IsType<BitmapCache>(fixture.Scope.CacheMode);
                Assert.IsType<BitmapCache>(fixture.SecondaryScope!.CacheMode);

                coordinator.Sync(showSecondaryLayer: true);

                Assert.False(refreshCoordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);
                Assert.Null(fixture.SecondaryScope.CacheMode);
                Assert.Equal(Visibility.Collapsed, fixture.Scope.Visibility);
                Assert.Equal(Visibility.Visible, fixture.SecondaryScope.Visibility);
                Assert.Equal(0d, fixture.Scope.Opacity);
                Assert.Equal(1d, fixture.SecondaryScope.Opacity);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void HiddenSlidingTransitionSynchronizesWithoutAcquiringRenderCache()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture(includeSecondaryLayer: true);
            try
            {
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);
                fixture.Root.Visibility = Visibility.Collapsed;
                PumpDispatcher(DispatcherPriority.Render);
                Assert.True(fixture.Root.IsLoaded);
                Assert.False(fixture.Root.IsVisible);

                var cacheAcquired = false;
                var coordinator = new SlidingContentTransitionCoordinator(
                    fixture.Root,
                    fixture.Root,
                    fixture.Scope,
                    fixture.SecondaryScope!,
                    secondaryFloatingElements: null,
                    useSlideTransition: true,
                    useScaleTransition: false,
                    transitionScale: SlidingContentTransitionCoordinator.DefaultTransitionScale,
                    renderCacheFactory: (transitionKind, elements) =>
                    {
                        cacheAcquired = true;
                        return AcquireSupportedCache(transitionKind, elements);
                    });
                coordinator.Sync(showSecondaryLayer: false);

                coordinator.AnimateTo(showSecondaryLayer: true);

                Assert.False(cacheAcquired);
                Assert.Null(fixture.Scope.CacheMode);
                Assert.Null(fixture.SecondaryScope!.CacheMode);
                Assert.Equal(Visibility.Collapsed, fixture.Scope.Visibility);
                Assert.Equal(Visibility.Visible, fixture.SecondaryScope.Visibility);
                Assert.Equal(0d, fixture.Scope.Opacity);
                Assert.Equal(1d, fixture.SecondaryScope.Opacity);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void ImageModePageTransitionWithActiveControlBlurUsesOriginalLiveRefreshPath()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture();
            try
            {
                EnableImageControlBlur(fixture.Window);
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);

                var service = new PageTransitionService(
                    Dispatcher.CurrentDispatcher,
                    page => string.Equals(page, "Settings", StringComparison.Ordinal)
                        ? fixture.Scope
                        : null,
                    "Home",
                    ["Home", "Settings"],
                    AcquireSupportedCache);

                service.MoveTo("Settings");
                PumpDispatcher(DispatcherPriority.Render);

                var coordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                Assert.True(coordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);

                service.SyncTo("Settings");

                Assert.False(coordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void ImageModeSlidingTransitionWithActiveControlBlurUsesOriginalLiveRefreshPath()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture(includeSecondaryLayer: true);
            try
            {
                EnableImageControlBlur(fixture.Window);
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);

                var coordinator = new SlidingContentTransitionCoordinator(
                    fixture.Root,
                    fixture.Root,
                    fixture.Scope,
                    fixture.SecondaryScope!,
                    secondaryFloatingElements: null,
                    useSlideTransition: true,
                    useScaleTransition: false,
                    transitionScale: SlidingContentTransitionCoordinator.DefaultTransitionScale,
                    renderCacheFactory: AcquireSupportedCache);
                coordinator.Sync(showSecondaryLayer: false);
                coordinator.AnimateTo(showSecondaryLayer: true);

                var refreshCoordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                Assert.True(refreshCoordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);
                Assert.Null(fixture.SecondaryScope!.CacheMode);

                coordinator.Sync(showSecondaryLayer: true);

                Assert.False(refreshCoordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);
                Assert.Null(fixture.SecondaryScope.CacheMode);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void ImageModeWithControlBlurDisabledStillUsesPageCache()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture();
            try
            {
                EnableImageControlBlur(fixture.Window);
                fixture.Control.IsBlurEnabled = false;
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);

                var service = new PageTransitionService(
                    Dispatcher.CurrentDispatcher,
                    page => string.Equals(page, "Settings", StringComparison.Ordinal)
                        ? fixture.Scope
                        : null,
                    "Home",
                    ["Home", "Settings"],
                    AcquireSupportedCache);

                service.MoveTo("Settings");
                PumpDispatcher(DispatcherPriority.Render);

                var coordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                Assert.False(coordinator.IsContinuousRenderingActive);
                Assert.IsType<BitmapCache>(fixture.Scope.CacheMode);

                service.SyncTo("Settings");
                Assert.Null(fixture.Scope.CacheMode);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void UnsupportedPageTransitionRetainsContinuousRefreshFallback()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture();
            try
            {
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);

                var service = new PageTransitionService(
                    Dispatcher.CurrentDispatcher,
                    page => string.Equals(page, "Settings", StringComparison.Ordinal)
                        ? fixture.Scope
                        : null,
                    "Home",
                    ["Home", "Settings"],
                    AcquireUnsupportedCache);

                service.MoveTo("Settings");
                PumpDispatcher(DispatcherPriority.Render);

                var coordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                Assert.True(coordinator.IsContinuousRenderingActive);
                Assert.Null(fixture.Scope.CacheMode);

                service.SyncTo("Settings");
                Assert.False(coordinator.IsContinuousRenderingActive);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void CachedPageTransitionNaturalCompletionRestoresStableVisualState()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture();
            try
            {
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);
                var service = new PageTransitionService(
                    Dispatcher.CurrentDispatcher,
                    page => string.Equals(page, "Settings", StringComparison.Ordinal)
                        ? fixture.Scope
                        : null,
                    "Home",
                    ["Home", "Settings"],
                    AcquireSupportedCache);
                var coordinator = Assert.IsType<BackdropBlurRefreshCoordinator>(
                    BackdropBlurRefreshCoordinator.TryGet(fixture.Control));
                var refreshCountBeforeTransition = coordinator.TotalRefreshCount;

                service.MoveTo("Settings");
                PumpDispatcher(DispatcherPriority.Render);
                Assert.IsType<BitmapCache>(fixture.Scope.CacheMode);

                PumpFor(TimeSpan.FromMilliseconds(320));

                Assert.Null(fixture.Scope.CacheMode);
                Assert.Equal(1d, fixture.Scope.Opacity);
                Assert.Equal(0d, Assert.IsType<TranslateTransform>(fixture.Scope.RenderTransform).Y);
                Assert.False(coordinator.IsContinuousRenderingActive);
                Assert.True(coordinator.TotalRefreshCount > refreshCountBeforeTransition);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    [Fact]
    public void CachedSlidingTransitionNaturalCompletionRestoresStableLayerState()
    {
        RunOnStaThread(() =>
        {
            var fixture = CreateFixture(includeSecondaryLayer: true);
            try
            {
                fixture.Window.Show();
                PumpDispatcher(DispatcherPriority.Render);
                var coordinator = new SlidingContentTransitionCoordinator(
                    fixture.Root,
                    fixture.Root,
                    fixture.Scope,
                    fixture.SecondaryScope!,
                    secondaryFloatingElements: null,
                    useSlideTransition: true,
                    useScaleTransition: false,
                    transitionScale: SlidingContentTransitionCoordinator.DefaultTransitionScale,
                    renderCacheFactory: AcquireSupportedCache);
                coordinator.Sync(showSecondaryLayer: false);

                coordinator.AnimateTo(showSecondaryLayer: true);
                Assert.IsType<BitmapCache>(fixture.Scope.CacheMode);
                Assert.IsType<BitmapCache>(fixture.SecondaryScope!.CacheMode);

                PumpFor(TimeSpan.FromMilliseconds(320));

                Assert.Null(fixture.Scope.CacheMode);
                Assert.Null(fixture.SecondaryScope.CacheMode);
                Assert.Equal(Visibility.Collapsed, fixture.Scope.Visibility);
                Assert.Equal(Visibility.Visible, fixture.SecondaryScope.Visibility);
                Assert.Equal(0d, fixture.Scope.Opacity);
                Assert.Equal(1d, fixture.SecondaryScope.Opacity);
            }
            finally
            {
                fixture.Window.Close();
            }
        });
    }

    private static BlurFixture CreateFixture(bool includeSecondaryLayer = false)
    {
        var source = new Border();
        var control = new BackdropBlurBorder
        {
            SourceElement = source,
            IsSourcePreblurred = true
        };
        var scope = new Grid();
        scope.Children.Add(control);

        var root = new Grid();
        root.Children.Add(source);
        root.Children.Add(scope);

        Grid? secondaryScope = null;
        if (includeSecondaryLayer)
        {
            secondaryScope = new Grid();
            root.Children.Add(secondaryScope);
        }

        var window = new Window
        {
            Width = 320d,
            Height = 200d,
            Left = -10000d,
            Top = -10000d,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = root
        };
        return new BlurFixture(window, root, scope, secondaryScope, control);
    }

    private static TransitionRenderCacheScope AcquireSupportedCache(
        string transitionKind,
        IReadOnlyList<FrameworkElement> elements)
    {
        return TransitionRenderCacheScope.TryAcquire(
            transitionKind,
            elements,
            SupportedCapabilities);
    }

    private static TransitionRenderCacheScope AcquireUnsupportedCache(
        string transitionKind,
        IReadOnlyList<FrameworkElement> elements)
    {
        return TransitionRenderCacheScope.TryAcquire(
            transitionKind,
            elements,
            SupportedCapabilities with { RenderingTier = 1 });
    }

    private static void EnableImageControlBlur(Window window)
    {
        window.Resources["Is.ImageBackground.ControlTint.Enabled"] = true;
        window.Resources["Is.Surface.BackdropBlur.Enabled"] = true;
        window.Resources["Is.SecondaryMenu.BackdropBlur.Enabled"] = true;
    }

    private static void PumpDispatcher(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            priority,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
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

    private sealed record BlurFixture(
        Window Window,
        Grid Root,
        Grid Scope,
        Grid? SecondaryScope,
        BackdropBlurBorder Control);
}
