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

/// <summary>
/// 从 PATH、常见安装目录和用户候选中发现 Java，并通过实际执行解析版本与架构。
/// </summary>
public sealed partial class JavaRuntimeDiscoveryService : IJavaRuntimeDiscoveryService
{
    // 路径发现只是候选阶段；只有 java -version 在限定时间内成功返回才形成可用运行时。
    private const int VersionProbeTimeoutMilliseconds = 1500;
    private const string UnknownArchitecture = "unknown";
    private static readonly Regex VersionRegex = new("\"(?<version>[0-9]+(?:\\.[0-9]+)*(?:[_+\\-][0-9A-Za-z.\\-]+)?)\"", RegexOptions.Compiled);

    public async Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
        string? minecraftDirectory,
        CancellationToken cancellationToken = default)
    {
        var candidates = await CollectDefaultCandidatesAsync(minecraftDirectory, cancellationToken);
        return await ProbeCandidatesAsync(candidates, cancellationToken);
    }

    internal async Task<IReadOnlyList<JavaRuntimeCandidate>> CollectDefaultCandidatesAsync(
        string? minecraftDirectory,
        CancellationToken cancellationToken)
    {
        // 普通发现保持轻量：刷新明确配置与常见目录，并复用已记录的深搜及手动导入结果。
        var candidates = await Task.Run(
            () => configuredCandidateCollector(minecraftDirectory).ToList(),
            cancellationToken);
        candidates.AddRange(await CollectRecordedCandidatesAsync(cancellationToken));
        candidates.AddRange(await CollectImportedCandidatesAsync(cancellationToken));
        return CollapseDuplicateCandidates(candidates);
    }

    public async Task<IReadOnlyList<JavaRuntimeInfo>> SearchAllAsync(
        string? minecraftDirectory,
        CancellationToken cancellationToken = default)
    {
        await deepSearchLock.WaitAsync(cancellationToken);
        try
        {
            return await SearchAndRememberAsync(minecraftDirectory, cancellationToken);
        }
        finally
        {
            deepSearchLock.Release();
        }
    }

    private async Task<IReadOnlyList<JavaRuntimeInfo>> SearchAndRememberAsync(
        string? minecraftDirectory,
        CancellationToken cancellationToken)
    {
        var candidates = await Task.Run(() =>
        {
            var found = configuredCandidateCollector(minecraftDirectory).ToList();
            found.AddRange(CollectLocalDriveCandidates(localDriveRootsProvider(), cancellationToken));
            return found;
        }, cancellationToken);

        var runtimes = await ProbeCandidatesAsync(candidates, cancellationToken);
        await RememberDiscoveredExecutablesAsync(
            runtimes.Select(runtime => runtime.ExecutablePath).ToList(), cancellationToken);

        var imported = await CollectImportedCandidatesAsync(cancellationToken);
        if (imported.Count == 0)
            return runtimes;

        var importedRuntimes = await ProbeCandidatesAsync(imported, cancellationToken);
        return CollapseDuplicateRuntimes([.. runtimes, .. importedRuntimes])
            .OrderByDescending(runtime => runtime.MajorVersion ?? 0)
            .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<JavaRuntimeInfo>> ProbeCandidatesAsync(
        IReadOnlyList<JavaRuntimeCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var runtimes = new List<JavaRuntimeInfo>();
        foreach (var candidate in CollapseDuplicateCandidates(candidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = await CreateRuntimeInfoAsync(candidate, cancellationToken);
            if (runtime.MajorVersion is not null)
                runtimes.Add(runtime);
        }

        return CollapseDuplicateRuntimes(runtimes)
            .OrderByDescending(runtime => runtime.MajorVersion ?? 0)
            .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<JavaRuntimeInfo> DiscoverExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var normalizedPath = NormalizePath(executablePath);
            if (!File.Exists(normalizedPath))
                throw new FileNotFoundException("Java executable was not found.", normalizedPath);

            var runtime = await CreateRuntimeInfoAsync(
                new JavaRuntimeCandidate(
                    normalizedPath,
                    "ManualImport",
                    NormalizePath(ResolveJavaExecutableIdentityPath(normalizedPath))),
                cancellationToken);
            if (runtime.MajorVersion is null)
                throw new InvalidOperationException("Java executable did not report a usable version.");

            return runtime;
        }, cancellationToken);
    }

    internal static IReadOnlyList<JavaRuntimeCandidate> CollectCandidatePaths(
        string? minecraftDirectory,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, string, SearchOption, IEnumerable<string>>? enumerateFiles = null,
        Func<string, string, SearchOption, IEnumerable<string>>? enumerateDirectories = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string>? getProgramFiles = null,
        Func<string>? getProgramFilesX86 = null,
        Func<string>? getApplicationData = null,
        Func<string>? getLocalApplicationData = null,
        Func<string>? getUserProfile = null,
        Func<string>? getDocuments = null,
        Func<IEnumerable<string>>? getRegisteredJavaHomes = null,
        Func<string, string>? resolveIdentityPath = null)
    {
        // 来源优先级保留明确配置，同时安全枚举常见厂商目录作为补充。
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        enumerateFiles ??= EnumerateFilesSafely;
        enumerateDirectories ??= EnumerateDirectoriesSafely;
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        getProgramFiles ??= () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        getProgramFilesX86 ??= () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        getApplicationData ??= () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        getLocalApplicationData ??= () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        getUserProfile ??= () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        getDocuments ??= () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        getRegisteredJavaHomes ??= EnumerateRegisteredJavaHomes;
        resolveIdentityPath ??= ResolveJavaExecutableIdentityPath;

        var candidates = new List<JavaRuntimeCandidate>();
        var seenExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var environmentVariable in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" })
        {
            var home = NormalizeConfiguredDirectory(getEnvironmentVariable(environmentVariable));
            if (string.IsNullOrWhiteSpace(home))
                continue;

            AddCandidate(candidates, seenExecutablePaths, Path.Combine(home, "bin", "java.exe"), environmentVariable, fileExists, resolveIdentityPath);
        }

        var pathValue = getEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalizedPathEntry = NormalizeConfiguredDirectory(pathEntry);
                if (!string.IsNullOrWhiteSpace(normalizedPathEntry))
                    AddCandidate(candidates, seenExecutablePaths, Path.Combine(normalizedPathEntry, "java.exe"), "PATH", fileExists, resolveIdentityPath);
            }
        }

        foreach (var javaHome in getRegisteredJavaHomes())
        {
            var normalizedJavaHome = NormalizeConfiguredDirectory(javaHome);
            if (!string.IsNullOrWhiteSpace(normalizedJavaHome))
                AddCandidate(candidates, seenExecutablePaths, Path.Combine(normalizedJavaHome, "bin", "java.exe"), "RegisteredJava", fileExists, resolveIdentityPath);
        }

        var searchRoots = GetJavaSearchRoots(
            minecraftDirectory,
            getProgramFiles(),
            getProgramFilesX86(),
            getApplicationData(),
            getLocalApplicationData(),
            getUserProfile(),
            getDocuments(),
            enumerateDirectories);
        foreach (var root in searchRoots)
        {
            if (!directoryExists(root.Path))
                continue;

            foreach (var executablePath in enumerateFiles(root.Path, "java.exe", SearchOption.AllDirectories))
                AddCandidate(candidates, seenExecutablePaths, executablePath, root.Source, fileExists, resolveIdentityPath);
        }

        return CollapseDuplicateCandidates(candidates);
    }

    internal static JavaVersionProbeResult ParseVersionOutput(string output)
    {
        // 不同 JVM 把版本写入 stdout 或 stderr，调用方合并后宽松提取引号版本。
        var version = VersionRegex.Match(output).Groups["version"].Value;
        if (string.IsNullOrWhiteSpace(version))
            return new JavaVersionProbeResult(null, null, UnknownArchitecture);

        return new JavaVersionProbeResult(
            version,
            ParseMajorVersion(version),
            ParseArchitecture(output));
    }

    internal static int? ParseMajorVersion(string? version)
    {
        // Java 8 及以前使用 1.8 形式，Java 9+ 首段即主版本，需要兼容两套规则。
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var parts = version.Split(['.', '_', '+', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && parts[0] == "1"
            && int.TryParse(parts[1], out var legacyMajorVersion))
            return legacyMajorVersion;

        return int.TryParse(parts[0], out var majorVersion) ? majorVersion : null;
    }

    internal static IReadOnlyList<JavaRuntimeInfo> CollapseDuplicateRuntimes(IReadOnlyList<JavaRuntimeInfo> runtimes)
    {
        // 同一 java.exe 可由多个来源发现，按规范身份只保留最高优先来源。
        return runtimes
            .GroupBy(GetRuntimeIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(runtime => GetSourcePriority(runtime.Source))
                .ThenBy(runtime => runtime.ExecutablePath.Length)
                .First())
            .ToList();
    }
}
