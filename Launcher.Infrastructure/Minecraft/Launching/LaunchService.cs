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

/// <summary>
/// 编排启动设置、文件检查、账户会话、Java 选择、进程监控和脱敏失败诊断。
/// </summary>
public sealed partial class LaunchService : ILaunchService
{
    private readonly ILaunchAccountSessionService accountSessionService;
    private readonly IGameFileIntegrityService gameFileIntegrityService;
    private readonly ILaunchGameLauncherFactory launcherFactory;
    private readonly ILaunchCrashMonitor crashMonitor;
    private readonly IGameStartupReadinessWaiter gameStartupReadinessWaiter;
    private readonly ILaunchCommandRunner commandRunner;
    private readonly ILaunchProcessTerminator launchProcessTerminator;
    private readonly IJavaRuntimeSelectionService? javaRuntimeSelectionService;
    private readonly IJavaRuntimeProvisioningService? javaRuntimeProvisioningService;
    private readonly IGameLanguageService? gameLanguageService;
    private readonly IAuthlibInjectorProvisioningService? authlibInjectorProvisioningService;
    private readonly IOfflineSkinLaunchService? offlineSkinLaunchService;
    private readonly LaunchSettingsResolver launchSettingsResolver;
    private readonly ILogger<LaunchService> logger;

    public LaunchService(
        ILaunchAccountSessionService accountSessionService,
        IJavaRuntimeSelectionService javaRuntimeSelectionService,
        IJavaRuntimeProvisioningService? javaRuntimeProvisioningService = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ISystemMemoryService? systemMemoryService = null,
        IModService? modService = null,
        IGameLanguageService? gameLanguageService = null,
        ILogger<LaunchService>? logger = null,
        IAuthlibInjectorProvisioningService? authlibInjectorProvisioningService = null,
        IOfflineSkinLaunchService? offlineSkinLaunchService = null)
        : this(
            accountSessionService,
            new GameFileIntegrityService(downloadSpeedLimitState),
            new LaunchGameLauncherFactory(downloadSpeedLimitState),
            new LaunchCrashMonitor(),
            new LaunchCommandRunner(),
            javaRuntimeSelectionService,
            javaRuntimeProvisioningService,
            systemMemoryService,
            modService,
            gameLanguageService,
            logger,
            authlibInjectorProvisioningService,
            offlineSkinLaunchService)
    {
    }

    public LaunchService(
        ILaunchAccountSessionService accountSessionService,
        IGameFileIntegrityService gameFileIntegrityService,
        IJavaRuntimeSelectionService javaRuntimeSelectionService,
        IJavaRuntimeProvisioningService? javaRuntimeProvisioningService = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ISystemMemoryService? systemMemoryService = null,
        IModService? modService = null,
        IGameLanguageService? gameLanguageService = null,
        ILogger<LaunchService>? logger = null,
        IAuthlibInjectorProvisioningService? authlibInjectorProvisioningService = null,
        IOfflineSkinLaunchService? offlineSkinLaunchService = null)
        : this(
            accountSessionService,
            gameFileIntegrityService,
            new LaunchGameLauncherFactory(downloadSpeedLimitState),
            new LaunchCrashMonitor(),
            new LaunchCommandRunner(),
            javaRuntimeSelectionService,
            javaRuntimeProvisioningService,
            systemMemoryService,
            modService,
            gameLanguageService,
            logger,
            authlibInjectorProvisioningService,
            offlineSkinLaunchService)
    {
    }

