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

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    private async Task<string?> TryCacheRemoteIconAsync(
        ModIconLookupCandidate lookup,
        RemoteIconCandidate icon,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CacheRemoteIconAsync(lookup, icon, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 通过共享资源缓存解析图像，在本地索引中记录文件别名与项目的映射。
    /// </summary>
    private async Task<string?> CacheRemoteIconAsync(
        ModIconLookupCandidate lookup,
        RemoteIconCandidate icon,
        CancellationToken cancellationToken)
    {
        var project = CreateModProject(icon.Source, icon.ProjectId, icon.IconUrl);
        if (project is null)
            return null;

        var iconSource = await thumbnailCache.GetOrCreateThumbnailSourceAsync(project, cancellationToken)
            .ConfigureAwait(false);
        if (iconSource is null)
            return null;

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var index = await cacheIndexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var entryKey = $"{icon.Source}:{icon.ProjectId}";
            index.Entries[entryKey] = new RemoteIconCacheEntry
            {
                Source = icon.Source,
                ProjectId = icon.ProjectId,
                IconUrl = icon.IconUrl,
                FileName = index.Entries.GetValueOrDefault(entryKey)?.FileName ?? string.Empty,
                CachedAt = now,
                LastUsedAt = now
            };
            index.Aliases[lookup.Sha1Alias] = entryKey;
            index.FileAliases[lookup.FileAlias] = entryKey;
            await cacheIndexStore.SaveAsync(index, cancellationToken).ConfigureAwait(false);

            logger.LogDebug(
                "Remote local mod icon mapped to the shared cache. Source={Source} ProjectId={ProjectId}",
                icon.Source,
                icon.ProjectId);
            return iconSource;
        }
        finally
        {
            cacheLock.Release();
        }
    }
}
