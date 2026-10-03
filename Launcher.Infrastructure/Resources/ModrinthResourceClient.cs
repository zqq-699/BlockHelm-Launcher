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

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Resources;

/// <summary>
/// 访问 Modrinth 搜索、版本和依赖接口，并映射到启动器统一资源模型。
/// </summary>
internal sealed class ModrinthResourceClient(HttpClient httpClient) : IResourceProviderClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2";
    private static readonly HashSet<string> KnownLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt"
    };

    public ResourceProjectSource Source => ResourceProjectSource.Modrinth;

    public bool Supports(ResourceProjectKind kind) => kind is not ResourceProjectKind.World;

    public async Task<ResourceProject?> GetProjectAsync(
        ResourceProjectReference reference,
        CancellationToken cancellationToken)
    {
        if (!Supports(reference.Kind) || string.IsNullOrWhiteSpace(reference.ProjectId))
            return null;

        var project = await httpClient.GetFromJsonAsync<ModrinthProject>(
            $"{BaseUrl}/project/{Uri.EscapeDataString(reference.ProjectId)}",
            cancellationToken).ConfigureAwait(false);
        return project is null ? null : MapProject(project, reference.Kind);
    }

    public async Task<ResourceProviderSearchResult> SearchAsync(
        ResourceCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        // facets 只包含资源类型支持的筛选项，World 等不支持类型在入口直接拒绝。
        if (!Supports(request.Kind))
            return new ResourceProviderSearchResult([], false);

        var pageSize = Math.Clamp(request.PageSize, 1, 50);
        var offset = Math.Max(0, request.Offset);
        var facets = new List<List<string>>
        {
            new() { $"project_type:{MapProjectType(request.Kind)}" }
        };
        var minecraftVersions = ResolveMinecraftVersions(request);
        if (minecraftVersions.Count > 0)
            facets.Add(minecraftVersions.Select(version => $"versions:{version}").ToList());
        if (HasLoaderFacet(request.Kind) && request.Loader is not LoaderKind.Vanilla)
            facets.Add([$"categories:{request.Loader.ToString().ToLowerInvariant()}"]);
        if (request.Category is { } category)
            facets.Add([$"categories:{MapCategory(category)}"]);

        var url = $"{BaseUrl}/search?limit={pageSize}&offset={offset}&index=downloads&query={Uri.EscapeDataString(request.Query ?? string.Empty)}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        var response = await httpClient.GetFromJsonAsync<ModrinthSearchResponse>(url, cancellationToken).ConfigureAwait(false);
        var projects = response?.Hits.Select(hit => new ResourceProject
        {
            Source = Source,
            Kind = request.Kind,
            ProjectId = hit.ProjectId,
            Slug = hit.Slug,
            Title = hit.Title,
            Description = hit.Description,
            IconUrl = hit.IconUrl,
            Downloads = hit.Downloads,
            Categories = ResourceProjectCategoryMapping.MapModrinth(request.Kind, hit.Categories),
            SupportedMinecraftVersions = NormalizeDistinct(hit.Versions),
            SupportedLoaders = HasLoaderFacet(request.Kind)
                ? NormalizeDistinct(hit.Categories.Where(KnownLoaders.Contains))
                : [],
            ProjectUrl = string.IsNullOrWhiteSpace(hit.Slug)
                ? string.Empty
                : $"https://modrinth.com/{MapProjectType(request.Kind)}/{hit.Slug}"
        }).ToList() ?? [];

        return new ResourceProviderSearchResult(
            projects,
            response?.TotalHits is { } total ? offset + projects.Count < total : projects.Count >= pageSize);
    }

    public async Task<ResourceProjectVersionsResult> GetVersionsAsync(
        ResourceProjectVersionsRequest request,
        CancellationToken cancellationToken)
    {
        // 项目版本按服务端顺序保留，并排除当前已展示 Id 支持稳定分页合并。
        var projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? request.Slug : request.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId)
            || !request.IncludeAllVersions && string.IsNullOrWhiteSpace(request.MinecraftVersion))
        {
            return new ResourceProjectVersionsResult();
        }

        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        if (!request.IncludeAllVersions)
        {
            var query = new List<string>
            {
                $"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { request.MinecraftVersion }))}"
            };
            if (HasLoaderFacet(request.Kind))
            {
                query.Insert(0, $"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { request.Loader.ToString().ToLowerInvariant() }))}");
            }
            url += $"?{string.Join("&", query)}";
        }

        var versions = await httpClient.GetFromJsonAsync<List<ModrinthVersion>>(url, cancellationToken).ConfigureAwait(false) ?? [];
        var currentIds = CreateCurrentIds(request.ProjectId, request.Slug, projectId);
        var dependencies = request.Kind is ResourceProjectKind.Mod && request.IncludeDependencies
            ? await LoadDependencyProjectsAsync(CollectRequiredProjectIds(versions, currentIds), cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, ResourceProject>(StringComparer.OrdinalIgnoreCase);

        return new ResourceProjectVersionsResult
        {
            Versions = versions
                .Where(version => !string.IsNullOrWhiteSpace(version.Id) && version.Files.Count > 0)
                .Select(version => MapVersion(version, request.Kind, dependencies, currentIds))
                .ToList()
        };
    }

    public async Task<ResourceProjectDependenciesResult> GetDependenciesAsync(
        ResourceProjectDependenciesRequest request,
        CancellationToken cancellationToken)
    {
        // Required 依赖先收集项目 Id，再批量获取项目元数据以避免 N+1 请求。
        var projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? request.Slug : request.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId) || request.Kind is not ResourceProjectKind.Mod)
            return new ResourceProjectDependenciesResult();

        var versions = await httpClient.GetFromJsonAsync<List<ModrinthVersion>>(
            $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version",
            cancellationToken).ConfigureAwait(false) ?? [];
        var currentIds = CreateCurrentIds(request.ProjectId, request.Slug, projectId);
        var requiredIds = CollectRequiredProjectIds(versions, currentIds);
        var projects = await LoadDependencyProjectsAsync(requiredIds, cancellationToken).ConfigureAwait(false);
        return new ResourceProjectDependenciesResult
        {
            RequiredProjects = requiredIds
                .Select(id => projects.TryGetValue(id, out var project) ? project : null)
                .OfType<ResourceProject>()
                .ToList()
        };
    }

    private async Task<IReadOnlyDictionary<string, ResourceProject>> LoadDependencyProjectsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return new Dictionary<string, ResourceProject>(StringComparer.OrdinalIgnoreCase);
        var parameter = Uri.EscapeDataString(JsonSerializer.Serialize(ids));
        var projects = await httpClient.GetFromJsonAsync<List<ModrinthProject>>(
            $"{BaseUrl}/projects?ids={parameter}", cancellationToken).ConfigureAwait(false) ?? [];
        return projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Id)
                && string.Equals(project.ProjectType, "mod", StringComparison.OrdinalIgnoreCase))
            .Select(project => MapProject(project, ResourceProjectKind.Mod))
            .GroupBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static ResourceProjectVersion MapVersion(
        ModrinthVersion version,
        ResourceProjectKind kind,
        IReadOnlyDictionary<string, ResourceProject> projects,
        ISet<string> excludedIds)
    {
        // primary 文件优先；缺失时选择首个文件，所有下载候选和哈希一并保留给安装层校验。
        var file = version.Files.FirstOrDefault(candidate => candidate.IsPrimary) ?? version.Files[0];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = version.Dependencies
            .Where(value => string.Equals(value.DependencyType, "required", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value.ProjectId))
            .Where(value => !excludedIds.Contains(value.ProjectId!) && seen.Add(value.ProjectId!))
            .Where(value => projects.ContainsKey(value.ProjectId!))
            .Select(value => new ResourceProjectDependency
            {
                Project = projects[value.ProjectId!],
                VersionId = value.VersionId ?? string.Empty
            })
            .ToList();
        return new ResourceProjectVersion
        {
            VersionId = version.Id,
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(version.Name) ? version.VersionNumber : version.Name,
            VersionNumber = version.VersionNumber,
            VersionType = version.VersionType,
            FileName = file.FileName,
            PrimaryDownloadUrl = file.Url,
            ExpectedFileSize = file.Size,
            FileHashes = file.Hashes
                .Where(pair => TryMapHashAlgorithm(pair.Key, out _))
                .Select(pair => new ResourceFileHash(MapHashAlgorithm(pair.Key), pair.Value))
                .ToList(),
            Downloads = version.Downloads,
            PublishedAt = version.DatePublished,
            GameVersions = NormalizeDistinct(version.GameVersions),
            Loaders = NormalizeDistinct(version.Loaders),
            RequiredDependencies = dependencies
        };
    }

    private static ResourceProject MapProject(ModrinthProject project, ResourceProjectKind kind) => new()
    {
        Source = ResourceProjectSource.Modrinth,
        Kind = kind,
        ProjectId = project.Id,
        Slug = project.Slug,
        Title = project.Title,
        Description = project.Description,
        IconUrl = project.IconUrl,
        Downloads = project.Downloads,
        Categories = ResourceProjectCategoryMapping.MapModrinth(kind, project.Categories),
        SupportedMinecraftVersions = NormalizeDistinct(project.GameVersions),
        SupportedLoaders = HasLoaderFacet(kind)
            ? NormalizeDistinct(project.Loaders.Where(KnownLoaders.Contains))
            : [],
        ProjectUrl = string.IsNullOrWhiteSpace(project.Slug)
            ? string.Empty
            : $"https://modrinth.com/{MapProjectType(kind)}/{project.Slug}"
    };

    private static List<string> CollectRequiredProjectIds(IEnumerable<ModrinthVersion> versions, ISet<string> excludedIds)
    {
        return versions.SelectMany(version => version.Dependencies)
            .Where(value => string.Equals(value.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.ProjectId)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !excludedIds.Contains(value!))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<string> CreateCurrentIds(params string?[] ids) =>
        new(ids.Where(id => !string.IsNullOrWhiteSpace(id))!, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ResolveMinecraftVersions(ResourceCatalogSearchRequest request)
    {
        if (request.MinecraftVersions.Count > 0)
            return request.MinecraftVersions.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return string.IsNullOrWhiteSpace(request.MinecraftVersion) ? [] : [request.MinecraftVersion];
    }

    private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim().ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool TryMapHashAlgorithm(string value, out ResourceFileHashAlgorithm algorithm)
    {
        if (string.Equals(value, "sha512", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = ResourceFileHashAlgorithm.Sha512;
            return true;
        }
        if (string.Equals(value, "sha1", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = ResourceFileHashAlgorithm.Sha1;
            return true;
        }
        if (string.Equals(value, "md5", StringComparison.OrdinalIgnoreCase))
        {
            algorithm = ResourceFileHashAlgorithm.Md5;
            return true;
        }
        algorithm = default;
        return false;
    }

    private static ResourceFileHashAlgorithm MapHashAlgorithm(string value)
    {
        return TryMapHashAlgorithm(value, out var algorithm)
            ? algorithm
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static bool HasLoaderFacet(ResourceProjectKind kind) =>
        kind is ResourceProjectKind.Mod or ResourceProjectKind.Modpack;

    private static string MapProjectType(ResourceProjectKind kind) => kind switch
    {
        ResourceProjectKind.ResourcePack => "resourcepack",
        ResourceProjectKind.ShaderPack => "shader",
        ResourceProjectKind.World => "world",
        ResourceProjectKind.Modpack => "modpack",
        _ => "mod"
    };

    private static string MapCategory(ResourceProjectCategory category) =>
        ResourceProjectCategoryMapping.GetModrinthId(category);

    private sealed class ModrinthSearchResponse
    {
        [JsonPropertyName("hits")] public List<ModrinthHit> Hits { get; init; } = [];
        [JsonPropertyName("total_hits")] public int? TotalHits { get; init; }
    }

    private sealed class ModrinthHit
    {
        [JsonPropertyName("project_id")] public string ProjectId { get; init; } = string.Empty;
        [JsonPropertyName("slug")] public string Slug { get; init; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
        [JsonPropertyName("downloads")] public long Downloads { get; init; }
        [JsonPropertyName("versions")] public List<string> Versions { get; init; } = [];
        [JsonPropertyName("categories")] public List<string> Categories { get; init; } = [];
    }

    private sealed class ModrinthVersion
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("version_number")] public string VersionNumber { get; init; } = string.Empty;
        [JsonPropertyName("version_type")] public string VersionType { get; init; } = string.Empty;
        [JsonPropertyName("date_published")] public DateTimeOffset? DatePublished { get; init; }
        [JsonPropertyName("downloads")] public long Downloads { get; init; }
        [JsonPropertyName("game_versions")] public List<string> GameVersions { get; init; } = [];
        [JsonPropertyName("loaders")] public List<string> Loaders { get; init; } = [];
        [JsonPropertyName("files")] public List<ModrinthFile> Files { get; init; } = [];
        [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; init; } = [];
    }

    private sealed class ModrinthFile
    {
        [JsonPropertyName("filename")] public string FileName { get; init; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;
        [JsonPropertyName("primary")] public bool IsPrimary { get; init; }
        [JsonPropertyName("size")] public long? Size { get; init; }
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ModrinthDependency
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
        [JsonPropertyName("version_id")] public string? VersionId { get; init; }
        [JsonPropertyName("dependency_type")] public string DependencyType { get; init; } = string.Empty;
    }

    private sealed class ModrinthProject
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("slug")] public string Slug { get; init; } = string.Empty;
        [JsonPropertyName("project_type")] public string ProjectType { get; init; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
        [JsonPropertyName("downloads")] public long Downloads { get; init; }
        [JsonPropertyName("game_versions")] public List<string> GameVersions { get; init; } = [];
        [JsonPropertyName("loaders")] public List<string> Loaders { get; init; } = [];
        [JsonPropertyName("categories")] public List<string> Categories { get; init; } = [];
    }
}
