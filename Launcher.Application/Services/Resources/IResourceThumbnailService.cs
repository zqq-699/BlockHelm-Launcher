/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

/// <summary>
/// Resolves resource project thumbnails to launcher-managed local cache files.
/// </summary>
public interface IResourceThumbnailService
{
    const int ThumbnailSize = 64;

    string? TryGetCachedThumbnailSource(ResourceProject project);

    Task<string?> GetOrCreateThumbnailSourceAsync(
        ResourceProject project,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uses locally cached project metadata without replacing a different icon URL already recorded for the project.
    /// </summary>
    Task<string?> GetOrCreateThumbnailFromCachedProjectAsync(
        ResourceProject project,
        CancellationToken cancellationToken = default);
}
