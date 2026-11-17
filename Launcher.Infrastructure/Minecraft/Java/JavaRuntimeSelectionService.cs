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
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 根据实例/全局偏好、版本元数据和兼容级别为启动选择最合适的 Java 运行时。
/// </summary>
public sealed class JavaRuntimeSelectionService : IJavaRuntimeSelectionService
{
    // 只返回经过发现服务验证的运行时，不把未经探测的路径直接交给启动进程。
    private readonly IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService;
    private readonly ILogger logger;

    public JavaRuntimeSelectionService(
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        ILogger<JavaRuntimeSelectionService>? logger = null)
    {
        this.javaRuntimeDiscoveryService = javaRuntimeDiscoveryService;
        this.logger = logger ?? NullLogger<JavaRuntimeSelectionService>.Instance;
    }

    public async Task<JavaRuntimeInfo> SelectForLaunchAsync(
        GameInstance instance,
        LauncherSettings settings,
        LaunchRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveSelection = ResolveEffectiveSelection(instance, settings);
        if (effectiveSelection.Mode is JavaSelectionMode.Manual)
            return await SelectManualRuntimeAsync(instance, settings, effectiveSelection.ExecutablePath, options, cancellationToken);

        return await SelectAutomaticRuntimeAsync(instance, settings, cancellationToken);
    }

    internal static EffectiveJavaSelection ResolveEffectiveSelection(GameInstance instance, LauncherSettings settings)
    {
        // 全局模式继承 LauncherSettings，否则使用实例自己的 Java 模式和路径。
        return instance.JavaSettingsMode is LaunchSettingsMode.PerInstance
            ? new EffectiveJavaSelection(instance.JavaSelectionMode, instance.SelectedJavaExecutablePath)
            : new EffectiveJavaSelection(settings.JavaSelectionMode, settings.SelectedJavaExecutablePath);
    }

    internal static int? ResolveRequiredMajorVersion(
        GameInstance instance,
        LauncherSettings settings,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readAllText = null) =>
        ResolveCompatibilityRequirement(instance, settings, fileExists, readAllText).RecommendedMajorVersion;

    internal static JavaRuntimeCompatibilityRequirement ResolveCompatibilityRequirement(
        GameInstance instance,
        LauncherSettings settings,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readAllText = null)
    {
        // JSON 的 javaVersion.majorVersion 最权威，缺失时才按 Minecraft 版本区间推断。
        fileExists ??= File.Exists;
        readAllText ??= File.ReadAllText;

        foreach (var versionJsonPath in EnumerateVersionJsonPaths(instance, settings))
        {
            if (!fileExists(versionJsonPath))
                continue;

            var requiredMajorVersion = TryReadJavaMajorVersion(versionJsonPath, readAllText);
            if (requiredMajorVersion is not null)
            {
                return JavaRuntimeCompatibilityResolver.Resolve(
                    ResolveMinecraftVersion(instance, settings, fileExists, readAllText),
                    instance.Loader,
                    instance.LoaderVersion,
                    requiredMajorVersion);
            }
        }

        var minecraftVersion = ResolveMinecraftVersion(instance, settings, fileExists, readAllText);
        return JavaRuntimeCompatibilityResolver.Resolve(
            minecraftVersion,
            instance.Loader,
            instance.LoaderVersion,
            metadataMajorVersion: null);
    }

    internal static JavaRuntimeInfo? SelectBestRuntime(
        IReadOnlyList<JavaRuntimeInfo> runtimes,
        int? requiredMajorVersion) =>
        SelectBestRuntime(
            runtimes,
            new JavaRuntimeCompatibilityRequirement(
                requiredMajorVersion,
                requiredMajorVersion is int required
                    ? new JavaVersionBound(new JavaVersionNumber(required), true)
                    : null,
                null));