    internal LaunchService(
        ILaunchAccountSessionService accountSessionService,
        IManagedVersionRepairService versionRepairService,
        ILaunchGameLauncherFactory launcherFactory,
        ILaunchCrashMonitor crashMonitor,
        ILaunchCommandRunner? commandRunner = null,
        IJavaRuntimeSelectionService? javaRuntimeSelectionService = null,
        IJavaRuntimeProvisioningService? javaRuntimeProvisioningService = null,
        ISystemMemoryService? systemMemoryService = null,
        IModService? modService = null,
        IGameLanguageService? gameLanguageService = null,
        ILogger<LaunchService>? logger = null,
        IAuthlibInjectorProvisioningService? authlibInjectorProvisioningService = null,
        IOfflineSkinLaunchService? offlineSkinLaunchService = null,
        IGameStartupReadinessWaiter? gameStartupReadinessWaiter = null,
        ILaunchProcessTerminator? launchProcessTerminator = null)
        : this(
            accountSessionService,
            new LegacyManagedVersionRepairAdapter(versionRepairService),
            launcherFactory,
            crashMonitor,
            commandRunner,
            javaRuntimeSelectionService,
            javaRuntimeProvisioningService,
            systemMemoryService,
            modService,
            gameLanguageService,
            logger,
            authlibInjectorProvisioningService,
            offlineSkinLaunchService,
            gameStartupReadinessWaiter,
            launchProcessTerminator)
    {
    }

    internal LaunchService(
        ILaunchAccountSessionService accountSessionService,
        IGameFileIntegrityService gameFileIntegrityService,
        ILaunchGameLauncherFactory launcherFactory,
        ILaunchCrashMonitor crashMonitor,
        ILaunchCommandRunner? commandRunner = null,
        IJavaRuntimeSelectionService? javaRuntimeSelectionService = null,
        IJavaRuntimeProvisioningService? javaRuntimeProvisioningService = null,
        ISystemMemoryService? systemMemoryService = null,
        IModService? modService = null,
        IGameLanguageService? gameLanguageService = null,
        ILogger<LaunchService>? logger = null,
        IAuthlibInjectorProvisioningService? authlibInjectorProvisioningService = null,
        IOfflineSkinLaunchService? offlineSkinLaunchService = null,
        IGameStartupReadinessWaiter? gameStartupReadinessWaiter = null,
        ILaunchProcessTerminator? launchProcessTerminator = null)
    {
        this.accountSessionService = accountSessionService;
        this.gameFileIntegrityService = gameFileIntegrityService;
        this.launcherFactory = launcherFactory;
        this.crashMonitor = crashMonitor;
        this.gameStartupReadinessWaiter = gameStartupReadinessWaiter ?? new GameStartupReadinessWaiter();
        this.commandRunner = commandRunner ?? new LaunchCommandRunner();
        this.launchProcessTerminator = launchProcessTerminator ?? new LaunchProcessTerminator();
        this.javaRuntimeSelectionService = javaRuntimeSelectionService;
        this.javaRuntimeProvisioningService = javaRuntimeProvisioningService;
        this.gameLanguageService = gameLanguageService;
        this.authlibInjectorProvisioningService = authlibInjectorProvisioningService;
        this.offlineSkinLaunchService = offlineSkinLaunchService;
        this.logger = logger ?? NullLogger<LaunchService>.Instance;
        launchSettingsResolver = new LaunchSettingsResolver(systemMemoryService, modService, this.logger);
    }

    private sealed class LegacyManagedVersionRepairAdapter(IManagedVersionRepairService repairService) : IGameFileIntegrityService
    {
        public async Task<GameFileRepairResult> ValidateAndRepairAsync(
            GameFileIntegrityRequest request,
            GameFileRepairOptions options,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await repairService.RepairAsync(
                request.MinecraftDirectory,
                request.VersionName,
                request.InstanceDirectory,
                progress,
                options.AllowRepair,
                cancellationToken,
                request.DownloadSourcePreference,
                request.DownloadSpeedLimitMbPerSecond).ConfigureAwait(false);
            return GameFileRepairResult.Empty;
        }

        public Task<GameFileRepairResult> ValidateFinalLaunchCommandAsync(
            GameFileIntegrityRequest request,
            System.Diagnostics.ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default) => Task.FromResult(GameFileRepairResult.Empty);
    }

