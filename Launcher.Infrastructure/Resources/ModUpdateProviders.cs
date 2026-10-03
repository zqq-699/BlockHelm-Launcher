/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Resources;

internal static class ModUpdateProviderConcurrency
{
    internal static SemaphoreSlim VersionLookupSlots { get; } = new(4, 4);
}

internal sealed class ModrinthModUpdateProvider(
    ModrinthApiClient apiClient,
    IResourceCatalogService catalogService,
    ILogger<ModrinthModUpdateProvider>? logger = null) : IModUpdateProvider
{
    private const int MatchBatchSize = 50;
    private const int MaximumVersionConcurrency = 4;
    private readonly ILogger<ModrinthModUpdateProvider> logger =
        logger ?? NullLogger<ModrinthModUpdateProvider>.Instance;

    public ResourceProjectSource Source => ResourceProjectSource.Modrinth;

    public async Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> FindUpdatesAsync(
        GameInstance instance,
        IReadOnlyList<ModUpdateLocalFileState> localFiles,
        CancellationToken cancellationToken = default,
        IProgress<string>? completedFileProgress = null)
    {
        var results = new ConcurrentDictionary<string, ModUpdateProviderResult>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<(ModUpdateLocalFileState LocalFile, ModrinthApiClient.ModrinthVersionFileMatch Current)>();
        foreach (var batch in localFiles.Chunk(MatchBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, ModrinthApiClient.ModrinthVersionFileMatch> matches;
            try
            {
                matches = await apiClient
                    .GetVersionFileMatchesAsync(batch.Select(item => item.Sha1).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Modrinth batch fingerprint lookup failed. Count={Count}",
                    batch.Length);
                foreach (var localFile in batch)
                {
                    results[localFile.FullPath] = new ModUpdateProviderResult(false, false);
                    completedFileProgress?.Report(localFile.FullPath);
                }
                continue;
            }

            foreach (var localFile in batch)
            {
                if (!matches.TryGetValue(localFile.Sha1, out var current))
                {
                    results[localFile.FullPath] = new ModUpdateProviderResult(true, false);
                    completedFileProgress?.Report(localFile.FullPath);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(current.ProjectId) || current.DatePublished is null)
                {
                    results[localFile.FullPath] = new ModUpdateProviderResult(false, true);
                    completedFileProgress?.Report(localFile.FullPath);
                    continue;
                }

                matched.Add((localFile, current));
            }
        }

        await Parallel.ForEachAsync(
            matched,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumVersionConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                await ModUpdateProviderConcurrency.VersionLookupSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    try
                    {
                        var versions = await catalogService.GetProjectVersionsAsync(new ResourceProjectVersionsRequest
                        {
                            Kind = ResourceProjectKind.Mod,
                            Source = Source,
                            ProjectId = item.Current.ProjectId,
                            MinecraftVersion = instance.MinecraftVersion,
                            Loader = instance.Loader,
                            IncludeDependencies = false,
                            PageSize = 10000
                        }, token).ConfigureAwait(false);
                        var target = SelectTarget(
                            versions.Versions,
                            item.Current.VersionId,
                            item.Current.DatePublished!.Value);
                        results[item.LocalFile.FullPath] = new ModUpdateProviderResult(
                            true,
                            true,
                            target is null ? null : new ModUpdateCandidate(
                                item.LocalFile,
                                Source,
                                item.Current.ProjectId,
                                item.Current.VersionId,
                                item.Current.VersionNumber,
                                item.Current.DatePublished,
                                target));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results[item.LocalFile.FullPath] = new ModUpdateProviderResult(false, true);
                        logger.LogWarning(
                            exception,
                            "Modrinth update version lookup failed. ProjectId={ProjectId} FileName={FileName}",
                            item.Current.ProjectId,
                            System.IO.Path.GetFileName(item.LocalFile.FullPath));
                    }
                }
                finally
                {
                    ModUpdateProviderConcurrency.VersionLookupSlots.Release();
                    completedFileProgress?.Report(item.LocalFile.FullPath);
                }
            }).ConfigureAwait(false);
        return new Dictionary<string, ModUpdateProviderResult>(results, StringComparer.OrdinalIgnoreCase);
    }

    internal static ResourceProjectVersion? SelectTarget(
        IReadOnlyList<ResourceProjectVersion> versions,
        string currentVersionId,
        DateTimeOffset currentPublishedAt) =>
        versions
            .Where(version => !string.Equals(version.VersionId, currentVersionId, StringComparison.OrdinalIgnoreCase))
            .Where(version => version.PublishedAt is { } publishedAt && publishedAt > currentPublishedAt)
            .Where(IsRelease)
            .OrderByDescending(version => version.PublishedAt)
            .FirstOrDefault();

    private static bool IsRelease(ResourceProjectVersion version) =>
        string.Equals(version.VersionType, "release", StringComparison.OrdinalIgnoreCase);
}

