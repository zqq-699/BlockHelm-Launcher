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
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Shell;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Settings;

public sealed partial class ThemeSettingsViewModel : SettingsSectionViewModelBase
{
    private const string CustomAccentColorId = "Custom";
    private readonly IThemeService themeService;
    private readonly LauncherBackgroundViewModel? launcherBackground;

    internal ThemeSettingsViewModel(
        SettingsPersistenceCoordinator persistence,
        IThemeService themeService,
        LauncherBackgroundViewModel? launcherBackground)
        : base(persistence)
    {
        this.themeService = themeService;
        this.launcherBackground = launcherBackground;
        ThemeOptions =
        [
            new(LauncherDefaults.DefaultTheme, Strings.Settings_ThemeDarkTitle),
            new("Light", Strings.Settings_ThemeLightTitle)
        ];
        AccentColorOptions =
        [
            new(LauncherAccentColors.Blue, Strings.Settings_AccentColorBlueTitle),
            new(LauncherAccentColors.Cyan, Strings.Settings_AccentColorCyanTitle),
            new(LauncherAccentColors.Green, Strings.Settings_AccentColorGreenTitle),
            new(LauncherAccentColors.Emerald, Strings.Settings_AccentColorEmeraldTitle),
            new(LauncherAccentColors.Purple, Strings.Settings_AccentColorPurpleTitle),
            new(LauncherAccentColors.Pink, Strings.Settings_AccentColorPinkTitle),
            new(LauncherAccentColors.Orange, Strings.Settings_AccentColorOrangeTitle),
            new(LauncherAccentColors.Amber, Strings.Settings_AccentColorAmberTitle),
            new(CustomAccentColorId, Strings.Settings_AccentColorCustomTitle)
        ];
        BackgroundEffectOptions =
        [
            new(LauncherBackgroundEffects.None, Strings.Settings_BackgroundEffectNoneTitle),
            new(LauncherBackgroundEffects.Acrylic, Strings.Settings_BackgroundEffectAcrylicTitle),
            new(LauncherBackgroundEffects.Image, Strings.Settings_BackgroundEffectImageTitle)
        ];
        selectedThemeOption = ThemeOptions[0];
        selectedAccentColorOption = AccentColorOptions[0];
        selectedBackgroundEffectOption = BackgroundEffectOptions[1];
    }

    public ObservableCollection<SettingsThemeOption> ThemeOptions { get; }
    public ObservableCollection<SettingsAccentColorOption> AccentColorOptions { get; }
    public ObservableCollection<SettingsBackgroundEffectOption> BackgroundEffectOptions { get; }

    [ObservableProperty] private SettingsThemeOption? selectedThemeOption;
    [ObservableProperty] private SettingsAccentColorOption? selectedAccentColorOption;
    [ObservableProperty] private string customAccentColorText = string.Empty;
    [ObservableProperty] private SettingsBackgroundEffectOption? selectedBackgroundEffectOption;
    [ObservableProperty] private bool followSystemTheme = true;
    [ObservableProperty] private int launcherBackgroundOpacityPercent = LauncherDefaults.DefaultLauncherBackgroundOpacityPercent;
    [ObservableProperty] private bool enableImageBackgroundControlBlur = LauncherDefaults.DefaultEnableImageBackgroundControlBlur;

    public bool IsThemeSelectionVisible => !FollowSystemTheme;
    public bool IsCustomAccentColorVisible => SelectedAccentColorOption?.Id == CustomAccentColorId;
    public bool IsBackgroundImageSelectionVisible => SelectedBackgroundEffectOption?.IsImageSelected ?? false;
    public bool IsBackgroundOpacityVisible => SelectedBackgroundEffectOption?.IsAcrylicEnabled ?? true;
    public string LauncherBackgroundOpacityText => $"{LauncherBackgroundOpacityPercent}%";

    public void Load(LauncherSettings settings)
    {
        LoadState(() =>
        {
            FollowSystemTheme = settings.ThemeFollowSystem;
            SelectedThemeOption = ThemeOptions.FirstOrDefault(option =>
                string.Equals(option.Id, settings.Theme, StringComparison.OrdinalIgnoreCase)) ?? ThemeOptions[0];
            var isCustomAccent = LauncherAccentColors.TryNormalizeCustom(settings.AccentColor, out var customAccent);
            CustomAccentColorText = isCustomAccent ? customAccent : string.Empty;
            SelectedAccentColorOption = isCustomAccent
                ? AccentColorOptions.Last()
                : AccentColorOptions.FirstOrDefault(option =>
                    string.Equals(option.Id, settings.AccentColor, StringComparison.OrdinalIgnoreCase)) ?? AccentColorOptions[0];
            var backgroundEffect = LauncherBackgroundEffects.Normalize(settings.LauncherBackgroundEffect);
            SelectedBackgroundEffectOption = BackgroundEffectOptions.First(option =>
                string.Equals(option.Id, backgroundEffect, StringComparison.Ordinal));
            LauncherBackgroundOpacityPercent = Math.Clamp(settings.LauncherBackgroundOpacityPercent, 0, 100);
            EnableImageBackgroundControlBlur = settings.EnableImageBackgroundControlBlur;
        });
        ApplyCustomAccentColorCommand.NotifyCanExecuteChanged();
    }

