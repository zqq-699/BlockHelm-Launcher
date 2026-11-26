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
using System.Text.Json;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

internal sealed class RemoteIconCacheIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string indexPath;
    private readonly ILogger logger;

    public RemoteIconCacheIndexStore(string cacheDirectory, ILogger logger)
    {
        indexPath = Path.Combine(cacheDirectory, "index.json");
        this.logger = logger;
    }

    public async Task<RemoteIconCacheIndex> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
            return new RemoteIconCacheIndex();

        try
        {
            await using var stream = File.OpenRead(indexPath);
            return await JsonSerializer.DeserializeAsync<RemoteIconCacheIndex>(
                       stream,
                       JsonOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? new RemoteIconCacheIndex();
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Failed to parse remote local mod icon cache index.");
            return new RemoteIconCacheIndex();
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to read remote local mod icon cache index.");
            return new RemoteIconCacheIndex();
        }
    }

    public Task SaveAsync(RemoteIconCacheIndex index, CancellationToken cancellationToken)
    {
        return AtomicJsonFileWriter.WriteAsync(indexPath, index, JsonOptions, cancellationToken);
    }
}

internal sealed class RemoteIconCacheIndex
{
    public Dictionary<string, RemoteIconCacheEntry> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> FileAliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class RemoteIconCacheEntry
{
    public string Source { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public DateTimeOffset CachedAt { get; init; }

    public DateTimeOffset LastUsedAt { get; set; }
}
