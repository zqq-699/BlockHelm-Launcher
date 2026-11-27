/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    private string? GetCachedIcon(RemoteIconCacheEntry entry) =>
        CreateModProject(entry.Source, entry.ProjectId, entry.IconUrl) is { } project
            ? thumbnailCache.TryGetCachedThumbnailSource(project)
            : null;

    private static ResourceProject? CreateModProject(string source, string projectId, string iconUrl)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(iconUrl)
            || !Enum.TryParse<ResourceProjectSource>(source, ignoreCase: true, out var projectSource)
            || projectSource is not (ResourceProjectSource.Modrinth or ResourceProjectSource.CurseForge))
        {
            return null;
        }

        return new ResourceProject
        {
            Kind = ResourceProjectKind.Mod,
            Source = projectSource,
            ProjectId = projectId,
            IconUrl = iconUrl
        };
    }
}
