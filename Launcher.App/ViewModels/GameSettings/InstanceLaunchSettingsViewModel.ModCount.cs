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
using Launcher.App.Resources;
using Launcher.App.Utilities;
using Launcher.App.ViewModels.Settings;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class InstanceLaunchSettingsViewModel
{
private async Task RefreshEnabledModCountAsync(GameInstance? instance)
    {
        if (instance is null || instance.Loader is LoaderKind.Vanilla)
        {
            enabledModCount = 0;
            RefreshSystemMemorySnapshot();
            return;
        }

        var cancellation = new CancellationTokenSource();
        modCountRefreshCancellation = cancellation;
        try
        {
            var mods = await modService.GetModsAsync(instance, cancellation.Token);
            // CancellationToken 之外再校验请求身份，覆盖底层服务忽略取消或恰好已完成的竞态。
            if (!ReferenceEquals(modCountRefreshCancellation, cancellation)
                || !string.Equals(selectedInstance?.InstanceDirectory, instance.InstanceDirectory, StringComparison.Ordinal))
            {
                return;
            }

            enabledModCount = mods.Count(mod => mod.IsEnabled);
            RefreshSystemMemorySnapshot();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Mod 统计只是估算增强项；失败时按 0 计算，不让非关键读取破坏整个启动设置页。
            if (string.Equals(selectedInstance?.InstanceDirectory, instance.InstanceDirectory, StringComparison.Ordinal))
            {
                enabledModCount = 0;
                RefreshSystemMemorySnapshot();
            }
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref modCountRefreshCancellation, null, cancellation), cancellation))
                cancellation.Dispose();
        }
    }

    private void CancelModCountRefresh()
    {
        var cancellation = Interlocked.Exchange(ref modCountRefreshCancellation, null);
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private SettingsMemoryModeOption ResolveMemoryModeOption(MemorySettingsMode mode)
    {
        return MemoryModeOptions.FirstOrDefault(option => option.Mode == mode) ?? MemoryModeOptions[0];
    }

    private GameSettingsLaunchSettingsModeOption ResolveLaunchSettingsModeOption(LaunchSettingsMode mode)
    {
        return LaunchSettingsModeOptions.FirstOrDefault(option => option.Mode == mode) ?? LaunchSettingsModeOptions[0];
    }

    private int NormalizeMemoryValue(double value)
    {
        return MemoryAllocationCalculator.NormalizeRecordedMemoryMb(value, MemorySliderMaximumMb);
    }

    private static string NormalizeSettingText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    // 快照同时承担无变化检测和失败回滚，字段必须覆盖 ApplyMutation 修改的全部实例属性。
    private sealed record InstanceLaunchSettingsSnapshot(
        LaunchSettingsMode Mode,
        bool CheckFilesBeforeLaunch,
        bool AutoRepairMissingFiles,
        bool MinimizeLauncherAfterLaunch,
        bool LaunchFullScreen,
        string AutoJoinServerAddress,
        string PreLaunchCommand,
        bool WaitForPreLaunchCommand,
        string PostExitCommand,
        string JvmArguments,
        string GameArguments,
        MemorySettingsMode MemorySettingsMode,
        int MemoryMb)
    {
        public static InstanceLaunchSettingsSnapshot Capture(GameInstance instance)
        {
            return new InstanceLaunchSettingsSnapshot(
                instance.LaunchSettingsMode,
                instance.CheckFilesBeforeLaunch,
                instance.AutoRepairMissingFiles,
                instance.MinimizeLauncherAfterLaunch,
                instance.LaunchFullScreen,
                instance.AutoJoinServerAddress,
                instance.PreLaunchCommand,
                instance.WaitForPreLaunchCommand,
                instance.PostExitCommand,
                instance.JvmArguments,
                instance.GameArguments,
                instance.MemorySettingsMode,
                instance.MemoryMb);
        }

        public bool Matches(
            LaunchSettingsMode mode,
            bool checkFilesBeforeLaunch,
            bool autoRepairMissingFiles,
            bool minimizeLauncherAfterLaunch,
            bool launchFullScreen,
            string autoJoinServerAddress,
            string preLaunchCommand,
            bool waitForPreLaunchCommand,
            string postExitCommand,
            string jvmArguments,
            string gameArguments,
            MemorySettingsMode memorySettingsMode,
            int memoryMb)
        {
            return Mode == mode
                && CheckFilesBeforeLaunch == checkFilesBeforeLaunch
                && AutoRepairMissingFiles == autoRepairMissingFiles
                && MinimizeLauncherAfterLaunch == minimizeLauncherAfterLaunch
                && LaunchFullScreen == launchFullScreen
                && string.Equals(AutoJoinServerAddress, autoJoinServerAddress, StringComparison.Ordinal)
                && string.Equals(PreLaunchCommand, preLaunchCommand, StringComparison.Ordinal)
                && WaitForPreLaunchCommand == waitForPreLaunchCommand
                && string.Equals(PostExitCommand, postExitCommand, StringComparison.Ordinal)
                && string.Equals(JvmArguments, jvmArguments, StringComparison.Ordinal)
                && string.Equals(GameArguments, gameArguments, StringComparison.Ordinal)
                && MemorySettingsMode == memorySettingsMode
                && MemoryMb == memoryMb;
        }

        public void Restore(GameInstance instance)
        {
            instance.LaunchSettingsMode = Mode;
            instance.CheckFilesBeforeLaunch = CheckFilesBeforeLaunch;
            instance.AutoRepairMissingFiles = AutoRepairMissingFiles;
            instance.MinimizeLauncherAfterLaunch = MinimizeLauncherAfterLaunch;
            instance.LaunchFullScreen = LaunchFullScreen;
            instance.AutoJoinServerAddress = AutoJoinServerAddress;
            instance.PreLaunchCommand = PreLaunchCommand;
            instance.WaitForPreLaunchCommand = WaitForPreLaunchCommand;
            instance.PostExitCommand = PostExitCommand;
            instance.JvmArguments = JvmArguments;
            instance.GameArguments = GameArguments;
            instance.MemorySettingsMode = MemorySettingsMode;
            instance.MemoryMb = MemoryMb;
        }
    }
}
