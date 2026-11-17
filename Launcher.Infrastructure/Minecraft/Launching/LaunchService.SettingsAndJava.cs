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

using System.IO;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using Launcher.Application;
using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class LaunchService
{
private async Task ApplyGameLanguageAsync(
        GameInstance instance,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.AutoSetGameLanguageToLauncherLanguage || gameLanguageService is null)
            return;

        try
        {
            var gameLanguage = await gameLanguageService.ApplyLauncherLanguageAsync(
                instance,
                settings.LauncherLanguage,
                cancellationToken);
            logger.LogInformation(
                "Game language synchronized with launcher language. InstanceDirectory={InstanceDirectory} MinecraftVersion={MinecraftVersion} LauncherLanguage={LauncherLanguage} GameLanguage={GameLanguage}",
                instance.InstanceDirectory,
                instance.MinecraftVersion,
                LauncherLanguages.Normalize(settings.LauncherLanguage),
                gameLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 语言同步属于启动增强项，失败不应阻止游戏启动。
            logger.LogWarning(
                exception,
                "Failed to synchronize game language before launch. InstanceDirectory={InstanceDirectory} LauncherLanguage={LauncherLanguage}",
                instance.InstanceDirectory,
                LauncherLanguages.Normalize(settings.LauncherLanguage));
        }
    }

    /// <summary>
    /// 选择兼容 Java；仅在自动运行时缺失时安装内置运行时并重试一次。
    /// </summary>
    private async Task<JavaRuntimeInfo?> ResolveJavaRuntimeForLaunchAsync(
        GameInstance instance,
        LauncherSettings settings,
        LaunchRequestOptions? options,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (javaRuntimeSelectionService is null)
            return null;

        try
        {
            progress?.Report(new LauncherProgress(LaunchProgressStages.CheckingJava, string.Empty, 90));
            return await javaRuntimeSelectionService.SelectForLaunchAsync(instance, settings, options, cancellationToken);
        }
        catch (JavaRuntimeSelectionException exception)
            when (javaRuntimeProvisioningService is not null && IsAutomaticJavaRuntimeDiscoveryFailure(exception.Reason))
        {
            // 仅自动发现缺失时允许安装内置 Java 后重试；用户手动配置错误仍应直接反馈。
            logger.LogInformation(
                exception,
                "Automatic Java runtime selection failed. Preparing bundled Java runtime before retrying. InstanceDirectory={InstanceDirectory} InstanceName={InstanceName} Reason={Reason} RequiredJavaMajorVersion={RequiredJavaMajorVersion}",
                instance.InstanceDirectory,
                instance.Name,
                exception.Reason,
                exception.RequiredMajorVersion);

            await javaRuntimeProvisioningService.EnsureForLaunchAsync(
                instance,
                settings,
                progress,
                cancellationToken);

            logger.LogInformation(
                "Retrying Java runtime selection after provisioning. InstanceDirectory={InstanceDirectory} InstanceName={InstanceName}",
                instance.InstanceDirectory,
                instance.Name);
            return await javaRuntimeSelectionService.SelectForLaunchAsync(instance, settings, options, cancellationToken);
        }
    }

    private static bool IsAutomaticJavaRuntimeDiscoveryFailure(JavaRuntimeSelectionFailureReason reason)
    {
        return reason is JavaRuntimeSelectionFailureReason.AutomaticRuntimeMissing
            or JavaRuntimeSelectionFailureReason.AutomaticRuntimeNotFound;
    }
}
