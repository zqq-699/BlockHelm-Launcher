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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Home;

public sealed partial class HomePageViewModel
{
    private void ReportLaunchWarnings(GameLaunchSession session)
    {
        foreach (var warning in session.Warnings.Distinct())
        {
            var message = warning switch
            {
                LaunchWarningKind.OfflineSkinUnavailable => Strings.Status_LaunchOfflineSkinUnavailable,
                _ => null
            };
            if (message is null)
                continue;
            statusService.Report(message);
            floatingMessageService.Show(message);
        }
    }

private void ObserveGameExit(GameLaunchSession session)
    {
        // 不阻塞 UI 等待游戏退出。TryMarkExitHandled 保证启动期和运行期诊断竞态下只弹一次失败。
        _ = Task.Run(async () =>
        {
            try
            {
                var exitResult = await session.ExitTask;
                if (!exitResult.IsFailure
                    || exitResult.FailureReport is null
                    || !session.TryMarkExitHandled())
                {
                    return;
                }

                ReportLaunchFailure(exitResult.FailureReport);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to observe Minecraft process exit. InstanceDirectory={InstanceDirectory}",
                    session.InstanceDirectory);
            }
        });
    }

    private void ReportLaunchFailure(LaunchFailureReport report)
    {
        // 退出观察发生在线程池，所有状态服务与事件回调都必须切回 UI 调度器。
        if (!uiDispatcher.HasAccess)
        {
            uiDispatcher.Post(() => ReportLaunchFailure(report));
            return;
        }

        statusService.Report(GetLaunchFailureStatus(report.Kind));
        LaunchFailureReported?.Invoke(this, report);
    }

    private static string GetLaunchFailureStatus(LaunchFailureKind kind)
    {
        return kind switch
        {
            LaunchFailureKind.StartupProcessExited => Strings.Status_LaunchProcessExited,
            LaunchFailureKind.RuntimeAbnormalExit => Strings.Status_LaunchRuntimeAbnormalExit,
            LaunchFailureKind.StartupAbnormalExit => Strings.Status_LaunchAbnormalExit,
            _ => Strings.Status_LaunchFailed
        };
    }

    private static bool IsAutomaticJavaRuntimeDiscoveryFailure(JavaRuntimeSelectionFailureReason reason)
    {
        return reason is JavaRuntimeSelectionFailureReason.AutomaticRuntimeMissing
            or JavaRuntimeSelectionFailureReason.AutomaticRuntimeNotFound;
    }

    private static bool ShouldShowJavaRequirementDialog(JavaRuntimeSelectionFailureReason reason)
    {
        return IsAutomaticJavaRuntimeDiscoveryFailure(reason)
            || reason is JavaRuntimeSelectionFailureReason.ManualRuntimeVersionTooLow
                or JavaRuntimeSelectionFailureReason.ManualRuntimeIncompatible;
    }
}
