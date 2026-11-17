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
[RelayCommand(CanExecute = nameof(CanLaunchSelectedGame))]
    private async Task LaunchAsync()
    {
        await LaunchCoreAsync(options: null, forcedInstance: null);
    }

    public Task ForceLaunchIgnoringJavaRequirementAsync(GameInstance instance)
    {
        return LaunchCoreAsync(
            new LaunchRequestOptions(IgnoreJavaVersionRequirement: true),
            instance);
    }

    private async Task LaunchCoreAsync(LaunchRequestOptions? options, GameInstance? forcedInstance)
    {
        // 强制启动只绕过 Java 版本要求，不绕过账户、实例或并发启动这些基本前置条件。
        var account = accountPage.SelectedAccount;
        var launchInstance = forcedInstance ?? SelectedInstance;
        if (IsLaunching || launchInstance is null || account is null)
        {
            statusService.Report(Strings.Status_NoLaunchableInstance);
            return;
        }

        try
        {
            var session = await StartGameSessionAsync(launchInstance, account, options);
            if (session is null)
                return;

            ObserveGameExit(session);
            ReportLaunchWarnings(session);

            if (ShouldMinimizeLauncherAfterLaunch(launchInstance))
                windowService.Minimize();
        }
        catch (OperationCanceledException exception) when (launchCancellationTokenSource?.IsCancellationRequested == true)
        {
            logger.LogDebug(
                exception,
                "Launch cancellation completed. InstanceDirectory={InstanceDirectory} InstanceName={InstanceName}",
                launchInstance.InstanceDirectory,
                launchInstance.Name);
            statusService.Report(Strings.Status_LaunchCanceled);
            floatingMessageService.Show(Strings.Status_LaunchCanceled);
        }
        catch (LaunchAccountSessionException exception)
        {
            var message = exception.Reason switch
            {
                LaunchAccountSessionFailureReason.AuthenticationNotConfigured
                    => Strings.Status_MicrosoftLoginNotConfigured,
                LaunchAccountSessionFailureReason.AuthenticationApplicationNotAuthorized
                    => Strings.Status_MicrosoftApplicationNotAuthorized,
                LaunchAccountSessionFailureReason.GameOwnershipRequired
                    => Strings.Status_MinecraftJavaOwnershipRequired,
                LaunchAccountSessionFailureReason.AuthenticationServerUnavailable
                    => Strings.Status_MicrosoftAuthenticationServerUnavailable,
                LaunchAccountSessionFailureReason.CredentialStorageFailed
                    => Strings.Status_MicrosoftCredentialStorageFailed,
                _ => Strings.Status_LaunchAccountUnavailable
            };
            statusService.Report(message);
            floatingMessageService.Show(message);
        }
        catch (LaunchFailedException exception)
        {
            // Java 自动发现失败需要用户决策，其余启动失败使用统一诊断报告，避免展示底层异常文本。
            if (exception.InnerException is JavaRuntimeSelectionException javaException
                && ShouldShowJavaRequirementDialog(javaException.Reason))
            {
                JavaRequirementNotMet?.Invoke(this, new JavaRequirementNotMetEventArgs(
                    javaException.RequiredMajorVersion,
                    javaException.Reason,
                    launchInstance,
                    javaException.CurrentMajorVersion,
                    javaException.CurrentVersion,
                    javaException.RecommendedMajorVersion));
                statusService.Report(Strings.Status_JavaSelectionFailed);
                return;
            }

            ReportLaunchFailure(exception.Report);
        }
        catch (LaunchProcessExitedException exception)
        {
            ReportLaunchFailure(exception.Report);
        }
        catch (InstanceRepairException)
        {
            statusService.Report(Strings.Status_LaunchInstanceRepairFailed);
        }
        catch (JavaRuntimeSelectionException exception)
        {
            if (ShouldShowJavaRequirementDialog(exception.Reason))
                JavaRequirementNotMet?.Invoke(this, new JavaRequirementNotMetEventArgs(
                    exception.RequiredMajorVersion,
                    exception.Reason,
                    launchInstance,
                    exception.CurrentMajorVersion,
                    exception.CurrentVersion,
                    exception.RecommendedMajorVersion));

            statusService.Report(Strings.Status_JavaSelectionFailed);
        }
        catch (Exception)
        {
            statusService.Report(Strings.Status_LaunchFailed);
        }
        finally
        {
            // 此处只结束“启动中”进度；Minecraft 进程生命周期由 GameLaunchSession 独立持有。
            ResetLaunchProgress();
        }
    }

    private async Task<GameLaunchSession?> StartGameSessionAsync(
        GameInstance launchInstance,
        LauncherAccount account,
        LaunchRequestOptions? options)
    {
        var cancellationTokenSource = BeginLaunchProgress();
        try
        {
            return await launchService.LaunchAsync(
                launchInstance,
                account,
                settings,
                CreateProgress(),
                options,
                cancellationTokenSource.Token);
        }
        catch (LaunchAccountSessionException exception) when (
            exception.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired
            && (account.IsThirdParty || account.IsMicrosoft)
            && accountDialogService is not null)
        {
            return await RetryAfterReauthenticationAsync(
                launchInstance,
                account,
                options,
                cancellationTokenSource.Token);
        }
    }

    private async Task<GameLaunchSession?> RetryAfterReauthenticationAsync(
        GameInstance launchInstance,
        LauncherAccount account,
        LaunchRequestOptions? options,
        CancellationToken cancellationToken)
    {
        statusService.Report(account.IsThirdParty
            ? Strings.Status_ThirdPartyReauthenticationRequired
            : Strings.Status_MicrosoftReauthenticationRequired);
        var reauthenticated = account.IsThirdParty
            ? await accountDialogService!.ShowThirdPartyReauthenticationDialogAsync(account)
            : await accountDialogService!.ShowMicrosoftReauthenticationDialogAsync(account);
        if (!reauthenticated)
        {
            statusService.Report(Strings.Status_LaunchCanceled);
            floatingMessageService.Show(Strings.Status_LaunchCanceled);
            return null;
        }

        var refreshedAccount = accountPage.SelectedAccount;
        if (refreshedAccount is null
            || !string.Equals(refreshedAccount.Id, account.Id, StringComparison.Ordinal))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.ReauthenticationRequired,
                "The reauthenticated account is no longer selected.");
        }

        return await launchService.LaunchAsync(
            launchInstance,
            refreshedAccount,
            settings,
            CreateProgress(),
            options,
            cancellationToken);
    }
}
