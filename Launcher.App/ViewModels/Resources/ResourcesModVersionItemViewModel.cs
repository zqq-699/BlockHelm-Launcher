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

using Launcher.App.Resources;
using Launcher.Domain.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Behaviors;

namespace Launcher.App.ViewModels.Resources;

public sealed class ResourcesModVersionItemViewModel : ObservableObject, IListEntrancePendingItem
{
    private bool isGroupEntrancePending;

    public bool IsGroupEntrancePending
    {
        get => isGroupEntrancePending;
        set => SetProperty(ref isGroupEntrancePending, value);
    }

    bool IListEntrancePendingItem.IsEntrancePending
    {
        get => IsGroupEntrancePending;
        set => IsGroupEntrancePending = value;
    }

    public ResourcesModVersionItemViewModel(
        ResourceProjectVersion version,
        ResourcesModProjectItemViewModel? project,
        string fallbackIconKey = "instance_setting_page/mod")
    {
        Version = version;
        IconSource = project?.IconSource;
        IconKey = string.IsNullOrWhiteSpace(IconSource)
            ? fallbackIconKey
            : string.Empty;
    }

    public ResourceProjectVersion Version { get; }

    /// <summary>是否为该兼容分组的第一个子项；用于隐藏其顶部重复分割线。</summary>
    public bool IsFirstInGroup { get; init; }

    public string? IconSource { get; }

    public string IconKey { get; }

    public string Title => string.IsNullOrWhiteSpace(Version.Name)
        ? Version.VersionNumber
        : Version.Name;

    public string Subtitle => string.IsNullOrWhiteSpace(Version.FileName)
        ? FormatSubtitle(Version.VersionNumber)
        : FormatSubtitle(Version.FileName);

    public string TrailingText => Version.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty;

    private string VersionTypeText => Version.VersionType.Trim().ToLowerInvariant() switch
    {
        "release" => Strings.Download_ReleaseCategory,
        "beta" => Strings.Download_BetaCategory,
        "alpha" => Strings.Download_AlphaCategory,
        _ => string.Empty
    };

    private string FormatSubtitle(string value)
    {
        return string.IsNullOrWhiteSpace(VersionTypeText)
            ? value
            : $"{value}  {VersionTypeText}";
    }
}
