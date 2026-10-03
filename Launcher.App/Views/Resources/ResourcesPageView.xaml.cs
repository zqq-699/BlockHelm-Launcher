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
using Launcher.App.Utilities;
using Launcher.App.ViewModels.Resources;

namespace Launcher.App.Views.Resources;

public partial class ResourcesPageView : UserControl
{
    private static readonly string[] SectionOrder =
    [
        "mods",
        "resource_packs",
        "shader_packs",
        "worlds",
        "modpacks"
    ];

    private readonly PageTransitionService sectionTransitionService;
    private INotifyPropertyChanged? currentViewModelNotifier;
    private FrameworkElement? sectionContentRoot;
    private bool isEnsureCurrentSectionLoadedQueued;

    public ResourcesPageView()
    {
        InitializeComponent();
        sectionTransitionService = new PageTransitionService(
            Dispatcher,
            _ => sectionContentRoot,
            GetCurrentSectionId(),
            SectionOrder);

        Loaded += ResourcesPageView_Loaded;
        DataContextChanged += ResourcesPageView_DataContextChanged;
        IsVisibleChanged += ResourcesPageView_IsVisibleChanged;
    }

    public FrameworkElement RootElement => PageRoot;

    private void ResourcesPageView_Loaded(object sender, RoutedEventArgs e)
    {
        sectionTransitionService.SyncTo(GetCurrentSectionId());
        ResetSectionPresentation();
        QueueEnsureCurrentSectionLoadedIfVisible();
    }

    private void ResourcesPageView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (currentViewModelNotifier is not null)
            currentViewModelNotifier.PropertyChanged -= ResourcesPageViewModel_PropertyChanged;

        currentViewModelNotifier = e.NewValue as INotifyPropertyChanged;
        if (currentViewModelNotifier is not null)
            currentViewModelNotifier.PropertyChanged += ResourcesPageViewModel_PropertyChanged;

        sectionTransitionService.SyncTo(GetCurrentSectionId());
        ResetSectionPresentation();
        QueueEnsureCurrentSectionLoadedIfVisible();
    }

    private void ResourcesPageView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        QueueEnsureCurrentSectionLoadedIfVisible();
    }

    private void ResourcesPageViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourcesPageViewModel.SelectedSection)
            && sender is ResourcesPageViewModel viewModel)
        {
            if (sectionContentRoot is null)
                return;

            if (!IsVisible)
            {
                // 跨页深链接会在资源页隐藏时预先选好分区。
                // 此时只同步终态，不强制布局、刷新虚拟列表或启动分区动画。
                sectionTransitionService.SyncTo(viewModel.SelectedSection?.Id ?? SectionOrder[0]);
                ResetSectionPresentation();
                return;
            }

            sectionContentRoot.UpdateLayout();
            ResetCurrentSectionScrollPosition();
            sectionTransitionService.MoveTo(viewModel.SelectedSection?.Id ?? SectionOrder[0]);
        }
    }

    private void ResetCurrentSectionScrollPosition()
    {
        if (sectionContentRoot is null)
            return;

        var modPageView = VisualTreeSearch.FindDescendant<ResourcesModPageView>(sectionContentRoot, _ => true);
        if (modPageView is null)
            return;

        try
        {
            modPageView.ScrollViewer.ScrollToVerticalOffset(0);
            modPageView.RefreshViewport();
        }
        catch (InvalidOperationException)
        {
            // The virtualized list template may not be available before the Mod section is realized.
        }
    }

    private void QueueEnsureCurrentSectionLoadedIfVisible()
    {
        if (!IsVisible
            || isEnsureCurrentSectionLoadedQueued
            || DataContext is not ResourcesPageViewModel)
        {
            return;
        }

        isEnsureCurrentSectionLoadedQueued = true;
        // 这里会做一次强制布局并触发分区加载，实测撞进动画时占用 21ms，必须让位给过渡。
        UiTransitionGate.RunWhenIdle(
            () =>
            {
                isEnsureCurrentSectionLoadedQueued = false;
                if (!IsVisible || DataContext is not ResourcesPageViewModel viewModel)
                    return;

                UpdateLayout();
                ResetCurrentSectionScrollPosition();
                viewModel.BeginEnsureCurrentSectionLoaded();
            });
    }

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
        return (DataContext as ResourcesPageViewModel)?.SelectedSection?.Id ?? SectionOrder[0];
    }

    private void SectionContentRoot_OnLoaded(object sender, RoutedEventArgs e)
    {
        sectionContentRoot = sender as FrameworkElement;
        sectionTransitionService.SyncTo(GetCurrentSectionId());
        ResetSectionPresentation();
    }

    private void SectionContentRoot_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sectionContentRoot, sender))
            sectionContentRoot = null;
    }
}
