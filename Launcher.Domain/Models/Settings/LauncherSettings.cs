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

public sealed class LauncherSettings
{
    public long Revision { get; set; }

    public bool IsMenuExpanded { get; set; }
    public bool IsHomeLaunchMenuPinned { get; set; }
    public string Theme { get; set; } = LauncherDefaults.DefaultTheme;
    public string AccentColor { get; set; } = LauncherDefaults.DefaultAccentColor;
    public string LauncherLanguage { get; set; } = LauncherDefaults.DefaultLauncherLanguage;
    public bool EnableDiagnosticLogging { get; set; }
    public bool HasAcceptedUserAgreement { get; set; }
    public double MainWindowWidth { get; set; } = LauncherDefaults.DefaultMainWindowWidth;
    public double MainWindowHeight { get; set; } = LauncherDefaults.DefaultMainWindowHeight;
    public bool MainWindowWasMaximized { get; set; }
    public bool AutoSetGameLanguageToLauncherLanguage { get; set; } = true;
    public bool ThemeFollowSystem { get; set; } = true;
    public string LauncherBackgroundEffect { get; set; } = LauncherDefaults.DefaultLauncherBackgroundEffect;
    public int LauncherBackgroundOpacityPercent { get; set; } = LauncherDefaults.DefaultLauncherBackgroundOpacityPercent;
    public bool EnableImageBackgroundControlBlur { get; set; } = LauncherDefaults.DefaultEnableImageBackgroundControlBlur;
    public LauncherUpdateChannel UpdateChannel { get; set; } = LauncherDefaults.DefaultUpdateChannel;
    public string DataDirectory { get; set; } = string.Empty;
    public string MinecraftDirectory { get; set; } = string.Empty;
    public List<string> MinecraftDirectories { get; set; } = [];
    public Dictionary<string, string> MinecraftDirectoryDisplayNames { get; set; } = [];
    public List<string> ExcludedMinecraftDirectories { get; set; } = [];
    public DownloadSourcePreference DownloadSourcePreference { get; set; } = LauncherDefaults.DefaultDownloadSourcePreference;
    public int MaximumDownloadConcurrency { get; set; } = LauncherDefaults.DefaultMaximumDownloadConcurrency;
    public int DownloadSpeedLimitMbPerSecond { get; set; }
    public MemorySettingsMode DefaultMemorySettingsMode { get; set; } = MemorySettingsMode.Auto;
    public int DefaultMemoryMb { get; set; } = LauncherDefaults.DefaultMemoryMb;
    public JavaSelectionMode JavaSelectionMode { get; set; } = JavaSelectionMode.Auto;
    public string? SelectedJavaExecutablePath { get; set; }
    public bool DefaultCheckFilesBeforeLaunch { get; set; } = true;
    public bool DefaultAutoRepairMissingFiles { get; set; } = true;
    public bool DefaultMinimizeLauncherAfterLaunch { get; set; }
    public bool DefaultLaunchFullScreen { get; set; }
    public string DefaultAutoJoinServerAddress { get; set; } = string.Empty;
    public string DefaultPreLaunchCommand { get; set; } = string.Empty;
    public bool DefaultWaitForPreLaunchCommand { get; set; } = true;
    public string DefaultPostExitCommand { get; set; } = string.Empty;
    public string DefaultJvmArguments { get; set; } = string.Empty;
    public string DefaultGameArguments { get; set; } = string.Empty;
    public string? DefaultInstanceDirectory { get; set; }
}
