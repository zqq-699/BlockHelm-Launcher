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

using System.Globalization;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Application.Services;

/// <summary>
/// 解析 Mod 的递归必需依赖，结合本地已启用版本生成安装计划并按依赖顺序执行。
/// </summary>
public sealed class ResourceDependencyPlanningService : IResourceDependencyPlanningService
{
    private const int DependencyVersionPageSize = 10000;
    private readonly IResourceCatalogService resourceCatalogService;
    private readonly IResourceProjectInstallationService installationService;
    private readonly IModService modService;
    private readonly ILogger<ResourceDependencyPlanningService> logger;

    public ResourceDependencyPlanningService(
        IResourceCatalogService resourceCatalogService,
        IResourceProjectInstallationService installationService,
        IModService modService,
        ILogger<ResourceDependencyPlanningService> logger)
    {
        this.resourceCatalogService = resourceCatalogService;
        this.installationService = installationService;
        this.modService = modService;
        this.logger = logger;
    }

    /// <summary>
    /// 解析直接必需依赖并结合本地版本标记为已安装、需更新或缺失。
    /// </summary>
    public async Task<ResourceDependencyInstallPlan> CreatePlanAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (version.RequiredDependencies.Count == 0)
            return new ResourceDependencyInstallPlan([], []);

        var installedDependencies = await LoadEnabledLocalModIdentifiersAsync(instance, cancellationToken)
            .ConfigureAwait(false);
        var requirements = new List<ResourceDependencyInstallCandidate>();
        foreach (var dependency in version.RequiredDependencies)
        {
            var candidate = await TryResolveCandidateAsync(dependency, instance, cancellationToken)
                .ConfigureAwait(false);
            requirements.Add(candidate with
            {
                State = ResolveRequirementState(candidate, installedDependencies)
            });
        }

