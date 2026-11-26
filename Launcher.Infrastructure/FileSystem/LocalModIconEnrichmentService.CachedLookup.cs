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
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    private async Task<string?> GetCachedIconAsync(
        RemoteIconCacheEntry entry,
        CancellationToken cancellationToken)
    {
        var project = CreateModProject(entry.Source, entry.ProjectId, entry.IconUrl);
        if (project is null)
            return null;

        var legacyPath = GetLegacyIconPath(entry);
        if (legacyPath is null)
            return thumbnailCache.TryGetCachedThumbnailSource(project);

        var shared = await thumbnailCache.MigrateModThumbnailAsync(project, legacyPath, cancellationToken)
            .ConfigureAwait(false);
        return shared ?? (File.Exists(legacyPath) ? new Uri(legacyPath).AbsoluteUri : null);
    }

    private string? GetLegacyIconPath(RemoteIconCacheEntry entry)
    {
        if (string.IsNullOrEmpty(entry.FileName))
            return null;

        var path = GetIconCachePath($"{entry.Source}:{entry.ProjectId}");
        if (!string.Equals(Path.GetFileName(path), entry.FileName, StringComparison.OrdinalIgnoreCase))
            return null;

        var file = new FileInfo(path);
        return file.Exists
            && (file.Attributes & FileAttributes.ReparsePoint) == 0
            && (new DirectoryInfo(cacheDirectory).Attributes & FileAttributes.ReparsePoint) == 0
            ? path
            : null;
    }

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

    private async Task MigrateLegacyCacheAsync()
    {
        try
        {
            var index = await LoadCacheSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var entry in index.Entries.Values)
            {
                if (GetLegacyIconPath(entry) is { } path
                    && CreateModProject(entry.Source, entry.ProjectId, entry.IconUrl) is { } project)
                {
                    await thumbnailCache.MigrateModThumbnailAsync(project, path, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not migrate the local Mod thumbnail cache.");
        }
    }
}