    /// <summary>
    /// 执行一次完整启动并返回可观察退出结果的会话；失败统一转换为带诊断报告的异常。
    /// </summary>
    public async Task<GameLaunchSession> LaunchAsync(
        GameInstance instance,
        LauncherAccount account,
        LauncherSettings settings,
        IProgress<LauncherProgress>? progress,
        LaunchRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedSettings = await launchSettingsResolver
            .ResolveAsync(instance, settings, cancellationToken)
            .ConfigureAwait(false);
        var versionName = resolvedSettings.VersionName;
        var memoryMb = resolvedSettings.MemoryMb;
        System.Diagnostics.Process? process = null;
        ILaunchCrashMonitorSession? crashMonitorSession = null;
        JavaRuntimeInfo? selectedJavaRuntime = null;
        LaunchDiagnosticContext diagnosticContext = CreateDiagnosticContext(
            instance,
            settings,
            versionName,
            javaRuntime: null,
            memoryMb,
            sensitiveValues: []);
        var launchAttemptStartedAt = DateTimeOffset.UtcNow;
        LaunchSessionDiagnosticCollector? failureDiagnosticCollector = null;
        logger.LogInformation(
            "Game launch started. InstanceId={InstanceId} InstanceName={InstanceName} VersionName={VersionName} Loader={Loader} MemoryMb={MemoryMb}",
            instance.Id,
            instance.Name,
            versionName,
            instance.Loader,
            memoryMb);
        logger.LogInformation(
            "Launch settings resolved. InstanceId={InstanceId} HasPreLaunchCommand={HasPreLaunchCommand} WaitForPreLaunchCommand={WaitForPreLaunchCommand} HasPostExitCommand={HasPostExitCommand} HasExtraJvmArguments={HasExtraJvmArguments} HasExtraGameArguments={HasExtraGameArguments}",
            instance.Id,
            !string.IsNullOrWhiteSpace(resolvedSettings.PreLaunchCommand),
            resolvedSettings.WaitForPreLaunchCommand,
            !string.IsNullOrWhiteSpace(resolvedSettings.PostExitCommand),
            !string.IsNullOrWhiteSpace(resolvedSettings.JvmArguments),
            !string.IsNullOrWhiteSpace(resolvedSettings.GameArguments));

        try
        {
            failureDiagnosticCollector = new LaunchSessionDiagnosticCollector(
                settings.MinecraftDirectory,
                instance.InstanceDirectory);
            var preparedRuntime = await PrepareRuntimeAsync(
                    instance,
                    account,
                    settings,
                    resolvedSettings,
                    options,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            selectedJavaRuntime = preparedRuntime.JavaRuntime;
            diagnosticContext = preparedRuntime.DiagnosticContext;

            var startedProcess = await BuildAndStartProcessAsync(
                    instance,
                    settings,
                    resolvedSettings,
                    preparedRuntime,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            process = startedProcess.Process;
            crashMonitorSession = startedProcess.CrashMonitorSession;

            var readiness = await gameStartupReadinessWaiter
                .WaitAsync(process, startedProcess.CrashMonitorSession.GameOutputReady, cancellationToken)
                .ConfigureAwait(false);
            if (readiness == GameStartupReadinessResult.ProcessExited)
            {
                var startupExitResult = await startedProcess.CrashMonitorSession
                    .CreateStartupExitResultAsync(process, diagnosticContext, cancellationToken)
                    .ConfigureAwait(false);
                LogLaunchFailureReport(
                    LogLevel.Warning,
                    "Minecraft process exited before startup readiness was confirmed.",
                    startupExitResult.Report,
                    diagnosticContext);
                throw new LaunchProcessExitedException(startupExitResult.Report);
            }

            progress?.Report(new LauncherProgress(
                LaunchProgressStages.StartingProcess,
                "Game startup confirmed",
                100));
            logger.LogInformation(
                "Minecraft startup readiness confirmed. VersionName={VersionName} ProcessId={ProcessId} Signal={Signal}",
                versionName,
                process.Id,
                readiness);
            var session = startedProcess.CrashMonitorSession.CreateGameLaunchSession(process, diagnosticContext);
            return new GameLaunchSession(
                session.InstanceId,
                session.InstanceName,
                LogGameExitAsync(session.ExitTask, diagnosticContext),
                preparedRuntime.Warnings);
        }
        catch (LaunchProcessExitedException)
        {
            // 快速退出已经生成完整报告，保留原异常可避免再次写入重复诊断。
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null && crashMonitorSession is not null)
            {
                var canceledProcess = process;
                try
                {
                    await launchProcessTerminator.TerminateAsync(canceledProcess).ConfigureAwait(false);
                    await crashMonitorSession.CompleteCanceledStartupAsync(canceledProcess).ConfigureAwait(false);
                    canceledProcess.Dispose();
                    process = null;
                }
                catch (Exception terminationException)
                {
                    logger.LogError(
                        terminationException,
                        "Failed to terminate Minecraft after launch cancellation. InstanceId={InstanceId} VersionName={VersionName}",
                        diagnosticContext.InstanceId,
                        diagnosticContext.VersionName);
                    var monitorSession = crashMonitorSession.CreateGameLaunchSession(canceledProcess, diagnosticContext);
                    _ = ObserveGameExitAfterCancellationCleanupFailureAsync(
                        monitorSession.ExitTask,
                        diagnosticContext);
                    var report = await WriteFailureDiagnosticAsync(
                        diagnosticContext,
                        "launch_cancellation_cleanup_failed",
                        "Failed to terminate the Minecraft process after launch cancellation.",
                        terminationException,
                        canceledProcess.StartInfo,
                        failureDiagnosticCollector,
                        launchAttemptStartedAt,
                        CancellationToken.None).ConfigureAwait(false);
                    throw new LaunchFailedException(report, terminationException);
                }
            }

            logger.LogInformation(
                "Game launch canceled. InstanceId={InstanceId} InstanceName={InstanceName} VersionName={VersionName} JavaVersion={JavaVersion} JavaSource={JavaSource}",
                diagnosticContext.InstanceId,
                diagnosticContext.InstanceName,
                diagnosticContext.VersionName,
                diagnosticContext.JavaVersion,
                diagnosticContext.JavaSource);
            logger.LogDebug("Canceled game launch Java path. InstanceId={InstanceId} JavaPath={JavaPath}", diagnosticContext.InstanceId, diagnosticContext.JavaPath);
            throw;
        }
        catch (LaunchAccountSessionException exception)
        {
            if (exception.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired)
                throw;

            var report = await WriteFailureDiagnosticAsync(
                diagnosticContext,
                "account_session_failed",
                "Failed to create a valid Minecraft account session.",
                exception,
                process?.StartInfo,
                failureDiagnosticCollector,
                launchAttemptStartedAt,
                cancellationToken);
            throw new LaunchFailedException(report, exception);
        }
        catch (InstanceRepairException exception)
        {
            var report = await WriteFailureDiagnosticAsync(
                diagnosticContext,
                "instance_repair_failed",
                exception.Message,
                exception,
                process?.StartInfo,
                failureDiagnosticCollector,
                launchAttemptStartedAt,
                cancellationToken);
            throw new LaunchFailedException(report, exception);
        }
        catch (JavaRuntimeSelectionException exception)
        {
            var report = await WriteFailureDiagnosticAsync(
                diagnosticContext,
                "java_runtime_selection_failed",
                exception.Message,
                exception,
                process?.StartInfo,
                failureDiagnosticCollector,
                launchAttemptStartedAt,
                cancellationToken);
            throw new LaunchFailedException(report, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var report = await WriteFailureDiagnosticAsync(
                diagnosticContext,
                "launch_failed",
                exception.Message,
                exception,
                process?.StartInfo,
                failureDiagnosticCollector,
                launchAttemptStartedAt,
                cancellationToken);
            throw new LaunchFailedException(report, exception);
        }
    }
}
