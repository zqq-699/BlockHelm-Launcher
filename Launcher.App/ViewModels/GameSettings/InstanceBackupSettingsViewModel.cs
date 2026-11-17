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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 管理实例备份的创建、筛选、多选删除、目录切换和带回滚的恢复确认流程。
/// </summary>
public sealed partial class InstanceBackupSettingsViewModel : GameSettingsDetailsSectionViewModelBase
{
    // 服务负责持久化和文件操作；本类只编排对话框、命令可用性和列表投影。
    private readonly IGameInstanceService instanceService;
    private readonly InstanceSettingsPersistenceCoordinator persistence;
    private readonly IInstanceBackupService backupService;
    private readonly DownloadTasksPageViewModel downloadTasksPage;
    private readonly IStatusService statusService;
    private readonly IInstanceFolderService instanceFolderService;
    private readonly IFilePickerService filePickerService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly ILogger<InstanceBackupSettingsViewModel> logger;
    // 使用路径保存多选身份，刷新后可将选择重新映射到新建的备份项 ViewModel。
    private readonly HashSet<string> selectedBackupPaths = new(StringComparer.OrdinalIgnoreCase);
    private GameInstance? selectedInstance;
    private IReadOnlyList<InstanceBackupItemViewModel> pendingDeleteBackups = Array.Empty<InstanceBackupItemViewModel>();
    private InstanceBackupItemViewModel? pendingRestoreBackup;
    private IReadOnlyList<InstanceBackupItemViewModel> allBackups = Array.Empty<InstanceBackupItemViewModel>();
    // 每次目录或实例变化都会推进 token，阻止较慢的旧目录读取覆盖当前列表。
    private int refreshToken;

    // 下面的属性共同描述页面状态机：加载、创建、恢复和三个互斥确认对话框。
    [ObservableProperty]
    private int backupCount;

    [ObservableProperty]
    private string backupDirectory = string.Empty;

    [ObservableProperty]
    private string backupSearchQuery = string.Empty;

    [ObservableProperty]
    private bool isMultiSelectMode;

    [ObservableProperty]
    private int selectedBackupCount;

    [ObservableProperty]
    private bool isLoadingBackups;

    [ObservableProperty]
    private bool hasLoadedBackups;

    [ObservableProperty]
    private bool isCreatingBackup;

    [ObservableProperty]
    private bool isRestoringBackup;

    [ObservableProperty]
    private bool isCreateBackupDialogOpen;

    [ObservableProperty]
    private string newBackupName = string.Empty;

    [ObservableProperty]
    private bool isBackupFailureDialogOpen;

    [ObservableProperty]
    private string backupFailureDialogMessage = string.Empty;

    [ObservableProperty]
    private bool isDeleteBackupDialogOpen;

    [ObservableProperty]
    private bool isRestoreBackupDialogOpen;

    [ObservableProperty]
    private int listEntranceAnimationToken;

    [ObservableProperty]
    private IReadOnlyList<InstanceBackupItemViewModel> visibleBackups = Array.Empty<InstanceBackupItemViewModel>();

    [ObservableProperty]
    private IReadOnlyList<object> visibleBackupListItems = Array.Empty<object>();

    internal InstanceBackupSettingsViewModel(
        GameSettingsDetailsViewModel parent,
        IGameInstanceService instanceService,
        InstanceSettingsPersistenceCoordinator persistence,
        IInstanceBackupService backupService,
        DownloadTasksPageViewModel downloadTasksPage,
        IStatusService statusService,
        IInstanceFolderService instanceFolderService,
        IFilePickerService filePickerService,
        IFloatingMessageService floatingMessageService,
        ILogger<InstanceBackupSettingsViewModel>? logger = null)
        : base(parent)
    {
        this.instanceService = instanceService;
        this.persistence = persistence;
        this.backupService = backupService;
        this.downloadTasksPage = downloadTasksPage;
        this.statusService = statusService;
        this.instanceFolderService = instanceFolderService;
        this.filePickerService = filePickerService;
        this.floatingMessageService = floatingMessageService;
        this.logger = logger ?? NullLogger<InstanceBackupSettingsViewModel>.Instance;
    }

    public string BackupInfoText => string.Format(Strings.GameSettings_BackupInfoSummaryFormat, BackupCount);

