/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

internal sealed class LocalResourceCategoryCacheStore
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string cachePath;
    private readonly ILogger logger;

    public LocalResourceCategoryCacheStore(string cacheDirectory, ILogger logger)
    {
        cachePath = Path.Combine(cacheDirectory, "index.json");
        this.logger = logger;
    }

    public async Task<LocalResourceCategoryCacheIndex> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
            return new LocalResourceCategoryCacheIndex();

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var index = await JsonSerializer.DeserializeAsync<LocalResourceCategoryCacheIndex>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (index is null || index.Version != CurrentVersion)
            {
                logger.LogInformation(
                    "Local resource category cache version changed. CachedVersion={CachedVersion} CurrentVersion={CurrentVersion}",
                    index?.Version,
                    CurrentVersion);
                return new LocalResourceCategoryCacheIndex();
            }

            return index;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Failed to parse local resource category cache. The cache will be rebuilt.");
            return new LocalResourceCategoryCacheIndex();
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to read local resource category cache. The cache will be rebuilt.");
            return new LocalResourceCategoryCacheIndex();
        }
    }

    public Task SaveAsync(LocalResourceCategoryCacheIndex index, CancellationToken cancellationToken) =>
        AtomicJsonFileWriter.WriteAsync(cachePath, index, JsonOptions, cancellationToken);
}

internal sealed class LocalResourceCategoryCacheIndex
{
    public int Version { get; init; } = LocalResourceCategoryCacheStore.CurrentVersion;

    public Dictionary<string, LocalResourceCategoryCacheEntry> Entries { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class LocalResourceCategoryCacheEntry
{
    public ResourceProjectKind Kind { get; init; }

    public long FileLength { get; init; }

    public long LastWriteTimeUtcTicks { get; init; }

    public string Sha1 { get; init; } = string.Empty;

    public long CurseForgeFingerprint { get; init; }

    public IReadOnlyList<ResourceProjectCategory> Categories { get; init; } = [];

    public ResourceProjectSource? Source { get; init; }

    public string ProjectId { get; init; } = string.Empty;

    public string? ProjectSlug { get; init; }

    public string IconUrl { get; init; } = string.Empty;

    public bool HasRemoteMetadata { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    public DateTimeOffset LastUsedAt { get; set; }
}
