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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class InstanceGeneralSettingsViewModel : GameSettingsDetailsSectionViewModelBase, IDisposable
{
    private static readonly TimeSpan DescriptionSaveDelay = TimeSpan.FromMilliseconds(450);
    private readonly GameSettingsEditDialogViewModel editDialog;
    private readonly IInstanceFolderService instanceFolderService;
    private readonly IStatusService statusService;
    private readonly InstanceSettingsPersistenceCoordinator persistence;
    private INotifyPropertyChanged? selectedInstanceNotifier;
    private GameSettingsInstanceItem? selectedInstance;
    private GameInstance? settingsInstance;
    private bool suppressAutoSave;
    private readonly IInstanceCleanupService? cleanupService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly ILogger logger;
    private InstanceCleanupPreview? cleanupPreview;

    [ObservableProperty] private bool isCleanupDialogOpen;
    [ObservableProperty] private bool isCleanupBusy;

    [ObservableProperty]
    private string descriptionText = string.Empty;

    internal InstanceGeneralSettingsViewModel(
        GameSettingsEditDialogViewModel editDialog,
        IInstanceFolderService instanceFolderService,
        IStatusService statusService,
        InstanceSettingsPersistenceCoordinator persistence,
        IInstanceCleanupService? cleanupService,
        IFloatingMessageService floatingMessageService,
        ILogger<InstanceGeneralSettingsViewModel>? logger)
    {
        this.editDialog = editDialog;
        this.instanceFolderService = instanceFolderService;
        this.statusService = statusService;
        this.persistence = persistence;
        this.cleanupService = cleanupService;
        this.floatingMessageService = floatingMessageService;
        this.logger = logger ?? NullLogger<InstanceGeneralSettingsViewModel>.Instance;
    }

    public event Action<GameSettingsInstanceItem>? DeleteInstanceRequested;

    public string InstanceName => selectedInstance?.Name ?? string.Empty;

    public string InstanceIconSource => selectedInstance?.IconSource ?? string.Empty;

    public string InstanceSubtitle => selectedInstance?.Subtitle ?? string.Empty;

    public string InstanceCreatedAtText => selectedInstance?.Instance.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;

    public void SetSelectedInstance(GameSettingsInstanceItem? value, GameInstance? editableInstance = null)
    {
        if (!ReferenceEquals(selectedInstance?.Instance, value?.Instance))
        {
            IsCleanupDialogOpen = false;
            cleanupPreview = null;
        }
        if (selectedInstanceNotifier is not null)
            selectedInstanceNotifier.PropertyChanged -= SelectedInstance_PropertyChanged;

        selectedInstance = value;
        settingsInstance = editableInstance ?? value?.Instance;
        selectedInstanceNotifier = value;
        if (selectedInstanceNotifier is not null)
            selectedInstanceNotifier.PropertyChanged += SelectedInstance_PropertyChanged;

        NotifyInstanceDisplayChanged();
        if (!persistence.HasPendingSave("description"))
            LoadDescriptionFromInstance();
    }

    public void Dispose()
    {
        if (selectedInstanceNotifier is not null)
            selectedInstanceNotifier.PropertyChanged -= SelectedInstance_PropertyChanged;
        selectedInstanceNotifier = null;
    }

    partial void OnDescriptionTextChanged(string value)
    {
        if (suppressAutoSave || settingsInstance is null)
            return;

        var instance = settingsInstance;
        var normalizedDescription = NormalizeDescription(value);
        persistence.Schedule(
            "description",
            instance,
            target =>
            {
                var originalDescription = target.Description;
                if (string.Equals(originalDescription, normalizedDescription, StringComparison.Ordinal))
                    return null;

                target.Description = normalizedDescription;
                return () => target.Description = originalDescription;
            },
            LoadDescriptionFromInstance,
            DescriptionSaveDelay);
    }

    [RelayCommand]
    private void RequestEditInstance()
    {
        if (selectedInstance is not null)
            editDialog.Open(selectedInstance);
    }

    [RelayCommand]
    private void OpenInstanceDirectory()
    {
        if (selectedInstance is null)
            return;

        var folderPath = selectedInstance.Instance.InstanceDirectory;
        if (!instanceFolderService.DirectoryExists(folderPath))
        {
            statusService.Report(Strings.Status_InstanceFolderNotFound);
            return;
        }

        if (!instanceFolderService.TryOpen(folderPath))
            statusService.Report(Strings.Status_OpenInstanceFolderFailed);
    }

    private bool CanRequestCleanup() => cleanupService is not null && !IsCleanupBusy;

    partial void OnIsCleanupBusyChanged(bool value) => RequestCleanupCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRequestCleanup))]
    private async Task RequestCleanupAsync()
    {
        if (selectedInstance is null || cleanupService is null || IsCleanupBusy) return;
        var instance = selectedInstance.Instance;
        IsCleanupBusy = true;
        try
        {
            var preview = await cleanupService.ScanAsync(instance);
            if (!ReferenceEquals(selectedInstance?.Instance, instance)) return;
            cleanupPreview = preview;
            IsCleanupDialogOpen = true;
        }
        catch (InstanceCleanupBusyException) { floatingMessageService.Show(Strings.GameSettings_CleanupBusy); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Instance cleanup scan failed. InstanceDirectory={InstanceDirectory}", instance.InstanceDirectory);
            floatingMessageService.Show(Strings.GameSettings_CleanupFailed);
        }
        finally { IsCleanupBusy = false; }
    }

    [RelayCommand]
    private void CancelCleanup() { IsCleanupDialogOpen = false; cleanupPreview = null; }

    [RelayCommand]
    private async Task ConfirmCleanupAsync()
    {
        var preview = cleanupPreview;
        if (cleanupService is null || preview is null || IsCleanupBusy) return;
        var categories = Enum.GetValues<InstanceCleanupCategory>().ToHashSet();
        IsCleanupDialogOpen = false;
        cleanupPreview = null;
        IsCleanupBusy = true;
        try
        {
            var result = await cleanupService.CleanAsync(preview, categories);
            var message = string.Format(Strings.GameSettings_CleanupResultFormat, result.Deleted, result.Skipped);
            statusService.Report(message);
            floatingMessageService.Show(message);
        }
        catch (InstanceCleanupBusyException) { floatingMessageService.Show(Strings.GameSettings_CleanupBusy); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Instance cleanup failed. InstanceDirectory={InstanceDirectory}", preview.Instance.InstanceDirectory);
            floatingMessageService.Show(Strings.GameSettings_CleanupFailed);
        }
        finally { IsCleanupBusy = false; }
    }

    [RelayCommand]
    private void RequestDeleteInstance()
    {
        if (selectedInstance is not null)
            DeleteInstanceRequested?.Invoke(selectedInstance);
    }

    private void SelectedInstance_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyInstanceDisplayChanged();
    }

    private void NotifyInstanceDisplayChanged()
    {
        OnPropertyChanged(nameof(InstanceName));
        OnPropertyChanged(nameof(InstanceIconSource));
        OnPropertyChanged(nameof(InstanceSubtitle));
        OnPropertyChanged(nameof(InstanceCreatedAtText));
    }

    private void LoadDescriptionFromInstance()
    {
        suppressAutoSave = true;
        try
        {
            DescriptionText = settingsInstance?.Description ?? string.Empty;
        }
        finally
        {
            suppressAutoSave = false;
        }
    }

    private static string NormalizeDescription(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
