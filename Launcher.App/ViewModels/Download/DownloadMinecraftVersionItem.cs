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

namespace Launcher.App.ViewModels.Download;

public sealed partial class DownloadMinecraftVersionItem : ObservableObject
{
    public DownloadMinecraftVersionItem(MinecraftVersionInfo version)
    {
        Version = version;
    }

    public MinecraftVersionInfo Version { get; }

    public string Name => Version.Name;

    public string Type => Version.Type;

    public string VersionType => MinecraftVersionIconResolver.NormalizeVersionType(Type);

    public string TypeLabel => MinecraftVersionTypeDisplayProvider.GetLabel(IsAprilFools ? "april_fools" : VersionType, Version.Type);

    public string ReleaseDateText => Version.ReleaseTime is { } releaseTime
        ? releaseTime.ToLocalTime().ToString("yyyy-MM-dd")
        : string.Empty;

    public bool IsRelease => VersionType.Equals("release", StringComparison.OrdinalIgnoreCase);

    public bool IsSnapshot => VersionType.Equals("snapshot", StringComparison.OrdinalIgnoreCase);

    public bool IsAprilFools => MinecraftAprilFoolsVersionClassifier.IsAprilFoolsVersion(Name);

    public bool IsBeta => VersionType.Equals("old_beta", StringComparison.OrdinalIgnoreCase);

    public bool IsAlpha => VersionType.Equals("old_alpha", StringComparison.OrdinalIgnoreCase);

    public string IconSource => MinecraftVersionIconResolver.Resolve(VersionType, Name);

    [ObservableProperty]
    private bool isSelected;
}

