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
using Launcher.App.ViewModels.Shared;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class InstanceModManagementSettingsViewModel
{
    [RelayCommand]
    private async Task RequestModUpdateAsync(ModManagementModItemViewModel? mod)
    {
        if (mod is null
            || !mod.HasProjectDetails
            || IsBulkUpdateBusy
            || selectedInstance is null
            || modUpdateService is null
            || downloadTasksPage is null)
            return;

        var localMod = ResolveLocalMod(mod.FullPath);
        if (localMod is null)
            return;
        var operationPath = Path.GetFullPath(localMod.FullPath);
        if (!activeUpdatePaths.Add(operationPath))
        {
            ReportModUpdateStatus(Strings.Status_ModUpdateAlreadyRunning);
            return;
        }

        NotifyUpdateCommandAvailabilityChanged();

        mod.IsUpdateBusy = true;
        ReportModUpdateStatus(Strings.Status_ModUpdateChecking);
        var instance = selectedInstance;
        try
        {
            var result = await modUpdateService.CheckAsync(instance, localMod);
            if (selectedInstance is null
                || !string.Equals(instance.Id, selectedInstance.Id, StringComparison.Ordinal)
                || !string.Equals(
                    instance.InstanceDirectory,
                    selectedInstance.InstanceDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                ReleaseModUpdate(operationPath, mod);
                return;
            }
            if (result.Status is not ModUpdateCheckStatus.UpdateAvailable || result.Candidate is null)
            {
                ReportModUpdateStatus(result.Status switch
                {
                    ModUpdateCheckStatus.UpToDate => Strings.Status_ModUpdateUpToDate,
                    ModUpdateCheckStatus.NotRecognized => Strings.Status_ModUpdateNotRecognized,
                    _ => Strings.Status_ModUpdateCheckUnavailable
                });
                ReleaseModUpdate(operationPath, mod);
                return;
            }

            var request = new ModUpdateConfirmationRequest(mod.Title, result.Candidate);
            ModUpdateConfirmationRequested?.Invoke(request);
            if (ModUpdateConfirmationRequested is null || !await request.Completion)
            {
                ReleaseModUpdate(operationPath, mod);
                return;
            }

            var task = downloadTasksPage.BeginTask(
                string.Format(Strings.Dialog_ModUpdateTitleFormat, mod.Title),
                string.Format(
                    Strings.DownloadTask_ModUpdateSubtitleFormat,
                    mod.FileName,
                    ResolveUpdatedFileName(result.Candidate)));
            task.Report(new LauncherProgress(
                ModUpdateProgressStages.Preparing,
                Strings.Status_ModUpdatePreparing,
                0));
            ReportModUpdateStatus(Strings.Status_ModUpdateTaskStarted);
            var progress = task.CreateProgress(value => task.Report(value with
            {
                Message = FormatModUpdateProgress(value.Stage)
            }));
            var operation = RunModUpdateAsync(
                instance,
                result.Candidate,
                operationPath,
                mod,
                task,
                progress);
            downloadTasksPage.TrackBackgroundTask(operation);
        }
        catch (OperationCanceledException)
        {
            ReleaseModUpdate(operationPath, mod);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to check mod update. InstanceId={InstanceId} FileName={FileName}",
                instance.Id,
                Path.GetFileName(operationPath));
            ReportModUpdateStatus(Strings.Status_ModUpdateCheckUnavailable);
            ReleaseModUpdate(operationPath, mod);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateAllMods))]
    private void UpdateAllMods()
    {
        if (!CanUpdateAllMods || selectedInstance is null || modUpdateService is null || downloadTasksPage is null)
            return;

        var instance = selectedInstance;
        var mods = localModsViewModel.CurrentMods.ToArray();
        if (mods.Length == 0)
            return;

        var operationPaths = mods
            .Select(mod => Path.GetFullPath(mod.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (operationPaths.Any(activeUpdatePaths.Contains))
        {
            ReportModUpdateStatus(Strings.Status_ModUpdateAlreadyRunning);
            return;
        }

        foreach (var path in operationPaths)
            activeUpdatePaths.Add(path);
        IsBulkUpdateBusy = true;
        SynchronizeModUpdateBusyStates();
        NotifyUpdateCommandAvailabilityChanged();

        var request = new ModBulkUpdateConfirmationRequest();
        ModBulkUpdateConfirmationRequested?.Invoke(request);
        if (ModBulkUpdateConfirmationRequested is null)
        {
            ReleaseBulkModUpdate(operationPaths);
            return;
        }

        var operation = RunBulkModUpdateAsync(instance, mods, operationPaths, request);
        downloadTasksPage.TrackBackgroundTask(operation);
    }

    private async Task RunBulkModUpdateAsync(
        GameInstance instance,
        IReadOnlyList<LocalMod> mods,
        IReadOnlyList<string> operationPaths,
        ModBulkUpdateConfirmationRequest request)
    {
        Launcher.App.ViewModels.Download.DownloadTaskItem? task = null;
        var updateStarted = false;
        try
        {
            if (!await request.RiskCompletion)
                return;
            request.CancellationToken.ThrowIfCancellationRequested();
            if (!IsSelectedInstance(instance))
                return;

            task = downloadTasksPage!.BeginTask(
                Strings.DownloadTask_ModBulkUpdateTitle,
                string.Format(Strings.DownloadTask_ModBulkUpdateSubtitleFormat, mods.Count));
            task.Report(new LauncherProgress(
                ModUpdateProgressStages.BatchChecking,
                Strings.Status_ModBulkUpdateChecking,
                0));
            var progress = task.CreateProgress(value =>
            {
                if (value.Stage is ModUpdateProgressStages.BatchChecking
                    && value.Percent is { } percent)
                {
                    ReportBulkCheckDialogProgress(request, percent);
                }

                task.Report(value with
                {
                    Message = FormatBulkModUpdateProgress(value.Stage)
                });
            });
            request.BeginChecking(mods.Count, () => downloadTasksPage.CancelTask(task));
            request.CancellationToken.ThrowIfCancellationRequested();

            var check = await modUpdateService!
                .CheckManyAsync(instance, mods, progress, task.CancellationToken);
            task.CancellationToken.ThrowIfCancellationRequested();
            if (!IsSelectedInstance(instance))
            {
                downloadTasksPage.CancelTask(task);
                return;
            }

            var candidates = check.Items
                .Where(item => item.Result.Status is ModUpdateCheckStatus.UpdateAvailable
                    && item.Result.Candidate is not null)
                .Select(item => item.Result.Candidate!)
                .ToArray();
            var unavailableCount = check.Items.Count(item => item.Result.Status is ModUpdateCheckStatus.Unavailable);
            var notRecognizedCount = check.Items.Count(item => item.Result.Status is ModUpdateCheckStatus.NotRecognized);
            var upToDateCount = check.Items.Count(item => item.Result.Status is ModUpdateCheckStatus.UpToDate);
            var partialProviderFailureCount = check.Items.Count(item =>
                item.Result.Status is ModUpdateCheckStatus.UpdateAvailable
                && item.Result.SuccessfulProviderCount < check.ProviderCount);

            task.Report(new LauncherProgress(
                ModUpdateProgressStages.BatchWaitingForConfirmation,
                Strings.Status_ModBulkUpdateWaitingForConfirmation,
                20));
            request.ShowSummary(
                candidates.Length,
                upToDateCount,
                notRecognizedCount,
                unavailableCount,
                partialProviderFailureCount);

            if (candidates.Length == 0)
            {
                var noUpdatesMessage = unavailableCount > 0
                    ? string.Format(
                        Strings.Status_ModBulkUpdateCheckIncompleteFormat,
                        upToDateCount,
                        notRecognizedCount,
                        unavailableCount)
                    : string.Format(
                        Strings.Status_ModBulkUpdateNoneFormat,
                        upToDateCount,
                        notRecognizedCount);
                if (unavailableCount > 0)
                    task.Fail(noUpdatesMessage);
                else
                    task.Complete(noUpdatesMessage);
                ReportModUpdateStatus(noUpdatesMessage);
                await request.SummaryCompletion.WaitAsync(task.CancellationToken);
                return;
            }

            if (!await request.SummaryCompletion.WaitAsync(task.CancellationToken))
            {
                downloadTasksPage.CancelTask(task);
                return;
            }

            ReportModUpdateStatus(string.Format(Strings.Status_ModBulkUpdateTaskStartedFormat, candidates.Length));
            updateStarted = true;
            var update = await modUpdateService.UpdateManyAsync(
                instance,
                candidates,
                progress,
                task.CancellationToken);
            var successCount = update.Items.Count(item => item.Result is not null);
            var failedItems = update.Items.Where(item => item.FailureReason is not null).ToArray();
            var failedCount = failedItems.Length;
            var firstFailure = failedItems.FirstOrDefault();
            var firstFailureFileName = firstFailure is not null
                ? Path.GetFileName(firstFailure.Candidate.LocalFile.FullPath)
                : Path.GetFileName(check.Items.FirstOrDefault(item =>
                    item.Result.Status is ModUpdateCheckStatus.Unavailable)?.FullPath);
            var message = failedCount > 0 || unavailableCount > 0
                ? string.Format(
                    Strings.Status_ModBulkUpdatePartialFormat,
                    successCount,
                    failedCount,
                    unavailableCount,
                    firstFailureFileName)
                : string.Format(
                    Strings.Status_ModBulkUpdateCompletedFormat,
                    successCount);
            if (failedCount > 0 || unavailableCount > 0)
                task.Fail(message);
            else
                task.Complete(message);
            ReportModUpdateStatus(message);
            await RefreshModsAfterBulkUpdateAsync(instance);
        }
        catch (OperationCanceledException) when (task?.CancellationToken.IsCancellationRequested is true)
        {
            if (task is not null)
                downloadTasksPage?.CancelTask(task);
            logger.LogInformation(
                "Mod update batch canceled. InstanceId={InstanceId} Count={Count}",
                instance.Id,
                mods.Count);
            if (updateStarted)
                await RefreshModsAfterBulkUpdateAsync(instance);
        }
        catch (Exception) when (request.CancellationToken.IsCancellationRequested
            || task?.CancellationToken.IsCancellationRequested is true)
        {
            if (task is not null)
                downloadTasksPage?.CancelTask(task);
            logger.LogInformation(
                "Mod update batch canceled while the current operation was completing. InstanceId={InstanceId} Count={Count}",
                instance.Id,
                mods.Count);
            if (updateStarted)
                await RefreshModsAfterBulkUpdateAsync(instance);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Mod update batch failed. InstanceId={InstanceId} Count={Count}",
                instance.Id,
                mods.Count);
            task?.Fail(Strings.Status_ModBulkUpdateFailed);
            ReportModUpdateStatus(Strings.Status_ModBulkUpdateFailed);
            if (!updateStarted && request.Stage is ModBulkUpdateConfirmationStage.Checking)
            {
                request.ShowFailure();
                await request.SummaryCompletion;
            }
            if (updateStarted)
                await RefreshModsAfterBulkUpdateAsync(instance);
        }
        finally
        {
            request.Close();
            ReleaseBulkModUpdate(operationPaths);
        }
    }

    private async Task RefreshModsAfterBulkUpdateAsync(GameInstance instance)
    {
        if (!IsSelectedInstance(instance))
            return;

        localModsViewModel.InvalidateSnapshot();
        try
        {
            await localModsViewModel.RefreshModsAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Mod update batch completed but the local mod list could not be refreshed. InstanceId={InstanceId}",
                instance.Id);
        }
    }

    private bool IsSelectedInstance(GameInstance instance) =>
        selectedInstance is not null
        && string.Equals(instance.Id, selectedInstance.Id, StringComparison.Ordinal)
        && string.Equals(
            instance.InstanceDirectory,
            selectedInstance.InstanceDirectory,
            StringComparison.OrdinalIgnoreCase);

    private void ReleaseBulkModUpdate(IReadOnlyList<string> operationPaths)
    {
        void Release()
        {
            foreach (var path in operationPaths)
                activeUpdatePaths.Remove(path);
            IsBulkUpdateBusy = false;
            SynchronizeModUpdateBusyStates();
            NotifyUpdateCommandAvailabilityChanged();
        }

        if (uiDispatcher.HasAccess)
            Release();
        else
            uiDispatcher.Post(Release);
    }

    private async Task RunModUpdateAsync(
        GameInstance instance,
        ModUpdateCandidate candidate,
        string operationPath,
        ModManagementModItemViewModel item,
        Launcher.App.ViewModels.Download.DownloadTaskItem task,
        IProgress<LauncherProgress> progress)
    {
        try
        {
            await modUpdateService!.UpdateAsync(instance, candidate, progress, task.CancellationToken);
            task.Complete(Strings.Status_ModUpdateCompleted);
            ReportModUpdateStatus(Strings.Status_ModUpdateCompleted);
            localModsViewModel.InvalidateSnapshot();
            try
            {
                await localModsViewModel.RefreshModsAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Mod update completed but the local mod list could not be refreshed. InstanceId={InstanceId} FileName={FileName}",
                    instance.Id,
                    Path.GetFileName(operationPath));
            }
        }
        catch (OperationCanceledException) when (task.CancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Mod update canceled. InstanceId={InstanceId} FileName={FileName}",
                instance.Id,
                Path.GetFileName(operationPath));
        }
        catch (ModUpdateConflictException exception)
        {
            var message = exception.Reason switch
            {
                ModUpdateConflictReason.TargetExists => string.Format(
                    Strings.Status_ModUpdateTargetExistsFormat,
                    Path.GetFileName(exception.Path)),
                ModUpdateConflictReason.SourceChanged => Strings.Status_ModUpdateSourceChanged,
                _ => Strings.Status_ModUpdateFailed
            };
            task.Fail(message);
            ReportModUpdateStatus(message);
            logger.LogWarning(
                exception,
                "Mod update stopped by a file conflict. InstanceId={InstanceId} FileName={FileName} Reason={Reason}",
                instance.Id,
                Path.GetFileName(operationPath),
                exception.Reason);
        }
        catch (ResourceProjectIntegrityException exception)
        {
            task.Fail(Strings.Status_ModUpdateIntegrityFailed);
            ReportModUpdateStatus(Strings.Status_ModUpdateIntegrityFailed);
            logger.LogWarning(
                exception,
                "Mod update integrity validation failed. InstanceId={InstanceId} FileName={FileName} VersionId={VersionId}",
                instance.Id,
                Path.GetFileName(operationPath),
                candidate.TargetVersion.VersionId);
        }
        catch (Exception exception)
        {
            task.Fail(Strings.Status_ModUpdateFailed);
            ReportModUpdateStatus(Strings.Status_ModUpdateFailed);
            logger.LogError(
                exception,
                "Mod update failed. InstanceId={InstanceId} FileName={FileName} Provider={Provider} VersionId={VersionId}",
                instance.Id,
                Path.GetFileName(operationPath),
                candidate.Source,
                candidate.TargetVersion.VersionId);
        }
        finally
        {
            ReleaseModUpdate(operationPath, item);
        }
    }

    private void ReleaseModUpdate(string operationPath, ModManagementModItemViewModel item)
    {
        void Release()
        {
            activeUpdatePaths.Remove(operationPath);
            item.IsUpdateBusy = false;
            if (allModsByFullPath.TryGetValue(operationPath, out var current))
                current.IsUpdateBusy = false;
            NotifyUpdateCommandAvailabilityChanged();
        }

        if (uiDispatcher.HasAccess)
            Release();
        else
            uiDispatcher.Post(Release);
    }

    private void ReportModUpdateStatus(string message)
    {
        void Report()
        {
            statusService.Report(message);
            floatingMessageService.Show(message);
        }

        if (uiDispatcher.HasAccess)
            Report();
        else
            uiDispatcher.Post(Report);
    }

    private static string FormatModUpdateProgress(string stage) => stage switch
    {
        ModUpdateProgressStages.Applying => Strings.Status_ModUpdateApplying,
        ModUpdateProgressStages.Downloading => Strings.Status_ModUpdateDownloading,
        _ => Strings.Status_ModUpdatePreparing
    };

    private static string FormatBulkModUpdateProgress(string stage) => stage switch
    {
        ModUpdateProgressStages.BatchWaitingForConfirmation => Strings.Status_ModBulkUpdateWaitingForConfirmation,
        ModUpdateProgressStages.BatchUpdating => Strings.Status_ModBulkUpdateUpdating,
        _ => Strings.Status_ModBulkUpdateChecking
    };

    private void ReportBulkCheckDialogProgress(
        ModBulkUpdateConfirmationRequest request,
        double batchPercent)
    {
        void Report() => request.ReportCheckingProgress(batchPercent);

        if (uiDispatcher.HasAccess)
            Report();
        else
            uiDispatcher.Post(Report);
    }

    private void NotifyUpdateCommandAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanUpdateAllMods));
        UpdateAllModsCommand.NotifyCanExecuteChanged();
    }

    private static string ResolveUpdatedFileName(ModUpdateCandidate candidate)
    {
        var fileName = candidate.TargetVersion.FileName;
        if (!candidate.LocalFile.IsEnabled && !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            fileName += ".disabled";
        return fileName;
    }

    [RelayCommand]
    private void OpenResourceDetails(ModManagementModItemViewModel? mod)
    {
        if (mod?.ProjectReference is { } reference)
            ResourceDetailsRequested?.Invoke(reference);
    }

    [RelayCommand]
    private void InstallOnlineMod()
    {
        if (selectedInstance is null || !IsModManagementSupported)
            return;

        logger.LogInformation(
            "Online mod install requested from instance mod management. InstanceId={InstanceId}, MinecraftVersion={MinecraftVersion}, Loader={Loader}",
            selectedInstance.Id,
            selectedInstance.MinecraftVersion,
            selectedInstance.Loader);
        OnlineModInstallRequested?.Invoke(selectedInstance);
    }

    [RelayCommand]
    private void SetModFilter(ModManagementFilter filter)
    {
        ModFilter = filter;
    }

    [RelayCommand]
    private void ToggleMultiSelectMode()
    {
        if (IsMultiSelectMode)
        {
            ExitMultiSelectMode();
            return;
        }

        EnterMultiSelectMode();
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectAllMods))]
    private void SelectAllMods()
    {
        if (AreAllVisibleModsSelected)
        {
            foreach (var mod in Mods)
                mod.IsSelected = false;

            selectedModPaths.Clear();
            SelectedMod = null;
            UpdateSelectedModState();
            return;
        }

        foreach (var mod in Mods)
        {
            mod.IsSelected = true;
            selectedModPaths.Add(mod.FullPath);
        }

        SelectedMod = null;
        UpdateSelectedModState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMods))]
    private async Task EnableSelectedModsAsync()
    {
        await SetSelectedModsEnabledAsync(enabled: true);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMods))]
    private async Task DisableSelectedModsAsync()
    {
        await SetSelectedModsEnabledAsync(enabled: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMods))]
    private void RequestDeleteSelectedMods()
    {
        var selectedMods = GetSelectedVisibleMods();
        if (selectedMods.Count == 0)
            return;

        DeleteModsRequested?.Invoke(new ModDeleteRequest(
            selectedMods.Select(mod => mod.FullPath).ToArray(),
            selectedMods.Select(mod => mod.Title).ToArray()));
    }

    [RelayCommand]
    private async Task ToggleModEnabledAsync(ModManagementModItemViewModel? mod)
    {
        if (mod is null)
            return;

        var localMod = ResolveLocalMod(mod.FullPath);
        if (localMod is null)
            return;

        var nextPath = GetPathForEnabledState(localMod.FullPath, !localMod.IsEnabled);
        var previousSelectedPath = lastSingleSelectedModPath;
        var wasSelectedInMultiSelect = selectedModPaths.Contains(localMod.FullPath);

        void RestoreSelectionAfterFailure()
        {
            if (IsMultiSelectMode)
            {
                selectedModPaths.Remove(nextPath);
                if (wasSelectedInMultiSelect)
                    selectedModPaths.Add(localMod.FullPath);
            }
            else
            {
                lastSingleSelectedModPath = previousSelectedPath;
            }
        }

        if (IsMultiSelectMode)
        {
            selectedModPaths.Remove(localMod.FullPath);
            if (wasSelectedInMultiSelect)
                selectedModPaths.Add(nextPath);
        }
        else
        {
            lastSingleSelectedModPath = nextPath;
        }

        logger.LogDebug(
            "Toggling local mod enabled state. InstanceId={InstanceId} Path={Path} Enabled={Enabled}",
            selectedInstance?.Id ?? "<none>",
            localMod.FullPath,
            !localMod.IsEnabled);

        try
        {
            suppressLocalCollectionEvents = true;
            try
            {
                await localModsViewModel.ToggleModAsync(localMod);
            }
            finally
            {
                suppressLocalCollectionEvents = false;
            }

            RefreshFromLocalMods();
        }
        catch (ModEnabledStateConflictException exception)
        {
            suppressLocalCollectionEvents = false;
            logger.LogWarning(
                exception,
                "Local mod enabled state target already exists. InstanceId={InstanceId} Path={Path} TargetPath={TargetPath}",
                selectedInstance?.Id ?? "<none>",
                localMod.FullPath,
                exception.TargetPath);
            var message = FormatModEnabledStateTargetExists(exception.TargetPath);
            statusService.Report(message);
            floatingMessageService.Show(message);
            RestoreSelectionAfterFailure();
            RefreshFromLocalMods();
        }
        catch (Exception exception)
        {
            suppressLocalCollectionEvents = false;
            logger.LogError(
                exception,
                "Failed to toggle local mod enabled state. InstanceId={InstanceId} Path={Path}",
                selectedInstance?.Id ?? "<none>",
                localMod.FullPath);
            statusService.Report(localMod.IsEnabled
                ? Strings.Status_SelectedModsDisableFailed
                : Strings.Status_SelectedModsEnableFailed);
            RestoreSelectionAfterFailure();
            RefreshFromLocalMods();
        }
    }

    [RelayCommand]
    private void OpenModFileLocation(ModManagementModItemViewModel? mod)
    {
        if (mod is null)
            return;

        try
        {
            if (!instanceFolderService.TryRevealFile(mod.FullPath))
            {
                logger.LogWarning(
                    "Failed to reveal local mod file. InstanceId={InstanceId} Path={Path}",
                    selectedInstance?.Id ?? "<none>",
                    mod.FullPath);
                statusService.Report(Strings.Status_OpenModFileLocationFailed);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to reveal local mod file. InstanceId={InstanceId} Path={Path}",
                selectedInstance?.Id ?? "<none>",
                mod.FullPath);
            statusService.Report(Strings.Status_OpenModFileLocationFailed);
        }
    }

    [RelayCommand]
    private void RequestDeleteMod(ModManagementModItemViewModel? mod)
    {
        if (mod is null)
            return;

        DeleteModsRequested?.Invoke(new ModDeleteRequest(
            [mod.FullPath],
            [mod.Title]));
    }

    [RelayCommand]
    private void SelectMod(ModManagementModItemViewModel? mod)
    {
        if (mod is null)
        {
            SelectedMod = null;
            if (IsMultiSelectMode)
                selectedModPaths.Clear();
            foreach (var item in Mods)
                item.IsSelected = false;
            UpdateSelectedModState();
            return;
        }

        if (IsMultiSelectMode)
        {
            var isSelected = !mod.IsSelected;
            mod.IsSelected = isSelected;
            if (isSelected)
                selectedModPaths.Add(mod.FullPath);
            else
                selectedModPaths.Remove(mod.FullPath);

            SelectedMod = null;
            UpdateSelectedModState();
            return;
        }

        SelectedMod = mod;
        lastSingleSelectedModPath = mod.FullPath;
        foreach (var item in Mods)
            item.IsSelected = false;
    }

    /// <summary>
    /// 删除路径对应的 Mod；批量操作允许部分失败，并在结束后统一刷新快照和选择状态。
    /// </summary>
    public async Task DeleteModsAsync(IReadOnlyList<string> fullPaths)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);

        // 根据当前快照解析路径，自动忽略筛选/刷新后已失效的选择。
        var modsToDelete = ResolveLocalMods(fullPaths);
        if (modsToDelete.Count == 0)
        {
            ExitMultiSelectMode();
            return;
        }

        logger.LogInformation(
            "Deleting selected mods. InstanceId={InstanceId} Count={Count}",
            selectedInstance?.Id ?? "<none>",
            modsToDelete.Count);
        try
        {
            // 底层逐项删除并返回失败数量，允许用户保留已成功删除的结果。
            var failedCount = await localModsViewModel.DeleteModsAsync(modsToDelete);
            ExitMultiSelectMode();
            ReportBatchOperationResult(
                modsToDelete.Count,
                failedCount,
                Strings.Status_SelectedModsDeletedFormat,
                Strings.Status_SelectedModsDeletePartialFailedFormat);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete selected mods. InstanceId={InstanceId}",
                selectedInstance?.Id ?? "<none>");
            statusService.Report(Strings.Status_SelectedModsDeleteFailed);
        }
    }

    partial void OnInstalledModCountChanged(int value)
    {
        OnPropertyChanged(nameof(InstalledSummaryText));
        RaiseAvailabilityPropertyChanges();
        OnPropertyChanged(nameof(ModEmptyMessage));
    }

    partial void OnEnabledModCountChanged(int value)
    {
        OnPropertyChanged(nameof(InstalledSummaryText));
    }

    partial void OnModSearchQueryChanged(string value)
    {
        RefreshFromLocalMods();
        OnPropertyChanged(nameof(ModEmptyMessage));
    }

    partial void OnModFilterChanged(ModManagementFilter value)
    {
        RefreshFromLocalMods();
        OnPropertyChanged(nameof(IsAllModsFilterSelected));
        OnPropertyChanged(nameof(IsEnabledModsFilterSelected));
        OnPropertyChanged(nameof(IsDisabledModsFilterSelected));
        OnPropertyChanged(nameof(ModEmptyMessage));
    }

    partial void OnSelectedModCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedMods));
        OnPropertyChanged(nameof(AreAllVisibleModsSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllModsCommand.NotifyCanExecuteChanged();
        EnableSelectedModsCommand.NotifyCanExecuteChanged();
        DisableSelectedModsCommand.NotifyCanExecuteChanged();
        RequestDeleteSelectedModsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AreAllVisibleModsSelected));
        OnPropertyChanged(nameof(SelectAllButtonText));
        SelectAllModsCommand.NotifyCanExecuteChanged();
    }
}
