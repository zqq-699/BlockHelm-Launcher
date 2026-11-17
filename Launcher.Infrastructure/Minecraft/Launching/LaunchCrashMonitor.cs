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

using System.Diagnostics;
using System.IO;
using Launcher.Application;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal interface ILaunchCrashMonitor
{
    ILaunchCrashMonitorSession CreateSession(string minecraftDirectory, string instanceDirectory, string versionName);
}

internal interface ILaunchCrashMonitorSession
{
    Task GameOutputReady { get; }

    void Configure(Process process);

    void BeginMonitoring(Process process, LaunchDiagnosticContext context);

    Task<LaunchCrashMonitorResult> CreateStartupExitResultAsync(
        Process process,
        LaunchDiagnosticContext context,
        CancellationToken cancellationToken);

    Task CompleteCanceledStartupAsync(Process process);

    GameLaunchSession CreateGameLaunchSession(Process process, LaunchDiagnosticContext context);
}

internal sealed record LaunchCrashMonitorResult(LaunchFailureReport Report);

internal sealed class LaunchCrashMonitor : ILaunchCrashMonitor
{
    public ILaunchCrashMonitorSession CreateSession(string minecraftDirectory, string instanceDirectory, string versionName)
    {
        return new Session(minecraftDirectory, instanceDirectory, versionName);
    }

    private sealed class Session : ILaunchCrashMonitorSession
    {
        private readonly string instanceDirectory;
        private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        private readonly LaunchSessionDiagnosticCollector diagnosticCollector;
        private readonly GameStartupLogReadinessDetector startupReadinessDetector = new();
        private LaunchOutputCapture? outputCapture;

        public Task GameOutputReady => startupReadinessDetector.Ready;

        public Session(
            string minecraftDirectory,
            string instanceDirectory,
            string versionName)
        {
            this.instanceDirectory = instanceDirectory;
            diagnosticCollector = new LaunchSessionDiagnosticCollector(minecraftDirectory, instanceDirectory);
        }

        public void Configure(Process process)
        {
            if (process.StartInfo.UseShellExecute)
                return;

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
        }

        public void BeginMonitoring(Process process, LaunchDiagnosticContext context)
        {
            EnsureOutputCaptureStarted(process, context);
        }

        public async Task<LaunchCrashMonitorResult> CreateStartupExitResultAsync(
            Process process,
            LaunchDiagnosticContext context,
            CancellationToken cancellationToken)
        {
            EnsureOutputCaptureStarted(process, context);
            await process.WaitForExitAsync(cancellationToken);
            var capturedOutput = await outputCapture!.CompleteAsync();
            var diagnosticCandidates = await diagnosticCollector.CollectAsync(
                GetProcessStartedAt(process),
                capturedOutput.FilePath,
                cancellationToken);
            var newCrashFiles = GetCrashFiles(diagnosticCandidates);
            var failureKind = IsAbnormalExit(process.ExitCode, newCrashFiles)
                ? LaunchFailureKind.StartupAbnormalExit
                : LaunchFailureKind.StartupProcessExited;
            var diagnostic = await LaunchDiagnosticsWriter.WriteQuickExitDiagnosticAsync(
                context,
                GetFailureKindText(failureKind),
                process.ExitCode,
                DateTimeOffset.UtcNow - createdAt,
                createdAt,
                process.StartInfo,
                diagnosticCandidates,
                capturedOutput.StdOut,
                capturedOutput.StdErr,
                capturedOutput.WasTruncated,
                cancellationToken);

            return new LaunchCrashMonitorResult(CreateReport(
                context,
                failureKind,
                process.ExitCode,
                diagnostic.DiagnosticPath,
                diagnostic.Analysis,
                diagnostic.FailureSummary,
                diagnostic.DiagnosticCandidates));
        }

        public GameLaunchSession CreateGameLaunchSession(Process process, LaunchDiagnosticContext context)
        {
            EnsureOutputCaptureStarted(process, context);
            var exitTask = MonitorProcessExitAsync(process, context);
            return new GameLaunchSession(context.InstanceDirectory, context.InstanceName, exitTask);
        }

        public async Task CompleteCanceledStartupAsync(Process process)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (outputCapture is null)
                return;

