/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class VanillaVersionMetadataCache(string? directory = null, TimeProvider? timeProvider = null, string? managedRoot = null,
    long? maximumDiskBytes = null)
{
    internal static readonly TimeSpan ManifestLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    internal const long MaximumDiskBytes = 64L * 1024 * 1024;
    private static readonly ConditionalWeakTable<HttpClient, VanillaVersionMetadataCache> ClientCaches = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? lastCleanup;
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    // The transport factory supplies BHL's cache location. Injected/custom clients
    // keep isolated memory caches unless their owner explicitly selects a location.
    internal static VanillaVersionMetadataCache ForClient(HttpClient client) => ClientCaches.GetValue(client, _ => new());

    internal static VanillaVersionMetadataCache ForLauncher(HttpClient client, LauncherPathProvider paths) =>
        ClientCaches.GetValue(client, _ => new(paths.MinecraftMetadataCacheDirectory, managedRoot: paths.DefaultDataDirectory));

    // The caller holds Gate through lookup/download/publication. Concurrent callers
    // share successful results; a canceled caller never leaves a cached failed task.
    internal async Task<byte[]?> ReadAsync(string key, TimeSpan? lifetime, ILogger? logger, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Prune(logger, token, key);
        if (entries.TryGetValue(key, out var entry) && IsFresh(entry.WrittenAt, lifetime))
        {
            TouchVersion(key, logger);
            logger?.LogDebug("Minecraft metadata cache hit. CacheKey={CacheKey} Storage={Storage}", key, "Memory");
            return entry.Bytes;
        }
        entries.Remove(key);
        if (directory is null) return null;
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path) || !IsFresh(File.GetLastWriteTimeUtc(path), lifetime)) return null;
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            Remember(key, bytes, File.GetLastWriteTimeUtc(path));
            TouchVersion(key, logger);
            logger?.LogDebug("Minecraft metadata cache hit. CacheKey={CacheKey} Storage={Storage}", key, "Disk");
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger?.LogDebug(exception, "Minecraft metadata cache could not be read. CacheKey={CacheKey}", key);
            return null;
        }
    }

    internal async Task WriteAsync(string key, byte[] bytes, ILogger? logger, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (directory is not null)
        {
            string? temporary = null;
            try
            {
                var path = CachePath(key);
                MinecraftPathGuard.EnsureSafeDirectory(directory, managedRoot ?? directory, "Minecraft metadata cache");
                temporary = Path.Combine(directory, $".{key}.{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _ = CachePath(key);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Cache persistence is optional; a valid download still succeeds.
                logger?.LogDebug(exception, "Minecraft metadata cache could not be written. CacheKey={CacheKey}", key);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        Remember(key, bytes, clock.GetUtcNow());
        Prune(logger, token, key);
    }

    internal void Forget(string key) => entries.Remove(key);

    // Only our own top-level files are eligible. Never follow links or recurse
    // into other caches; inability to maintain this optional cache is harmless.
    private void Prune(ILogger? logger, CancellationToken token, string protectedKey)
    {
        var now = clock.GetUtcNow();
        if (directory is null || lastCleanup is { } previous && now - previous < TimeSpan.FromDays(1)) return;
        lastCleanup = now;
        var removed = 0;
        try
        {
            MinecraftPathGuard.EnsureNoReparsePoints(managedRoot ?? directory, directory, "Minecraft metadata cache");
            if (!Directory.Exists(directory)) return;
            var retained = new List<(FileInfo File, DateTimeOffset UsedAt)>();
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name == protectedKey || !IsCacheFile(name) && !IsTemporaryFile(name)) continue;
                try
                {
                    var info = new FileInfo(CachePath(name));
                    var usedAt = new DateTimeOffset(info.LastAccessTimeUtc > info.LastWriteTimeUtc
                        ? info.LastAccessTimeUtc : info.LastWriteTimeUtc);
                    if (IsTemporaryFile(name) ? now - info.LastWriteTimeUtc > TimeSpan.FromDays(1) : now - usedAt > Retention)
                    {
                        File.Delete(CachePath(name));
                        entries.Remove(name);
                        removed++;
                    }
                    else if (IsCacheFile(name)) retained.Add((info, usedAt));
                }
                catch (Exception exception) when (IsCacheIoFailure(exception))
                {
                    logger?.LogDebug(exception, "Minecraft metadata cache entry could not be cleaned. CacheKey={CacheKey}", name);
                }
            }
            var total = retained.Sum(item => item.File.Length);
            var protectedPath = CachePath(protectedKey);
            if (File.Exists(protectedPath)) total += new FileInfo(protectedPath).Length;
            foreach (var item in retained.OrderBy(item => item.UsedAt))
            {
                token.ThrowIfCancellationRequested();
                if (total <= (maximumDiskBytes ?? MaximumDiskBytes)) break;
                try
                {
                    var length = item.File.Length;
                    File.Delete(CachePath(item.File.Name));
                    entries.Remove(item.File.Name);
                    total -= length;
                    removed++;
                }
                catch (Exception exception) when (IsCacheIoFailure(exception))
                {
                    logger?.LogDebug(exception, "Minecraft metadata cache entry could not be cleaned. CacheKey={CacheKey}", item.File.Name);
                }
            }
        }
        catch (Exception exception) when (IsCacheIoFailure(exception))
        {
            logger?.LogDebug(exception, "Minecraft metadata cache cleanup could not be completed.");
        }
        if (removed > 0) logger?.LogInformation("Minecraft metadata cache cleanup completed. RemovedFiles={RemovedFiles}", removed);
    }

    private void TouchVersion(string key, ILogger? logger)
    {
        if (directory is null || !key.StartsWith("version-", StringComparison.Ordinal)) return;
        try
        {
            var path = CachePath(key);
            if (File.Exists(path)) File.SetLastAccessTimeUtc(path, clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception exception) when (IsCacheIoFailure(exception))
        {
            logger?.LogDebug(exception, "Minecraft metadata cache access time could not be updated. CacheKey={CacheKey}", key);
        }
    }

    private static bool IsCacheFile(string name) =>
        name.StartsWith("version-", StringComparison.Ordinal) && name.Length == 53
            && name.EndsWith(".json", StringComparison.Ordinal) && name.AsSpan(8, 40).ToString().All(Uri.IsHexDigit)
        || name.StartsWith("manifest-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
            && Enum.TryParse<DownloadSourcePreference>(name[9..^5], out var preference)
            && name == $"manifest-{preference}.json" && Enum.IsDefined(preference);

    private static bool IsTemporaryFile(string name) => name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal)
        && name.Length > 38 && Guid.TryParseExact(name.AsSpan(name.Length - 36, 32), "N", out _)
        && name[^37] == '.' && IsCacheFile(name[1..^37]);

    private static bool IsCacheIoFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidDataException;

    private string CachePath(string key) => MinecraftPathGuard.EnsureSafeFileDestination(
        Path.Combine(directory!, key), managedRoot ?? directory!, "Minecraft metadata cache");

    private bool IsFresh(DateTimeOffset writtenAt, TimeSpan? lifetime) => lifetime is null
        || clock.GetUtcNow() >= writtenAt && clock.GetUtcNow() - writtenAt < lifetime;

    private void Remember(string key, byte[] bytes, DateTimeOffset writtenAt)
    {
        if (entries.Count >= 64) entries.Remove(entries.Keys.First());
        entries[key] = new(bytes, writtenAt);
    }

    private sealed record Entry(byte[] Bytes, DateTimeOffset WrittenAt);
}
