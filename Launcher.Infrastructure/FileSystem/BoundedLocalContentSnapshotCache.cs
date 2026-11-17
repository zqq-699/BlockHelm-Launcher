/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 保留最近访问的少量本地资源快照。调用方负责通过自己的资源服务锁串行访问。
/// </summary>
internal sealed class BoundedLocalContentSnapshotCache<TIdentity, TItem>
    where TIdentity : notnull
{
    internal const int DefaultCapacity = 4;

    private readonly int capacity;
    private readonly Dictionary<string, CacheEntry> entries;
    private readonly LinkedList<string> recency = [];

    public BoundedLocalContentSnapshotCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
        entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    }

    internal int Count => entries.Count;

    public LocalContentSnapshot<TIdentity, TItem> GetOrCreate(string directory, string? ownerProof = null)
    {
        var key = NormalizeDirectory(directory);
        if (entries.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.OwnerProof, ownerProof, StringComparison.Ordinal))
            {
                Remove(directory);
                return GetOrCreate(directory, ownerProof);
            }
            Promote(existing);
            return existing.Snapshot;
        }

        if (entries.Count == capacity)
            RemoveLeastRecentlyUsed();

        var snapshot = new LocalContentSnapshot<TIdentity, TItem>();
        var node = recency.AddFirst(key);
        entries.Add(key, new CacheEntry(snapshot, node, ownerProof));
        return snapshot;
    }

    public bool TryGet(
        string directory,
        out LocalContentSnapshot<TIdentity, TItem> snapshot)
    {
        var key = NormalizeDirectory(directory);
        if (entries.TryGetValue(key, out var entry))
        {
            Promote(entry);
            snapshot = entry.Snapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public bool Remove(string directory)
    {
        var key = NormalizeDirectory(directory);
        if (!entries.Remove(key, out var entry))
            return false;

        recency.Remove(entry.Node);
        return true;
    }

    private static string NormalizeDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private void Promote(CacheEntry entry)
    {
        if (ReferenceEquals(recency.First, entry.Node))
            return;

        recency.Remove(entry.Node);
        recency.AddFirst(entry.Node);
    }

    private void RemoveLeastRecentlyUsed()
    {
        var node = recency.Last
            ?? throw new InvalidOperationException("Snapshot recency list is empty.");
        entries.Remove(node.Value);
        recency.RemoveLast();
    }

    private sealed record CacheEntry(
        LocalContentSnapshot<TIdentity, TItem> Snapshot,
        LinkedListNode<string> Node,
        string? OwnerProof);
}
