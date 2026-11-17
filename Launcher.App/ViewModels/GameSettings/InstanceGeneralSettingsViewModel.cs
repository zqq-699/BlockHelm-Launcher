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

    [ObservableProperty]
    private string descriptionText = string.Empty;

    internal InstanceGeneralSettingsViewModel(
        GameSettingsEditDialogViewModel editDialog,
        IInstanceFolderService instanceFolderService,
        IStatusService statusService,
        InstanceSettingsPersistenceCoordinator persistence)
    {
        this.editDialog = editDialog;
        this.instanceFolderService = instanceFolderService;
        this.statusService = statusService;
        this.persistence = persistence;
    }

    public event Action<GameSettingsInstanceItem>? DeleteInstanceRequested;

    public string InstanceName => selectedInstance?.Name ?? string.Empty;

    public string InstanceIconSource => selectedInstance?.IconSource ?? string.Empty;

    public string InstanceSubtitle => selectedInstance?.Subtitle ?? string.Empty;

    public string InstanceCreatedAtText => selectedInstance?.Instance.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;

    public void SetSelectedInstance(GameSettingsInstanceItem? value, GameInstance? editableInstance = null)
    {
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
