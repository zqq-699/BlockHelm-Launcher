/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Resources.ResourceLocalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 通过文件身份持久缓存哈希与分类，并仅为缺失或过期条目执行远端精确识别。
/// </summary>
public sealed class LocalResourceCategoryEnrichmentService : ILocalResourceCategoryEnrichmentService
{
    internal const int ProviderBatchSize = 50;
    internal static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(90);

    private readonly RemoteModIconProviderClient providerClient;
    private readonly LocalFileFingerprintService fingerprintService;
    private readonly IModNameLocalizationService modNameLocalizationService;
    private readonly IResourceThumbnailService? thumbnailService;
    private readonly ILogger<LocalResourceCategoryEnrichmentService> logger;
    private readonly LocalResourceCategoryCacheStore cacheStore;
    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private LocalResourceCategoryCacheIndex? cacheIndex;

    public LocalResourceCategoryEnrichmentService(
        LauncherPathProvider? pathProvider = null,
        HttpClient? httpClient = null,
        ICurseForgeApiKeyResolver? curseForgeApiKeyResolver = null,
        ILogger<LocalResourceCategoryEnrichmentService>? logger = null,
        IResourceThumbnailService? thumbnailService = null,
        LocalFileFingerprintService? fingerprintService = null,
        IModNameLocalizationService? modNameLocalizationService = null)
    {
        var resolvedPathProvider = pathProvider ?? new LauncherPathProvider();
        var resolvedHttpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.logger = logger ?? NullLogger<LocalResourceCategoryEnrichmentService>.Instance;
        this.thumbnailService = thumbnailService;
        this.fingerprintService = fingerprintService ?? new LocalFileFingerprintService();
        this.modNameLocalizationService = modNameLocalizationService ?? new EmbeddedModNameLocalizationService();
        var apiKeyResolver = curseForgeApiKeyResolver ?? new CurseForgeApiKeyResolver(resolvedPathProvider);
        providerClient = new RemoteModIconProviderClient(resolvedHttpClient, apiKeyResolver, this.logger);
        cacheStore = new LocalResourceCategoryCacheStore(
            Path.Combine(resolvedPathProvider.DefaultDataDirectory, "cache", "resources", "local-categories"),
            this.logger);
    }

