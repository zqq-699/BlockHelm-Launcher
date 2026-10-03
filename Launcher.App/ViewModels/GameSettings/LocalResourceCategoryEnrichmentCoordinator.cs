/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Services;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 在本地内容列表发布后异步补全在线项目分类，并拒绝过期列表的返回结果。
/// </summary>
internal sealed class LocalResourceCategoryEnrichmentCoordinator<T> : IDisposable
{
    private const int EnrichmentBatchSize = 50;
    private readonly ILocalResourceCategoryEnrichmentService? service;
    private readonly ResourceProjectKind kind;
    private readonly Func<T, string> pathSelector;
    private readonly Func<T, IReadOnlyList<ResourceProjectCategory>> categoriesSelector;
    private readonly Action<T, IReadOnlyList<ResourceProjectCategory>> categoriesSetter;
    private readonly Func<T, string?>? iconSourceSelector;
    private readonly Action<T, string>? iconSourceSetter;
    private readonly bool preferResolvedIconSource;
    private readonly Action<T, string>? iconChanged;
    private readonly Func<T, ResourceProjectReference?>? projectReferenceSelector;
    private readonly Action<T, ResourceProjectReference>? projectReferenceSetter;
    private readonly Func<IReadOnlyList<T>> currentItemsProvider;
    private readonly Action changed;
    private readonly IUiDispatcher dispatcher;
    private readonly ILogger logger;
    private CancellationTokenSource? cancellationTokenSource;
    private long generation;

    public LocalResourceCategoryEnrichmentCoordinator(
        ILocalResourceCategoryEnrichmentService? service,
        ResourceProjectKind kind,
        Func<T, string> pathSelector,
        Func<T, IReadOnlyList<ResourceProjectCategory>> categoriesSelector,
        Action<T, IReadOnlyList<ResourceProjectCategory>> categoriesSetter,
        Func<IReadOnlyList<T>> currentItemsProvider,
        Action changed,
        IUiDispatcher dispatcher,
        ILogger logger,
        Func<T, string?>? iconSourceSelector = null,
        Action<T, string>? iconSourceSetter = null,
        bool preferResolvedIconSource = false,
        Func<T, ResourceProjectReference?>? projectReferenceSelector = null,
        Action<T, ResourceProjectReference>? projectReferenceSetter = null,
        Action<T, string>? iconChanged = null)
    {
        this.service = service;
        this.kind = kind;
        this.pathSelector = pathSelector;
        this.categoriesSelector = categoriesSelector;
        this.categoriesSetter = categoriesSetter;
        this.iconSourceSelector = iconSourceSelector;
        this.iconSourceSetter = iconSourceSetter;
        this.preferResolvedIconSource = preferResolvedIconSource;
        this.iconChanged = iconChanged;
        this.projectReferenceSelector = projectReferenceSelector;
        this.projectReferenceSetter = projectReferenceSetter;
        this.currentItemsProvider = currentItemsProvider;
        this.changed = changed;
        this.dispatcher = dispatcher;
        this.logger = logger;
    }

    public void Queue(IReadOnlyList<T> items)
    {
        if (service is null || items.Count == 0)
            return;

        var candidates = items
            .Select(item => pathSelector(item))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new LocalResourceCategoryCandidate(path, kind))
            .ToArray();
        if (candidates.Length == 0)
            return;

