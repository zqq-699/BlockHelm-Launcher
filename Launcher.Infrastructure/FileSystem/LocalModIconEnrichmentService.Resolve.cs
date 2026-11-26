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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    /// <summary>
    /// 按文件顺序计算指纹，内容缓存命中后立即报告；远程缺失项由单消费者分批补全。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveMissingIconSourcesAsync(
        IReadOnlyList<LocalMod> mods,
        CancellationToken cancellationToken = default,
        IProgress<LocalContentIconResolution>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var candidates = mods
            .Where(mod => string.IsNullOrWhiteSpace(mod.IconSource))
            .Where(mod => !string.IsNullOrWhiteSpace(mod.FullPath) && File.Exists(mod.FullPath))
            .GroupBy(mod => mod.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (candidates.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        StartCacheMigration();
        logger.LogDebug(
            "Remote local mod icon enrichment started. CandidateCount={CandidateCount}",
            candidates.Count);

        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reported = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cacheFileAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var touchedEntryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentCacheHits = 0;
        long timeToFirstIconMs = -1;

        void ReportResolvedIcon(string fullPath, string iconSource)
        {
            while (true)
            {
                if (reported.TryGetValue(fullPath, out var current))
                {
                    if (string.Equals(current, iconSource, StringComparison.Ordinal))
                        return;
                    if (!reported.TryUpdate(fullPath, iconSource, current))
                        continue;
                }
                else if (!reported.TryAdd(fullPath, iconSource))
                {
                    continue;
                }

                Interlocked.CompareExchange(ref timeToFirstIconMs, stopwatch.ElapsedMilliseconds, -1);
                ReportProgress(progress, fullPath, iconSource);
                return;
            }
        }

        var cacheSnapshot = await LoadCacheSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var remoteChannel = Channel.CreateUnbounded<ModIconLookupCandidate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var remoteConsumer = ConsumeRemoteCandidatesAsync(
            remoteChannel.Reader,
            result,
            ReportResolvedIcon,
            cancellationToken);

        int remoteResolvedCount;
        try
        {
            foreach (var mod in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lookup = await CreateLookupCandidateAsync(mod, cancellationToken).ConfigureAwait(false);
                if (lookup is null)
                    continue;

                if (cacheSnapshot.Aliases.TryGetValue(lookup.Sha1Alias, out var entryKey)
                    && cacheSnapshot.Entries.TryGetValue(entryKey, out var entry)
                    && await GetCachedIconAsync(entry, cancellationToken).ConfigureAwait(false) is { } cachedIcon)
                {
                    result[lookup.FullPath] = cachedIcon;
                    contentCacheHits++;
                    cacheFileAliases[lookup.FileAlias] = entryKey;
                    touchedEntryKeys.Add(entryKey);

                    ReportResolvedIcon(lookup.FullPath, cachedIcon);
                    if (now - entry.CachedAt <= RefreshAfter)
                        continue;
                }

                await remoteChannel.Writer.WriteAsync(lookup, cancellationToken).ConfigureAwait(false);
            }

            remoteChannel.Writer.TryComplete();
            await PersistCacheUsageAsync(cacheFileAliases, touchedEntryKeys, now, cancellationToken)
                .ConfigureAwait(false);
            remoteResolvedCount = await remoteConsumer.ConfigureAwait(false);
        }
        catch
        {
            remoteChannel.Writer.TryComplete();
            try
            {
                await remoteConsumer.ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }

        await CleanupCacheOnceAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Remote local mod icon enrichment completed. CandidateCount={CandidateCount} ResolvedCount={ResolvedCount} ContentCacheHitCount={ContentCacheHitCount} RemoteResolvedCount={RemoteResolvedCount} TimeToFirstIconMs={TimeToFirstIconMs}",
            candidates.Count,
            result.Count,
            contentCacheHits,
            remoteResolvedCount,
            timeToFirstIconMs);
        return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 仅通过文件别名读取现有新鲜缓存，适合列表首次发布前的快速同步。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveCachedIconSourcesAsync(
        IReadOnlyList<LocalMod> mods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var candidates = mods
            .Where(mod => string.IsNullOrWhiteSpace(mod.IconSource))
            .Where(mod => !string.IsNullOrWhiteSpace(mod.FullPath) && File.Exists(mod.FullPath))
            .GroupBy(mod => mod.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (candidates.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = await LoadCacheSnapshotAsync(cancellationToken).ConfigureAwait(false);
        foreach (var mod in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileAlias = TryCreateFileAlias(mod.FullPath);
            if (fileAlias is not null
                && index.FileAliases.TryGetValue(fileAlias, out var entryKey)
                && index.Entries.TryGetValue(entryKey, out var entry)
                && await GetCachedIconAsync(entry, cancellationToken).ConfigureAwait(false) is { } cachedIcon)
            {
                result[mod.FullPath] = cachedIcon;
            }
        }

        StartCacheMigration();
        logger.LogDebug(
            "Remote local mod icon cache checked. CandidateCount={CandidateCount} HitCount={HitCount}",
            candidates.Count,
            result.Count);
        return result;
    }

    private void StartCacheMigration()
    {
        if (Interlocked.Exchange(ref cacheMigrationStarted, 1) == 0)
            _ = Task.Run(MigrateLegacyCacheAsync);
    }

    private async Task<RemoteIconCacheIndex> LoadCacheSnapshotAsync(CancellationToken cancellationToken)
    {
        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await cacheIndexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task<int> ConsumeRemoteCandidatesAsync(
        ChannelReader<ModIconLookupCandidate> reader,
        ConcurrentDictionary<string, string> result,
        Action<string, string> reportResolvedIcon,
        CancellationToken cancellationToken)
    {
        var batch = new List<ModIconLookupCandidate>(ProviderBatchSize);
        var resolvedCount = 0;
        await foreach (var candidate in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(candidate);
            if (batch.Count < ProviderBatchSize)
                continue;

            resolvedCount += await ResolveRemoteBatchAsync(
                    batch,
                    result,
                    reportResolvedIcon,
                    cancellationToken)
                .ConfigureAwait(false);
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            resolvedCount += await ResolveRemoteBatchAsync(
                    batch,
                    result,
                    reportResolvedIcon,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return resolvedCount;
    }

    private async Task<int> ResolveRemoteBatchAsync(
        IReadOnlyList<ModIconLookupCandidate> batch,
        ConcurrentDictionary<string, string> result,
        Action<string, string> reportResolvedIcon,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveRemoteIconsAsync(
                    batch,
                    cancellationToken,
                    reportResolvedIcon)
                .ConfigureAwait(false);
            foreach (var (fullPath, iconSource) in resolved)
                result[fullPath] = iconSource;
            return resolved.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to resolve a remote local mod icon batch. BatchCount={BatchCount}",
                batch.Count);
            return 0;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveRemoteIconsAsync(
        IReadOnlyList<ModIconLookupCandidate> candidates,
        CancellationToken cancellationToken,
        Action<string, string> reportResolvedIcon)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = candidates
            .GroupBy(candidate => candidate.Sha1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ModIconLookupCandidate>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var providerCandidates = unresolved.Values.Select(group => group[0]).ToArray();

        var modrinthIcons = await providerClient
            .ResolveModrinthAsync(providerCandidates, cancellationToken)
            .ConfigureAwait(false);
        var resolvedModrinthIcons = await CacheProviderIconsAsync(
                modrinthIcons,
                unresolved,
                reportResolvedIcon,
                cancellationToken)
            .ConfigureAwait(false);
        ApplyProviderResults(resolvedModrinthIcons, unresolved, result);

        if (unresolved.Count > 0)
        {
            var curseForgeCandidates = unresolved.Values.Select(group => group[0]).ToArray();
            var curseForgeIcons = await providerClient
                .ResolveCurseForgeAsync(curseForgeCandidates, cancellationToken)
                .ConfigureAwait(false);
            var resolvedCurseForgeIcons = await CacheProviderIconsAsync(
                    curseForgeIcons,
                    unresolved,
                    reportResolvedIcon,
                    cancellationToken)
                .ConfigureAwait(false);
            ApplyProviderResults(resolvedCurseForgeIcons, unresolved, result);
        }

        await PersistResolvedRemoteFileAliasesAsync(candidates, result.Keys, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyDictionary<string, string>> CacheProviderIconsAsync(
        IReadOnlyDictionary<string, RemoteIconCandidate> providerIcons,
        IReadOnlyDictionary<string, IReadOnlyList<ModIconLookupCandidate>> unresolved,
        Action<string, string> reportResolvedIcon,
        CancellationToken cancellationToken)
    {
        var resolved = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tasks = providerIcons.Select(async pair =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!unresolved.TryGetValue(pair.Key, out var matchingCandidates)
                || matchingCandidates.Count == 0)
            {
                return;
            }

            var iconSource = await TryCacheRemoteIconAsync(
                    matchingCandidates[0],
                    pair.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (iconSource is null)
                return;

            resolved[pair.Key] = iconSource;
            foreach (var candidate in matchingCandidates)
                reportResolvedIcon(candidate.FullPath, iconSource);
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return resolved;
    }

    private static void ApplyProviderResults(
        IReadOnlyDictionary<string, string> resolvedBySha1,
        IDictionary<string, IReadOnlyList<ModIconLookupCandidate>> unresolved,
        IDictionary<string, string> result)
    {
        foreach (var (sha1, iconSource) in resolvedBySha1)
        {
            if (!unresolved.Remove(sha1, out var matchingCandidates))
                continue;

            foreach (var candidate in matchingCandidates)
                result[candidate.FullPath] = iconSource;
        }
    }

    private async Task PersistCacheUsageAsync(
        IReadOnlyDictionary<string, string> fileAliases,
        IReadOnlyCollection<string> touchedEntryKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (fileAliases.Count == 0 && touchedEntryKeys.Count == 0)
            return;

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await cacheIndexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entryKey in touchedEntryKeys)
            {
                if (index.Entries.TryGetValue(entryKey, out var entry))
                    entry.LastUsedAt = now;
            }

            foreach (var (fileAlias, entryKey) in fileAliases)
            {
                if (index.Entries.ContainsKey(entryKey))
                    index.FileAliases[fileAlias] = entryKey;
            }

            await cacheIndexStore.SaveAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task PersistResolvedRemoteFileAliasesAsync(
        IReadOnlyList<ModIconLookupCandidate> candidates,
        IEnumerable<string> resolvedPaths,
        CancellationToken cancellationToken)
    {
        var resolvedPathSet = resolvedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (resolvedPathSet.Count == 0)
            return;

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await cacheIndexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var changed = false;
            foreach (var candidate in candidates)
            {
                if (!resolvedPathSet.Contains(candidate.FullPath)
                    || !index.Aliases.TryGetValue(candidate.Sha1Alias, out var entryKey)
                    || !index.Entries.ContainsKey(entryKey))
                {
                    continue;
                }

                index.FileAliases[candidate.FileAlias] = entryKey;
                changed = true;
            }

            if (changed)
                await cacheIndexStore.SaveAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private static void ReportProgress(
        IProgress<LocalContentIconResolution>? progress,
        string fullPath,
        string iconSource)
    {
        if (progress is null
            || string.IsNullOrWhiteSpace(fullPath)
            || string.IsNullOrWhiteSpace(iconSource))
        {
            return;
        }

        progress.Report(new LocalContentIconResolution(fullPath, iconSource));
    }
}
