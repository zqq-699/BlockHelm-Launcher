/*
 * BlockHelm Launcher
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Runtime.CompilerServices;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// Binds inventory objects to the physical directories and entries from which they were read.
/// These transient credentials never identify an instance or enter its settings.
/// </summary>
internal static class InstanceContentOwnership
{
    internal sealed record Owner(string Root, string Directory, string Proof);
    private readonly record struct EntryIdentity(string Proof, long Length, long LastWriteTime);
    private sealed record Receipt(Owner Owner, EntryIdentity Entry);
    private static readonly ConditionalWeakTable<object, Receipt> Owners = new();

    private static EntryIdentity ReadEntryIdentity(string path)
    {
        var proof = WindowsDirectoryHandleMover.CaptureProof(path);
        if (Directory.Exists(path)) return new EntryIdentity(proof, -1, -1);
        var info = new FileInfo(path);
        return new EntryIdentity(proof, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    internal static bool CanReuse(object item, string path)
    {
        if (!Owners.TryGetValue(item, out var receipt)) return true;
        try { return receipt.Entry == ReadEntryIdentity(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string GetItemPath(object item) => item switch
    {
        LocalMod mod => mod.FullPath,
        LocalSave save => save.FullPath,
        LocalResourcePack pack => pack.FullPath,
        LocalShaderPack shader => shader.FullPath,
        _ => throw new ArgumentException("Unsupported instance content item.", nameof(item))
    };

    internal static Scope Open(GameInstance instance, string directory, bool createDirectory = false)
    {
        var rootLease = InstanceDirectoryOwnership.AcquireReadLease(instance);
        IDisposable? contentLease = null;
        try
        {
            var root = Path.GetFullPath(instance.InstanceDirectory);
            directory = MinecraftPathGuard.EnsureWithin(directory, root, "Instance content directory");
            MinecraftPathGuard.EnsureNoReparsePoints(root, directory, "Instance content directory");
            if (createDirectory) MinecraftPathGuard.EnsureSafeDirectory(directory, root, "Instance content directory");
            if (System.IO.Directory.Exists(directory)) contentLease = WindowsDirectoryHandleMover.AcquireReadLease(directory);
            var proof = InstanceDirectoryOwnership.Capture(root) + "/" +
                (contentLease is null ? "missing" : InstanceDirectoryOwnership.Capture(directory));
            return new Scope(new Owner(root, directory, proof), rootLease, contentLease);
        }
        catch
        {
            contentLease?.Dispose();
            rootLease.Dispose();
            throw;
        }
    }

    internal static IDisposable? Acquire(object item, string path)
    {
        // Explicit caller-created file objects have no earlier snapshot to compare.
        if (!Owners.TryGetValue(item, out var receipt)) return null;
        var owner = receipt.Owner;
        Scope? scope = null;
        try
        {
            scope = Open(new GameInstance { InstanceDirectory = owner.Root }, owner.Directory);
            if (scope.CacheIdentity != owner.Proof || receipt.Entry != ReadEntryIdentity(path)) throw Conflict(owner);
            MinecraftPathGuard.EnsureNoReparsePoints(owner.Directory, path, "Instance content item");
            return scope;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            scope?.Dispose();
            throw Conflict(owner);
        }
        catch
        {
            scope?.Dispose();
            throw;
        }
    }

    private static GameInstanceMutationConflictException Conflict(Owner owner) => new(owner.Root, Path.GetFileName(owner.Root));

    internal sealed class Scope(Owner owner, IDisposable rootLease, IDisposable? contentLease) : IDisposable
    {
        internal string CacheIdentity => owner.Proof;
        internal bool IsCurrent
        {
            get
            {
                try
                {
                    return owner.Proof == InstanceDirectoryOwnership.Capture(owner.Root) + "/" +
                        (Directory.Exists(owner.Directory) ? InstanceDirectoryOwnership.Capture(owner.Directory) : "missing");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
            }
        }

        internal void EnsureCurrent()
        {
            if (!IsCurrent) throw Conflict(owner);
        }

        internal T Bind<T>(T item) where T : class
        {
            var identity = ReadEntryIdentity(GetItemPath(item));
            var original = Owners.GetValue(item, _ => new Receipt(owner, identity));
            if (original.Owner.Proof != owner.Proof || original.Entry != identity) throw Conflict(original.Owner);
            return item;
        }

        public void Dispose()
        {
            contentLease?.Dispose();
            rootLease.Dispose();
        }
    }

    internal static T Transfer<T>(object source, T target) where T : class
    {
        if (Owners.TryGetValue(source, out var owner)) Owners.GetValue(target, _ => owner);
        return target;
    }
}
