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

using System.Reflection;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Settings;

public sealed partial class InfoSettingsViewModel
{
[RelayCommand]
    private void OpenGithubRepository()
    {
        try
        {
            if (!externalLinkService.TryOpen(LauncherProjectLinks.GitHubRepositoryUrl))
                statusService.Report(Strings.Status_OpenGithubRepositoryFailed);
        }
        catch (Exception)
        {
            statusService.Report(Strings.Status_OpenGithubRepositoryFailed);
        }
    }

    [RelayCommand]
    private void OpenReferenceProject(InfoReferenceProjectItem? project)
    {
        // 外部链接统一交给服务校验和打开，ViewModel 不直接启动进程。
        if (project is null)
            return;

        try
        {
            if (!externalLinkService.TryOpen(project.ProjectUrl))
                ReportVisibleStatus(Strings.Status_OpenReferenceProjectFailed);
        }
        catch (Exception)
        {
            ReportVisibleStatus(Strings.Status_OpenReferenceProjectFailed);
        }
    }

    [RelayCommand]
    private void OpenCopyrightNotice()
    {
        OpenLegalDocument(LauncherProjectLinks.GitHubRepositoryUrl);
    }

    [RelayCommand]
    private void OpenOpenSourceLicense()
    {
        OpenLegalDocument(LauncherProjectLinks.GitHubLicenseUrl);
    }

    [RelayCommand]
    private void OpenUserAgreement()
    {
        OpenLegalDocument(LauncherProjectLinks.UserAgreementUrl);
    }

    private void OpenLegalDocument(string url)
    {
        try
        {
            if (externalLinkService.TryOpen(url))
                return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open a legal document link.");
            ReportVisibleStatus(Strings.Status_OpenLegalDocumentFailed);
            return;
        }

        logger.LogWarning("Failed to open a legal document link.");
        ReportVisibleStatus(Strings.Status_OpenLegalDocumentFailed);
    }

    [RelayCommand(CanExecute = nameof(CanCheckUpdates), AllowConcurrentExecutions = true)]
    private async Task CheckUpdatesAsync()
    {
        await CheckUpdatesCoreAsync(UpdateCheckPresentation.Manual);
    }

    public Task CheckUpdatesOnStartupAsync()
    {
        // 启动检查使用静默展示策略：无更新不打扰用户，有更新才打开对话框。
        return CheckUpdatesCoreAsync(UpdateCheckPresentation.StartupSilent);
    }

    [RelayCommand]
    private void OpenUpdateChangelog()
    {
        if (!TryOpenUpdateUrl(updateDialogReleasePageUrl))
            ReportVisibleStatus(Strings.Status_OpenUpdatePageFailed);
    }

    [RelayCommand]
    private void PreviewUpdateDialog()
    {
#if DEBUG
        var markdown = new StringBuilder()
            .AppendLine("# 1.0.0-preview")
            .AppendLine()
            .AppendLine($"## {Strings.Dialog_UpdatePreviewNewFeaturesTitle}")
            .AppendLine()
            .AppendLine($"- {Strings.Dialog_UpdatePreviewNewFeatureOne}")
            .AppendLine($"- {Strings.Dialog_UpdatePreviewNewFeatureTwo}");
        for (var index = 1; index <= 5; index++)
        {
            markdown.AppendLine($"- {string.Format(Strings.Dialog_UpdatePreviewAdditionalItemFormat, index)}");
        }
        markdown
            .AppendLine()
            .AppendLine($"## {Strings.Dialog_UpdatePreviewFixesTitle}")
            .AppendLine()
            .AppendLine($"- {Strings.Dialog_UpdatePreviewFixOne}")
            .AppendLine($"- {Strings.Dialog_UpdatePreviewFixTwo}");
        for (var index = 6; index <= 12; index++)
        {
            markdown.AppendLine($"- {string.Format(Strings.Dialog_UpdatePreviewAdditionalItemFormat, index)}");
        }
        var update = new LauncherUpdateInfo(
            "preview",
            "1.0.0-preview",
            LauncherProjectLinks.GitHubReleasesUrl,
            null,
            markdown.ToString(),
            null,
            LauncherUpdateAssetKind.None,
            0,
            string.Empty);
        ShowUpdateAvailableDialog(update, isPreview: true);
#endif
    }

    [RelayCommand]
    private void OpenUpdateReleaseNotesLink(string? url)
    {
        if (!TryOpenUpdateUrl(url))
            ReportVisibleStatus(Strings.Status_OpenUpdatePageFailed);
    }

    [RelayCommand]
    private void CancelUpdateDialog()
    {
        IsUpdateAvailableDialogOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmUpdate))]
    private async Task ConfirmUpdateAsync()
    {
        // 自更新开始后不再允许重复确认；下载、校验、替换和退出由专用服务负责。
        if (IsStartingUpdate)
            return;

        if (availableUpdate is null)
        {
            ReportVisibleStatus(Strings.Status_OpenUpdatePageFailed);
            return;
        }

        if (!availableUpdate.CanAutoInstall)
        {
            ReportVisibleStatus(Strings.Status_UpdateAutoInstallPackageNotFound);
            return;
        }

        IsStartingUpdate = true;
        ReportStatus(Strings.Status_DownloadingLauncherUpdate);
        try
        {
            var result = await launcherSelfUpdateService.StartUpdateAsync(availableUpdate);
            if (!result.Succeeded)
            {
                ReportVisibleStatus(Strings.Status_LauncherUpdateStartFailed);
                return;
            }

            IsUpdateAvailableDialogOpen = false;
            ReportVisibleStatus(Strings.Status_LauncherUpdateRestarting);
            applicationExitService.Shutdown();
        }
        catch (Exception)
        {
            ReportVisibleStatus(Strings.Status_LauncherUpdateStartFailed);
        }
        finally
        {
            IsStartingUpdate = false;
        }
    }

    private bool CanCheckUpdates()
    {
        return !IsStartingUpdate && !isUpdateCheckRunning;
    }

    private bool CanConfirmUpdate()
    {
        return !IsStartingUpdate && !isUpdateDialogPreview;
    }

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckUpdatesButtonText));
    }

    partial void OnIsStartingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(ConfirmUpdateButtonText));
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        ConfirmUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedUpdateChannelOptionChanged(
        SettingsUpdateChannelOption? oldValue,
        SettingsUpdateChannelOption? newValue)
    {
        if (newValue is null)
        {
            LoadState(() => SelectedUpdateChannelOption = oldValue ?? UpdateChannelOptions[0]);
            return;
        }

        Persist(settings => settings.UpdateChannel = newValue.Channel);
    }
}