        return new ResourceDependencyInstallPlan(
            requirements,
            requirements.Where(candidate => candidate.State is not ResourceDependencyRequirementState.Installed).ToArray());
    }

    /// <summary>
    /// 以深度优先顺序安装依赖图，自动去重并截断循环依赖。
    /// </summary>
    public async Task InstallRequiredDependenciesAsync(
        IReadOnlyList<ResourceDependencyInstallCandidate> dependencies,
        GameInstance instance,
        IProgress<ResourceDependencyInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installedDependencies = await LoadEnabledLocalModIdentifiersAsync(instance, cancellationToken)
            .ConfigureAwait(false);
        // visiting 用于截断依赖环，visited 用于避免多个父依赖重复安装同一项目。
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            await InstallDependencyAsync(
                dependency,
                instance,
                installedDependencies,
                visiting,
                visited,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 根据项目标识符和最低版本判断本地依赖是否满足要求。
    /// </summary>
    internal static ResourceDependencyRequirementState ResolveRequirementState(
        ResourceDependencyInstallCandidate candidate,
        InstalledDependencyCatalog installedDependencies)
    {
        if (candidate.MinimumVersion is null || string.IsNullOrWhiteSpace(candidate.MinimumVersion.VersionNumber))
            return ResourceDependencyRequirementState.Missing;

        var found = false;
        foreach (var identifier in EnumerateIdentifiers(candidate.Dependency.Project))
        {
            foreach (var installedVersion in installedDependencies.GetVersions(identifier))
            {
                found = true;
                if (DependencyVersionComparer.IsGreaterThanOrEqual(
                    installedVersion,
                    candidate.MinimumVersion.VersionNumber))
                {
                    return ResourceDependencyRequirementState.Installed;
                }
            }
        }

        return found ? ResourceDependencyRequirementState.UpdateRequired : ResourceDependencyRequirementState.Missing;
    }

    internal static ResourceProjectVersion? SelectInstallVersion(IReadOnlyList<ResourceProjectVersion> versions)
    {
        return versions.FirstOrDefault(version => string.Equals(version.VersionType, "release", StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault();
    }

    /// <summary>
    /// 递归安装一个依赖及其子依赖，并同步更新本次计划的内存安装目录。
    /// </summary>
    private async Task InstallDependencyAsync(
        ResourceDependencyInstallCandidate candidate,
        GameInstance instance,
        InstalledDependencyCatalog installedDependencies,
        ISet<string> visiting,
        ISet<string> visited,
        IProgress<ResourceDependencyInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = candidate.Dependency.Project;
        var key = string.IsNullOrWhiteSpace(project.ProjectId) ? project.Slug : project.ProjectId;
        if (string.IsNullOrWhiteSpace(key)
            || visited.Contains(key)
            || ResolveRequirementState(candidate, installedDependencies) is ResourceDependencyRequirementState.Installed)
        {
            return;
        }
        if (!visiting.Add(key))
            return;

        try
        {
            var version = candidate.InstallVersion ?? throw new ResourceDependencyInstallException(project);
            foreach (var child in version.RequiredDependencies)
            {
                var childCandidate = await ResolveCandidateAsync(child, instance, cancellationToken).ConfigureAwait(false);
                await InstallDependencyAsync(
                    childCandidate with { State = ResolveRequirementState(childCandidate, installedDependencies) },
                    instance,
                    installedDependencies,
                    visiting,
                    visited,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (ResolveRequirementState(candidate, installedDependencies) is ResourceDependencyRequirementState.Installed)
                return;
            progress?.Report(new ResourceDependencyInstallProgress(project.Title));
            var downloadProgress = SpeedMeterProgress.TryGet(progress) is { } speedMeter
                ? new SpeedMeterProgress(speedMeter, static _ => { })
                : null;
            await installationService.ExecuteAsync(
                new ResourceProjectInstallationRequest(
                    version,
                    ResourceProjectInstallationTargetKind.ExistingInstance,
                    Instance: instance),
                downloadProgress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            // 立即更新内存目录，使同一计划中的后续分支能看到刚安装的依赖而无需重新扫描磁盘。
            AddIdentifiers(installedDependencies, candidate.Dependency, version);
            logger.LogInformation(
                "Installed required resource dependency. DependencyProjectId={DependencyProjectId} VersionId={VersionId} InstanceDirectory={InstanceDirectory}",
                project.ProjectId,
                version.VersionId,
                instance.InstanceDirectory);
        }
        finally
        {
            visiting.Remove(key);
            visited.Add(key);
        }
    }

    private async Task<ResourceDependencyInstallCandidate> TryResolveCandidateAsync(
        ResourceProjectDependency dependency,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveCandidateAsync(dependency, instance, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceDependencyInstallException)
        {
            return new ResourceDependencyInstallCandidate(
                dependency,
                null,
                null,
                ResourceDependencyRequirementState.Missing);
        }
    }

    /// <summary>
    /// 查询与实例 Minecraft/Loader 兼容的依赖版本，并选择最低要求和实际安装版本。
    /// </summary>
    private async Task<ResourceDependencyInstallCandidate> ResolveCandidateAsync(
        ResourceProjectDependency dependency,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var project = dependency.Project;
        if (project.Source is not (ResourceProjectSource.Modrinth or ResourceProjectSource.CurseForge)
            || project.Kind is not ResourceProjectKind.Mod)
        {
            throw new ResourceDependencyInstallException(project);
        }

        var result = await resourceCatalogService.GetProjectVersionsAsync(
            new ResourceProjectVersionsRequest
            {
                Kind = ResourceProjectKind.Mod,
                Source = project.Source,
                ProjectId = project.ProjectId,
                Slug = project.Slug,
                MinecraftVersion = instance.MinecraftVersion,
                Loader = instance.Loader,
                IncludeAllVersions = false,
                Offset = 0,
                PageSize = DependencyVersionPageSize
            },
            cancellationToken).ConfigureAwait(false);
        var installVersion = SelectInstallVersion(result.Versions)
            ?? throw new ResourceDependencyInstallException(project);
        var minimumVersion = string.IsNullOrWhiteSpace(dependency.VersionId)
            ? installVersion
            : result.Versions.FirstOrDefault(version => string.Equals(
                version.VersionId,
                dependency.VersionId,
                StringComparison.OrdinalIgnoreCase)) ?? installVersion;
        return new ResourceDependencyInstallCandidate(
            dependency,
            minimumVersion,
            installVersion,
            ResourceDependencyRequirementState.Missing);
    }

    /// <summary>
    /// 扫描已启用 Mod，构建忽略大小写的 ModId 到版本列表索引。
    /// </summary>
    private async Task<InstalledDependencyCatalog> LoadEnabledLocalModIdentifiersAsync(
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var mods = await modService.GetModsAsync(instance, cancellationToken).ConfigureAwait(false);
        var versions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(mod => mod.IsEnabled
            && !string.IsNullOrWhiteSpace(mod.ModId)
            && !string.IsNullOrWhiteSpace(mod.Version)))
        {
            var id = mod.ModId!.Trim();
            if (!versions.TryGetValue(id, out var values))
                versions[id] = values = [];
            values.Add(mod.Version!.Trim());
        }
        return new InstalledDependencyCatalog(versions);
    }

    private static void AddIdentifiers(
        InstalledDependencyCatalog installedDependencies,
        ResourceProjectDependency dependency,
        ResourceProjectVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.VersionNumber))
            return;
        foreach (var identifier in EnumerateIdentifiers(dependency.Project))
            installedDependencies.Add(identifier, version.VersionNumber);
    }

    private static IEnumerable<string> EnumerateIdentifiers(ResourceProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.Slug))
            yield return project.Slug;
        if (!string.IsNullOrWhiteSpace(project.ProjectId))
            yield return project.ProjectId;
    }

    internal sealed class InstalledDependencyCatalog(Dictionary<string, List<string>> versionsByModId)
    {
        public IEnumerable<string> GetVersions(string modId)
        {
            return !string.IsNullOrWhiteSpace(modId)
                && versionsByModId.TryGetValue(modId.Trim(), out var versions)
                    ? versions
                    : [];
        }

        public void Add(string modId, string version)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(version))
                return;
            var key = modId.Trim();
            if (!versionsByModId.TryGetValue(key, out var versions))
                versionsByModId[key] = versions = [];
            versions.Add(version.Trim());
        }
    }

    private static class DependencyVersionComparer
    {
        private static readonly string[] ContextTokens = ["mc", "minecraft", "fabric", "forge", "neoforge", "quilt"];

        public static bool IsGreaterThanOrEqual(string installedVersion, string minimumVersion)
        {
            return TryParse(installedVersion, out var installed)
                && TryParse(minimumVersion, out var minimum)
                && installed.CompareTo(minimum) >= 0;
        }

        private static bool TryParse(string value, out ParsedVersion version)
        {
            // Mod 版本经常混入 Minecraft/Loader 上下文；比较时提取最后一个版本样数字段并保留预发布权重。
            version = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var tokens = value.Trim()
                .Split(['+', '-', '_', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => !IsContextToken(token)).ToList();
            var numeric = tokens.LastOrDefault(token => char.IsDigit(token[0]) && token.Contains('.', StringComparison.Ordinal))
                ?? tokens.LastOrDefault(token => char.IsDigit(token[0]));
            if (numeric is null)
                return false;
            var numbers = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => new string(part.TakeWhile(char.IsDigit).ToArray()))
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            if (numbers.Length == 0)
                return false;
            var qualifier = tokens.FirstOrDefault(token =>
                token.StartsWith("alpha", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("beta", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("rc", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("pre", StringComparison.OrdinalIgnoreCase));
            version = new ParsedVersion(numbers, QualifierWeight(qualifier));
            return true;
        }

        private static bool IsContextToken(string token)
        {
            return ContextTokens.Any(context => string.Equals(token, context, StringComparison.OrdinalIgnoreCase))
                || token.StartsWith("mc", StringComparison.OrdinalIgnoreCase) && token.Skip(2).Any(char.IsDigit)
                || token.Contains("minecraft", StringComparison.OrdinalIgnoreCase);
        }

        private static int QualifierWeight(string? qualifier)
        {
            if (string.IsNullOrWhiteSpace(qualifier)) return 3;
            if (qualifier.StartsWith("alpha", StringComparison.OrdinalIgnoreCase)) return 0;
            if (qualifier.StartsWith("beta", StringComparison.OrdinalIgnoreCase)) return 1;
            return qualifier.StartsWith("pre", StringComparison.OrdinalIgnoreCase)
                || qualifier.StartsWith("rc", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        }

        private readonly record struct ParsedVersion(IReadOnlyList<int> Numbers, int QualifierWeight) : IComparable<ParsedVersion>
        {
            public int CompareTo(ParsedVersion other)
            {
                var count = Math.Max(Numbers.Count, other.Numbers.Count);
                for (var index = 0; index < count; index++)
                {
                    var comparison = (index < Numbers.Count ? Numbers[index] : 0)
                        .CompareTo(index < other.Numbers.Count ? other.Numbers[index] : 0);
                    if (comparison != 0)
                        return comparison;
                }
                return QualifierWeight.CompareTo(other.QualifierWeight);
            }
        }
    }
}
