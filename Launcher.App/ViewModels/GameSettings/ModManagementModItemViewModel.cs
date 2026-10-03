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
using Launcher.App.Resources;
using Launcher.App.ViewModels.Resources;
using Launcher.Domain.Models;
using System.IO;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class ModManagementModItemViewModel : ObservableObject
{
    public ModManagementModItemViewModel(LocalMod mod)
    {
        SyncFrom(mod);
    }

    public string Subtitle => FileName;

    public string TrailingText => IsEnabled
        ? Strings.GameSettings_ModManagementEnabledState
        : Strings.GameSettings_ModManagementDisabledState;

    public string IconKey => string.IsNullOrWhiteSpace(IconSource)
        ? "instance_setting_page/mod"
        : string.Empty;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string fileName = string.Empty;

    [ObservableProperty]
    private string fullPath = string.Empty;

    [ObservableProperty]
    private string? iconSource;

    [ObservableProperty]
    private string? loader;

    [ObservableProperty]
    private string? modId;

    [ObservableProperty]
    private string? version;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool isUpdateBusy;

    public bool CanUpdate => HasProjectDetails && !IsUpdateBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTitleTags))]
    private IReadOnlyList<string> titleTags = [];

    public bool HasTitleTags => TitleTags.Count > 0;

    public bool HasProjectDetails => ProjectReference is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectDetails))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private ResourceProjectReference? projectReference;

    public void SyncFrom(LocalMod mod)
    {
        Title = string.IsNullOrWhiteSpace(mod.Name)
            ? GetDisplayFileNameWithoutModExtensions(mod.FileName)
            : mod.Name;
        Loader = NormalizeSubtitlePart(mod.Loader);
        ModId = NormalizeSubtitlePart(mod.ModId);
        Version = NormalizeSubtitlePart(mod.Version);
        FileName = mod.FileName;
        FullPath = mod.FullPath;
        IconSource = mod.IconSource;
        TitleTags = ResourceProjectCategoryTitleFormatter.Format(ResourceProjectKind.Mod, mod.Categories);
        ProjectReference = mod.ProjectReference;
        IsEnabled = mod.IsEnabled;
    }

    partial void OnFileNameChanged(string value)
    {
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnIconSourceChanged(string? value)
    {
        OnPropertyChanged(nameof(IconKey));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(TrailingText));
    }

    private static string? NormalizeSubtitlePart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string GetDisplayFileNameWithoutModExtensions(string fileName)
    {
        if (fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".jar.disabled".Length];

        if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".jar".Length];

        return Path.GetFileNameWithoutExtension(fileName);
    }
}
