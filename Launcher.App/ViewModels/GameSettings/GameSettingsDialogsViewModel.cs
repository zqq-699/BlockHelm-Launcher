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
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Launcher.Domain.Models;
using System.Globalization;
using System.IO;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class GameSettingsDialogsViewModel : ObservableObject
{
    private readonly IGameInstanceService instanceService;
    private readonly IStatusService statusService;
    private readonly GameSettingsDetailsViewModel details;
    private readonly ILogger<GameSettingsDialogsViewModel> logger;

    [ObservableProperty] private bool isDeleteInstanceDialogOpen;
    [ObservableProperty] private GameSettingsInstanceItem? instancePendingDelete;
    [ObservableProperty] private bool isDeleteInstanceDialogBusy;
    [ObservableProperty] private bool hasDeleteInstanceDialogError;
    [ObservableProperty] private bool isDeleteContentDialogOpen;
    private PendingContentDeletion? pendingContentDeletion;
    [ObservableProperty] private bool isReplaceModImportDialogOpen;
    [ObservableProperty] private ModImportConflictRequest? pendingModImportConflict;
    [ObservableProperty] private bool isInvalidImportDialogOpen;
    [ObservableProperty] private string invalidImportDialogMessage = string.Empty;
    [ObservableProperty] private string invalidImportDialogTitle = Strings.Dialog_InvalidSaveImportTitle;
    [ObservableProperty] private bool isModUpdateDialogOpen;
    [ObservableProperty] private ModUpdateConfirmationRequest? pendingModUpdate;
    [ObservableProperty] private bool isModBulkUpdateDialogOpen;
    [ObservableProperty] private ModBulkUpdateConfirmationRequest? pendingModBulkUpdate;

    public GameSettingsDialogsViewModel(
        IGameInstanceService instanceService,
        IStatusService statusService,
        GameSettingsDetailsViewModel details,
        ILogger<GameSettingsDialogsViewModel>? logger = null)
    {
        this.instanceService = instanceService;
        this.statusService = statusService;
        this.details = details;
        this.logger = logger ?? NullLogger<GameSettingsDialogsViewModel>.Instance;
    }

    public event Action<GameSettingsInstanceItem>? InstanceDeleted;

    public string DeleteInstanceDialogTitle => IsDeleteInstanceDialogBusy
        ? Strings.Dialog_DeleteInstanceBusyTitle
        : HasDeleteInstanceDialogError
            ? Strings.Dialog_DeleteInstanceFailedTitle
            : Strings.Dialog_DeleteInstanceTitle;

    public string DeleteInstanceDialogMessage => InstancePendingDelete is null
        ? string.Empty
        : IsDeleteInstanceDialogBusy
            ? string.Format(Strings.Dialog_DeleteInstanceBusyMessageFormat, InstancePendingDelete.Name)
            : HasDeleteInstanceDialogError
                ? Strings.Status_DeleteInstanceFailed
                : string.Format(Strings.Dialog_DeleteInstanceMessageFormat, InstancePendingDelete.Name);

    public string DeleteInstanceDialogActionText => IsDeleteInstanceDialogBusy
        ? Strings.Dialog_DeleteInstanceBusyTitle
        : HasDeleteInstanceDialogError
            ? Strings.Retry_Button
            : Strings.Delete_Button;

    public bool CanShowDeleteInstanceCancelButton => !IsDeleteInstanceDialogBusy;

    private bool CanCancelDeleteInstanceDialog => !IsDeleteInstanceDialogBusy;

    private bool CanConfirmDeleteInstanceDialog => !IsDeleteInstanceDialogBusy && InstancePendingDelete is not null;

    public string DeleteContentDialogTitle => pendingContentDeletion?.Kind switch
    {
        ContentKind.Saves => Strings.Dialog_DeleteSavesTitle,
        ContentKind.ResourcePacks => Strings.Dialog_DeleteResourcePacksTitle,
        ContentKind.ShaderPacks => Strings.Dialog_DeleteShaderPacksTitle,
        _ => Strings.Dialog_DeleteModsTitle
    };

    public string DeleteContentDialogMessage => pendingContentDeletion is null
        ? string.Empty
        : FormatDeleteMessage(pendingContentDeletion);

    public string ReplaceModImportDialogMessage => PendingModImportConflict is null
        ? string.Empty
        : string.Format(Strings.Dialog_ReplaceModImportMessageFormat, PendingModImportConflict.FileName);

    public string ModUpdateDialogTitle => PendingModUpdate is null
        ? Strings.Dialog_ModUpdateTitle
        : string.Format(Strings.Dialog_ModUpdateTitleFormat, PendingModUpdate.Title);

    public string ModUpdateDialogMessage
    {
        get
        {
            if (PendingModUpdate is null)
                return string.Empty;

            var candidate = PendingModUpdate.Candidate;
            var currentVersion = string.IsNullOrWhiteSpace(candidate.CurrentVersionNumber)
                ? Path.GetFileName(candidate.LocalFile.FullPath)
                : candidate.CurrentVersionNumber;
            var targetVersion = string.IsNullOrWhiteSpace(candidate.TargetVersion.VersionNumber)
                ? candidate.TargetVersion.Name
                : candidate.TargetVersion.VersionNumber;
            var source = candidate.Source is ResourceProjectSource.Modrinth
                ? Strings.Resources_ModSourceModrinth
                : Strings.Resources_ModSourceCurseForge;
            var targetFileName = candidate.TargetVersion.FileName;
            if (!candidate.LocalFile.IsEnabled
                && !targetFileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                targetFileName += ".disabled";
            }
            var publishedAt = candidate.TargetVersion.PublishedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                ?? Strings.Dialog_ModUpdateUnknownDate;
            return string.Format(
                Strings.Dialog_ModUpdateMessageFormat,
                currentVersion,
                targetVersion,
                source,
                targetFileName,
                publishedAt);
        }
    }

    public string ModBulkUpdateDialogMessage => PendingModBulkUpdate?.Stage switch
    {
        ModBulkUpdateConfirmationStage.RiskConfirmation => Strings.Dialog_ModUpdateRiskWarning,
        ModBulkUpdateConfirmationStage.Checking => Strings.Status_ModBulkUpdateChecking,
        ModBulkUpdateConfirmationStage.Failed => Strings.Dialog_ModBulkUpdateFailed,
        ModBulkUpdateConfirmationStage.Summary => string.Concat(
            string.Format(
                Strings.Dialog_ModBulkUpdateMessageFormat,
                PendingModBulkUpdate.UpdateCount,
                PendingModBulkUpdate.UpToDateCount,
                PendingModBulkUpdate.NotRecognizedCount,
                PendingModBulkUpdate.UnavailableCount),
            Environment.NewLine,
            Environment.NewLine,
            Strings.Dialog_ModUpdateRiskWarning),
        _ => string.Empty
    };

    public bool CanConfirmModBulkUpdate => PendingModBulkUpdate is
        { Stage: not ModBulkUpdateConfirmationStage.Checking };

    public bool CanCancelModBulkUpdate => PendingModBulkUpdate is
        { Stage: not ModBulkUpdateConfirmationStage.Failed };

    public bool IsModBulkUpdateChecking => PendingModBulkUpdate?.Stage is
        ModBulkUpdateConfirmationStage.Checking;

    public double ModBulkUpdateCheckingProgress => PendingModBulkUpdate?.CheckProgressPercent ?? 0;

    public string ModBulkUpdateCheckingCountText => PendingModBulkUpdate is null
        ? string.Empty
        : string.Format(
            Strings.Dialog_ModBulkUpdateCheckingCountFormat,
            PendingModBulkUpdate.CheckCompletedCount,
            PendingModBulkUpdate.CheckTotalCount);

    public string ModBulkUpdateConfirmButtonText => PendingModBulkUpdate?.Stage is
        ModBulkUpdateConfirmationStage.Summary
            ? PendingModBulkUpdate.UpdateCount > 0
                ? Strings.GameSettings_ModManagementUpdateAllButton
                : Strings.Confirm_Button
            : Strings.Confirm_Button;

    [RelayCommand]
    public void OpenDeleteInstance(GameSettingsInstanceItem instance)
    {
        if (IsDeleteInstanceDialogBusy)
            return;

        IsDeleteInstanceDialogBusy = false;
        HasDeleteInstanceDialogError = false;
        InstancePendingDelete = instance;
        IsDeleteInstanceDialogOpen = true;
    }

    public void OpenDeleteMods(ModDeleteRequest request) =>
        OpenContentDeletion(ContentKind.Mods, request.FullPaths, request.Titles);

    public void OpenDeleteSaves(SaveDeleteRequest request) =>
        OpenContentDeletion(ContentKind.Saves, request.FullPaths, request.Titles);

    public void OpenDeleteResourcePacks(ResourcePackDeleteRequest request) =>
        OpenContentDeletion(ContentKind.ResourcePacks, request.FullPaths, request.Titles);

    public void OpenDeleteShaderPacks(ShaderPackDeleteRequest request) =>
        OpenContentDeletion(ContentKind.ShaderPacks, request.FullPaths, request.Titles);

    public void OpenModImportConflict(ModImportConflictRequest request)
    {
        PendingModImportConflict = request;
        IsReplaceModImportDialogOpen = true;
    }

    public void OpenModUpdate(ModUpdateConfirmationRequest request)
    {
        CloseModBulkUpdateDialog(cancel: true);
        PendingModUpdate?.Resolve(false);
        PendingModUpdate = request;
        OnPropertyChanged(nameof(ModUpdateDialogTitle));
        OnPropertyChanged(nameof(ModUpdateDialogMessage));
        IsModUpdateDialogOpen = true;
    }

    public void OpenModBulkUpdate(ModBulkUpdateConfirmationRequest request)
    {
        PendingModUpdate?.Resolve(false);
        PendingModUpdate = null;
        IsModUpdateDialogOpen = false;
        CloseModBulkUpdateDialog(cancel: true);
        PendingModBulkUpdate = request;
        request.Changed += PendingModBulkUpdate_Changed;
        request.Closed += PendingModBulkUpdate_Closed;
        NotifyModBulkUpdateDialogPropertiesChanged();
        IsModBulkUpdateDialogOpen = true;
    }

    [RelayCommand]
    private void CancelModUpdateDialog()
    {
        var pending = PendingModUpdate;
        PendingModUpdate = null;
        IsModUpdateDialogOpen = false;
        pending?.Resolve(false);
    }

    [RelayCommand]
    private void ConfirmModUpdateDialog()
    {
        var pending = PendingModUpdate;
        PendingModUpdate = null;
        IsModUpdateDialogOpen = false;
        pending?.Resolve(true);
    }

    [RelayCommand]
    private void CancelModBulkUpdateDialog()
    {
        var pending = PendingModBulkUpdate;
        pending?.Cancel();
        CloseModBulkUpdateDialog(cancel: false);
    }

    [RelayCommand(CanExecute = nameof(CanConfirmModBulkUpdate))]
    private void ConfirmModBulkUpdateDialog()
    {
        var pending = PendingModBulkUpdate;
        if (pending is null || !CanConfirmModBulkUpdate)
            return;

        var close = pending.Stage is ModBulkUpdateConfirmationStage.Summary
            or ModBulkUpdateConfirmationStage.Failed;
        pending.Confirm();
        if (close)
            CloseModBulkUpdateDialog(cancel: false);
    }

    public void OpenSaveImportFailure(SaveImportFailureRequest request) =>
        OpenImportFailure(Strings.Dialog_InvalidSaveImportTitle, request.Message);

    public void OpenResourcePackImportFailure(ResourcePackImportFailureRequest request) =>
        OpenImportFailure(Strings.Dialog_InvalidResourcePackImportTitle, request.Message);

    public void OpenShaderPackImportFailure(ShaderPackImportFailureRequest request) =>
        OpenImportFailure(Strings.Dialog_InvalidShaderPackImportTitle, request.Message);

    [RelayCommand(CanExecute = nameof(CanCancelDeleteInstanceDialog))]
    private void CancelDeleteInstanceDialog()
    {
        if (IsDeleteInstanceDialogBusy)
            return;

        IsDeleteInstanceDialogOpen = false;
        InstancePendingDelete = null;
        HasDeleteInstanceDialogError = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDeleteInstanceDialog))]
    private async Task ConfirmDeleteInstanceDialogAsync()
    {
        if (IsDeleteInstanceDialogBusy || InstancePendingDelete is null)
            return;

        var pending = InstancePendingDelete;
        HasDeleteInstanceDialogError = false;
        IsDeleteInstanceDialogBusy = true;
        details.SuspendLocalWatchersForInstanceMove();
        var deletionCommitted = false;
        try
        {
            if (!await instanceService.DeleteInstanceAsync(pending.Instance.Id))
            {
                statusService.Report(Strings.Status_DeleteInstanceFailed);
                HasDeleteInstanceDialogError = true;
                return;
            }

            deletionCommitted = true;
            details.ClearSelectedInstanceIf(pending.Instance.Id);
            statusService.Report(string.Format(Strings.Status_InstanceDeletedFormat, pending.Name));
            InstanceDeleted?.Invoke(pending);
            IsDeleteInstanceDialogOpen = false;
            InstancePendingDelete = null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to delete game instance. InstanceId={InstanceId}", pending.Instance.Id);
            statusService.Report(Strings.Status_DeleteInstanceFailed);
            HasDeleteInstanceDialogError = true;
        }
        finally
        {
            IsDeleteInstanceDialogBusy = false;
            // 删除提交后只解除挂起，不能再用旧实例上下文重启 watcher；失败时恢复当前活动分区。
            details.ResumeLocalWatchersAfterInstanceMove(restart: !deletionCommitted);
        }
    }

    [RelayCommand]
    private void CancelDeleteContentDialog()
    {
        IsDeleteContentDialogOpen = false;
        SetPendingContentDeletion(null);
    }

    [RelayCommand]
    private async Task ConfirmDeleteContentDialogAsync()
    {
        var pending = pendingContentDeletion;
        if (pending is null)
            return;
        IsDeleteContentDialogOpen = false;
        SetPendingContentDeletion(null);
        switch (pending.Kind)
        {
            case ContentKind.Mods:
                await details.DeleteModsAsync(pending.FullPaths);
                break;
            case ContentKind.Saves:
                await details.DeleteSavesAsync(pending.FullPaths);
                break;
            case ContentKind.ResourcePacks:
                await details.DeleteResourcePacksAsync(pending.FullPaths);
                break;
            case ContentKind.ShaderPacks:
                await details.DeleteShaderPacksAsync(pending.FullPaths);
                break;
        }
    }

    [RelayCommand]
    private void CancelReplaceModImportDialog()
    {
        IsReplaceModImportDialogOpen = false;
        PendingModImportConflict = null;
        details.ResolvePendingModImportConflict(false);
    }

    [RelayCommand]
    private void ConfirmReplaceModImportDialog()
    {
        if (PendingModImportConflict is null)
            return;
        IsReplaceModImportDialogOpen = false;
        PendingModImportConflict = null;
        details.ResolvePendingModImportConflict(true);
    }

    [RelayCommand]
    private void CloseInvalidImportDialog()
    {
        IsInvalidImportDialogOpen = false;
        InvalidImportDialogMessage = string.Empty;
        InvalidImportDialogTitle = Strings.Dialog_InvalidSaveImportTitle;
    }

    partial void OnInstancePendingDeleteChanged(GameSettingsInstanceItem? value)
    {
        OnPropertyChanged(nameof(DeleteInstanceDialogMessage));
        ConfirmDeleteInstanceDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDeleteInstanceDialogBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(DeleteInstanceDialogTitle));
        OnPropertyChanged(nameof(DeleteInstanceDialogMessage));
        OnPropertyChanged(nameof(DeleteInstanceDialogActionText));
        OnPropertyChanged(nameof(CanShowDeleteInstanceCancelButton));
        CancelDeleteInstanceDialogCommand.NotifyCanExecuteChanged();
        ConfirmDeleteInstanceDialogCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasDeleteInstanceDialogErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(DeleteInstanceDialogTitle));
        OnPropertyChanged(nameof(DeleteInstanceDialogMessage));
        OnPropertyChanged(nameof(DeleteInstanceDialogActionText));
    }

    partial void OnPendingModImportConflictChanged(ModImportConflictRequest? value) =>
        OnPropertyChanged(nameof(ReplaceModImportDialogMessage));

    partial void OnPendingModBulkUpdateChanged(ModBulkUpdateConfirmationRequest? value) =>
        NotifyModBulkUpdateDialogPropertiesChanged();

    private void PendingModBulkUpdate_Changed()
    {
        NotifyModBulkUpdateDialogPropertiesChanged();
    }

    private void PendingModBulkUpdate_Closed()
    {
        CloseModBulkUpdateDialog(cancel: false);
    }

    private void CloseModBulkUpdateDialog(bool cancel)
    {
        var pending = PendingModBulkUpdate;
        if (pending is null)
            return;

        pending.Changed -= PendingModBulkUpdate_Changed;
        pending.Closed -= PendingModBulkUpdate_Closed;
        if (cancel)
            pending.Cancel();
        PendingModBulkUpdate = null;
        IsModBulkUpdateDialogOpen = false;
    }

    private void NotifyModBulkUpdateDialogPropertiesChanged()
    {
        OnPropertyChanged(nameof(ModBulkUpdateDialogMessage));
        OnPropertyChanged(nameof(CanConfirmModBulkUpdate));
        OnPropertyChanged(nameof(CanCancelModBulkUpdate));
        OnPropertyChanged(nameof(IsModBulkUpdateChecking));
        OnPropertyChanged(nameof(ModBulkUpdateCheckingProgress));
        OnPropertyChanged(nameof(ModBulkUpdateCheckingCountText));
        OnPropertyChanged(nameof(ModBulkUpdateConfirmButtonText));
        ConfirmModBulkUpdateDialogCommand.NotifyCanExecuteChanged();
    }

    private void OpenContentDeletion(ContentKind kind, IReadOnlyList<string> fullPaths, IReadOnlyList<string> titles)
    {
        SetPendingContentDeletion(new PendingContentDeletion(kind, fullPaths, titles));
        IsDeleteContentDialogOpen = true;
    }

    private void SetPendingContentDeletion(PendingContentDeletion? value)
    {
        if (!SetProperty(ref pendingContentDeletion, value))
            return;
        OnPropertyChanged(nameof(DeleteContentDialogTitle));
        OnPropertyChanged(nameof(DeleteContentDialogMessage));
    }

    private void OpenImportFailure(string title, string message)
    {
        InvalidImportDialogTitle = title;
        InvalidImportDialogMessage = message;
        IsInvalidImportDialogOpen = true;
    }

    private static string FormatDeleteMessage(PendingContentDeletion pending)
    {
        var single = pending.Titles.Count == 1;
        return pending.Kind switch
        {
            ContentKind.Saves => single
                ? string.Format(Strings.Dialog_DeleteSingleSaveMessageFormat, pending.Titles[0])
                : string.Format(Strings.Dialog_DeleteMultipleSavesMessageFormat, pending.Titles.Count),
            ContentKind.ResourcePacks => single
                ? string.Format(Strings.Dialog_DeleteSingleResourcePackMessageFormat, pending.Titles[0])
                : string.Format(Strings.Dialog_DeleteMultipleResourcePacksMessageFormat, pending.Titles.Count),
            ContentKind.ShaderPacks => single
                ? string.Format(Strings.Dialog_DeleteSingleShaderPackMessageFormat, pending.Titles[0])
                : string.Format(Strings.Dialog_DeleteMultipleShaderPacksMessageFormat, pending.Titles.Count),
            _ => single
                ? string.Format(Strings.Dialog_DeleteSingleModMessageFormat, pending.Titles[0])
                : string.Format(Strings.Dialog_DeleteMultipleModsMessageFormat, pending.Titles.Count)
        };
    }

    private enum ContentKind { Mods, Saves, ResourcePacks, ShaderPacks }

    private sealed record PendingContentDeletion(
        ContentKind Kind,
        IReadOnlyList<string> FullPaths,
        IReadOnlyList<string> Titles);
}
