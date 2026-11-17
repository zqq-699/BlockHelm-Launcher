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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal interface IForgeInstallerRunner
{
    Task RunInstallerAsync(string javaExecutablePath, string installerJarPath, string minecraftDirectory, CancellationToken cancellationToken);

    Task RunServerInstallerAsync(
        string javaExecutablePath,
        string installerJarPath,
        string serverDirectory,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Server installation is not supported by this installer runner.");
}

internal interface IFinalVersionInstaller
{
    Task InstallAsync(
        string gameDirectory,
        string versionName,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0);

    Task InstallAsync(
        MinecraftPath path,
        string versionName,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        return InstallAsync(
            path.BasePath,
            versionName,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    Task InstallAsync(
        MinecraftPath path,
        string versionName,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        SpeedMeter? speedMeter)
    {
        return InstallAsync(
            path,
            versionName,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    Task InstallAsync(
        MinecraftPath path,
        string versionName,
        MinecraftDownloadOperationContext operationContext,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        return InstallAsync(
            path,
            versionName,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    Task InstallAsync(
        MinecraftPath path,
        string versionName,
        MinecraftDownloadOperationContext operationContext,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        SpeedMeter? speedMeter)
    {
        return InstallAsync(
            path,
            versionName,
            operationContext,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }
}

internal sealed class FinalVersionInstaller(ILogger? logger = null) : IFinalVersionInstaller
{
    public Task InstallAsync(
        string gameDirectory,
        string versionName,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        return InstallAsync(
            new MinecraftPath(gameDirectory),
            versionName,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    public async Task InstallAsync(
        MinecraftPath path,
        string versionName,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        using var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(path);
        await InstallAsync(
            path,
            versionName,
            downloadOperation,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond).ConfigureAwait(false);
    }

    public async Task InstallAsync(
        MinecraftPath path,
        string versionName,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        SpeedMeter? speedMeter)
    {
        using var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(path);
        await InstallAsync(
            path,
            versionName,
            downloadOperation,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond,
            speedMeter).ConfigureAwait(false);
    }

    public async Task InstallAsync(
        MinecraftPath path,
        string versionName,
        MinecraftDownloadOperationContext operationContext,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        await InstallAsync(
            path,
            versionName,
            operationContext,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond,
            speedMeter: SpeedMeterProgress.TryGet(progress)).ConfigureAwait(false);
    }

    public async Task InstallAsync(
        MinecraftPath path,
        string versionName,
        MinecraftDownloadOperationContext operationContext,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        SpeedMeter? speedMeter)
    {
        ArgumentNullException.ThrowIfNull(operationContext);
        var launcher = VanillaLoaderProvider.CreateLauncher(
            path,
            progress,
            downloadSourcePreference,
            logger: logger,
            downloadSpeedLimitMbPerSecond: downloadSpeedLimitMbPerSecond,
            operationContext: operationContext,
            sharedSpeedMeter: speedMeter);
        VanillaLoaderProvider.AttachProgress(launcher, progress);
        await launcher.InstallAsync(versionName, cancellationToken);
    }
}

internal sealed class ForgeInstallerRunner : IForgeInstallerRunner
{
    internal const string InstallClientUnrecognizedOptionMessage = "'installClient' is not a recognized option";
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly ILogger? logger;

    public ForgeInstallerRunner(ILogger? logger = null)
        : this(Process.Start, logger)
    {
    }

    internal ForgeInstallerRunner(Func<ProcessStartInfo, Process?> startProcess, ILogger? logger = null)
    {
        this.startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
        this.logger = logger;
    }

    public Task RunInstallerAsync(
        string javaExecutablePath,
        string installerJarPath,
        string minecraftDirectory,
        CancellationToken cancellationToken) =>
        RunInstallerCoreAsync(
            javaExecutablePath,
            installerJarPath,
            minecraftDirectory,
            "--installClient",
            cancellationToken);

    public Task RunServerInstallerAsync(
        string javaExecutablePath,
        string installerJarPath,
        string serverDirectory,
        CancellationToken cancellationToken) =>
        RunInstallerCoreAsync(
            javaExecutablePath,
            installerJarPath,
            serverDirectory,
            "--installServer",
            cancellationToken);

    private async Task RunInstallerCoreAsync(
        string javaExecutablePath,
        string installerJarPath,
        string minecraftDirectory,
        string installArgument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerJarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftDirectory);
        if (!Path.IsPathFullyQualified(javaExecutablePath))
        {
            throw new ArgumentException(
                "The Java executable path must be fully qualified.",
                nameof(javaExecutablePath));
        }

        using var operation = DownloadDiagnostics.BeginOperation(logger, "LoaderInstallerProcess");
        var stopwatch = Stopwatch.StartNew();
        logger?.LogInformation("Loader installer process starting. JavaPath={JavaPath} InstallerFile={InstallerFile} WorkingDirectory={WorkingDirectory} InstallMode={InstallMode}", javaExecutablePath, Path.GetFileName(installerJarPath), minecraftDirectory, installArgument);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = javaExecutablePath,
                WorkingDirectory = minecraftDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-jar");
            startInfo.ArgumentList.Add(installerJarPath);
            startInfo.ArgumentList.Add(installArgument);
            startInfo.ArgumentList.Add(minecraftDirectory);

            using var process = startProcess(startInfo)
                ?? throw SafeExceptionLog.Describe(new InvalidOperationException("Forge installer could not be started."), "DownloadValidation", ("Reason", "Forge installer could not be started."));

            // The JVM echoes these inherited values before main(). Treat them as secrets;
            // inspect only these known entries, never enumerate or log the environment.
            var inheritedOptions = new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" }
                .Select(name => startInfo.Environment.TryGetValue(name, out var value) ? value : null)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
            var capture = new InstallerProcessOutput(logger, process.Id, inheritedOptions);
            var outputTask = capture.PumpAsync(process.StandardOutput, "stdout");
            var errorTask = capture.PumpAsync(process.StandardError, "stderr");
            using var heartbeat = new Timer(_ => logger?.LogInformation("Loader installer process still running. ProcessId={ProcessId} DurationMs={DurationMs} LastOutputAgeMs={LastOutputAgeMs}", process.Id, stopwatch.ElapsedMilliseconds, capture.SinceLastOutput.TotalMilliseconds), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { await TerminateProcessTreeAsync(process, outputTask, errorTask).ConfigureAwait(false); }
                catch (Exception cleanupException)
                {
                    logger?.LogError(cleanupException, "Canceled installer process cleanup failed. ProcessId={ProcessId} PrimaryFailure=Cancellation", process.Id);
                    throw new OperationCanceledException("The installer was canceled; process cleanup also failed.", cleanupException, cancellationToken);
                }
                logger?.LogInformation("Loader installer process canceled and terminated. ProcessId={ProcessId} DurationMs={DurationMs} StdoutTail={StdoutTail} StderrTail={StderrTail}", process.Id, stopwatch.ElapsedMilliseconds, await outputTask, await errorTask);
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;
            logger?.LogInformation("Loader installer process exited. ProcessId={ProcessId} ExitCode={ExitCode} DurationMs={DurationMs}", process.Id, process.ExitCode, stopwatch.ElapsedMilliseconds);
            if (process.ExitCode == 0)
                return;
            var failure = new InstallerProcessException(process.ExitCode, capture.UnsupportedInstallClient, output, error);
            logger?.LogError(failure, "Loader installer process failed. ProcessId={ProcessId} ExitCode={ExitCode}", process.Id, process.ExitCode);
            throw failure;
        }
        catch (Win32Exception exception)
        {
            logger?.LogError(exception, "Loader installer process could not start. JavaPath={JavaPath} Stage=JavaProcessStart", javaExecutablePath);
            throw new InvalidOperationException("No usable Java runtime was found for Forge installation.", exception);
        }
        catch (FileNotFoundException exception)
        {
            logger?.LogError(exception, "Loader installer executable was not found. JavaPath={JavaPath} Stage=JavaProcessStart", javaExecutablePath);
            throw new InvalidOperationException("No usable Java runtime was found for Forge installation.", exception);
        }
    }

    private static async Task TerminateProcessTreeAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (Win32Exception exception)
        {
            throw new IOException("The canceled Forge installer process tree could not be terminated.", exception);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ProcessTerminationTimeout)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process was already reaped by the operating system.
        }
        catch (TimeoutException exception)
        {
            throw new IOException("The canceled Forge installer process did not terminate in time.", exception);
        }

        try
        {
            await Task.WhenAll(outputTask, errorTask)
                .WaitAsync(ProcessTerminationTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new IOException("The canceled Forge installer output streams did not close in time.", exception);
        }
    }
}
