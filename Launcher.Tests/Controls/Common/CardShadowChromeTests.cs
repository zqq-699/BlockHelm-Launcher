/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Launcher.App.Behaviors;
using Launcher.App.Controls;

namespace Launcher.Tests.Controls.Common;

public sealed class CardShadowChromeTests
{
    [Fact]
    public void SectionFieldSurfaceStyleUsesOnlyLightweightShadow()
    {
        var appDirectory = TestRepository.ProjectDirectory("Launcher.App");
        var pageStyles = File.ReadAllText(Path.Combine(appDirectory, "Styles", "ControlStyles.Page.xaml"));
        var sectionStart = pageStyles.IndexOf(
            "<Style x:Key=\"SectionFieldSurfaceStyle\"",
            StringComparison.Ordinal);
        var sectionEnd = pageStyles.IndexOf(
            "<Style x:Key=\"ReadOnlyFieldSurfaceStyle\"",
            sectionStart,
            StringComparison.Ordinal);
        var sectionStyle = pageStyles[sectionStart..sectionEnd];

        Assert.DoesNotContain("LightweightShadowSectionFieldSurfaceStyle", pageStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"Effect\"", sectionStyle, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(
            sectionStyle,
            "Property=\"behaviors:BackdropBlurHost.LightweightShadowEffect\""));

        foreach (var path in TestRepository.EnumerateProjectFiles("Launcher.App", "*.xaml"))
        {
            Assert.DoesNotContain(
                "LightweightShadowSectionFieldSurfaceStyle",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 位图缓存是否启用取决于运行环境的渲染层级与纹理预算，
    /// 因此断言实际状态必须与该判定一致，而不能写死。
    /// </summary>
    private static void AssertRenderCacheMatchesCapability(CardShadowChrome chrome)
    {
        var expected = CardShadowChrome.ShouldUseRenderCache(
            RenderCapability.Tier >> 16,
            chrome.ActualWidth,
            chrome.ActualHeight,
            VisualTreeHelper.GetDpi(chrome).DpiScaleX);
        if (expected)
            Assert.IsType<BitmapCache>(chrome.CacheMode);
        else
            Assert.Null(chrome.CacheMode);
    }

    [Fact]
    public void ChromeBuildsRetainedDrawingWithoutWpfEffect()
    {
        RunOnStaThread(() =>
        {
            var effect = CreateReferenceEffect();
            var chrome = new CardShadowChrome
            {
                Width = 320d,
                Height = 180d,
                CornerRadius = new CornerRadius(8d),
                ReferenceEffect = effect,
                SurfaceBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                SurfaceBorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF))
            };
            var window = CreateWindow(chrome);

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.Render);
                window.UpdateLayout();

                // 设计意图是不依赖 WPF 的 DropShadowEffect；位图缓存则按渲染能力启用。
                Assert.Null(chrome.Effect);
                AssertRenderCacheMatchesCapability(chrome);
                var initialBuildCount = chrome.DrawingBuildCount;
                Assert.InRange(initialBuildCount, 1, 2);
                Assert.InRange(chrome.DrawingPrimitiveCount, 2, 25);

                chrome.RenderTransform = new TranslateTransform(0d, -92d);
                PumpDispatcher(DispatcherPriority.Render);

                Assert.Equal(initialBuildCount, chrome.DrawingBuildCount);

                effect.Opacity = 0.2d;
                PumpDispatcher(DispatcherPriority.Render);

                Assert.Equal(initialBuildCount + 1, chrome.DrawingBuildCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BackdropHostUsesLightweightShadowBehindExistingContent()
    {
        RunOnStaThread(() =>
        {
            var frozenThemeEffect = CreateReferenceEffect();
            frozenThemeEffect.Freeze();
            var originalContent = new TextBlock { Text = "Card content" };
            var border = new Border
            {
                Width = 320d,
                Height = 180d,
                Padding = new Thickness(14d, 10d, 14d, 10d),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1d),
                CornerRadius = new CornerRadius(8d),
                Child = originalContent
            };
            BackdropBlurHost.SetFallbackBrush(border, Brushes.Gray);
            BackdropBlurHost.SetLightweightShadowEffect(border, frozenThemeEffect);
            BackdropBlurHost.SetIsApplied(border, true);
            var window = CreateWindow(border);

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.Loaded);
                PumpDispatcher(DispatcherPriority.Render);
                window.UpdateLayout();

                var chrome = Assert.Single(FindDescendants<CardShadowChrome>(border));
                Assert.NotNull(chrome.ReferenceEffect);
                Assert.Null(chrome.Effect);
                AssertRenderCacheMatchesCapability(chrome);
                Assert.Null(border.Effect);
                Assert.True(chrome.ReferenceEffect!.IsFrozen);
                Assert.Same(originalContent, Assert.Single(FindDescendants<TextBlock>(border)));

                BackdropBlurHost.SetLightweightShadowEffect(border, null);

                Assert.Empty(FindDescendants<CardShadowChrome>(border));
                Assert.Same(originalContent, Assert.Single(FindDescendants<TextBlock>(border)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static DropShadowEffect CreateReferenceEffect() => new()
    {
        BlurRadius = 17d,
        Color = Colors.Black,
        Direction = 270d,
        Opacity = 0.13d,
        ShadowDepth = 0d
    };

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static Window CreateWindow(UIElement content) => new()
    {
        Width = 420d,
        Height = 280d,
        Content = content,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Opacity = 0d
    };

    private static IReadOnlyList<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var matches = new List<T>();
        FindDescendants(root, matches);
        return matches;
    }

    private static void FindDescendants<T>(DependencyObject root, ICollection<T> matches) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                matches.Add(match);
            FindDescendants(child, matches);
        }
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
