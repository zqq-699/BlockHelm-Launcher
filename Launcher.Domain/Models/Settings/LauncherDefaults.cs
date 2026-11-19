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

namespace Launcher.Domain.Models;

public static class LauncherDefaults
{
    public const string DefaultOfflineUsername = "Player";
    public const string DefaultTheme = "Dark";
    public const string DefaultAccentColor = LauncherAccentColors.Blue;
    public const string DefaultLauncherLanguage = LauncherLanguages.SimplifiedChinese;
    public const string DefaultLauncherBackgroundEffect = LauncherBackgroundEffects.Acrylic;
    public const int DefaultLauncherBackgroundOpacityPercent = 85;
    public const bool DefaultEnableImageBackgroundControlBlur = true;
    public const double DefaultMainWindowWidth = 1000d;
    public const double DefaultMainWindowHeight = 700d;
    public const double MinimumMainWindowWidth = 900d;
    public const double MinimumMainWindowHeight = 600d;
    public const int MinimumDownloadConcurrency = 1;
    public const int DefaultMaximumDownloadConcurrency = 64;
    public const int MaximumDownloadConcurrency = 128;
    public const DownloadSourcePreference DefaultDownloadSourcePreference = DownloadSourcePreference.Auto;
    public const int DefaultMemoryMb = 4096;
    public const LauncherUpdateChannel DefaultUpdateChannel = LauncherUpdateChannel.Release;
}
