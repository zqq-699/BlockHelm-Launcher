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
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Resources;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Settings;

public sealed partial class DownloadSettingsViewModel : SettingsSectionViewModelBase
{
    internal DownloadSettingsViewModel(
        SettingsPersistenceCoordinator persistence,
        CustomFileDownloadViewModel customFileDownload)
        : base(persistence)
    {
        CustomFileDownload = customFileDownload;
        DownloadSourceOptions =
        [
            new(DownloadSourcePreference.Auto, Strings.Settings_DownloadSourceAuto),
            new(DownloadSourcePreference.Official, Strings.Settings_DownloadSourceOfficial),
            new(DownloadSourcePreference.BmclApi, Strings.Settings_DownloadSourceBmclApi)
        ];
        selectedDownloadSourceOption = DownloadSourceOptions[0];
    }

    public event EventHandler<SettingsDownloadSourceChangedEventArgs>? DownloadSourceChanged;
    public event EventHandler<SettingsMaximumDownloadConcurrencyChangedEventArgs>? MaximumDownloadConcurrencyChanged;
    public event EventHandler<SettingsDownloadSpeedLimitChangedEventArgs>? DownloadSpeedLimitChanged;

    public ObservableCollection<SettingsDownloadSourceOption> DownloadSourceOptions { get; }
    public CustomFileDownloadViewModel CustomFileDownload { get; }

    [ObservableProperty]
    private SettingsDownloadSourceOption? selectedDownloadSourceOption;

    [ObservableProperty]
    private int maximumDownloadConcurrency = LauncherDefaults.DefaultMaximumDownloadConcurrency;

    [ObservableProperty]
    private string downloadSpeedLimitMbPerSecondText = string.Empty;

    public int MinimumDownloadConcurrency => LauncherDefaults.MinimumDownloadConcurrency;

    public int MaximumAllowedDownloadConcurrency => LauncherDefaults.MaximumDownloadConcurrency;

    public void Load(LauncherSettings settings)
    {
        LoadState(() =>
        {
            SelectedDownloadSourceOption = DownloadSourceOptions.FirstOrDefault(option =>
                option.Preference == settings.DownloadSourcePreference) ?? DownloadSourceOptions[0];
            MaximumDownloadConcurrency = Math.Clamp(
                settings.MaximumDownloadConcurrency,
                MinimumDownloadConcurrency,
                MaximumAllowedDownloadConcurrency);
            DownloadSpeedLimitMbPerSecondText = FormatDownloadSpeedLimit(settings.DownloadSpeedLimitMbPerSecond);
        });
    }

    partial void OnSelectedDownloadSourceOptionChanged(
        SettingsDownloadSourceOption? oldValue,
        SettingsDownloadSourceOption? newValue)
    {
        if (newValue is null)
        {
            LoadState(() => SelectedDownloadSourceOption = oldValue ?? DownloadSourceOptions[0]);
            return;
        }

        if (!CanPersist)
            return;

        var preference = newValue.Preference;
        Persist(settings => settings.DownloadSourcePreference = preference);
        DownloadSourceChanged?.Invoke(this, new SettingsDownloadSourceChangedEventArgs(preference));
    }

    partial void OnMaximumDownloadConcurrencyChanged(int value)
    {
        var normalized = Math.Clamp(value, MinimumDownloadConcurrency, MaximumAllowedDownloadConcurrency);
        if (value != normalized)
        {
            MaximumDownloadConcurrency = normalized;
            return;
        }

        if (!CanPersist)
            return;

        Persist(settings => settings.MaximumDownloadConcurrency = normalized);
        MaximumDownloadConcurrencyChanged?.Invoke(
            this,
            new SettingsMaximumDownloadConcurrencyChangedEventArgs(normalized));
    }

    partial void OnDownloadSpeedLimitMbPerSecondTextChanged(string value)
    {
        if (!CanPersist)
            return;

        var limit = NormalizeDownloadSpeedLimit(value);
        Persist(settings => settings.DownloadSpeedLimitMbPerSecond = limit);
        DownloadSpeedLimitChanged?.Invoke(this, new SettingsDownloadSpeedLimitChangedEventArgs(limit));
    }

    private static int NormalizeDownloadSpeedLimit(string? value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(parsed, 0)
            : 0;
    }

    private static string FormatDownloadSpeedLimit(int value)
        => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;
}
