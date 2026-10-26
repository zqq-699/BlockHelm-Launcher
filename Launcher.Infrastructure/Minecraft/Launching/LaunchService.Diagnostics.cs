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
/// <summary>
    /// 尽力写入脱敏诊断文件；诊断写入失败时仍返回可展示的最小失败报告。
    /// </summary>
    private async Task<LaunchFailureReport> WriteFailureDiagnosticAsync(
        LaunchDiagnosticContext context,
        string failureKind,
        string failureSummary,
        Exception exception,
        System.Diagnostics.ProcessStartInfo? startInfo,
        LaunchSessionDiagnosticCollector? diagnosticCollector,
        DateTimeOffset launchAttemptStartedAt,
        CancellationToken cancellationToken)
    {
        LaunchDiagnosticResult? diagnostic = null;
        try
        {
            IReadOnlyList<LaunchDiagnosticReference> diagnosticCandidates = diagnosticCollector is null
                ? []
                : await diagnosticCollector
                    .CollectAsync(
                        launchAttemptStartedAt,
                        capturedOutputPath: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            diagnostic = await LaunchDiagnosticsWriter.WriteExceptionDiagnosticAsync(
                context,
                failureKind,
                failureSummary,
                exception,
                startInfo,
                launchAttemptStartedAt,
                diagnosticCandidates,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(diagnostic.DiagnosticPath))
                exception.Data["DiagnosticPath"] = diagnostic.DiagnosticPath;
        }
        catch (Exception diagnosticException)
        {
            logger.LogWarning(
                diagnosticException,
                "Failed to write launch diagnostic. FailureKind={FailureKind} InstanceId={InstanceId}",
                failureKind,
                context.InstanceId);
        }

        var report = new LaunchFailureReport(
            LaunchFailureKind.StartupFailed,
            context.InstanceName,
            context.VersionName,
            null,
            diagnostic?.DiagnosticPath,
            ResolveDiagnosticDirectory(context, diagnostic?.DiagnosticPath),
            diagnostic?.Analysis,
            diagnostic?.FailureSummary ?? LaunchDiagnosticRedactor.Redact(failureSummary, context.SensitiveValues))
        {
            DiagnosticCandidates = diagnostic?.DiagnosticCandidates ?? [],
            ExportSensitiveValues = context.SensitiveValues.ToArray()
        };
        LogLaunchFailureReport(
            LogLevel.Error,
            "Game launch failed.",
            report,
            context,
            exception,
            failureKind);
        return report;
    }

    /// <summary>
    /// 等待进程退出分析结果并记录成功退出或崩溃诊断摘要。
    /// </summary>
    private async Task<LaunchExitResult> LogGameExitAsync(
        Task<LaunchExitResult> exitTask,
        LaunchDiagnosticContext context)
    {
        try
        {
            var result = await exitTask.ConfigureAwait(false);
            if (result.FailureReport is null)
            {
                logger.LogInformation(
                    "Minecraft process exited normally. InstanceId={InstanceId} VersionName={VersionName} ExitCode={ExitCode} Runtime={Runtime} JavaVersion={JavaVersion} JavaSource={JavaSource}",
                    context.InstanceId,
                    context.VersionName,
                    result.ExitCode,
                    FormatRuntime(result.Runtime),
                    context.JavaVersion,
                    context.JavaSource);
                logger.LogDebug("Minecraft process Java path. InstanceId={InstanceId} JavaPath={JavaPath}", context.InstanceId, context.JavaPath);
                return result;
            }

            LogLaunchFailureReport(
                LogLevel.Error,
                "Minecraft process crashed after startup.",
                result.FailureReport,
                context);
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed while monitoring Minecraft process exit. InstanceId={InstanceId} VersionName={VersionName} JavaPath={JavaPath} JavaVersion={JavaVersion} JavaSource={JavaSource}",
                context.InstanceId,
                context.VersionName,
                context.JavaPath,
                context.JavaVersion,
                context.JavaSource);
            throw;
        }
    }

    private async Task ObserveGameExitAfterCancellationCleanupFailureAsync(
        Task<LaunchExitResult> exitTask,
        LaunchDiagnosticContext context)
    {
        try
        {
            await LogGameExitAsync(exitTask, context).ConfigureAwait(false);
        }
        catch
        {
            // LogGameExitAsync 已记录监控失败；此处消费异常，避免后台任务产生未观察异常。
        }
    }

    private void LogLaunchFailureReport(
        LogLevel level,
        string message,
        LaunchFailureReport report,
        LaunchDiagnosticContext context,
        Exception? exception = null,
        string? failureKindText = null)
    {
        logger.Log(
            level,
            "{Message} FailureKind={FailureKind} ReportKind={ReportKind} InstanceName={InstanceName} VersionName={VersionName} ExitCode={ExitCode} DiagnosticPath={DiagnosticPath} AnalysisCategory={AnalysisCategory} JavaPath={JavaPath} JavaVersion={JavaVersion} JavaSource={JavaSource} ErrorType={ErrorType} HResult={HResult}",
            message,
            failureKindText ?? report.Kind.ToString(),
            report.Kind,
            report.InstanceName,
            report.VersionName,
            report.ExitCode,
            report.DiagnosticPath,
            report.Analysis?.Category,
            context.JavaPath,
            context.JavaVersion,
            context.JavaSource,
            exception?.GetType().Name,
            exception?.HResult);
    }

    private static string? FormatRuntime(TimeSpan? runtime)
    {
        return runtime is null
            ? null
            : runtime.Value.ToString(@"hh\:mm\:ss\.fff");
    }

    private static LaunchDiagnosticContext CreateDiagnosticContext(
        GameInstance instance,
        LauncherSettings settings,
        string versionName,
        JavaRuntimeInfo? javaRuntime,
        int memoryMb,
        IReadOnlyList<string> sensitiveValues)
    {
        return new LaunchDiagnosticContext(
            settings.MinecraftDirectory,
            instance.InstanceDirectory,
            instance.Id,
            string.IsNullOrWhiteSpace(instance.Name) ? versionName : instance.Name,
            versionName,
            instance.MinecraftVersion,
            instance.Loader,
            instance.LoaderVersion,
            javaRuntime?.ExecutablePath,
            javaRuntime?.Version,
            javaRuntime?.Source,
            memoryMb,
            sensitiveValues);
    }

    private static string ResolveDiagnosticDirectory(LaunchDiagnosticContext context, string? diagnosticPath)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticPath))
            return Path.GetDirectoryName(diagnosticPath) ?? Path.Combine(
                context.InstanceDirectory,
                LauncherApplicationIdentity.StorageDirectoryName,
                "logs");

        return Path.Combine(
            context.InstanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName,
            "logs");
    }

    private static MinecraftPath CreateIsolatedLaunchPath(string minecraftDirectory, string versionName)
    {
        var sharedPath = new MinecraftPath(minecraftDirectory);
        var versionDirectory = Path.Combine(sharedPath.Versions, versionName);
        var isolatedPath = new MinecraftPath(versionDirectory)
        {
            Versions = sharedPath.Versions,
            Library = sharedPath.Library,
            Assets = sharedPath.Assets,
            Runtime = sharedPath.Runtime
        };

        return isolatedPath;
    }

    internal static string? ResolveWindowlessJavaPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !OperatingSystem.IsWindows()
            || !string.Equals(Path.GetFileName(executablePath), "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            return executablePath;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
            return executablePath;

        var javawPath = Path.Combine(directory, "javaw.exe");
        return File.Exists(javawPath) ? javawPath : executablePath;
    }

    private void ConfigurePostExitCommand(
        System.Diagnostics.Process process,
        string postExitCommand,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(postExitCommand))
            return;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = RunPostExitCommandAsync(postExitCommand, workingDirectory);
    }

    private async Task RunPostExitCommandAsync(string postExitCommand, string workingDirectory)
    {
        try
        {
            await commandRunner.RunAsync(
                postExitCommand,
                workingDirectory,
                waitForExit: false,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Post-exit command failed. WorkingDirectory={WorkingDirectory}", workingDirectory);
        }
    }

    private sealed record PreparedLaunchRuntime(
        LaunchAccountSession AccountSession,
        AuthlibInjectorArtifact? AuthlibInjector,
        OfflineSkinLaunchContext? OfflineSkin,
        JavaRuntimeInfo? JavaRuntime,
        LaunchDiagnosticContext DiagnosticContext,
        IReadOnlyList<LaunchWarningKind> Warnings);

    private sealed record StartedLaunchProcess(
        System.Diagnostics.Process Process,
        ILaunchCrashMonitorSession CrashMonitorSession);
}