    partial void OnFollowSystemThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThemeSelectionVisible));
        ApplyPreferenceAndPersist();
    }

    partial void OnSelectedThemeOptionChanged(SettingsThemeOption? oldValue, SettingsThemeOption? newValue)
    {
        if (newValue is null)
        {
            LoadState(() => SelectedThemeOption = oldValue ?? ThemeOptions[0]);
            return;
        }

        ApplyPreferenceAndPersist();
    }

    partial void OnSelectedAccentColorOptionChanged(
        SettingsAccentColorOption? oldValue,
        SettingsAccentColorOption? newValue)
    {
        if (newValue is null)
        {
            LoadState(() => SelectedAccentColorOption = oldValue ?? AccentColorOptions[0]);
            return;
        }

        OnPropertyChanged(nameof(IsCustomAccentColorVisible));
        ApplyCustomAccentColorCommand.NotifyCanExecuteChanged();
        if (!CanPersist)
            return;
        if (IsCustomAccentColorVisible)
        {
            ApplyCustomAccentColor();
            return;
        }
        var accent = newValue.Id;
        themeService.ApplyAccent(accent);
        Persist(settings => settings.AccentColor = accent);
        ApplyCustomAccentColorCommand.NotifyCanExecuteChanged();
    }

    partial void OnCustomAccentColorTextChanged(string value)
    {
        ApplyCustomAccentColorCommand.NotifyCanExecuteChanged();
    }

    private bool CanApplyCustomAccentColor() =>
        CanPersist && IsCustomAccentColorVisible
        && LauncherAccentColors.TryNormalizeCustom(CustomAccentColorText, out var accent)
        && !string.Equals(Settings.AccentColor, accent, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanApplyCustomAccentColor))]
    private void ApplyCustomAccentColor()
    {
        if (!CanApplyCustomAccentColor()
            || !LauncherAccentColors.TryNormalizeCustom(CustomAccentColorText, out var accent))
            return;

        themeService.ApplyAccent(accent);
        Persist(settings => settings.AccentColor = accent);
        ApplyCustomAccentColorCommand.NotifyCanExecuteChanged();
    }

    partial void OnLauncherBackgroundOpacityPercentChanged(int value)
    {
        var normalized = Math.Clamp(value, 0, 100);
        if (value != normalized)
        {
            LauncherBackgroundOpacityPercent = normalized;
            return;
        }
        OnPropertyChanged(nameof(LauncherBackgroundOpacityText));
        if (!CanPersist)
            return;
        themeService.ApplyBackgroundOpacity(normalized);
        Persist(settings => settings.LauncherBackgroundOpacityPercent = normalized);
    }

    partial void OnEnableImageBackgroundControlBlurChanged(bool value)
    {
        if (!CanPersist)
            return;

        var backgroundEffect = SelectedBackgroundEffectOption?.Id
                               ?? LauncherDefaults.DefaultLauncherBackgroundEffect;
        themeService.ApplyBackgroundEffect(backgroundEffect, value);
        Persist(settings => settings.EnableImageBackgroundControlBlur = value);
    }

    [RelayCommand]
    private void OpenLauncherBackgroundImageFolder()
    {
        launcherBackground?.TryOpenDirectory();
    }

    [RelayCommand]
    private void RefreshLauncherBackgroundImage()
    {
        launcherBackground?.Refresh();
    }

    [RelayCommand]
    private void ClearLauncherBackgroundImages()
    {
        launcherBackground?.ClearImages();
    }

    partial void OnSelectedBackgroundEffectOptionChanged(
        SettingsBackgroundEffectOption? oldValue,
        SettingsBackgroundEffectOption? newValue)
    {
        if (newValue is null)
        {
            LoadState(() => SelectedBackgroundEffectOption = oldValue ?? BackgroundEffectOptions[1]);
            return;
        }

        OnPropertyChanged(nameof(IsBackgroundImageSelectionVisible));
        OnPropertyChanged(nameof(IsBackgroundOpacityVisible));
        if (!CanPersist)
            return;
        themeService.ApplyBackgroundEffect(newValue.Id, EnableImageBackgroundControlBlur);
        launcherBackground?.ApplyEffect(newValue.Id, reportFailure: newValue.IsImageSelected);
        Persist(settings =>
        {
            settings.LauncherBackgroundEffect = newValue.Id;
        });
    }

    private void ApplyPreferenceAndPersist()
    {
        if (!CanPersist || SelectedThemeOption is null)
            return;
        var theme = SelectedThemeOption.Id;
        themeService.ApplyPreference(theme, FollowSystemTheme, LauncherBackgroundOpacityPercent);
        Persist(settings =>
        {
            settings.Theme = theme;
            settings.ThemeFollowSystem = FollowSystemTheme;
        });
    }
}
