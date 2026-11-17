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

using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 合并全局与实例启动设置，并在自动内存计算不可用时回退到安全的已配置值。
/// </summary>
internal sealed class LaunchSettingsResolver
{
    private readonly ISystemMemoryService? systemMemoryService;
    private readonly IModService? modService;
    private readonly ILogger logger;

    public LaunchSettingsResolver(
        ISystemMemoryService? systemMemoryService,
        IModService? modService,
        ILogger logger)
    {
        this.systemMemoryService = systemMemoryService;
        this.modService = modService;
        this.logger = logger;
    }

    /// <summary>
    /// 按实例设置模式选择全局或实例字段，并解析最终内存值和版本身份。
    /// </summary>
    public async Task<ResolvedLaunchSettings> ResolveAsync(
        GameInstance instance,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        var useGlobal = instance.LaunchSettingsMode is LaunchSettingsMode.UseGlobal;
        return new ResolvedLaunchSettings(
            string.IsNullOrWhiteSpace(instance.VersionName) ? instance.MinecraftVersion : instance.VersionName,
            useGlobal ? settings.DefaultCheckFilesBeforeLaunch : instance.CheckFilesBeforeLaunch,
            useGlobal ? settings.DefaultAutoRepairMissingFiles : instance.AutoRepairMissingFiles,
            useGlobal ? settings.DefaultLaunchFullScreen : instance.LaunchFullScreen,
            useGlobal ? settings.DefaultAutoJoinServerAddress : instance.AutoJoinServerAddress,
            useGlobal ? settings.DefaultPreLaunchCommand : instance.PreLaunchCommand,
            useGlobal ? settings.DefaultWaitForPreLaunchCommand : instance.WaitForPreLaunchCommand,
            useGlobal ? settings.DefaultPostExitCommand : instance.PostExitCommand,
            useGlobal ? settings.DefaultJvmArguments : instance.JvmArguments,
            useGlobal ? settings.DefaultGameArguments : instance.GameArguments,
            await ResolveMemoryMbAsync(instance, settings, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 按实例/全局与手动/自动优先级计算内存，并在非取消失败时安全回退。
    /// </summary>
    private async Task<int> ResolveMemoryMbAsync(
        GameInstance instance,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        // 实例级设置优先；自动计算失败只影响内存优化，不应让启动整体失败。
        if (instance.LaunchSettingsMode is LaunchSettingsMode.PerInstance && instance.MemoryMb > 0)
        {
            if (instance.MemorySettingsMode is MemorySettingsMode.Manual)
                return instance.MemoryMb;

            try
            {
                return await ResolveAutomaticMemoryMbAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Failed to calculate automatic instance launch memory. Falling back to configured instance memory.");
                return NormalizeConfiguredMemoryMb(instance.MemoryMb);
            }
        }

        if (settings.DefaultMemorySettingsMode is MemorySettingsMode.Manual)
            return NormalizeConfiguredMemoryMb(settings.DefaultMemoryMb);

        if (systemMemoryService is null)
            return NormalizeConfiguredMemoryMb(settings.DefaultMemoryMb);

        try
        {
            return await ResolveAutomaticMemoryMbAsync(instance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Failed to calculate automatic launch memory. Falling back to configured memory.");
            return NormalizeConfiguredMemoryMb(settings.DefaultMemoryMb);
        }
    }

    /// <summary>
    /// 结合系统内存快照、Loader 和已启用 Mod 数量计算自动分配值。
    /// </summary>
    private async Task<int> ResolveAutomaticMemoryMbAsync(
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        if (systemMemoryService is null)
            return NormalizeConfiguredMemoryMb(instance.MemoryMb);

        var enabledModCount = await CountEnabledModsAsync(instance, cancellationToken).ConfigureAwait(false);
        return MemoryAllocationCalculator.CalculateAutomaticMemoryMb(
            systemMemoryService.GetSnapshot(),
            instance.Loader,
            enabledModCount);
    }

    private async Task<int> CountEnabledModsAsync(
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        if (modService is null || instance.Loader is LoaderKind.Vanilla)
            return 0;

        // Mod 数量仅是自动内存估算信号，扫描失败时按零处理并保留可启动性。
        try
        {
            var mods = await modService.GetModsAsync(instance, cancellationToken).ConfigureAwait(false);
            return mods.Count(mod => mod.IsEnabled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Failed to count enabled mods for automatic memory allocation. InstanceDirectory={InstanceDirectory}",
                instance.InstanceDirectory);
            return 0;
        }
    }

    private static int NormalizeConfiguredMemoryMb(int memoryMb)
    {
        return Math.Clamp(
            memoryMb,
            MemoryAllocationCalculator.MinimumMemoryMb,
            MemoryAllocationCalculator.FallbackMaximumMemoryMb);
    }
}

internal sealed record ResolvedLaunchSettings(
    string VersionName,
    bool CheckFilesBeforeLaunch,
    bool AutoRepairMissingFiles,
    bool LaunchFullScreen,
    string AutoJoinServerAddress,
    string PreLaunchCommand,
    bool WaitForPreLaunchCommand,
    string PostExitCommand,
    string JvmArguments,
    string GameArguments,
    int MemoryMb);
