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
using System.Windows.Media;
using System.Windows.Threading;
using Launcher.App.Services;
using Launcher.App.ViewModels.Settings;

namespace Launcher.App.Views.Settings;

public partial class SettingsPageView : UserControl
{
    private static readonly string[] SectionOrder =
    [
        nameof(SettingsPageSection.General),
        nameof(SettingsPageSection.Download),
        nameof(SettingsPageSection.Language),
        nameof(SettingsPageSection.LaunchMemory),
        nameof(SettingsPageSection.Java),
        nameof(SettingsPageSection.Theme),
        nameof(SettingsPageSection.Info)
    ];

    private readonly DispatcherTimer memoryRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly PageTransitionService sectionTransitionService;
    private INotifyPropertyChanged? currentViewModelNotifier;
    private FrameworkElement? sectionContentRoot;
    private string previousSectionId = SectionOrder[0];
    private int sectionChangeGeneration;
    private bool transitionPendingAfterReparent;

    public SettingsPageView()
    {
        InitializeComponent();
        sectionTransitionService = new PageTransitionService(
            Dispatcher,
            _ => sectionContentRoot,
            GetCurrentSectionId(),
            SectionOrder);
        memoryRefreshTimer.Tick += MemoryRefreshTimer_Tick;
        Loaded += SettingsPageView_Loaded;
        Unloaded += SettingsPageView_Unloaded;
        DataContextChanged += SettingsPageView_DataContextChanged;
    }

    public FrameworkElement RootElement => PageRoot;

    private void SettingsPageView_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshMemorySnapshot();
        sectionTransitionService.SyncTo(GetCurrentSectionId());
        ResetSectionPresentation();
        memoryRefreshTimer.Start();
    }

    private void SettingsPageView_Unloaded(object sender, RoutedEventArgs e)
    {
        memoryRefreshTimer.Stop();
        sectionChangeGeneration++;
        transitionPendingAfterReparent = false;
        sectionTransitionService.SyncTo(GetCurrentSectionId());
        ResetSectionPresentation();
    }

    private void MemoryRefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshMemorySnapshot();
    }

    private void RefreshMemorySnapshot()
    {
        if (DataContext is SettingsPageViewModel viewModel)
            viewModel.LaunchMemory.RefreshSystemMemorySnapshot();
    }

    private void SettingsPageView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        sectionChangeGeneration++;
        transitionPendingAfterReparent = false;
        if (currentViewModelNotifier is not null)
            currentViewModelNotifier.PropertyChanged -= SettingsPageViewModel_PropertyChanged;

        currentViewModelNotifier = e.NewValue as INotifyPropertyChanged;
        if (currentViewModelNotifier is not null)
            currentViewModelNotifier.PropertyChanged += SettingsPageViewModel_PropertyChanged;

        previousSectionId = GetCurrentSectionId();
        sectionTransitionService.SyncTo(previousSectionId);
        ResetSectionPresentation();
    }

    private void SettingsPageViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsPageViewModel.SelectedSection)
            && sender is SettingsPageViewModel viewModel)
        {
            var nextSectionId = viewModel.SelectedSection?.Section.ToString() ?? SectionOrder[0];
            var previous = previousSectionId;
            previousSectionId = nextSectionId;
            var generation = ++sectionChangeGeneration;
            if (UsesDirectScrollHost(previous) != UsesDirectScrollHost(nextSectionId))
            {
                // Switching scroll hosts reparents the content. Start the transition only after
                // Loaded, otherwise Unloaded cancels it before the first frame.
                transitionPendingAfterReparent = true;
                sectionTransitionService.SyncTo(previous);
                if (sectionContentRoot is not null)
                    sectionContentRoot.Opacity = PageTransitionService.WarmupOpacity;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (generation != sectionChangeGeneration || !transitionPendingAfterReparent)
                        return;
                    transitionPendingAfterReparent = false;
                    if (sectionContentRoot is null || !sectionContentRoot.IsLoaded)
                    {
                        sectionTransitionService.SyncTo(nextSectionId);
                        return;
                    }
                    SettingsListFrame.ScrollViewer.ScrollToVerticalOffset(0);
                    SettingsListFrame.ScrollViewer.UpdateLayout();
                    sectionContentRoot.UpdateLayout();
                    sectionTransitionService.MoveTo(nextSectionId);
                }));
                return;
            }

            transitionPendingAfterReparent = false;
            if (sectionContentRoot is null)
                return;

            SettingsListFrame.ScrollViewer.ScrollToVerticalOffset(0);
            SettingsListFrame.ScrollViewer.UpdateLayout();
            sectionContentRoot.UpdateLayout();
            sectionTransitionService.MoveTo(viewModel.SelectedSection?.Section.ToString() ?? SectionOrder[0]);
        }
    }

    private static bool UsesDirectScrollHost(string sectionId) =>
        sectionId is nameof(SettingsPageSection.Java) or nameof(SettingsPageSection.Info);

    private void ResetSectionPresentation()
    {
        if (sectionContentRoot is null)
            return;

        sectionContentRoot.BeginAnimation(OpacityProperty, null);
        sectionContentRoot.Opacity = 1;

        var transform = EnsureTranslateTransform();
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 0;
    }

    private TranslateTransform EnsureTranslateTransform()
    {
        if (sectionContentRoot?.RenderTransform is TranslateTransform transform)
            return transform;

        transform = new TranslateTransform();
        if (sectionContentRoot is not null)
            sectionContentRoot.RenderTransform = transform;
        return transform;
    }

    private string GetCurrentSectionId()
    {
        return (DataContext as SettingsPageViewModel)?.SelectedSection?.Section.ToString() ?? SectionOrder[0];
    }

    private void SectionContentRoot_OnLoaded(object sender, RoutedEventArgs e)
    {
        sectionContentRoot = sender as FrameworkElement;
        if (transitionPendingAfterReparent)
        {
            if (sectionContentRoot is not null)
                sectionContentRoot.Opacity = PageTransitionService.WarmupOpacity;
            return;
        }
        sectionTransitionService.SyncTo(GetCurrentSectionId());
        ResetSectionPresentation();
    }

    private void SectionContentRoot_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sectionContentRoot, sender))
            sectionContentRoot = null;
    }
}
