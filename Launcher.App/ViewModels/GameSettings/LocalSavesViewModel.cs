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
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

public sealed class LocalSavesViewModel : IDisposable
{
    private readonly ILocalSaveService localSaveService;
    private readonly IStatusService statusService;
    private readonly ILogger<LocalSavesViewModel> logger;
    private readonly LocalContentRefreshCoordinator<LocalSave> refreshCoordinator;

    public LocalSavesViewModel(
        ILocalSaveService localSaveService,
        IStatusService statusService,
        IInstanceDirectoryMonitor instanceDirectoryMonitor,
        IUiDispatcher? uiDispatcher = null,
        ILogger<LocalSavesViewModel>? logger = null)
    {
        this.localSaveService = localSaveService;
        this.statusService = statusService;
        this.logger = logger ?? NullLogger<LocalSavesViewModel>.Instance;
        refreshCoordinator = new LocalContentRefreshCoordinator<LocalSave>(
            instanceDirectoryMonitor,
            InstanceDirectoryKind.Saves,
            localSaveService.GetSavesAsync,
            ApplySaves,
            ClearSaves,
            _ => ReportStatus(Strings.Status_LoadLocalSavesFailed),
            uiDispatcher ?? ImmediateUiDispatcher.Instance,
            this.logger);
    }

    public event EventHandler? SavesChanged;

    public ObservableCollection<LocalSave> Saves { get; } = [];

    public IReadOnlyList<LocalSave> CurrentSaves => refreshCoordinator.CurrentItems;

    public long Revision => refreshCoordinator.Revision;

    public void SetSelectedInstance(GameInstance? instance)
    {
        refreshCoordinator.SetInstance(instance);
        logger.LogInformation("Selected instance changed for local saves view. InstanceDirectory={InstanceDirectory}", instance?.InstanceDirectory ?? "<none>");
    }

    public void SetSectionActive(bool active) => refreshCoordinator.SetSectionActive(active);

    public Task<bool> RefreshIfInvalidatedAsync() => refreshCoordinator.RefreshIfInvalidatedAsync();

    public void InvalidateSnapshot() => refreshCoordinator.InvalidateSnapshot();

    public void ReleaseObservation() => refreshCoordinator.ReleaseObservation();

    public void SuspendWatcherForInstanceRename() => refreshCoordinator.SuspendForRename();

    public void ResumeWatcherAfterInstanceRename(bool restart = true) => refreshCoordinator.ResumeAfterRename(restart);

    public Task<bool> RefreshSavesAsync() => refreshCoordinator.RefreshAsync();

    public async Task DeleteSaveAsync(LocalSave save)
    {
        await refreshCoordinator.ExecuteInternalOperationAsync(
            () => localSaveService.DeleteAsync(save));
    }

    public async Task<int> DeleteSavesAsync(IEnumerable<LocalSave> saves)
    {
        return await refreshCoordinator.ExecuteInternalOperationAsync(
            () => LocalContentBatchExecutor.ExecuteAsync(
                saves,
                save => save.FullPath,
                save => localSaveService.DeleteAsync(save),
                (save, exception) => logger.LogWarning(exception, "Failed to delete local save. Path={Path}", save.FullPath)));
    }

    public async Task<LocalSaveImportResult> ImportSaveFromArchiveAsync(string archivePath, bool reportStatus = true)
    {
        var instance = refreshCoordinator.SelectedInstance;
        if (instance is null || string.IsNullOrWhiteSpace(archivePath))
            return LocalSaveImportResult.Failure(LocalSaveImportFailureReason.UnexpectedError);
        var result = await refreshCoordinator.ExecuteInternalOperationAsync(
            () => localSaveService.ImportFromArchiveAsync(instance, archivePath),
            static value => value.IsSuccess);
        if (!result.IsSuccess)
        {
            if (reportStatus)
            {
                ReportStatus(result.FailureReason is LocalSaveImportFailureReason.FileNotFound
                    ? Strings.Status_LocalSaveImportFileNotFound
                    : Strings.Status_LocalSaveImportFailed);
            }
            return result;
        }
        if (reportStatus)
            ReportStatus(Strings.Status_LocalSaveImported);
        return result;
    }

    public void Dispose() => refreshCoordinator.Dispose();

    private void ApplySaves(IReadOnlyList<LocalSave> saves)
    {
        if (Saves.SynchronizeByKey(
                saves,
                static save => save.FullPath,
                StringComparer.OrdinalIgnoreCase))
        {
            SavesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearSaves()
    {
        if (Saves.Count == 0)
            return;
        Saves.Clear();
        SavesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReportStatus(string message) => statusService.Report(message);
}
