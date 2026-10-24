/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Launcher.App.Behaviors;
using Launcher.App.Utilities;

namespace Launcher.Tests.Behaviors;

public sealed class VirtualizedListScrollResetBehaviorTests
{
    [Fact]
    public void TargetedEntranceReleasesItemsOutsideTheInitialViewport()
    {
        RunOnStaThread(() =>
        {
            var visibleItem = new PendingItem { IsGroupEntrancePending = true };
            var item = new PendingItem { IsGroupEntrancePending = true };
            var listBox = new ListBox
            {
                Width = 360,
                Height = 220,
                ItemsSource = Enumerable.Range(0, 200)
                    .Select(index => index == 1 ? (object)visibleItem
                        : index == 150 ? item : $"Version {index}")
                    .ToArray()
            };
            VirtualizingPanel.SetIsVirtualizing(listBox, true);
            VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
            VirtualizedListItemStateBehavior.SetIsEnabled(listBox, true);
            var window = new Window
            {
                Width = 360,
                Height = 220,
                Content = listBox,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0
            };

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                listBox.UpdateLayout();
                Assert.Null(listBox.ItemContainerGenerator.ContainerFromItem(item));

                VirtualizedListItemStateBehavior.QueueItemEntranceAnimation(listBox, [visibleItem, item]);
                PumpDispatcher(DispatcherPriority.ContextIdle);
                var visibleContainer = Assert.IsType<ListBoxItem>(
                    listBox.ItemContainerGenerator.ContainerFromItem(visibleItem));
                Assert.True(Assert.IsType<VirtualizedListItemState>(visibleContainer.Tag).ShouldPlayEnterAnimation);
                Assert.True(visibleItem.IsGroupEntrancePending);
                Assert.False(item.IsGroupEntrancePending);
                var frame = new DispatcherFrame();
                var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    frame.Continue = false;
                };
                wait.Start();
                Dispatcher.PushFrame(frame);
                Assert.False(item.IsGroupEntrancePending);

                listBox.ScrollIntoView(item);
                listBox.UpdateLayout();
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                VirtualizedListItemStateBehavior.Refresh(listBox);
                PumpDispatcher(DispatcherPriority.ApplicationIdle);

                var container = Assert.IsType<ListBoxItem>(listBox.ItemContainerGenerator.ContainerFromItem(item));
                var state = Assert.IsType<VirtualizedListItemState>(container.Tag);
                Assert.False(state.ShouldPlayEnterAnimation);
            }
            finally
            {
                window.Close();
                VirtualizedListItemStateBehavior.SetIsEnabled(listBox, false);
            }
        });
    }

    [Fact]
    public void TargetedEntranceWaitsForRealizationAndReleasesRemovedItems()
    {
        RunOnStaThread(() =>
        {
            var item = new PendingItem { IsGroupEntrancePending = true };
            var listBox = new ListBox();
            listBox.Items.Add(item);
            VirtualizedListItemStateBehavior.SetIsEnabled(listBox, true);
            try
            {
                VirtualizedListItemStateBehavior.QueueItemEntranceAnimation(listBox, [item]);
                Assert.True(item.IsGroupEntrancePending);

                var frame = new DispatcherFrame();
                var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    frame.Continue = false;
                };
                wait.Start();
                Dispatcher.PushFrame(frame);

                // 旧的固定八轮扫描会在约 360 ms 后放弃这些尚未生成容器的项。
                Assert.True(item.IsGroupEntrancePending);
                listBox.Items.Remove(item);
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                Assert.False(item.IsGroupEntrancePending);
            }
            finally
            {
                VirtualizedListItemStateBehavior.SetIsEnabled(listBox, false);
            }
        });
    }

    [Fact]
    public void LayoutShiftAnimationKeepsVisibleContentMovingWhenGroupRowsFillViewport()
    {
        RunOnStaThread(() =>
        {
            var items = new ObservableCollection<string>(
                Enumerable.Range(0, 60).Select(index => $"Version {index}"));
            var listBox = new ListBox
            {
                Width = 360,
                Height = 220,
                ItemsSource = items,
                ItemContainerStyle = new Style(typeof(ListBoxItem))
                {
                    Setters = { new Setter(FrameworkElement.HeightProperty, 52d) }
                }
            };
            VirtualizingPanel.SetIsVirtualizing(listBox, true);
            VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
            VirtualizingPanel.SetCacheLength(listBox, new VirtualizationCacheLength(3, 3));
            VirtualizingPanel.SetCacheLengthUnit(listBox, VirtualizationCacheLengthUnit.Page);
            VirtualizedListItemStateBehavior.SetIsEnabled(listBox, true);
            var window = new Window
            {
                Width = 360,
                Height = 220,
                Content = listBox,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0
            };

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                listBox.UpdateLayout();
                Assert.NotNull(listBox.ItemContainerGenerator.ContainerFromItem("Version 0"));

                VirtualizedListItemStateBehavior.QueueLayoutShiftAnimation(listBox);
                for (var index = 0; index < 7; index++)
                    items.Insert(index, $"Inserted {index}");
                PumpDispatcher(DispatcherPriority.Loaded);

                var shiftedContainer = Assert.IsType<ListBoxItem>(
                    listBox.ItemContainerGenerator.ContainerFromItem("Version 0"));
                var translate = Assert.IsType<TranslateTransform>(shiftedContainer.RenderTransform);
                Assert.True(translate.Y < 0);
                Assert.True(translate.HasAnimatedProperties);

                var frame = new DispatcherFrame();
                var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                wait.Tick += (_, _) =>
                {
                    wait.Stop();
                    frame.Continue = false;
                };
                wait.Start();
                Dispatcher.PushFrame(frame);

                Assert.Null(shiftedContainer.RenderTransform);
            }
            finally
            {
                window.Close();
                VirtualizedListItemStateBehavior.SetIsEnabled(listBox, false);
            }
        });
    }

    [Fact]
    public void LayoutShiftAnimationCompensatesForRemovedItems()
    {
        RunOnStaThread(() =>
        {
            var items = new ObservableCollection<string>(
                Enumerable.Range(0, 60).Select(index => $"Version {index}"));
            var listBox = new ListBox
            {
                Width = 360,
                Height = 220,
                ItemsSource = items
            };
            VirtualizingPanel.SetIsVirtualizing(listBox, true);
            VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
            VirtualizedListItemStateBehavior.SetIsEnabled(listBox, true);
            var window = new Window
            {
                Width = 360,
                Height = 220,
                Content = listBox,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0
            };

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                listBox.UpdateLayout();
                Assert.NotNull(listBox.ItemContainerGenerator.ContainerFromItem("Version 1"));

                VirtualizedListItemStateBehavior.QueueLayoutShiftAnimation(listBox);
                items.RemoveAt(0);
                PumpDispatcher(DispatcherPriority.Loaded);

                var shiftedContainer = Assert.IsType<ListBoxItem>(
                    listBox.ItemContainerGenerator.ContainerFromItem("Version 1"));
                var translate = Assert.IsType<TranslateTransform>(shiftedContainer.RenderTransform);
                Assert.True(translate.Y > 0);
                Assert.True(translate.HasAnimatedProperties);
            }
            finally
            {
                window.Close();
                VirtualizedListItemStateBehavior.SetIsEnabled(listBox, false);
            }
        });
    }

    [Fact]
    public void NewScrollResetTokenReturnsRetainedListToTopOnlyOnce()
    {
        RunOnStaThread(() =>
        {
            var listBox = new ListBox
            {
                Width = 360,
                Height = 220,
                ItemsSource = Enumerable.Range(0, 200)
                    .Select(index => $"Mod {index}")
                    .ToArray()
            };
            VirtualizedListItemStateBehavior.SetIsEnabled(listBox, true);

            var window = new Window
            {
                Width = 360,
                Height = 220,
                Content = listBox,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Opacity = 0
            };

            try
            {
                window.Show();
                PumpDispatcher(DispatcherPriority.ApplicationIdle);
                listBox.UpdateLayout();

                var scrollViewer = VisualTreeSearch.FindDescendant<ScrollViewer>(listBox, _ => true)
                    ?? throw new InvalidOperationException("ListBox ScrollViewer was not generated.");
                scrollViewer.ScrollToEnd();
                listBox.UpdateLayout();
                Assert.True(scrollViewer.VerticalOffset > 0);

                VirtualizedListItemStateBehavior.SetScrollResetToken(listBox, 1);
                PumpDispatcher(DispatcherPriority.Loaded);
                Assert.Equal(0, scrollViewer.VerticalOffset);

                scrollViewer.ScrollToVerticalOffset(240);
                listBox.UpdateLayout();
                var retainedOffset = scrollViewer.VerticalOffset;
                Assert.True(retainedOffset > 0);

                VirtualizedListItemStateBehavior.SetScrollResetToken(listBox, 1);
                PumpDispatcher(DispatcherPriority.Loaded);
                Assert.Equal(retainedOffset, scrollViewer.VerticalOffset);

                VirtualizedListItemStateBehavior.SetScrollResetToken(listBox, 2);
                PumpDispatcher(DispatcherPriority.Loaded);
                Assert.Equal(0, scrollViewer.VerticalOffset);
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

    private sealed class PendingItem : IListEntrancePendingItem
    {
        public bool IsGroupEntrancePending { get; set; }
    }
}
