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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private void ShowUpdateAvailableDialog(LauncherUpdateInfo update, bool isPreview = false)
    {
        // 冻结本次更新的发布页与下载地址，弹窗打开后远端状态变化不影响用户确认目标。
        availableUpdate = update;
        isUpdateDialogPreview = isPreview;
        UpdateDialogVersionText = update.DisplayVersion;
        UpdateDialogMessage = string.Format(Strings.Dialog_UpdateAvailableVersionFormat, update.DisplayVersion);
        UpdateReleaseNotes = update.EffectiveReleaseNotesSections;
        updateDialogReleasePageUrl = update.ReleasePageUrl;
        updateDialogDownloadUrl = string.IsNullOrWhiteSpace(update.DownloadUrl)
            ? update.ReleasePageUrl
            : update.DownloadUrl;
        ConfirmUpdateCommand.NotifyCanExecuteChanged();
        IsUpdateAvailableDialogOpen = true;
    }

    private void ReportStatus(string message)
    {
        statusService.Report(message);
    }

    private void ReportVisibleStatus(string message)
    {
        statusService.Report(message);
        floatingMessageService.Show(message);
    }

    private bool TryOpenUpdateUrl(string? url)
    {
        // 下载地址不可用时回退发布页，让用户仍有可操作路径而不是静默失败。
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            return externalLinkService.TryOpen(url);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveLauncherVersion()
    {
        // 优先使用 InformationalVersion 以保留发布元数据，再回退程序集版本支持本地构建。
        var assembly = typeof(InfoSettingsViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Trim();

        var assemblyVersion = assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion)
            ? Strings.Settings_LauncherVersionUnknown
            : assemblyVersion;
    }

    private enum UpdateCheckPresentation
    {
        Manual,
        StartupSilent
    }
}
