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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Launcher.App.Behaviors;
using Launcher.App.Controls;
using Launcher.App.Services;
using Launcher.App.Utilities;
using Launcher.App.ViewModels.Resources;

namespace Launcher.App.Views.Resources;

public partial class ResourcesModPageView : UserControl
{
    private const double LoadMoreThreshold = 320d;
    private static readonly TimeSpan CollapseExitOverlap = TimeSpan.FromMilliseconds(300);
    private readonly SlidingContentTransitionCoordinator stepTransition;
    private readonly SlidingContentTransitionCoordinator detailsTransition;
    private ScrollViewer? scrollViewer;
    private ScrollViewer? versionScrollViewer;
    private INotifyPropertyChanged? currentViewModelNotifier;
    private INotifyPropertyChanged? currentVersionsNotifier;
    private bool isVersionAutoLoadQueued;

    public ResourcesModPageView()
    {
        InitializeComponent();

        stepTransition = new SlidingContentTransitionCoordinator(
            this,
            ModStepHost,
            ProjectListStep,
            ProjectDetailsStep);
        detailsTransition = new SlidingContentTransitionCoordinator(
            this,
            DetailsStepHost,
            InstallTargetStep,
            ProjectVersionsStep);

        Loaded += (_, _) =>
        {
            if (currentViewModelNotifier is null)
                AttachViewModelNotifier(DataContext);
            AttachScrollViewers();
            stepTransition.Sync(IsProjectContentStep());
            detailsTransition.Sync(IsProjectVersionsStep());
            if (DataContext is ResourcesModPageViewModel viewModel)
                viewModel.Versions.IsGroupExpansionStagingEnabled = true;
        };
        Unloaded += (_, _) =>
        {
            DetachScrollViewers();
            DetachViewModelNotifier();
        };
        DataContextChanged += ResourcesModPageView_OnDataContextChanged;
    }

    public ScrollViewer ScrollViewer
    {
        get
        {
            AttachScrollViewer();
            return scrollViewer
                ?? throw new InvalidOperationException("Resources mod list scroll viewer is not available.");
        }
    }

    public void RefreshViewport()
    {
        ResourcesModListBox.UpdateLayout();
        VirtualizedListItemStateBehavior.Refresh(ResourcesModListBox);
    }

    private void ResourcesModPageView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModelNotifier();
        AttachViewModelNotifier(e.NewValue);

