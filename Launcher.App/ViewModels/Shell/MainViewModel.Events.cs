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

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Resources;
using Launcher.App.ViewModels.Settings;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Shell;

public sealed partial class MainViewModel
{
public Task ActivateCurrentPageAsync()
{
    // 页面变化必须在完整初始化期间也送达协调器；协调器会先记录当前页，
    // 并自行限制尚不能执行的网络与刷新工作。
    return sessionCoordinator.ActivatePageAsync(CurrentPage);
}

public Task SyncExternalInstanceCatalogAsync()
{
    return hasInitialized
        ? sessionCoordinator.RefreshExternalInstanceCatalogAsync()
        : Task.CompletedTask;
}

    private void AccountPage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountPageViewModel.SelectedAccount))
            UpdateAccountNavigationAvatar();
    }

    private void AccountDialog_DroppedThirdPartyAccountAdditionCompleted()
    {
        CurrentPage = NavigationCatalog.AccountPage;
        UpdateSecondaryItems();
        UpdateNavigationSelection();
    }

    private void GameSettingsPage_LocalImportRequested()
    {
        DownloadPage.LocalImportDialog.Open();
    }

    private void GameSettingsPage_MinecraftDirectorySwitchRequested()
    {
        SettingsPage.General.OpenMinecraftDirectorySwitchDialog();
    }

    private void GameSettingsPage_ResourceProjectDetailsRequested(ResourceProjectReference reference)
    {
        ObserveShellTask(OpenResourceProjectDetailsAsync(reference), "open recognized resource project details");
    }

    private async Task OpenResourceProjectDetailsAsync(ResourceProjectReference reference)
    {
        var navigationPrepared = false;
        uiDispatcher.Invoke(() =>
        {
            navigationPrepared = ResourcesPage.BeginLoadProjectDetails(reference);
            if (!navigationPrepared)
                return;

            // 先展示轻量详情加载态并立即启动整页过渡，
            // 远程项目信息不再阻塞用户对点击的视觉反馈。
            CurrentPage = NavigationCatalog.ResourcesPage;
            UpdateSecondaryItems();
            UpdateNavigationSelection();
        });
        if (!navigationPrepared)
            return;

        var project = await ResourcesPage.LoadProjectDetailsAsync(reference);
        if (project is null)
        {
            await uiDispatcher.PostAfterTransitionAsync(
                () => ResourcesPage.CancelLoadProjectDetails(reference));
            return;
        }

        if (!NavigationCatalog.IsPage(CurrentPage, NavigationCatalog.ResourcesPage))
        {
            await uiDispatcher.PostAfterTransitionAsync(
                () => ResourcesPage.CancelLoadProjectDetails(reference));
            return;
        }

        // 详情数据可能在整页动画期间返回；收尾后再替换加载态，
        // 避免复杂绑定和依赖集合初始化抢占过渡帧。
        await uiDispatcher.PostAfterTransitionAsync(
            () => ResourcesPage.ShowProjectDetails(reference, project));
    }

    private void UpdateSecondaryItems()
    {
        SecondaryItems.Clear();
        foreach (var item in NavigationCatalog.CreateSecondaryItems(CurrentPage))
            SecondaryItems.Add(item);
    }

    private void UpdateNavigationSelection()
    {
        // 原地更新稳定导航对象可保留控件动画、焦点和外部引用。
        foreach (var item in NavigationItems)
            item.IsSelected = NavigationCatalog.IsPage(item.Page, CurrentPage);

        DownloadTasksNavigationItem.IsSelected = NavigationCatalog.IsPage(
            DownloadTasksNavigationItem.Page,
            CurrentPage);
    }

    private void SessionCoordinator_NavigationRequested(string page)
    {
        CurrentPage = page;
        UpdateSecondaryItems();
        UpdateNavigationSelection();
    }

    private void HomePage_JavaRequirementNotMet(object? sender, JavaRequirementNotMetEventArgs e)
    {
        pendingJavaRequirementInstance = e.Instance;
        IsJavaRequirementForceLaunchAvailable = e.Reason is JavaRuntimeSelectionFailureReason.ManualRuntimeVersionTooLow
            or JavaRuntimeSelectionFailureReason.ManualRuntimeIncompatible;

        if (e.Reason is JavaRuntimeSelectionFailureReason.ManualRuntimeVersionTooLow)
        {
            JavaRequirementDialogTitle = Strings.Dialog_JavaManualVersionTooLowTitle;
            JavaRequirementDialogMessage = string.Format(
                Strings.Dialog_JavaManualVersionTooLowMessageFormat,
                string.IsNullOrWhiteSpace(e.Instance.Name) ? e.Instance.VersionName : e.Instance.Name,
                e.RequiredMajorVersion?.ToString() ?? Strings.Dialog_LaunchStatusUnknownExitCode,
                e.CurrentMajorVersion?.ToString() ?? Strings.Dialog_LaunchStatusUnknownExitCode);
        }
        else if (e.Reason is JavaRuntimeSelectionFailureReason.ManualRuntimeIncompatible)
        {
            JavaRequirementDialogTitle = Strings.Dialog_JavaManualVersionIncompatibleTitle;
            JavaRequirementDialogMessage = string.Format(
                Strings.Dialog_JavaManualVersionIncompatibleMessageFormat,
                string.IsNullOrWhiteSpace(e.Instance.Name) ? e.Instance.VersionName : e.Instance.Name,
                e.RecommendedMajorVersion?.ToString() ?? Strings.Dialog_LaunchStatusUnknownExitCode,
                e.CurrentVersion ?? e.CurrentMajorVersion?.ToString() ?? Strings.Dialog_LaunchStatusUnknownExitCode);
        }
        else if (e.Reason is JavaRuntimeSelectionFailureReason.AutomaticRuntimeMissing)
        {
            JavaRequirementDialogTitle = Strings.Dialog_JavaRuntimeMissingTitle;
            JavaRequirementDialogMessage = e.RequiredMajorVersion is int missingRequiredMajorVersion
                ? string.Format(Strings.Dialog_JavaCompatibilityNotMetMessageFormat, missingRequiredMajorVersion)
                : Strings.Dialog_JavaRuntimeMissingMessage;
        }
        else
        {
            JavaRequirementDialogTitle = Strings.Dialog_JavaRequirementNotMetTitle;
            JavaRequirementDialogMessage = e.RequiredMajorVersion is int requiredMajorVersion
                ? string.Format(Strings.Dialog_JavaCompatibilityNotMetMessageFormat, requiredMajorVersion)
                : Strings.Dialog_JavaRequirementNotMetMessage;
        }

        IsJavaRequirementDialogOpen = true;
    }

    private void HomePage_LaunchActivityChanged(object? sender, EventArgs e)
    {
        // 启动准备期间会向当前 Minecraft 目录写入文件，设置页据此暂时禁用目录切换。
        SettingsPage.General.SetGameLaunchInProgress(HomePage.IsLaunching);
    }

    private void HomePage_LaunchFailureReported(object? sender, LaunchFailureReport report)
    {
        // Shell 只承载诊断弹窗，错误分类和脱敏内容已由启动服务准备。
        if (!uiDispatcher.HasAccess)
        {
            uiDispatcher.Post(() => HomePage_LaunchFailureReported(sender, report));
            return;
        }

        windowService.RestoreAndActivate();
        LaunchStatusDialog.Show(report);
    }

    private void UpdateAccountNavigationAvatar()
    {
        var accountItem = NavigationItems.FirstOrDefault(item => item.Page == NavigationCatalog.AccountPage);
        if (accountItem is not null)
            accountItem.AvatarUrl = AccountPage.SelectedAccount?.AvatarUrl;
    }

    private Task OpenGameSettingsForInstanceAsync(GameInstance? instance)
    {
        GameSettingsPage.ShowInstanceDetails(instance);
        CurrentPage = NavigationCatalog.GameSettingsPage;
        UpdateSecondaryItems();
        UpdateNavigationSelection();
        return Task.CompletedTask;
    }
}