    internal static JavaRuntimeInfo? SelectBestRuntime(
        IReadOnlyList<JavaRuntimeInfo> runtimes,
        JavaRuntimeCompatibilityRequirement requirement)
    {
        // 先按兼容层级筛选，再偏好 x64 和更接近要求的版本。
        if (runtimes.Count == 0)
            return null;

        if (!requirement.HasKnownRequirement)
            return runtimes
                .OrderByDescending(IsX64)
                .ThenByDescending(runtime => runtime.MajorVersion ?? 0)
                .ThenByDescending(runtime => TryParseRuntimeVersion(runtime, out var version)
                    ? (JavaVersionNumber?)version
                    : null)
                .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .First();

        if (requirement.IsConflicting)
            return null;

        return runtimes
            .Where(requirement.IsCompatible)
            .Select(runtime => new
            {
                Runtime = runtime,
                Tier = GetCompatibilityTier(runtime.MajorVersion, requirement.RecommendedMajorVersion),
                Distance = GetCompatibilityDistance(runtime.MajorVersion, requirement.RecommendedMajorVersion),
                ParsedVersion = TryParseRuntimeVersion(runtime, out var parsedVersion)
                    ? parsedVersion
                    : (JavaVersionNumber?)null
            })
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.Distance)
            .ThenByDescending(item => IsX64(item.Runtime))
            .ThenByDescending(item => item.ParsedVersion)
            .ThenBy(item => item.Runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Runtime)
            .FirstOrDefault();
    }

    internal static int? GuessRequiredMajorVersion(string? minecraftVersion)
    {
        // 解析失败时不武断限制 Java。
        if (!MinecraftVersionNumber.TryParse(minecraftVersion, out var version))
            return null;

        var (major, minor, patch) = version;

        if (major >= 26)
            return 25;

        if (major > 1 || major == 1 && (minor > 20 || minor == 20 && patch >= 5))
            return 21;

        if (major == 1 && minor >= 18)
            return 17;

        if (major == 1 && minor >= 17)
            return 16;

        return 8;
    }

    private async Task<JavaRuntimeInfo> SelectManualRuntimeAsync(
        GameInstance instance,
        LauncherSettings settings,
        string? executablePath,
        LaunchRequestOptions? options,
        CancellationToken cancellationToken)
    {
        // 手动路径仍需探测；不存在、不可执行和版本过低映射为不同失败原因。
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new JavaRuntimeSelectionException(
                "No Java runtime is selected for manual Java mode.",
                JavaRuntimeSelectionFailureReason.ManualRuntimeMissing);
        }

        try
        {
            var runtime = await javaRuntimeDiscoveryService.DiscoverExecutableAsync(
                executablePath,
                cancellationToken);
            ValidateManualRuntimeRequirement(instance, settings, runtime, options);
            return runtime;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (exception is JavaRuntimeSelectionException)
                throw;

            throw new JavaRuntimeSelectionException(
                "The selected Java runtime is not available.",
                exception,
                JavaRuntimeSelectionFailureReason.ManualRuntimeUnavailable);
        }
    }

    private static void ValidateManualRuntimeRequirement(
        GameInstance instance,
        LauncherSettings settings,
        JavaRuntimeInfo runtime,
        LaunchRequestOptions? options)
    {
        if (options?.IgnoreJavaVersionRequirement == true)
            return;

        var requirement = ResolveCompatibilityRequirement(instance, settings);
        if (!requirement.HasKnownRequirement || requirement.IsCompatible(runtime))
            return;

        var required = requirement.RecommendedMajorVersion;
        var current = runtime.MajorVersion;
        var failureReason = current is int currentMajor
                            && requirement.Minimum is JavaVersionBound minimum
                            && currentMajor < minimum.Version.Major
            ? JavaRuntimeSelectionFailureReason.ManualRuntimeVersionTooLow
            : JavaRuntimeSelectionFailureReason.ManualRuntimeIncompatible;

        throw new JavaRuntimeSelectionException(
            $"The selected Java runtime does not meet the game and loader compatibility requirement. Requirement: {requirement}; selected: {runtime.Version ?? runtime.MajorVersion?.ToString() ?? "unknown"}.",
            failureReason,
            required,
            current,
            currentVersion: runtime.Version,
            recommendedMajorVersion: requirement.RecommendedMajorVersion);
    }

    private async Task<JavaRuntimeInfo> SelectAutomaticRuntimeAsync(
        GameInstance instance,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        // 自动发现后应用统一排序，没有兼容项时保留实际版本信息用于诊断。
        var runtimes = await javaRuntimeDiscoveryService.DiscoverAsync(
            settings.MinecraftDirectory,
            cancellationToken);
        var requirement = ResolveCompatibilityRequirement(instance, settings);
        var requiredMajorVersion = requirement.RecommendedMajorVersion;
        if (runtimes.Count == 0)
        {
            var missingRequirementText = requiredMajorVersion is int missingRequired
                ? $"Java {missingRequired} or newer"
                : "a usable Java runtime";
            throw new JavaRuntimeSelectionException(
                $"No {missingRequirementText} was found.",
                JavaRuntimeSelectionFailureReason.AutomaticRuntimeMissing,
                requiredMajorVersion);
        }

        var selectedRuntime = SelectBestRuntime(runtimes, requirement);

        if (selectedRuntime is not null)
        {
            logger.LogInformation(
                "Selected Java runtime for game launch. InstanceDirectory={InstanceDirectory} MinecraftVersion={MinecraftVersion} Loader={Loader} LoaderVersion={LoaderVersion} JavaRequirement={JavaRequirement} JavaVersion={JavaVersion} JavaArchitecture={JavaArchitecture} JavaSource={JavaSource}",
                instance.InstanceDirectory,
                instance.MinecraftVersion,
                instance.Loader,
                instance.LoaderVersion,
                requirement,
                selectedRuntime.Version,
                selectedRuntime.Architecture,
                selectedRuntime.Source);
            return selectedRuntime;
        }

        foreach (var runtime in runtimes.Where(runtime => !requirement.IsCompatible(runtime)))
        {
            logger.LogDebug(
                "Excluded incompatible Java runtime from automatic selection. InstanceDirectory={InstanceDirectory} JavaVersion={JavaVersion} JavaArchitecture={JavaArchitecture} JavaSource={JavaSource} JavaRequirement={JavaRequirement} Reason={Reason}",
                instance.InstanceDirectory,
                runtime.Version,
                runtime.Architecture,
                runtime.Source,
                requirement,
                requirement.GetIncompatibilityReason(runtime));
        }

        var requirementText = requiredMajorVersion is int required
            ? $"a Java runtime compatible with the game and loader (recommended Java {required})"
            : "a usable Java runtime";
        var discoveredVersions = string.Join(
            ", ",
            runtimes
                .Select(runtime => runtime.MajorVersion?.ToString() ?? "unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => version));

        throw new JavaRuntimeSelectionException(
            string.IsNullOrWhiteSpace(discoveredVersions)
                ? $"No {requirementText} was found."
                : $"No {requirementText} was found. Discovered Java versions: {discoveredVersions}.",
            JavaRuntimeSelectionFailureReason.AutomaticRuntimeNotFound,
            requiredMajorVersion);
    }

    private static IEnumerable<string> EnumerateVersionJsonPaths(GameInstance instance, LauncherSettings settings)
    {
        // 隔离和全局 versions 都可能持有元数据，按实例优先去重兼容旧布局。
        var versionName = string.IsNullOrWhiteSpace(instance.VersionName)
            ? instance.MinecraftVersion
            : instance.VersionName;

        if (!string.IsNullOrWhiteSpace(instance.InstanceDirectory) && !string.IsNullOrWhiteSpace(versionName))
            yield return Launcher.Infrastructure.Persistence.InstanceVersionFiles.TryResolveVersionJsonPath(instance.InstanceDirectory, versionName)
                ?? Path.Combine(instance.InstanceDirectory, $"{versionName}.json");

        if (!string.IsNullOrWhiteSpace(settings.MinecraftDirectory) && !string.IsNullOrWhiteSpace(versionName))
            yield return Launcher.Infrastructure.Persistence.InstanceVersionFiles.TryResolveVersionJsonPath(Path.Combine(settings.MinecraftDirectory, "versions", versionName), versionName)
                ?? Path.Combine(settings.MinecraftDirectory, "versions", versionName, $"{versionName}.json");
    }

    private static int? TryReadJavaMajorVersion(
        string versionJsonPath,
        Func<string, string> readAllText)
    {
        try
        {
            using var document = JsonDocument.Parse(readAllText(versionJsonPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("javaVersion", out var javaVersion)
                || javaVersion.ValueKind is not JsonValueKind.Object
                || !javaVersion.TryGetProperty("majorVersion", out var majorVersion))
            {
                return null;
            }

            return majorVersion.ValueKind is JsonValueKind.Number
                   && majorVersion.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveMinecraftVersion(
        GameInstance instance,
        LauncherSettings settings,
        Func<string, bool> fileExists,
        Func<string, string> readAllText)
    {
        if (!string.IsNullOrWhiteSpace(instance.MinecraftVersion))
            return instance.MinecraftVersion;

        foreach (var versionJsonPath in EnumerateVersionJsonPaths(instance, settings))
        {
            if (!fileExists(versionJsonPath))
                continue;

            var metadataVersion = TryReadLauncherMinecraftVersion(versionJsonPath, readAllText);
            if (!string.IsNullOrWhiteSpace(metadataVersion))
                return metadataVersion;
        }

        return string.IsNullOrWhiteSpace(instance.VersionName)
            ? string.Empty
            : instance.VersionName;
    }

    private static string TryReadLauncherMinecraftVersion(
        string versionJsonPath,
        Func<string, string> readAllText)
    {
        try
        {
            using var document = JsonDocument.Parse(readAllText(versionJsonPath));
            return LauncherVersionMetadata.ReadMinecraftVersion(document.RootElement);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetCompatibilityTier(int? runtimeMajorVersion, int? recommendedMajorVersion)
    {
        if (runtimeMajorVersion is null)
            return 3;

        if (recommendedMajorVersion is null)
            return 0;

        if (runtimeMajorVersion == recommendedMajorVersion)
            return 0;

        return runtimeMajorVersion > recommendedMajorVersion ? 1 : 2;
    }

    private static int GetCompatibilityDistance(int? runtimeMajorVersion, int? recommendedMajorVersion)
    {
        return runtimeMajorVersion is int runtime && recommendedMajorVersion is int recommended
            ? Math.Abs(runtime - recommended)
            : 0;
    }

    private static bool TryParseRuntimeVersion(JavaRuntimeInfo runtime, out JavaVersionNumber version) =>
        JavaVersionNumber.TryParse(runtime.Version, out version);

    private static bool IsX64(JavaRuntimeInfo runtime)
    {
        return string.Equals(runtime.Architecture, "x64", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record EffectiveJavaSelection(JavaSelectionMode Mode, string? ExecutablePath);
}