    public override bool UsesFullViewportLayout => true;

    public string BackupDirectoryText => string.IsNullOrWhiteSpace(BackupDirectory)
        ? Strings.GameSettings_BackupDirectoryNotSelected
        : BackupDirectory;

    public bool CanOpenBackupDirectory => !string.IsNullOrWhiteSpace(BackupDirectory);

    public bool CanCreateBackupNow => selectedInstance is not null
        && !IsCreatingBackup
        && !IsRestoringBackup
        && !string.IsNullOrWhiteSpace(BackupDirectory);

    public bool CanConfirmCreateBackupDialog => !IsCreatingBackup && !IsRestoringBackup && !string.IsNullOrWhiteSpace(NewBackupName);

    public bool CanRestoreBackup => selectedInstance is not null
        && !IsCreatingBackup
        && !IsRestoringBackup
        && !string.IsNullOrWhiteSpace(BackupDirectory);

    public bool CanShowBackupScrollableContent => selectedInstance is not null && HasLoadedBackups;

    public bool HasVisibleBackups => VisibleBackups.Count > 0;

    public bool HasSelectedBackups => SelectedBackupCount > 0;

    public bool AreAllVisibleBackupsSelected => HasVisibleBackups && SelectedBackupCount == VisibleBackups.Count;

    public bool CanShowBackupLoadingState => selectedInstance is not null && IsLoadingBackups && !HasLoadedBackups;

    public bool CanShowBackupEmptyState => selectedInstance is not null
        && HasLoadedBackups
        && !IsLoadingBackups
        && !HasVisibleBackups
        && !string.IsNullOrWhiteSpace(BackupDirectory);

    public string BackupEmptyMessage => string.IsNullOrWhiteSpace(BackupSearchQuery)
        ? Strings.GameSettings_BackupEmpty
        : Strings.GameSettings_BackupSearchEmpty;

    public string SelectAllButtonText => AreAllVisibleBackupsSelected
        ? Strings.GameSettings_BackupCancelSelectAllButton
        : Strings.GameSettings_BackupSelectAllButton;

    public string DeleteBackupDialogMessage => pendingDeleteBackups.Count switch
    {
        0 => string.Empty,
        1 => string.Format(Strings.Dialog_DeleteBackupMessageFormat, pendingDeleteBackups[0].Title),
        _ => string.Format(Strings.Dialog_DeleteMultipleBackupsMessageFormat, pendingDeleteBackups.Count)
    };

    public string RestoreBackupDialogMessage => pendingRestoreBackup is null
        ? string.Empty
        : string.Format(Strings.Dialog_RestoreBackupMessageFormat, pendingRestoreBackup.Title);

    /// <summary>
    /// 切换备份所属实例并清除所有对话框、选择和旧实例加载状态。
    /// </summary>
    public override void OnSelectedInstanceChanged(GameInstance? instance)
    {
        selectedInstance = instance;
        pendingDeleteBackups = Array.Empty<InstanceBackupItemViewModel>();
        pendingRestoreBackup = null;
        selectedBackupPaths.Clear();
        BackupDirectory = instance?.BackupDirectory ?? string.Empty;
        BackupCount = 0;
        SelectedBackupCount = 0;
        allBackups = Array.Empty<InstanceBackupItemViewModel>();
        VisibleBackups = Array.Empty<InstanceBackupItemViewModel>();
        HasLoadedBackups = false;
        IsLoadingBackups = false;
        IsCreateBackupDialogOpen = false;
        IsBackupFailureDialogOpen = false;
        IsDeleteBackupDialogOpen = false;
        IsRestoreBackupDialogOpen = false;
        NewBackupName = string.Empty;
        IsMultiSelectMode = false;
        RefreshVisibleBackupItems(playEntranceAnimation: false);
        OnPropertyChanged(nameof(CanShowBackupScrollableContent));
        OpenBackupFolderCommand.NotifyCanExecuteChanged();
        CreateBackupNowCommand.NotifyCanExecuteChanged();
        RequestRestoreBackupCommand.NotifyCanExecuteChanged();
        _ = RefreshBackupsAsync();
    }

    public override Task OnSectionActivatedAsync()
    {
        return RefreshBackupsAsync();
    }
}
