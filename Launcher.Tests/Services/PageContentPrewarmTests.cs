/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Launcher.App.Services;

namespace Launcher.Tests.Services;

public sealed class PageContentPrewarmTests
{
    /// <summary>
    /// 确认正常布局能在预热收尾前初始化虚拟化视口。
    /// </summary>
    [Fact]
    public void NormalLayoutWarmsHiddenVirtualizedList()
    {
        RunOnStaThread(() =>
        {
            var (page, _, window) = CreateBoundPage();
            try
            {
                var list = new ListBox { ItemsSource = Enumerable.Range(0, 100) };
                ScrollViewer.SetCanContentScroll(list, true);
                VirtualizingPanel.SetIsVirtualizing(list, true);
                VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
                page.Child = list;
                Assert.Equal(Visibility.Collapsed, page.Visibility);

                PageContentPrewarm.Begin(page);
                Assert.Equal(Visibility.Hidden, page.Visibility);
                Assert.Equal(0d, page.ActualWidth);
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));

                page.Dispatcher.Invoke(() =>
                {
                    Assert.True(page.IsMeasureValid);
                    Assert.True(page.IsArrangeValid);
                    Assert.True(page.ActualWidth > 0d);
                    Assert.False(page.IsVisible);
                    Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(99));
                    PageContentPrewarm.End(page);
                }, DispatcherPriority.ContextIdle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PrewarmKeepsVisibilityBindingActiveWhenNavigating()
    {
        RunOnStaThread(() =>
        {
            var (page, source, window) = CreateBoundPage();
            try
            {
                var binding = BindingOperations.GetBindingExpressionBase(page, UIElement.VisibilityProperty);
                Assert.NotNull(binding);
                PageContentPrewarm.Begin(page);
                Assert.Equal(Visibility.Hidden, page.Visibility);
                Assert.Same(binding, BindingOperations.GetBindingExpressionBase(page, UIElement.VisibilityProperty));

                source.IsCurrent = true;
                Assert.Equal(Visibility.Visible, page.Visibility);
                PageContentPrewarm.End(page);
                Assert.Equal(Visibility.Visible, page.Visibility);
                source.IsCurrent = false;
                Assert.Equal(Visibility.Collapsed, page.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EndRestoresCollapsedPageWhenItWasNotNavigatedTo(bool isBound)
    {
        RunOnStaThread(() =>
        {
            var (page, _, window) = CreateBoundPage();
            try
            {
                if (!isBound)
                    page.Visibility = Visibility.Collapsed;

                var binding = BindingOperations.GetBindingExpressionBase(page, UIElement.VisibilityProperty);
                PageContentPrewarm.Begin(page);
                PageContentPrewarm.End(page);
                Assert.Equal(Visibility.Collapsed, page.Visibility);
                Assert.Same(binding, BindingOperations.GetBindingExpressionBase(page, UIElement.VisibilityProperty));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (Border Page, PageSource Source, Window Window) CreateBoundPage()
    {
        var source = new PageSource();
        var page = new Border { Width = 120d, Height = 80d };
        page.SetBinding(
            UIElement.VisibilityProperty,
            new Binding(nameof(PageSource.IsCurrent))
            {
                Source = source,
                Converter = new BooleanToVisibilityConverter()
            });
        var window = CreateHostWindow(page);
        window.Show();
        return (page, source, window);
    }

    private static Window CreateHostWindow(UIElement content)
    {
        var host = new Grid();
        host.Children.Add(content);
        return new Window
        {
            Width = 320d,
            Height = 200d,
            Left = -10000d,
            Top = -10000d,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = host
        };
    }

    private sealed class PageSource : INotifyPropertyChanged
    {
        private bool isCurrent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsCurrent
        {
            get => isCurrent;
            set
            {
                if (isCurrent == value)
                    return;
                isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
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
