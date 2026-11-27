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
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    private async Task<string?> TryCacheRemoteIconAsync(
        RemoteIconCandidate icon,
        CancellationToken cancellationToken)
    {
        var project = CreateModProject(icon.Source, icon.ProjectId, icon.IconUrl);
        if (project is null)
            return null;

        try
        {
            return await thumbnailCache.GetOrCreateThumbnailSourceAsync(project, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to cache remote local mod icon. Source={Source} ProjectId={ProjectId}",
                icon.Source,
                icon.ProjectId);
            return null;
        }
    }

    private async Task PersistRemoteIconBatchAsync(
        IReadOnlyList<(ModIconLookupCandidate Lookup, RemoteIconCandidate Icon)> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
            return;

        try
        {
            await cacheIndexStore.UpdateAsync(index =>
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var (lookup, icon) in updates)
                {
                    var entryKey = $"{icon.Source}:{icon.ProjectId}";
                    index.Entries[entryKey] = new RemoteIconCacheEntry
                    {
                        Source = icon.Source,
                        ProjectId = icon.ProjectId,
                        IconUrl = icon.IconUrl,
                        CachedAt = now,
                        LastUsedAt = now
                    };
                    index.Aliases[lookup.Sha1Alias] = entryKey;
                    index.FileAliases[lookup.FileAlias] = entryKey;
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);

            logger.LogDebug(
                "Remote local mod icon mappings saved. Count={Count}", updates.Count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to save remote local mod icon mappings. Count={Count}", updates.Count);
        }
    }
}