            var capturedOutput = await outputCapture.CompleteAsync().ConfigureAwait(false);
            TryDeleteCapturedOutput(capturedOutput.FilePath);
        }

        private async Task<LaunchExitResult> MonitorProcessExitAsync(Process process, LaunchDiagnosticContext context)
        {
            await process.WaitForExitAsync(CancellationToken.None);
            var capturedOutput = await outputCapture!.CompleteAsync();
            var diagnosticCandidates = await diagnosticCollector.CollectAsync(
                GetProcessStartedAt(process),
                capturedOutput.FilePath,
                CancellationToken.None);
            var newCrashFiles = GetCrashFiles(diagnosticCandidates);
            var runtime = DateTimeOffset.UtcNow - createdAt;

            if (!IsAbnormalExit(process.ExitCode, newCrashFiles))
            {
                TryDeleteCapturedOutput(capturedOutput.FilePath);
                return new LaunchExitResult(null, process.ExitCode, runtime);
            }

            var diagnostic = await LaunchDiagnosticsWriter.WriteQuickExitDiagnosticAsync(
                context,
                "runtime_abnormal_exit",
                process.ExitCode,
                runtime,
                createdAt,
                process.StartInfo,
                diagnosticCandidates,
                capturedOutput.StdOut,
                capturedOutput.StdErr,
                capturedOutput.WasTruncated,
                CancellationToken.None);

            var report = CreateReport(
                context,
                LaunchFailureKind.RuntimeAbnormalExit,
                process.ExitCode,
                diagnostic.DiagnosticPath,
                diagnostic.Analysis,
                diagnostic.FailureSummary,
                diagnostic.DiagnosticCandidates);
            return new LaunchExitResult(report, process.ExitCode, runtime);
        }

        private void EnsureOutputCaptureStarted(Process process, LaunchDiagnosticContext context)
        {
            if (outputCapture is not null)
                return;

            var outputPath = Path.Combine(
                instanceDirectory,
                LauncherApplicationIdentity.StorageDirectoryName,
                "logs",
                $"launch-output-{Guid.NewGuid():N}.log");
            outputCapture = new LaunchOutputCapture(
                outputPath,
                context.SensitiveValues,
                lineObserver: startupReadinessDetector.Observe);
            outputCapture.Start(process);
        }

        private static bool IsAbnormalExit(int exitCode, IReadOnlyCollection<string> newCrashFiles)
        {
            return exitCode != 0 || newCrashFiles.Count > 0;
        }

        private static LaunchFailureReport CreateReport(
            LaunchDiagnosticContext context,
            LaunchFailureKind kind,
            int? exitCode,
            string? diagnosticPath,
            LaunchFailureAnalysis? analysis,
            string? failureSummary,
            IReadOnlyList<LaunchDiagnosticReference>? diagnosticCandidates)
        {
            return new LaunchFailureReport(
                kind,
                context.InstanceName,
                context.VersionName,
                exitCode,
                diagnosticPath,
                ResolveDiagnosticDirectory(context, diagnosticPath),
                analysis,
                failureSummary)
            {
                DiagnosticCandidates = diagnosticCandidates ?? [],
                ExportSensitiveValues = context.SensitiveValues.ToArray()
            };
        }

        private static DateTimeOffset GetProcessStartedAt(Process process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return DateTimeOffset.UtcNow;
            }
        }

        private static IReadOnlyList<string> GetCrashFiles(
            IReadOnlyList<LaunchDiagnosticReference> candidates)
        {
            return candidates
                .Where(candidate => candidate.Type is LaunchDiagnosticType.MinecraftCrashReport
                    or LaunchDiagnosticType.JvmCrashReport)
                .Select(candidate => candidate.Path)
                .ToArray();
        }

        private static void TryDeleteCapturedOutput(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
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

        private static string GetFailureKindText(LaunchFailureKind failureKind)
        {
            return failureKind switch
            {
                LaunchFailureKind.StartupProcessExited => "startup_process_exited",
                LaunchFailureKind.StartupAbnormalExit => "startup_abnormal_exit",
                LaunchFailureKind.RuntimeAbnormalExit => "runtime_abnormal_exit",
                _ => "startup_failed"
            };
        }
    }
}