    public async Task<IReadOnlyDictionary<string, LocalResourceEnrichmentResult>> ResolveCachedMetadataAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default,
        IProgress<LocalContentIconResolution>? iconProgress = null)
    {
        var categories = await ResolveCachedCategoriesAsync(resources, cancellationToken).ConfigureAwait(false);
        var icons = await ResolveProjectIconSourcesAsync(
                resources,
                downloadMissing: false,
                resolution => iconProgress?.Report(resolution),
                cancellationToken)
            .ConfigureAwait(false);
        var references = await ResolveProjectReferencesAsync(resources, cancellationToken).ConfigureAwait(false);
        return CombineMetadata(categories, icons, references);
    }

    public async Task<IReadOnlyDictionary<string, LocalResourceEnrichmentResult>> ResolveMetadataAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default,
        IProgress<LocalContentIconResolution>? iconProgress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        long timeToFirstIconMs = -1;
        var icons = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reportedIcons = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var iconTasks = new ConcurrentBag<Task>();
        var kindsByPath = resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.FullPath))
            .GroupBy(resource => resource.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Kind, StringComparer.OrdinalIgnoreCase);

        void ReportIcon(LocalContentIconResolution resolution)
        {
            icons[resolution.FullPath] = resolution.IconSource;
            while (true)
            {
                if (reportedIcons.TryGetValue(resolution.FullPath, out var current))
                {
                    if (string.Equals(current, resolution.IconSource, StringComparison.Ordinal))
                        return;
                    if (!reportedIcons.TryUpdate(resolution.FullPath, resolution.IconSource, current))
                        continue;
                }
                else if (!reportedIcons.TryAdd(resolution.FullPath, resolution.IconSource))
                {
                    continue;
                }

                Interlocked.CompareExchange(ref timeToFirstIconMs, stopwatch.ElapsedMilliseconds, -1);
                iconProgress?.Report(resolution);
                return;
            }
        }

        var cachedIconTask = ResolveProjectIconSourcesAsync(
                resources,
                downloadMissing: true,
                ReportIcon,
                cancellationToken);
        var categories = await ResolveCategoriesCoreAsync(
                resources,
                metadata =>
                {
                    var task = ResolveProjectIconSourcesAsync(
                            metadata,
                            kindsByPath,
                            downloadMissing: true,
                            ReportIcon,
                            cancellationToken);
                    iconTasks.Add(task);
                },
                cancellationToken)
            .ConfigureAwait(false);

        var cachedIcons = await cachedIconTask.ConfigureAwait(false);
        foreach (var (fullPath, iconSource) in cachedIcons)
            icons[fullPath] = iconSource;
        await Task.WhenAll(iconTasks).ConfigureAwait(false);

        var references = await ResolveProjectReferencesAsync(resources, cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Local resource metadata enrichment completed. CandidateCount={CandidateCount} IconCount={IconCount} TimeToFirstIconMs={TimeToFirstIconMs}",
            resources.Count,
            icons.Count,
            timeToFirstIconMs);
        return CombineMetadata(
            categories,
            new Dictionary<string, string>(icons, StringComparer.OrdinalIgnoreCase),
            references);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectCategory>>> ResolveCachedCategoriesAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var identities = CreateFileIdentities(resources);
        if (identities.Count == 0)
            return new Dictionary<string, IReadOnlyList<ResourceProjectCategory>>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, IReadOnlyList<ResourceProjectCategory>>(StringComparer.OrdinalIgnoreCase);
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var identity in identities)
            {
                if (!TryGetCurrentEntry(index, identity, out var entry))
                    continue;

                entry.LastUsedAt = now;
                if (entry.Categories.Count > 0)
                    result[identity.Resource.FullPath] = entry.Categories;
            }
        }
        finally
        {
            cacheLock.Release();
        }

        logger.LogDebug(
            "Local resource category disk cache checked. CandidateCount={CandidateCount} HitCount={HitCount}",
            identities.Count,
            result.Count);
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectCategory>>> ResolveCategoriesAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default) =>
        await ResolveCategoriesCoreAsync(resources, metadataResolved: null, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectCategory>>> ResolveCategoriesCoreAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        Action<IReadOnlyDictionary<string, RemoteIconCandidate>>? metadataResolved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var identities = CreateFileIdentities(resources);
        var result = new Dictionary<string, IReadOnlyList<ResourceProjectCategory>>(StringComparer.OrdinalIgnoreCase);
        if (identities.Count == 0)
            return result;

        var now = DateTimeOffset.UtcNow;
        var lookups = new List<ModIconLookupCandidate>();
        var identitiesByPath = identities.ToDictionary(
            identity => identity.Resource.FullPath,
            StringComparer.OrdinalIgnoreCase);
        var resourcesNeedingHashes = new List<LocalResourceFileIdentity>();
        var contentEntries = new Dictionary<string, LocalResourceCategoryCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var pendingLookups = new Dictionary<ResourceProjectKind, List<ModIconLookupCandidate>>();
        var resolvedMetadata = new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);

        async Task ResolveProviderBatchAsync(IReadOnlyList<ModIconLookupCandidate> batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchResolvedMetadata =
                new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase);
            var unresolved = batch
                .GroupBy(lookup => lookup.Sha1, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ModIconLookupCandidate>)group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var modrinth = await providerClient.ResolveModrinthAsync(batch, cancellationToken).ConfigureAwait(false);
            ApplyResolvedCategories(modrinth, unresolved, result, batchResolvedMetadata);

            if (unresolved.Count > 0)
            {
                var curseForgeCandidates = unresolved.Values
                    .SelectMany(values => values)
                    .DistinctBy(candidate => candidate.CurseForgeFingerprint)
                    .ToArray();
                var curseForge = await providerClient
                    .ResolveCurseForgeAsync(curseForgeCandidates, cancellationToken)
                    .ConfigureAwait(false);
                ApplyResolvedCategories(curseForge, unresolved, result, batchResolvedMetadata);
            }

            foreach (var (fullPath, metadata) in batchResolvedMetadata)
                resolvedMetadata[fullPath] = metadata;
            if (batchResolvedMetadata.Count > 0)
                metadataResolved?.Invoke(batchResolvedMetadata);
        }

        async Task EnqueueLookupAsync(ModIconLookupCandidate lookup)
        {
            if (!pendingLookups.TryGetValue(lookup.Kind, out var pending))
            {
                pending = [];
                pendingLookups[lookup.Kind] = pending;
            }

            pending.Add(lookup);
            if (pending.Count < ProviderBatchSize)
                return;

            var batch = pending.ToArray();
            pending.Clear();
            await ResolveProviderBatchAsync(batch).ConfigureAwait(false);
        }

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in index.Entries.Values
                         .Where(entry => !string.IsNullOrWhiteSpace(entry.Sha1))
                         .OrderByDescending(entry => entry.CheckedAt))
            {
                contentEntries.TryAdd(CreateContentCacheKey(entry.Kind, entry.Sha1), entry);
            }

            foreach (var identity in identities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetCurrentEntry(index, identity, out var entry))
                {
                    resourcesNeedingHashes.Add(identity);
                    continue;
                }

                entry.LastUsedAt = now;
                if (entry.Categories.Count > 0)
                    result[identity.Resource.FullPath] = entry.Categories;

                var needsIconMetadataUpgrade = SupportsRemoteProjectIcon(identity.Resource.Kind)
                    && thumbnailService is not null
                    && !entry.HasRemoteMetadata;
                var needsSlugUpgrade = identity.Resource.Kind is ResourceProjectKind.Mod
                    && entry.HasRemoteMetadata
                    && entry.ProjectSlug is null;
                if (!needsIconMetadataUpgrade
                    && !needsSlugUpgrade
                    && entry.CheckedAt != default
                    && now - entry.CheckedAt < RefreshAfter)
                    continue;

                if (string.IsNullOrWhiteSpace(entry.Sha1))
                {
                    resourcesNeedingHashes.Add(identity);
                    continue;
                }

                lookups.Add(CreateLookupFromCache(identity, entry));
            }
        }
        finally
        {
            cacheLock.Release();
        }

        foreach (var lookup in lookups.ToArray())
            await EnqueueLookupAsync(lookup).ConfigureAwait(false);

        var contentCacheCopies =
            new Dictionary<string, LocalResourceCategoryCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in resourcesNeedingHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lookup = await CreateLookupCandidateAsync(identity, cancellationToken).ConfigureAwait(false);
            if (lookup is null)
                continue;

            if (contentEntries.TryGetValue(
                    CreateContentCacheKey(identity.Resource.Kind, lookup.Sha1),
                    out var contentEntry))
            {
                var copiedEntry = CopyContentEntry(identity, contentEntry, now);
                contentCacheCopies[identity.Resource.FullPath] = copiedEntry;
                if (copiedEntry.Categories.Count > 0)
                    result[identity.Resource.FullPath] = copiedEntry.Categories;

                var cachedMetadata = TryCreateRemoteMetadata(copiedEntry);
                if (cachedMetadata is not null)
                {
                    metadataResolved?.Invoke(
                        new Dictionary<string, RemoteIconCandidate>(StringComparer.OrdinalIgnoreCase)
                        {
                            [identity.Resource.FullPath] = cachedMetadata
                        });
                }

                var needsIconMetadataUpgrade = SupportsRemoteProjectIcon(identity.Resource.Kind)
                    && thumbnailService is not null
                    && !copiedEntry.HasRemoteMetadata;
                var needsSlugUpgrade = identity.Resource.Kind is ResourceProjectKind.Mod
                    && copiedEntry.HasRemoteMetadata
                    && copiedEntry.ProjectSlug is null;
                if (!needsIconMetadataUpgrade
                    && !needsSlugUpgrade
                    && copiedEntry.CheckedAt != default
                    && now - copiedEntry.CheckedAt < RefreshAfter)
                {
                    continue;
                }
            }

            lookups.Add(lookup);
            await EnqueueLookupAsync(lookup).ConfigureAwait(false);
        }

        if (contentCacheCopies.Count > 0)
        {
            await PersistContentCacheCopiesAsync(
                    contentCacheCopies,
                    identitiesByPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (lookups.Count == 0)
            return result;

        await PersistLookupHashesAsync(lookups, identitiesByPath, now, cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Local resource category enrichment started. CandidateCount={CandidateCount} HashedCount={HashedCount}",
            lookups.Count,
            resourcesNeedingHashes.Count);

        foreach (var pending in pendingLookups.Values)
        {
            if (pending.Count > 0)
                await ResolveProviderBatchAsync(pending.ToArray()).ConfigureAwait(false);
        }

        if (resolvedMetadata.Count > 0)
        {
            await PersistResolvedMetadataAsync(
                    resolvedMetadata,
                    identitiesByPath,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogDebug(
            "Local resource category enrichment completed. CandidateCount={CandidateCount} TaggedCount={TaggedCount} CachedCount={CachedCount}",
            lookups.Count,
            result.Count,
            resolvedMetadata.Count);
        return result;
    }

    private static void ApplyResolvedCategories(
        IReadOnlyDictionary<string, RemoteIconCandidate> remote,
        IDictionary<string, IReadOnlyList<ModIconLookupCandidate>> unresolved,
        IDictionary<string, IReadOnlyList<ResourceProjectCategory>> result,
        IDictionary<string, RemoteIconCandidate> resolvedMetadata)
    {
        foreach (var (sha1, metadata) in remote)
        {
            if (!unresolved.Remove(sha1, out var lookups))
                continue;

            var categories = metadata.Categories.Distinct().ToArray();
            foreach (var lookup in lookups)
            {
                resolvedMetadata[lookup.FullPath] = metadata with { Categories = categories };
                if (categories.Length > 0)
                    result[lookup.FullPath] = categories;
            }
        }
    }

    private async Task PersistLookupHashesAsync(
        IReadOnlyList<ModIconLookupCandidate> lookups,
        IReadOnlyDictionary<string, LocalResourceFileIdentity> identitiesByPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var lookup in lookups)
            {
                if (!identitiesByPath.TryGetValue(lookup.FullPath, out var identity))
                    continue;

                var previous = TryGetCurrentEntry(index, identity, out var entry) ? entry : null;
                index.Entries[identity.CacheKey] = new LocalResourceCategoryCacheEntry
                {
                    Kind = identity.Resource.Kind,
                    FileLength = identity.FileLength,
                    LastWriteTimeUtcTicks = identity.LastWriteTimeUtcTicks,
                    Sha1 = lookup.Sha1,
                    CurseForgeFingerprint = lookup.CurseForgeFingerprint,
                    Categories = previous?.Categories ?? [],
                    Source = previous?.Source,
                    ProjectId = previous?.ProjectId ?? string.Empty,
                    ProjectSlug = previous?.ProjectSlug,
                    IconUrl = previous?.IconUrl ?? string.Empty,
                    HasRemoteMetadata = previous?.HasRemoteMetadata ?? false,
                    CheckedAt = previous?.CheckedAt ?? default,
                    LastUsedAt = now
                };
            }

            CleanupCache(index, now);
            await TrySaveCacheIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task PersistResolvedMetadataAsync(
        IReadOnlyDictionary<string, RemoteIconCandidate> resolvedMetadata,
        IReadOnlyDictionary<string, LocalResourceFileIdentity> identitiesByPath,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (fullPath, metadata) in resolvedMetadata)
            {
                if (!identitiesByPath.TryGetValue(fullPath, out var identity)
                    || !TryGetCurrentEntry(index, identity, out var entry))
                {
                    continue;
                }

                index.Entries[identity.CacheKey] = new LocalResourceCategoryCacheEntry
                {
                    Kind = entry.Kind,
                    FileLength = entry.FileLength,
                    LastWriteTimeUtcTicks = entry.LastWriteTimeUtcTicks,
                    Sha1 = entry.Sha1,
                    CurseForgeFingerprint = entry.CurseForgeFingerprint,
                    Categories = metadata.Categories.Distinct().ToArray(),
                    Source = ParseSource(metadata.Source),
                    ProjectId = metadata.ProjectId,
                    ProjectSlug = metadata.Slug ?? string.Empty,
                    IconUrl = metadata.IconUrl,
                    HasRemoteMetadata = true,
                    CheckedAt = checkedAt,
                    LastUsedAt = checkedAt
                };
            }

            CleanupCache(index, checkedAt);
            await TrySaveCacheIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task PersistContentCacheCopiesAsync(
        IReadOnlyDictionary<string, LocalResourceCategoryCacheEntry> copies,
        IReadOnlyDictionary<string, LocalResourceFileIdentity> identitiesByPath,
        CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (fullPath, entry) in copies)
            {
                if (identitiesByPath.TryGetValue(fullPath, out var identity))
                    index.Entries[identity.CacheKey] = entry;
            }

            CleanupCache(index, DateTimeOffset.UtcNow);
            await TrySaveCacheIndexAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private static LocalResourceCategoryCacheEntry CopyContentEntry(
        LocalResourceFileIdentity identity,
        LocalResourceCategoryCacheEntry source,
        DateTimeOffset now) =>
        new()
        {
            Kind = identity.Resource.Kind,
            FileLength = identity.FileLength,
            LastWriteTimeUtcTicks = identity.LastWriteTimeUtcTicks,
            Sha1 = source.Sha1,
            CurseForgeFingerprint = source.CurseForgeFingerprint,
            Categories = source.Categories,
            Source = source.Source,
            ProjectId = source.ProjectId,
            ProjectSlug = source.ProjectSlug,
            IconUrl = source.IconUrl,
            HasRemoteMetadata = source.HasRemoteMetadata,
            CheckedAt = source.CheckedAt,
            LastUsedAt = now
        };

    private static RemoteIconCandidate? TryCreateRemoteMetadata(LocalResourceCategoryCacheEntry entry)
    {
        if (!entry.HasRemoteMetadata
            || entry.Source is null
            || string.IsNullOrWhiteSpace(entry.ProjectId)
            || string.IsNullOrWhiteSpace(entry.IconUrl))
        {
            return null;
        }

        return new RemoteIconCandidate(
            entry.Source.Value.ToString().ToLowerInvariant(),
            entry.ProjectId,
            entry.ProjectSlug,
            entry.IconUrl,
            entry.Categories);
    }

    private static string CreateContentCacheKey(ResourceProjectKind kind, string sha1) =>
        $"{kind}:{sha1}";

    private async Task<LocalResourceCategoryCacheIndex> GetCacheIndexAsync(CancellationToken cancellationToken)
    {
        cacheIndex ??= await cacheStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        return cacheIndex;
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveProjectIconSourcesAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        bool downloadMissing,
        Action<LocalContentIconResolution>? iconResolved,
        CancellationToken cancellationToken)
    {
        if (thumbnailService is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var identities = CreateFileIdentities(resources)
            .Where(identity => SupportsRemoteProjectIcon(identity.Resource.Kind))
            .ToArray();
        if (identities.Length == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var projects = new List<(string FullPath, ResourceProject Project)>();
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var identity in identities)
            {
                if (!TryGetCurrentEntry(index, identity, out var entry)
                    || entry.Source is null
                    || string.IsNullOrWhiteSpace(entry.ProjectId)
                    || string.IsNullOrWhiteSpace(entry.IconUrl))
                {
                    continue;
                }

                projects.Add((identity.Resource.FullPath, new ResourceProject
                {
                    Kind = identity.Resource.Kind,
                    Source = entry.Source.Value,
                    ProjectId = entry.ProjectId,
                    IconUrl = entry.IconUrl
                }));
            }
        }
        finally
        {
            cacheLock.Release();
        }

        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var progressGate = new object();
        var tasks = projects.Select(async projectEntry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (fullPath, project) = projectEntry;
            var iconSource = thumbnailService.TryGetCachedThumbnailSource(project);
            if (iconSource is null && downloadMissing)
            {
                iconSource = await thumbnailService
                    .GetOrCreateThumbnailSourceAsync(project, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(iconSource))
            {
                result[fullPath] = iconSource;
                if (iconResolved is not null)
                    lock (progressGate)
                        iconResolved(new LocalContentIconResolution(fullPath, iconSource));
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveProjectIconSourcesAsync(
        IReadOnlyDictionary<string, RemoteIconCandidate> metadata,
        IReadOnlyDictionary<string, ResourceProjectKind> kindsByPath,
        bool downloadMissing,
        Action<LocalContentIconResolution>? iconResolved,
        CancellationToken cancellationToken)
    {
        if (thumbnailService is null || metadata.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var progressGate = new object();
        var tasks = metadata.Select(async pair =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ParseSource(pair.Value.Source);
            if (source is null
                || !kindsByPath.TryGetValue(pair.Key, out var kind)
                || string.IsNullOrWhiteSpace(pair.Value.ProjectId)
                || string.IsNullOrWhiteSpace(pair.Value.IconUrl))
            {
                return;
            }

            var project = new ResourceProject
            {
                Kind = kind,
                Source = source.Value,
                ProjectId = pair.Value.ProjectId,
                IconUrl = pair.Value.IconUrl
            };
            if (!SupportsRemoteProjectIcon(project.Kind))
                return;

            var iconSource = thumbnailService.TryGetCachedThumbnailSource(project);
            if (iconSource is null && downloadMissing)
            {
                iconSource = await thumbnailService
                    .GetOrCreateThumbnailSourceAsync(project, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(iconSource))
                return;

            result[pair.Key] = iconSource;
            if (iconResolved is not null)
                lock (progressGate)
                    iconResolved(new LocalContentIconResolution(pair.Key, iconSource));
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, LocalResourceEnrichmentResult> CombineMetadata(
        IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectCategory>> categories,
        IReadOnlyDictionary<string, string> icons,
        IReadOnlyDictionary<string, (ResourceProjectReference Reference, string? LocalizedName)> references)
    {
        var paths = categories.Keys
            .Concat(icons.Keys)
            .Concat(references.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return paths.ToDictionary(
            path => path,
            path =>
            {
                references.TryGetValue(path, out var match);
                return new LocalResourceEnrichmentResult(
                    categories.TryGetValue(path, out var values) ? values : [],
                    icons.GetValueOrDefault(path),
                    match.Reference,
                    match.LocalizedName);
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, (ResourceProjectReference Reference, string? LocalizedName)>> ResolveProjectReferencesAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken)
    {
        var identities = CreateFileIdentities(resources);
        var result = new Dictionary<string, (ResourceProjectReference Reference, string? LocalizedName)>(StringComparer.OrdinalIgnoreCase);
        if (identities.Count == 0)
            return result;

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await GetCacheIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var identity in identities)
            {
                if (!TryGetCurrentEntry(index, identity, out var entry)
                    || entry.Source is null
                    || string.IsNullOrWhiteSpace(entry.ProjectId))
                {
                    continue;
                }

                var reference = new ResourceProjectReference(
                    identity.Resource.Kind,
                    entry.Source.Value,
                    entry.ProjectId);
                var localizedName = identity.Resource.Kind is ResourceProjectKind.Mod
                    ? modNameLocalizationService.FindByProjectSlug(entry.Source.Value, entry.ProjectSlug, null)
                    : null;
                result[identity.Resource.FullPath] = (reference, localizedName);
            }
        }
        finally
        {
            cacheLock.Release();
        }

        return result;
    }

    private static bool SupportsRemoteProjectIcon(ResourceProjectKind kind) =>
        kind is ResourceProjectKind.ResourcePack or ResourceProjectKind.ShaderPack;

    private static ResourceProjectSource? ParseSource(string source) => source.ToLowerInvariant() switch
    {
        "modrinth" => ResourceProjectSource.Modrinth,
        "curseforge" => ResourceProjectSource.CurseForge,
        _ => null
    };

    private async Task TrySaveCacheIndexAsync(
        LocalResourceCategoryCacheIndex index,
        CancellationToken cancellationToken)
    {
        try
        {
            await cacheStore.SaveAsync(index, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to save local resource category cache.");
        }
    }

    private async Task<ModIconLookupCandidate?> CreateLookupCandidateAsync(
        LocalResourceFileIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            var fingerprint = await fingerprintService
                .GetFingerprintAsync(identity.Resource.FullPath, cancellationToken)
                .ConfigureAwait(false);
            return new ModIconLookupCandidate(
                identity.Resource.FullPath,
                fingerprint.Sha1,
                identity.FileAlias,
                fingerprint.CurseForgeFingerprint,
                identity.Resource.Kind);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogWarning(
                exception,
                "Failed to hash local resource for category lookup. Kind={Kind} FileName={FileName}",
                identity.Resource.Kind,
                Path.GetFileName(identity.Resource.FullPath));
            return null;
        }
    }

    private static ModIconLookupCandidate CreateLookupFromCache(
        LocalResourceFileIdentity identity,
        LocalResourceCategoryCacheEntry entry) => new(
        identity.Resource.FullPath,
        entry.Sha1,
        identity.FileAlias,
        entry.CurseForgeFingerprint,
        identity.Resource.Kind);

    private static IReadOnlyList<LocalResourceFileIdentity> CreateFileIdentities(
        IReadOnlyList<LocalResourceCategoryCandidate> resources)
    {
        var result = new List<LocalResourceFileIdentity>();
        foreach (var resource in resources
                     .Where(resource => !string.IsNullOrWhiteSpace(resource.FullPath))
                     .DistinctBy(resource => resource.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var identity = TryCreateFileIdentity(resource);
            if (identity is not null)
                result.Add(identity);
        }

        return result;
    }

    private static LocalResourceFileIdentity? TryCreateFileIdentity(LocalResourceCategoryCandidate resource)
    {
        try
        {
            var file = new FileInfo(resource.FullPath);
            if (!file.Exists)
                return null;

            var normalizedPath = NormalizeCachePath(file.FullName, resource.Kind).ToUpperInvariant();
            var cacheKey = $"{resource.Kind}:{normalizedPath}";
            var fileAlias = $"{cacheKey}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            return new LocalResourceFileIdentity(
                resource,
                cacheKey,
                fileAlias,
                file.Length,
                file.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string NormalizeCachePath(string path, ResourceProjectKind kind)
    {
        var fullPath = Path.GetFullPath(path);
        return kind is ResourceProjectKind.Mod && fullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fullPath[..^".disabled".Length]
            : fullPath;
    }

    private static bool TryGetCurrentEntry(
        LocalResourceCategoryCacheIndex index,
        LocalResourceFileIdentity identity,
        out LocalResourceCategoryCacheEntry entry)
    {
        if (index.Entries.TryGetValue(identity.CacheKey, out var cached)
            && cached.Kind == identity.Resource.Kind
            && cached.FileLength == identity.FileLength
            && cached.LastWriteTimeUtcTicks == identity.LastWriteTimeUtcTicks)
        {
            entry = cached;
            return true;
        }

        entry = null!;
        return false;
    }

    private static void CleanupCache(LocalResourceCategoryCacheIndex index, DateTimeOffset now)
    {
        foreach (var key in index.Entries
                     .Where(pair => pair.Value.LastUsedAt != default && now - pair.Value.LastUsedAt > CacheRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            index.Entries.Remove(key);
        }
    }

    private sealed record LocalResourceFileIdentity(
        LocalResourceCategoryCandidate Resource,
        string CacheKey,
        string FileAlias,
        long FileLength,
        long LastWriteTimeUtcTicks);
}
