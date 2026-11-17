/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Infrastructure.FileSystem;

internal sealed class LocalContentSnapshot<TIdentity, TItem>
    where TIdentity : notnull
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TItem> Reconcile(
        IReadOnlyList<TIdentity> inventory,
        Func<TIdentity, string> pathSelector,
        Func<TIdentity, TItem> createItem,
        Func<TItem, bool>? canReuse = null)
    {
        var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var result = new TItem[inventory.Count];

        for (var index = 0; index < inventory.Count; index++)
        {
            var identity = inventory[index];
            var path = pathSelector(identity);
            if (entries.TryGetValue(path, out var previous)
                && EqualityComparer<TIdentity>.Default.Equals(previous.Identity, identity)
                && (canReuse?.Invoke(previous.Item) ?? true))
            {
                next[path] = previous;
                result[index] = previous.Item;
                continue;
            }

            var item = createItem(identity);
            next[path] = new Entry(identity, item);
            result[index] = item;
        }

        entries.Clear();
        foreach (var pair in next)
            entries.Add(pair.Key, pair.Value);
        return result;
    }

    public bool TryMove(
        string oldPath,
        string newPath,
        TIdentity newIdentity,
        Action<TItem> updateItem)
    {
        if (!entries.Remove(oldPath, out var entry))
            return false;

        updateItem(entry.Item);
        entries[newPath] = new Entry(newIdentity, entry.Item);
        return true;
    }

    private sealed record Entry(TIdentity Identity, TItem Item);
}

internal readonly record struct LocalContentFileIdentity(
    string FullPath,
    long Length,
    long LastWriteTimeUtcTicks,
    long CreationTimeUtcTicks);

internal readonly record struct LocalSaveIdentity(
    string FullPath,
    long CreationTimeUtcTicks,
    bool HasIcon,
    long IconLength,
    long IconLastWriteTimeUtcTicks);