        stepTransition.Sync(IsProjectContentStep());
        detailsTransition.Sync(IsProjectVersionsStep());
    }

    private void AttachViewModelNotifier(object? dataContext)
    {
        currentViewModelNotifier = dataContext as INotifyPropertyChanged;
        if (currentViewModelNotifier is not null)
            currentViewModelNotifier.PropertyChanged += ResourcesModPageViewModel_OnPropertyChanged;

        if (dataContext is ResourcesModPageViewModel viewModel)
        {
            currentVersionsNotifier = viewModel.Versions;
            currentVersionsNotifier.PropertyChanged += ResourcesProjectVersionsViewModel_OnPropertyChanged;
            viewModel.Versions.IsGroupExpansionStagingEnabled = true;
            viewModel.Versions.GroupExpansionViewportHeight = versionScrollViewer?.ActualHeight ?? 0;
            viewModel.Versions.GroupEntranceRequested += Versions_OnGroupEntranceRequested;
            viewModel.Versions.GroupCollapseRequested += Versions_OnGroupCollapseRequested;
        }
    }

    private void AttachScrollViewers()
    {
        AttachScrollViewer();
        AttachVersionScrollViewer();
    }

    private void DetachViewModelNotifier()
    {
        if (currentViewModelNotifier is not null)
            currentViewModelNotifier.PropertyChanged -= ResourcesModPageViewModel_OnPropertyChanged;

        if (currentVersionsNotifier is not null)
            currentVersionsNotifier.PropertyChanged -= ResourcesProjectVersionsViewModel_OnPropertyChanged;

        if (currentVersionsNotifier is ResourcesProjectVersionsViewModel versions)
        {
            versions.GroupEntranceRequested -= Versions_OnGroupEntranceRequested;
            versions.GroupCollapseRequested -= Versions_OnGroupCollapseRequested;
        }

        currentViewModelNotifier = null;
        currentVersionsNotifier = null;
    }

    private void ResourcesModPageViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourcesModPageViewModel.CurrentStep)
            && sender is ResourcesModPageViewModel viewModel)
        {
            stepTransition.AnimateTo(viewModel.CurrentStep is not ResourcesModPageStep.ProjectList);
            detailsTransition.AnimateTo(viewModel.CurrentStep is ResourcesModPageStep.ProjectVersions);
            if (viewModel.CurrentStep is ResourcesModPageStep.ProjectVersions)
            {
                AttachVersionScrollViewer();
                QueueVersionAutoLoadIfNeeded();
            }
        }

    }

    private void ResourcesProjectVersionsViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ResourcesProjectVersionsViewModel
            && e.PropertyName is nameof(ResourcesProjectVersionsViewModel.IsLoading)
                or nameof(ResourcesProjectVersionsViewModel.IsLoadingMore)
                or nameof(ResourcesProjectVersionsViewModel.HasMore)
                or nameof(ResourcesProjectVersionsViewModel.VisibleVersionCount)
                or nameof(ResourcesProjectVersionsViewModel.CanAutoLoadMoreVersions))
        {
            QueueVersionAutoLoadIfNeeded();
        }
    }

    private void Versions_OnGroupEntranceRequested(IReadOnlyList<object> children)
    {
        VirtualizedListItemStateBehavior.QueueLayoutShiftAnimation(ResourcesModVersionListBox);
        VirtualizedListItemStateBehavior.QueueItemEntranceAnimation(ResourcesModVersionListBox, children);
    }

    private async void Versions_OnGroupCollapseRequested(
        IReadOnlyList<ResourcesModVersionItemViewModel> children,
        Action complete)
    {
        try
        {
            var buttons = new List<ListPageItemButton>();
            foreach (var item in children)
            {
                if (ResourcesModVersionListBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container
                    || container.ActualHeight <= 0
                    || !IsContainerInVersionViewport(container))
                {
                    continue;
                }

                var button = VisualTreeSearch.FindDescendant<ListPageItemButton>(container, _ => true);
                if (button is not null)
                    buttons.Add(button);
            }

            // 从最下面的可见行开始，依次向上错峰淡出。
            for (var index = 0; index < buttons.Count; index++)
                _ = buttons[buttons.Count - 1 - index].PlayExitAnimation(index);

            if (buttons.Count > 0)
            {
                var lastDelay = ListEntranceAnimationDefaults.GetStaggerDelay(buttons.Count - 1);
                var exitDuration = lastDelay + ListEntranceAnimationDefaults.Duration;
                var wait = exitDuration > CollapseExitOverlap
                    ? exitDuration - CollapseExitOverlap
                    : TimeSpan.Zero;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);
            }
        }
        finally
        {
            // 最后一行淡出结束前约 100ms 就开始收回下方内容，让两段动画轻微重叠。
            complete();
        }
    }

    private void CollapseSpacer_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border { DataContext: ResourcesGroupCollapseSpacerItem spacer } border)
            return;

        var animation = new DoubleAnimation(spacer.Height, 0, spacer.Duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        border.BeginAnimation(HeightProperty, animation);
    }

    private bool IsContainerInVersionViewport(ListBoxItem container)
    {
        var listBox = ResourcesModVersionListBox;
        if (listBox.ActualHeight <= 0)
            return true;

        try
        {
            var bounds = container.TransformToAncestor(listBox).TransformBounds(
                new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            return bounds.Bottom > 0 && bounds.Top < listBox.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void AttachScrollViewer()
    {
        ResourcesModListBox.ApplyTemplate();

        var nextScrollViewer = VisualTreeSearch.FindDescendant<ScrollViewer>(ResourcesModListBox, _ => true);
        if (ReferenceEquals(scrollViewer, nextScrollViewer))
            return;

        DetachScrollViewer();
        scrollViewer = nextScrollViewer;
        if (scrollViewer is not null)
            scrollViewer.ScrollChanged += ScrollViewer_OnScrollChanged;
    }

    private void AttachVersionScrollViewer()
    {
        ResourcesModVersionListBox.ApplyTemplate();

        var nextScrollViewer = VisualTreeSearch.FindDescendant<ScrollViewer>(ResourcesModVersionListBox, _ => true);
        if (ReferenceEquals(versionScrollViewer, nextScrollViewer))
            return;

        DetachVersionScrollViewer();
        versionScrollViewer = nextScrollViewer;
        if (versionScrollViewer is not null)
        {
            versionScrollViewer.ScrollChanged += VersionScrollViewer_OnScrollChanged;
            versionScrollViewer.SizeChanged += VersionScrollViewer_OnSizeChanged;
            UpdateGroupExpansionViewportHeight();
        }
    }

    private void DetachScrollViewer()
    {
        if (scrollViewer is not null)
            scrollViewer.ScrollChanged -= ScrollViewer_OnScrollChanged;

        scrollViewer = null;
    }

    private void DetachVersionScrollViewer()
    {
        if (versionScrollViewer is not null)
        {
            versionScrollViewer.ScrollChanged -= VersionScrollViewer_OnScrollChanged;
            versionScrollViewer.SizeChanged -= VersionScrollViewer_OnSizeChanged;
        }

        versionScrollViewer = null;
    }

    private void VersionScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGroupExpansionViewportHeight();
    }

    private void UpdateGroupExpansionViewportHeight()
    {
        if (DataContext is ResourcesModPageViewModel viewModel)
            viewModel.Versions.GroupExpansionViewportHeight = versionScrollViewer?.ActualHeight ?? 0;
    }

    private void DetachScrollViewers()
    {
        DetachScrollViewer();
        DetachVersionScrollViewer();
    }

    private void ScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ResourcesModPageViewModel viewModel
            || sender is not ScrollViewer currentScrollViewer)
        {
            return;
        }

        if (currentScrollViewer.ScrollableHeight <= 0)
            return;

        var remainingDistance = currentScrollViewer.ScrollableHeight - currentScrollViewer.VerticalOffset;
        if (remainingDistance <= LoadMoreThreshold)
            viewModel.BeginLoadMoreProjects();
    }

    private void VersionScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ResourcesModPageViewModel viewModel
            || sender is not ScrollViewer currentScrollViewer)
        {
            return;
        }

        if (currentScrollViewer.ScrollableHeight <= 0)
            return;

        var remainingDistance = currentScrollViewer.ScrollableHeight - currentScrollViewer.VerticalOffset;
        if (remainingDistance <= LoadMoreThreshold)
            viewModel.BeginLoadMoreAvailableVersions();
    }

    private void QueueVersionAutoLoadIfNeeded()
    {
        if (isVersionAutoLoadQueued)
            return;

        isVersionAutoLoadQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            isVersionAutoLoadQueued = false;
            BeginVersionAutoLoadIfNeeded();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void BeginVersionAutoLoadIfNeeded()
    {
        if (DataContext is not ResourcesModPageViewModel viewModel
            || viewModel.CurrentStep is not ResourcesModPageStep.ProjectVersions
            || !viewModel.Versions.HasMore
            || viewModel.Versions.IsLoading
            || viewModel.Versions.IsLoadingMore
            || !viewModel.Versions.CanAutoLoadMoreVersions)
        {
            return;
        }

        AttachVersionScrollViewer();
        if (versionScrollViewer is null)
            return;

        ResourcesModVersionListBox.UpdateLayout();
        if (versionScrollViewer.ScrollableHeight <= 0)
            viewModel.BeginLoadMoreAvailableVersions();
    }

    private bool IsProjectContentStep()
    {
        return DataContext is ResourcesModPageViewModel viewModel
            && viewModel.CurrentStep is not ResourcesModPageStep.ProjectList;
    }

    private bool IsProjectVersionsStep()
    {
        return DataContext is ResourcesModPageViewModel viewModel
            && viewModel.CurrentStep is ResourcesModPageStep.ProjectVersions;
    }

}
