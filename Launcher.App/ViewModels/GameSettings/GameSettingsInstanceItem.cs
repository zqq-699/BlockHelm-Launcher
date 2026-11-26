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
using Launcher.App.Utilities;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class GameSettingsInstanceItem : ObservableObject
{
    private InstanceCatalogEntrySnapshot catalogSnapshot;

    public GameSettingsInstanceItem(GameInstance instance, string versionType)
    {
        Instance = instance;
        VersionType = NormalizeVersionType(versionType);
        catalogSnapshot = InstanceCatalogEntrySnapshot.Create(instance);
    }

    public GameInstance Instance { get; private set; }

    public string VersionType { get; private set; }

    public string Name => GameInstanceDisplayFormatter.GetName(Instance);

    public string MinecraftVersion => GameInstanceDisplayFormatter.GetMinecraftVersion(Instance);

    public string VersionName => GameInstanceDisplayFormatter.GetVersionName(Instance);

    public LoaderKind Loader => Instance.Loader;

    public bool HasModLoader => Loader is not LoaderKind.Vanilla;

    public bool IsRelease => VersionType.Equals("release", StringComparison.OrdinalIgnoreCase);

    public bool IsSnapshot => VersionType.Equals("snapshot", StringComparison.OrdinalIgnoreCase);

    public bool IsAprilFools => MinecraftAprilFoolsVersionClassifier.IsAprilFoolsVersion(MinecraftVersion)
        || MinecraftAprilFoolsVersionClassifier.IsAprilFoolsVersion(VersionName);

    public bool IsBeta => VersionType.Equals("old_beta", StringComparison.OrdinalIgnoreCase);

    public bool IsAlpha => VersionType.Equals("old_alpha", StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => MinecraftVersionTypeDisplayProvider.GetLabel(IsAprilFools ? "april_fools" : VersionType);

    public string LoaderLabel => GameInstanceDisplayFormatter.GetLoaderLabel(Loader);

    public string LoaderVersionDisplay => LoaderVersionDisplayFormatter.Format(Loader, Instance.LoaderVersion);

    public string Subtitle => GameInstanceDisplayFormatter.GetSubtitle(Instance);

    public string UpdatedDateText => Instance.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string IconSource => MinecraftVersionIconResolver.Resolve(Instance, VersionType, MinecraftVersion);

    [ObservableProperty]
    private bool isSelected;

    public bool Update(GameInstance instance, string versionType)
    {
        var normalizedVersionType = NormalizeVersionType(versionType);
        var nextSnapshot = InstanceCatalogEntrySnapshot.Create(instance);
        var changed = catalogSnapshot != nextSnapshot
            || !string.Equals(VersionType, normalizedVersionType, StringComparison.OrdinalIgnoreCase);

        Instance = instance;
        VersionType = normalizedVersionType;
        catalogSnapshot = nextSnapshot;
        if (!changed)
            return false;

        NotifyDisplayPropertiesChanged();
        return true;
    }

    public bool MatchesSearch(string query)
    {
        return Contains(Name, query)
            || Contains(MinecraftVersion, query)
            || Contains(VersionName, query)
            || Contains(LoaderLabel, query)
            || Contains(Instance.LoaderVersion ?? string.Empty, query)
            || Contains(TypeLabel, query);
    }

    public static string NormalizeVersionType(string? type)
    {
        return MinecraftVersionIconResolver.NormalizeVersionType(type);
    }

    private static bool Contains(string value, string query)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyDisplayPropertiesChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(MinecraftVersion));
        OnPropertyChanged(nameof(VersionName));
        OnPropertyChanged(nameof(Loader));
        OnPropertyChanged(nameof(HasModLoader));
        OnPropertyChanged(nameof(IsRelease));
        OnPropertyChanged(nameof(IsSnapshot));
        OnPropertyChanged(nameof(IsAprilFools));
        OnPropertyChanged(nameof(IsBeta));
        OnPropertyChanged(nameof(IsAlpha));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(LoaderLabel));
        OnPropertyChanged(nameof(LoaderVersionDisplay));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(UpdatedDateText));
        OnPropertyChanged(nameof(IconSource));
    }
}