        Cancel();
        var expectedGeneration = generation;
        var cts = new CancellationTokenSource();
        cancellationTokenSource = cts;
        _ = EnrichAsync(candidates, expectedGeneration, cts);
    }

    public void Cancel()
    {
        Interlocked.Increment(ref generation);
        var previous = Interlocked.Exchange(ref cancellationTokenSource, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    public void Dispose() => Cancel();

    private async Task EnrichAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> candidates,
        long expectedGeneration,
        CancellationTokenSource cts)
    {
        try
        {
            var iconProgress = new CallbackProgress<LocalContentIconResolution>(resolution =>
                dispatcher.PostAfterTransition(() => ApplyIcon(resolution, expectedGeneration, cts)));
            foreach (var batch in candidates.Chunk(EnrichmentBatchSize))
            {
                var cached = await service!
                    .ResolveCachedMetadataAsync(batch, cts.Token, iconProgress)
                    .ConfigureAwait(false);
                if (!IsCurrent(expectedGeneration, cts))
                    return;

                if (cached.Count > 0)
                    dispatcher.PostAfterTransition(() => Apply(cached, expectedGeneration, cts));

                var resolved = await service!
                    .ResolveMetadataAsync(batch, cts.Token, iconProgress)
                    .ConfigureAwait(false);
                if (!IsCurrent(expectedGeneration, cts))
                    return;

                if (resolved.Count > 0)
                    dispatcher.PostAfterTransition(() => Apply(resolved, expectedGeneration, cts));
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to enrich local resource categories. Kind={Kind}", kind);
        }
        finally
        {
            var current = Interlocked.CompareExchange(ref cancellationTokenSource, null, cts);
            if (ReferenceEquals(current, cts))
                cts.Dispose();
        }
    }

    private void Apply(
        IReadOnlyDictionary<string, LocalResourceEnrichmentResult> resolved,
        long expectedGeneration,
        CancellationTokenSource cts)
    {
        if (!IsCurrent(expectedGeneration, cts))
            return;

        var updated = false;
        foreach (var item in currentItemsProvider())
        {
            var path = pathSelector(item);
            if (!resolved.TryGetValue(path, out var metadata))
                continue;

            var currentCategories = categoriesSelector(item);
            if (metadata.Categories.Count > 0 && !currentCategories.SequenceEqual(metadata.Categories))
            {
                categoriesSetter(item, metadata.Categories);
                updated = true;
            }

            if (iconSourceSelector is not null
                && iconSourceSetter is not null
                && !string.IsNullOrWhiteSpace(metadata.IconSource))
            {
                var currentIconSource = iconSourceSelector(item);
                if ((preferResolvedIconSource || string.IsNullOrWhiteSpace(currentIconSource))
                    && !string.Equals(currentIconSource, metadata.IconSource, StringComparison.Ordinal))
                {
                    iconSourceSetter(item, metadata.IconSource);
                    if (iconChanged is null)
                        updated = true;
                    else
                        iconChanged(item, metadata.IconSource);
                }
            }

            if (projectReferenceSelector is not null
                && projectReferenceSetter is not null
                && metadata.ProjectReference is not null
                && projectReferenceSelector(item) != metadata.ProjectReference)
            {
                projectReferenceSetter(item, metadata.ProjectReference);
                updated = true;
            }
        }

        if (updated)
            changed();
    }

    private void ApplyIcon(
        LocalContentIconResolution resolution,
        long expectedGeneration,
        CancellationTokenSource cts)
    {
        if (!IsCurrent(expectedGeneration, cts)
            || iconSourceSelector is null
            || iconSourceSetter is null
            || string.IsNullOrWhiteSpace(resolution.FullPath)
            || string.IsNullOrWhiteSpace(resolution.IconSource))
        {
            return;
        }

        var item = currentItemsProvider().FirstOrDefault(candidate =>
            string.Equals(pathSelector(candidate), resolution.FullPath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return;

        var currentIconSource = iconSourceSelector(item);
        if ((!preferResolvedIconSource && !string.IsNullOrWhiteSpace(currentIconSource))
            || string.Equals(currentIconSource, resolution.IconSource, StringComparison.Ordinal))
        {
            return;
        }

        iconSourceSetter(item, resolution.IconSource);
        if (iconChanged is null)
            changed();
        else
            iconChanged(item, resolution.IconSource);
    }

    private bool IsCurrent(long expectedGeneration, CancellationTokenSource cts) =>
        !cts.IsCancellationRequested
        && expectedGeneration == Interlocked.Read(ref generation);

}
