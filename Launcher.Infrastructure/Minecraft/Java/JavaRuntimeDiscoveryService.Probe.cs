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
using System.Text.RegularExpressions;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class JavaRuntimeDiscoveryService
{
private static async Task<JavaRuntimeInfo> CreateRuntimeInfoAsync(
        JavaRuntimeCandidate candidate,
        CancellationToken cancellationToken)
    {
        var probeResult = await ProbeVersionAsync(candidate.ExecutablePath, cancellationToken);
        var installationDirectory = GetInstallationDirectory(candidate.ExecutablePath);
        var displayName = probeResult.MajorVersion is int majorVersion
            ? $"Java {majorVersion}"
            : "Java";

        return new JavaRuntimeInfo(
            displayName,
            probeResult.Version,
            probeResult.MajorVersion,
            probeResult.Architecture,
            candidate.ExecutablePath,
            installationDirectory,
            candidate.Source);
    }

    private static async Task<JavaVersionProbeResult> ProbeVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        // 子进程设超时并异步读取双输出流，避免损坏 JVM 永久挂起发现页面。
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(exitTask, Task.Delay(VersionProbeTimeoutMilliseconds, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completedTask != exitTask)
            {
                TryKill(process);
                return new JavaVersionProbeResult(null, null, UnknownArchitecture);
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                return new JavaVersionProbeResult(null, null, UnknownArchitecture);

            return ParseVersionOutput(string.Concat(error, Environment.NewLine, output));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new JavaVersionProbeResult(null, null, UnknownArchitecture);
        }
    }

    private static void TryKill(Process process)
    {
        // 超时清理尽力而为，进程已退出或权限不足不能覆盖原发现结果。
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string ParseArchitecture(string output)
    {
        if (output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)
            || output.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
            || output.Contains("amd64", StringComparison.OrdinalIgnoreCase))
            return "x64";

        if (output.Contains("32-Bit", StringComparison.OrdinalIgnoreCase)
            || output.Contains("x86", StringComparison.OrdinalIgnoreCase))
            return "x86";

        return UnknownArchitecture;
    }

    private static string GetInstallationDirectory(string executablePath)
    {
        var binDirectory = Path.GetDirectoryName(executablePath);
        var installationDirectory = binDirectory is null ? null : Directory.GetParent(binDirectory);

        if (installationDirectory is not null
            && string.Equals(installationDirectory.Name, "jre", StringComparison.OrdinalIgnoreCase)
            && installationDirectory.Parent is not null)
            return installationDirectory.Parent.FullName;

        return installationDirectory?.FullName ?? string.Empty;
    }

    private static string GetRuntimeIdentityKey(JavaRuntimeInfo runtime)
    {
        var installationDirectory = NormalizePath(runtime.InstallationDirectory);
        if (!string.IsNullOrWhiteSpace(runtime.Version))
            return string.Join('|', installationDirectory, runtime.Version, runtime.Architecture);

        return NormalizePath(runtime.ExecutablePath);
    }

    private static int GetSourcePriority(string source)
    {
        return source.ToUpperInvariant() switch
        {
            "JAVA_HOME" => 0,
            "JDK_HOME" => 1,
            "JRE_HOME" => 2,
            "PROGRAMFILES" => 3,
            "MINECRAFTRUNTIME" => 4,
            "OFFICIALMINECRAFTRUNTIME" => 5,
            "REGISTEREDJAVA" => 6,
            "USERJAVA" => 7,
            "THIRDPARTYLAUNCHERRUNTIME" => 8,
            "PATH" => 9,
            _ => 10
        };
    }
}
