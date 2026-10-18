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
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Settings;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class InstanceJavaSettingsViewModel : GameSettingsDetailsSectionViewModelBase, IDisposable
{
    private static readonly TimeSpan SaveMergeDelay = TimeSpan.FromMilliseconds(100);
    private readonly InstanceSettingsPersistenceCoordinator persistence;
    private LauncherSettings globalSettings = new();
    private GameInstance? selectedInstance;
    private bool suppressAutoSave;

    [ObservableProperty]
    private GameSettingsLaunchSettingsModeOption? selectedInstanceJavaSettingsModeOption;

    internal InstanceJavaSettingsViewModel(
        InstanceSettingsPersistenceCoordinator persistence,
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        IStatusService statusService,
        IFilePickerService filePickerService,
        IFloatingMessageService floatingMessageService)
    {
        this.persistence = persistence;
        InstanceJavaSettings = new JavaSettingsEditorViewModel(
            javaRuntimeDiscoveryService,
            statusService,
            filePickerService,
            floatingMessageService,
            () => globalSettings.MinecraftDirectory);
        InstanceJavaSettings.IsEditorEnabled = false;
        InstanceJavaSettings.PropertyChanged += InstanceJavaSettings_PropertyChanged;
        InstanceJavaSettings.JavaSelectionChanged += InstanceJavaSettings_JavaSelectionChanged;
        SelectedInstanceJavaSettingsModeOption = LaunchSettingsModeOptions[0];
    }

    public IReadOnlyList<GameSettingsLaunchSettingsModeOption> LaunchSettingsModeOptions { get; } =
    [
        new(LaunchSettingsMode.UseGlobal, Strings.GameSettings_LaunchSettingsModeUseGlobal),
        new(LaunchSettingsMode.PerInstance, Strings.GameSettings_LaunchSettingsModePerInstance)
    ];

    public JavaSettingsEditorViewModel InstanceJavaSettings { get; }

    public ObservableCollection<SettingsJavaSelectionOption> InstanceJavaSelectionOptions => InstanceJavaSettings.JavaSelectionOptions;

    public ObservableCollection<SettingsJavaRuntimeItem> InstanceJavaRuntimes => InstanceJavaSettings.JavaRuntimes;

    public SettingsJavaSelectionOption? SelectedInstanceJavaSelectionOption
    {
        get => InstanceJavaSettings.SelectedJavaSelectionOption;
        set => InstanceJavaSettings.SelectedJavaSelectionOption = value;
    }

    public SettingsJavaRuntimeItem? SelectedInstanceJavaRuntime
    {
        get => InstanceJavaSettings.SelectedJavaRuntime;
        set => InstanceJavaSettings.SelectedJavaRuntime = value;
    }

    public bool AreInstanceJavaSettingsOverridesEnabled => SelectedInstanceJavaSettingsModeOption?.Mode is LaunchSettingsMode.PerInstance;

    public bool IsInstanceJavaManualSelection => InstanceJavaSettings.IsJavaManualSelection;

    public bool CanInteractWithInstanceJavaRuntimeList => AreInstanceJavaSettingsOverridesEnabled && IsInstanceJavaManualSelection;

    public bool IsInstanceJavaRuntimeScanRunning => InstanceJavaSettings.IsJavaRuntimeScanRunning;

    public string InstanceJavaRuntimeListMessage => InstanceJavaSettings.JavaRuntimeListMessage;

    public bool HasInstanceJavaRuntimeListMessage => InstanceJavaSettings.HasJavaRuntimeListMessage;

    public IAsyncRelayCommand RefreshInstanceJavaRuntimesCommand => InstanceJavaSettings.RefreshJavaRuntimesCommand;

    public IAsyncRelayCommand DeepSearchInstanceJavaRuntimesCommand => InstanceJavaSettings.DeepSearchJavaRuntimesCommand;

    public IAsyncRelayCommand ImportInstanceJavaRuntimeCommand => InstanceJavaSettings.ImportJavaRuntimeCommand;

    public void PrimeFromSettings(LauncherSettings launcherSettings)
    {
        globalSettings = launcherSettings;
        if (SelectedInstanceJavaSettingsModeOption?.Mode is LaunchSettingsMode.UseGlobal)
            LoadEditorFromInstance();
    }

    public void SetSelectedInstance(GameInstance? instance)
    {
        selectedInstance = instance;
        LoadEditorFromInstance();
        if (InstanceJavaRuntimes.Count == 0)
            _ = InstanceJavaSettings.RefreshJavaRuntimesForDisplayAsync();
    }

    public override Task OnSectionActivatedAsync()
    {
        return InstanceJavaSettings.RefreshJavaRuntimesForDisplayAsync();
    }

    public void Dispose()
    {
        InstanceJavaSettings.PropertyChanged -= InstanceJavaSettings_PropertyChanged;
        InstanceJavaSettings.JavaSelectionChanged -= InstanceJavaSettings_JavaSelectionChanged;
    }

    partial void OnSelectedInstanceJavaSettingsModeOptionChanged(
        GameSettingsLaunchSettingsModeOption? oldValue,
        GameSettingsLaunchSettingsModeOption? newValue)
    {
        if (newValue is null)
        {
            if (selectedInstance is not null)
                RestoreRequiredModeSelection(oldValue);
            else
            {
                OnPropertyChanged(nameof(AreInstanceJavaSettingsOverridesEnabled));
                OnPropertyChanged(nameof(CanInteractWithInstanceJavaRuntimeList));
            }
            return;
        }

        OnPropertyChanged(nameof(AreInstanceJavaSettingsOverridesEnabled));
        OnPropertyChanged(nameof(CanInteractWithInstanceJavaRuntimeList));
        if (suppressAutoSave)
            return;

        LoadJavaSelectionForMode();
        ScheduleSave();
        _ = InstanceJavaSettings.RefreshJavaRuntimesForDisplayAsync();
    }

    private void InstanceJavaSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(JavaSettingsEditorViewModel.SelectedJavaSelectionOption):
                OnPropertyChanged(nameof(SelectedInstanceJavaSelectionOption));
                break;
            case nameof(JavaSettingsEditorViewModel.SelectedJavaRuntime):
                OnPropertyChanged(nameof(SelectedInstanceJavaRuntime));
                break;
            case nameof(JavaSettingsEditorViewModel.IsJavaRuntimeScanRunning):
                OnPropertyChanged(nameof(IsInstanceJavaRuntimeScanRunning));
                break;
            case nameof(JavaSettingsEditorViewModel.JavaRuntimeListMessage):
                OnPropertyChanged(nameof(InstanceJavaRuntimeListMessage));
                OnPropertyChanged(nameof(HasInstanceJavaRuntimeListMessage));
                break;
            case nameof(JavaSettingsEditorViewModel.HasJavaRuntimeListMessage):
                OnPropertyChanged(nameof(HasInstanceJavaRuntimeListMessage));
                break;
            case nameof(JavaSettingsEditorViewModel.IsJavaManualSelection):
                OnPropertyChanged(nameof(IsInstanceJavaManualSelection));
                OnPropertyChanged(nameof(CanInteractWithInstanceJavaRuntimeList));
                break;
        }
    }

    private void InstanceJavaSettings_JavaSelectionChanged(object? sender, EventArgs e)
    {
        if (!suppressAutoSave)
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        var instance = selectedInstance;
        var selectedMode = SelectedInstanceJavaSettingsModeOption;
        if (instance is null || selectedMode is null)
            return;

        var javaSettingsMode = selectedMode.Mode;
        if (javaSettingsMode is LaunchSettingsMode.PerInstance
            && InstanceJavaSettings.SelectedJavaSelectionOption is null)
        {
            return;
        }
        var javaSelectionMode = javaSettingsMode is LaunchSettingsMode.UseGlobal
            ? instance.JavaSelectionMode
            : InstanceJavaSettings.SelectedMode;
        var selectedJavaExecutablePath = javaSettingsMode is LaunchSettingsMode.UseGlobal
            ? instance.SelectedJavaExecutablePath
            : NormalizeExecutablePath(InstanceJavaSettings.SelectedExecutablePath);

        persistence.Schedule(
            "java",
            instance,
            target =>
            {
                var originalMode = target.JavaSettingsMode;
                var originalSelectionMode = target.JavaSelectionMode;
                var originalExecutablePath = target.SelectedJavaExecutablePath;
                if (originalMode == javaSettingsMode
                    && originalSelectionMode == javaSelectionMode
                    && string.Equals(originalExecutablePath, selectedJavaExecutablePath, StringComparison.Ordinal))
                {
                    return null;
                }

                target.JavaSettingsMode = javaSettingsMode;
                target.JavaSelectionMode = javaSelectionMode;
                target.SelectedJavaExecutablePath = selectedJavaExecutablePath;
                return () =>
                {
                    target.JavaSettingsMode = originalMode;
                    target.JavaSelectionMode = originalSelectionMode;
                    target.SelectedJavaExecutablePath = originalExecutablePath;
                };
            },
            LoadEditorFromInstance,
            SaveMergeDelay);
    }

    private void LoadEditorFromInstance()
    {
        suppressAutoSave = true;
        try
        {
            var mode = selectedInstance?.JavaSettingsMode ?? LaunchSettingsMode.UseGlobal;
            SelectedInstanceJavaSettingsModeOption = ResolveLaunchSettingsModeOption(mode);
            LoadJavaSelectionForMode();
        }
        finally
        {
            suppressAutoSave = false;
        }
    }

    private void LoadJavaSelectionForMode()
    {
        var usePerInstanceSettings = SelectedInstanceJavaSettingsModeOption?.Mode is LaunchSettingsMode.PerInstance;
        var javaSelectionMode = usePerInstanceSettings
            ? selectedInstance?.JavaSelectionMode ?? JavaSelectionMode.Auto
            : globalSettings.JavaSelectionMode;
        var executablePath = usePerInstanceSettings
            ? selectedInstance?.SelectedJavaExecutablePath
            : globalSettings.SelectedJavaExecutablePath;

        InstanceJavaSettings.IsEditorEnabled = usePerInstanceSettings;
        InstanceJavaSettings.LoadSelection(javaSelectionMode, executablePath);
        OnPropertyChanged(nameof(IsInstanceJavaManualSelection));
        OnPropertyChanged(nameof(CanInteractWithInstanceJavaRuntimeList));
    }

    private GameSettingsLaunchSettingsModeOption ResolveLaunchSettingsModeOption(LaunchSettingsMode mode)
    {
        return LaunchSettingsModeOptions.FirstOrDefault(option => option.Mode == mode) ?? LaunchSettingsModeOptions[0];
    }

    private void RestoreRequiredModeSelection(GameSettingsLaunchSettingsModeOption? previousSelection)
    {
        var wasSuppressingAutoSave = suppressAutoSave;
        suppressAutoSave = true;
        try
        {
            SelectedInstanceJavaSettingsModeOption = previousSelection
                ?? ResolveLaunchSettingsModeOption(selectedInstance?.JavaSettingsMode ?? LaunchSettingsMode.UseGlobal);
        }
        finally
        {
            suppressAutoSave = wasSuppressingAutoSave;
        }
    }

    private static string? NormalizeExecutablePath(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
