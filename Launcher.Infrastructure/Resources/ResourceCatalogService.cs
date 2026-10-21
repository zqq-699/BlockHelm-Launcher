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
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Resources.ResourceLocalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Resources;

public sealed class ResourceCatalogService :
    IResourceCatalogService,
    IResourceCatalogProgressReporter,
    IResourceCatalogDestinationWriter,
    IResourceThumbnailService
{
    private readonly IReadOnlyDictionary<ResourceProjectSource, IResourceProviderClient> providers;
    private readonly ResourceProjectStorage storage;
    private readonly ResourceThumbnailCacheService thumbnailCache;
    private readonly McresBhlClient mcresBhlClient;
    private readonly IModNameLocalizationService modNameLocalizationService;
    private readonly ILogger<ResourceCatalogService> logger;

    public ResourceCatalogService(
        HttpClient? httpClient = null,
        LauncherPathProvider? pathProvider = null,
        ISettingsService? settingsService = null,
        ILogger<ResourceCatalogService>? logger = null,
        ICurseForgeApiKeyResolver? curseForgeApiKeyResolver = null,
        ILocalSaveService? localSaveService = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        IImportConcurrencyLimiter? limiter = null,
        IMcresBhlApiKeyResolver? mcresBhlApiKeyResolver = null,
        IModNameLocalizationService? modNameLocalizationService = null)
    {
        var resolvedPathProvider = pathProvider ?? new LauncherPathProvider();
        var resolvedHttpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.logger = logger ?? NullLogger<ResourceCatalogService>.Instance;
        this.modNameLocalizationService = modNameLocalizationService
            ?? new EmbeddedModNameLocalizationService();
        var keyResolver = curseForgeApiKeyResolver
            ?? new CurseForgeApiKeyResolver(resolvedPathProvider, settingsService);
        var resolvedLocalSaveService = localSaveService ?? new LocalSaveService(resolvedPathProvider);
        var resolvedMcresBhlApiKeyResolver = mcresBhlApiKeyResolver
            ?? new McresBhlApiKeyResolver(resolvedPathProvider, settingsService);

        var clients = new IResourceProviderClient[]
        {
            new ModrinthResourceClient(resolvedHttpClient),
            new CurseForgeResourceClient(resolvedHttpClient, keyResolver, this.logger)
        };
        providers = clients.ToDictionary(client => client.Source);
        mcresBhlClient = new McresBhlClient(
            resolvedHttpClient,
            resolvedMcresBhlApiKeyResolver,
            this.logger);
        storage = new ResourceProjectStorage(
            resolvedHttpClient,
            resolvedLocalSaveService,
            this.logger,
            settingsService,
            downloadSpeedLimitState,
            limiter);
        thumbnailCache = new ResourceThumbnailCacheService(
            resolvedPathProvider,
            resolvedHttpClient,
            limiter,
            downloadSpeedLimitState,
            this.logger);
    }

    public string? TryGetCachedThumbnailSource(ResourceProject project) =>
        thumbnailCache.TryGetCachedThumbnailSource(project);

    public Task<string?> GetOrCreateThumbnailSourceAsync(
        ResourceProject project,
        CancellationToken cancellationToken = default) =>
        thumbnailCache.GetOrCreateThumbnailSourceAsync(project, cancellationToken);

    public async Task<ResourceProject?> GetProjectAsync(
        ResourceProjectReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!providers.TryGetValue(reference.Source, out var provider)
            || !provider.Supports(reference.Kind))
            return null;

        var project = await provider.GetProjectAsync(reference, cancellationToken).ConfigureAwait(false);
        if (project is not null)
            ApplyLocalization(project);

        return project;
    }

    public Task<ResourceProjectRelatedWebsite?> GetRelatedWebsiteAsync(
        ResourceProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();

        if (project.Kind is ResourceProjectKind.Mod)
        {
            var mcmodId = modNameLocalizationService.FindMcmodIdByProjectSlug(project.Source, project.Slug);
            return Task.FromResult<ResourceProjectRelatedWebsite?>(mcmodId is > 0
                ? new ResourceProjectRelatedWebsite(
                    project.Source,
                    project.ProjectId,
                    "MCMod",
                    $"https://www.mcmod.cn/class/{mcmodId}.html")
                : null);
        }

        return GetRelatedWebsiteAsync(
            new ResourceProjectReference(project.Kind, project.Source, project.ProjectId),
            cancellationToken);
    }

    public Task<ResourceProjectRelatedWebsite?> GetRelatedWebsiteAsync(
        ResourceProjectReference reference,
        CancellationToken cancellationToken = default) =>
        mcresBhlClient.GetRelatedWebsiteAsync(reference, cancellationToken);

    public Task<ResourceCatalogSearchResult> SearchModsAsync(
        ResourceCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        return SearchProjectsAsync(new ResourceCatalogSearchRequest
        {
            Kind = ResourceProjectKind.Mod,
            Query = request.Query,
            MinecraftVersion = request.MinecraftVersion,
            MinecraftVersions = request.MinecraftVersions,
            Loader = request.Loader,
            Source = request.Source,
            Category = request.Category,
            Offset = request.Offset,
            PageSize = request.PageSize
        }, cancellationToken);
    }

    public async Task<ResourceCatalogSearchResult> SearchProjectsAsync(
        ResourceCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Searching resource projects. Kind={Kind} Query={Query} Source={Source} Offset={Offset} PageSize={PageSize}",
            request.Kind,
            request.Query,
            request.Source,
            request.Offset,
            request.PageSize);

        var totalStopwatch = Stopwatch.StartNew();
        var selectedProviders = request.Source is { } source
            ? providers.TryGetValue(source, out var selectedProvider) ? [selectedProvider] : []
            : providers.Values.ToArray();
        var supportedProviders = selectedProviders
            .Where(value => value.Supports(request.Kind))
            .ToArray();
        var projects = new List<ResourceProject>();
        var hasMore = false;
        var curseForgeUnavailable = false;
        var curseForgeApiKeyMissing = false;

        var isChineseModSearch = request.Kind is ResourceProjectKind.Mod
            && request.Query?.Any(character => character is >= '\u3400' and <= '\u9FFF'
                or >= '\uF900' and <= '\uFAFF') == true;
        var providerResults = await Task.WhenAll(supportedProviders.Select(provider =>
        {
            var searchTerm = isChineseModSearch
                ? modNameLocalizationService.FindSearchTerm(provider.Source, request.Query ?? string.Empty)
                : null;
            if (!string.IsNullOrWhiteSpace(searchTerm))
                logger.LogDebug("Resolved Chinese mod search term. Source={Source}", provider.Source);
            return SearchProviderAsync(provider,
                searchTerm is null ? request : CopyWithQuery(request, searchTerm), cancellationToken);
        })).ConfigureAwait(false);
        foreach (var (providerSource, result) in providerResults)
        {
            projects.AddRange(result.Projects);
            hasMore |= result.HasMore;
            if (providerSource is ResourceProjectSource.CurseForge)
            {
                curseForgeUnavailable |= result.IsUnavailable;
                curseForgeApiKeyMissing |= result.IsApiKeyMissing;
            }
        }

        foreach (var project in projects)
            ApplyLocalization(project);

        var searchResult = new ResourceCatalogSearchResult
        {
            Projects = projects
                .OrderByDescending(project => isChineseModSearch
                    && project.LocalizedTitle?.Contains(request.Query?.Trim() ?? string.Empty, StringComparison.Ordinal) == true)
                .ThenByDescending(project => project.Downloads)
                .ThenBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            IsCurseForgeUnavailable = curseForgeUnavailable,
            IsCurseForgeApiKeyMissing = curseForgeApiKeyMissing,
            HasMore = hasMore
        };
        logger.LogInformation(
            "Resource project search completed. Kind={Kind} ProviderCount={ProviderCount} ResultCount={ResultCount} ElapsedMilliseconds={ElapsedMilliseconds}",
            request.Kind,
            supportedProviders.Length,
            searchResult.Projects.Count,
            totalStopwatch.ElapsedMilliseconds);
        return searchResult;
    }

    private void ApplyLocalization(ResourceProject project) =>
        project.LocalizedTitle = project.Kind is ResourceProjectKind.Mod
            ? modNameLocalizationService.FindByProjectSlug(project.Source, project.Slug, project.Title)
            : null;

    private static ResourceCatalogSearchRequest CopyWithQuery(ResourceCatalogSearchRequest request, string query) => new()
    {
        Kind = request.Kind,
        Query = query,
        MinecraftVersion = request.MinecraftVersion,
        MinecraftVersions = request.MinecraftVersions,
        Loader = request.Loader,
        Source = request.Source,
        Category = request.Category,
        Offset = request.Offset,
        PageSize = request.PageSize
    };

    private async Task<(ResourceProjectSource Source, ResourceProviderSearchResult Result)> SearchProviderAsync(
        IResourceProviderClient provider,
        ResourceCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await provider.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            logger.LogDebug(
                "Resource provider search completed. Kind={Kind} Source={Source} ResultCount={ResultCount} ElapsedMilliseconds={ElapsedMilliseconds}",
                request.Kind,
                provider.Source,
                result.Projects.Count,
                stopwatch.ElapsedMilliseconds);
            return (provider.Source, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Resource provider search failed. Kind={Kind} Source={Source} ElapsedMilliseconds={ElapsedMilliseconds}",
                request.Kind,
                provider.Source,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(
        ResourceProjectVersionsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Kind is ResourceProjectKind.Mod
            && !request.IncludeAllVersions
            && request.Loader is LoaderKind.Vanilla)
        {
            return new ResourceProjectVersionsResult();
        }

        if (!providers.TryGetValue(request.Source, out var provider))
            return new ResourceProjectVersionsResult();

        var result = await provider.GetVersionsAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var dependency in result.Versions.SelectMany(version => version.RequiredDependencies))
            ApplyLocalization(dependency.Project);
        return result;
    }

    public async Task<ResourceProjectDependenciesResult> GetProjectDependenciesAsync(
        ResourceProjectDependenciesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Kind is not ResourceProjectKind.Mod)
            return new ResourceProjectDependenciesResult();
        if (!providers.TryGetValue(request.Source, out var provider))
            return new ResourceProjectDependenciesResult();

        var result = await provider.GetDependenciesAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var project in result.RequiredProjects)
            ApplyLocalization(project);
        return result;
    }

    public async Task<string> InstallProjectVersionAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        var target = await storage.InstallAsync(version, instance, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Resource project version installed. VersionId={VersionId}", version.VersionId);
        logger.LogDebug("Resource project installation target resolved. VersionId={VersionId} Target={Target}", version.VersionId, target);
        return target;
    }

    public async Task<string> DownloadProjectVersionAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var target = await storage.DownloadAsync(version, targetDirectory, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Resource project version downloaded. VersionId={VersionId}", version.VersionId);
        logger.LogDebug("Resource project download target resolved. VersionId={VersionId} Target={Target}", version.VersionId, target);
        return target;
    }

    Task<string> IResourceCatalogProgressReporter.InstallProjectVersionWithProgressAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken) =>
        InstallProjectVersionWithProgressAsync(version, instance, progress, cancellationToken);

    Task<string> IResourceCatalogProgressReporter.DownloadProjectVersionWithProgressAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken) =>
        DownloadProjectVersionWithProgressAsync(version, targetDirectory, progress, cancellationToken);

    private async Task<string> InstallProjectVersionWithProgressAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var target = await storage.InstallAsync(version, instance, progress, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Resource project version installed. VersionId={VersionId}", version.VersionId);
        logger.LogDebug("Resource project installation target resolved. VersionId={VersionId} Target={Target}", version.VersionId, target);
        return target;
    }

    private async Task<string> DownloadProjectVersionWithProgressAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var target = await storage.DownloadAsync(version, targetDirectory, progress, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Resource project version downloaded. VersionId={VersionId}", version.VersionId);
        logger.LogDebug("Resource project download target resolved. VersionId={VersionId} Target={Target}", version.VersionId, target);
        return target;
    }

    Task<ResourceProjectDestinationState> IResourceCatalogDestinationWriter.CaptureDownloadDestinationAsync(
        ResourceProjectVersion version,
        string destinationPath,
        CancellationToken cancellationToken) =>
        storage.CaptureDownloadDestinationAsync(version, destinationPath, cancellationToken);

    Task<ResourceProjectDestinationState> IResourceCatalogDestinationWriter.CaptureInstallDestinationAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        string destinationPath,
        CancellationToken cancellationToken) =>
        storage.CaptureInstallDestinationAsync(version, instance, destinationPath, cancellationToken);

    async Task<string> IResourceCatalogDestinationWriter.DownloadProjectVersionToDestinationAsync(
        ResourceProjectVersion version,
        string destinationPath,
        ResourceProjectDestinationState expectedState,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var target = await storage.DownloadToDestinationAsync(
                version,
                destinationPath,
                expectedState,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("Resource project version downloaded. VersionId={VersionId}", version.VersionId);
        logger.LogDebug(
            "Resource project explicit download target committed. VersionId={VersionId} Target={Target}",
            version.VersionId,
            target);
        return target;
    }

    async Task<string> IResourceCatalogDestinationWriter.InstallProjectVersionToDestinationAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        string destinationPath,
        ResourceProjectDestinationState expectedState,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var target = await storage.InstallToDestinationAsync(
                version,
                instance,
                destinationPath,
                expectedState,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("Resource project version installed. VersionId={VersionId}", version.VersionId);
        logger.LogDebug(
            "Resource project explicit install target committed. VersionId={VersionId} Target={Target}",
            version.VersionId,
            target);
        return target;
    }

    public Task<bool> ProjectVersionDownloadExistsAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        CancellationToken cancellationToken = default) =>
        storage.DownloadExistsAsync(version, targetDirectory, cancellationToken);

    public Task<bool> ProjectVersionInstallExistsAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken = default) =>
        storage.InstallExistsAsync(version, instance, cancellationToken);

    Task<string> IResourceCatalogDestinationWriter.EnsureInstanceContentDirectoryAsync(
        ResourceProjectKind kind,
        GameInstance instance,
        CancellationToken cancellationToken) =>
        storage.EnsureInstanceContentDirectoryAsync(kind, instance, cancellationToken);
}
