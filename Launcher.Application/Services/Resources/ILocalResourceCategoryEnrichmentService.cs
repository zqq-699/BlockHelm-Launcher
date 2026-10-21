/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public sealed record LocalResourceCategoryCandidate(
    string FullPath,
    ResourceProjectKind Kind);

public sealed record LocalResourceEnrichmentResult(
    IReadOnlyList<ResourceProjectCategory> Categories,
    string? IconSource = null,
    ResourceProjectReference? ProjectReference = null,
    string? LocalizedName = null);

public interface ILocalResourceCategoryEnrichmentService
{
    Task<IReadOnlyDictionary<string, LocalResourceEnrichmentResult>> ResolveCachedMetadataAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default,
        IProgress<LocalContentIconResolution>? iconProgress = null);

    Task<IReadOnlyDictionary<string, LocalResourceEnrichmentResult>> ResolveMetadataAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default,
        IProgress<LocalContentIconResolution>? iconProgress = null);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectCategory>>> ResolveCachedCategoriesAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectCategory>>> ResolveCategoriesAsync(
        IReadOnlyList<LocalResourceCategoryCandidate> resources,
        CancellationToken cancellationToken = default);
}
