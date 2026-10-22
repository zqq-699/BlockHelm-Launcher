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

/// <summary>
/// 展示启动器版本与参考项目，并协调手动/启动时更新检查及自更新确认流程。
/// </summary>
public sealed partial class InfoSettingsViewModel : SettingsSectionViewModelBase
{
    // 检查更新与执行更新是两个独立阶段：前者可静默，后者必须经过用户确认。
    private readonly IStatusService statusService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly IExternalLinkService externalLinkService;
    private readonly ILauncherUpdateService launcherUpdateService;
    private readonly ILauncherSelfUpdateService launcherSelfUpdateService;
    private readonly IApplicationExitService applicationExitService;
    private readonly ILogger<InfoSettingsViewModel> logger;
    private LauncherUpdateInfo? availableUpdate;
    private string? updateDialogReleasePageUrl;
    private string? updateDialogDownloadUrl;
    private bool isUpdateCheckRunning;
    private bool isUpdateDialogPreview;

    internal InfoSettingsViewModel(
        SettingsPersistenceCoordinator persistence,
        IStatusService statusService,
        IFloatingMessageService floatingMessageService,
        IExternalLinkService externalLinkService,
        ILauncherUpdateService launcherUpdateService,
        ILauncherSelfUpdateService launcherSelfUpdateService,
        IApplicationExitService applicationExitService,
        IInfoReferenceProjectCatalog referenceProjectCatalog,
        ILogger<InfoSettingsViewModel>? logger = null)
        : base(persistence)
    {
        this.statusService = statusService;
        this.floatingMessageService = floatingMessageService;
        this.externalLinkService = externalLinkService;
        this.launcherUpdateService = launcherUpdateService;
        this.launcherSelfUpdateService = launcherSelfUpdateService;
        this.applicationExitService = applicationExitService;
        this.logger = logger ?? NullLogger<InfoSettingsViewModel>.Instance;
        LauncherVersionText = ResolveLauncherVersion();
        ReferenceProjects = referenceProjectCatalog.GetProjects();
        UpdateChannelOptions =
        [
            new(LauncherUpdateChannel.Release, Strings.Settings_UpdateChannelReleaseTitle),
            new(LauncherUpdateChannel.Beta, Strings.Settings_UpdateChannelBetaTitle)
        ];
        selectedUpdateChannelOption = UpdateChannelOptions[0];
    }

    public string LauncherVersionText { get; }

    public IReadOnlyList<InfoReferenceProjectItem> ReferenceProjects { get; }

    public ObservableCollection<SettingsUpdateChannelOption> UpdateChannelOptions { get; }

    [ObservableProperty]
    private SettingsUpdateChannelOption? selectedUpdateChannelOption;

    [ObservableProperty]
    private bool isUpdateAvailableDialogOpen;

    [ObservableProperty]
    private string updateDialogVersionText = string.Empty;

    [ObservableProperty]
    private string updateDialogMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateReleaseNotes))]
    private string updateReleaseNotesMarkdown = string.Empty;

    [ObservableProperty]
    private bool isCheckingUpdates;

    [ObservableProperty]
    private bool isStartingUpdate;

    public void Load(LauncherSettings settings)
    {
        // 这里只读取更新偏好和当前版本，不在设置页加载时自动发起网络请求。
        LoadState(() => SelectedUpdateChannelOption = UpdateChannelOptions.FirstOrDefault(option =>
            option.Channel == settings.UpdateChannel) ?? UpdateChannelOptions[0]);
    }

    public string CheckUpdatesButtonText => IsCheckingUpdates
        ? Strings.Status_CheckingUpdates
        : Strings.Settings_CheckUpdatesButton;

    public string ConfirmUpdateButtonText => IsStartingUpdate
        ? Strings.Status_DownloadingLauncherUpdate
        : Strings.Dialog_UpdateButton;

    public bool HasUpdateReleaseNotes => !string.IsNullOrWhiteSpace(UpdateReleaseNotesMarkdown);

#if DEBUG
    public bool IsUpdateDialogPreviewAvailable => true;
#else
    public bool IsUpdateDialogPreviewAvailable => false;
#endif
}