internal sealed class CurseForgeModUpdateProvider(
    CurseForgeApiClient apiClient,
    ICurseForgeApiKeyResolver apiKeyResolver,
    IResourceCatalogService catalogService,
    ILogger<CurseForgeModUpdateProvider>? logger = null) : IModUpdateProvider
{
    private const int MatchBatchSize = 50;
    private const int MaximumVersionConcurrency = 4;
    private readonly ILogger<CurseForgeModUpdateProvider> logger =
        logger ?? NullLogger<CurseForgeModUpdateProvider>.Instance;

    public ResourceProjectSource Source => ResourceProjectSource.CurseForge;

    public async Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> FindUpdatesAsync(
        GameInstance instance,
        IReadOnlyList<ModUpdateLocalFileState> localFiles,
        CancellationToken cancellationToken = default,
        IProgress<string>? completedFileProgress = null)
    {
        var results = new ConcurrentDictionary<string, ModUpdateProviderResult>(StringComparer.OrdinalIgnoreCase);
        var apiKey = await apiKeyResolver.TryResolveAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            foreach (var localFile in localFiles)
                completedFileProgress?.Report(localFile.FullPath);
            return localFiles.ToDictionary(
                item => item.FullPath,
                _ => new ModUpdateProviderResult(false, false),
                StringComparer.OrdinalIgnoreCase);
        }

        var matched = new List<(ModUpdateLocalFileState LocalFile, CurseForgeApiClient.CurseForgeFingerprintMatch Current)>();
        foreach (var batch in localFiles.Chunk(MatchBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<long, CurseForgeApiClient.CurseForgeFingerprintMatch> matches;
            try
            {
                matches = await apiClient
                    .GetFingerprintMatchesAsync(
                        batch.Select(item => item.CurseForgeFingerprint).ToArray(),
                        apiKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "CurseForge batch fingerprint lookup failed. Count={Count}",
                    batch.Length);
                foreach (var localFile in batch)
                {
                    results[localFile.FullPath] = new ModUpdateProviderResult(false, false);
                    completedFileProgress?.Report(localFile.FullPath);
                }
                continue;
            }

            foreach (var localFile in batch)
            {
                if (!matches.TryGetValue(localFile.CurseForgeFingerprint, out var current))
                {
                    results[localFile.FullPath] = new ModUpdateProviderResult(true, false);
                    completedFileProgress?.Report(localFile.FullPath);
                    continue;
                }

                if (current.FileDate is null)
                {
                    results[localFile.FullPath] = new ModUpdateProviderResult(false, true);
                    completedFileProgress?.Report(localFile.FullPath);
                    continue;
                }

                matched.Add((localFile, current));
            }
        }

        await Parallel.ForEachAsync(
            matched,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumVersionConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                await ModUpdateProviderConcurrency.VersionLookupSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var projectId = item.Current.ProjectId.ToString();
                    try
                    {
                        var versions = await catalogService.GetProjectVersionsAsync(new ResourceProjectVersionsRequest
                        {
                            Kind = ResourceProjectKind.Mod,
                            Source = Source,
                            ProjectId = projectId,
                            MinecraftVersion = instance.MinecraftVersion,
                            Loader = instance.Loader,
                            IncludeDependencies = false,
                            PageSize = 10000
                        }, token).ConfigureAwait(false);
                        if (versions.IsCurseForgeUnavailable)
                        {
                            results[item.LocalFile.FullPath] = new ModUpdateProviderResult(false, true);
                            return;
                        }

                        var currentVersionId = item.Current.FileId.ToString();
                        var target = ModrinthModUpdateProvider.SelectTarget(
                            versions.Versions,
                            currentVersionId,
                            item.Current.FileDate!.Value);
                        results[item.LocalFile.FullPath] = new ModUpdateProviderResult(
                            true,
                            true,
                            target is null ? null : new ModUpdateCandidate(
                                item.LocalFile,
                                Source,
                                projectId,
                                currentVersionId,
                                item.Current.VersionNumber,
                                item.Current.FileDate,
                                target));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results[item.LocalFile.FullPath] = new ModUpdateProviderResult(false, true);
                        logger.LogWarning(
                            exception,
                            "CurseForge update version lookup failed. ProjectId={ProjectId} FileName={FileName}",
                            projectId,
                            System.IO.Path.GetFileName(item.LocalFile.FullPath));
                    }
                }
                finally
                {
                    ModUpdateProviderConcurrency.VersionLookupSlots.Release();
                    completedFileProgress?.Report(item.LocalFile.FullPath);
                }
            }).ConfigureAwait(false);
        return new Dictionary<string, ModUpdateProviderResult>(results, StringComparer.OrdinalIgnoreCase);
    }
}
