/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Per launcher/install operation state. It deliberately never outlives the
/// launcher instance and does not classify downloads from their URLs.
/// </summary>
internal sealed class MinecraftDownloadOperationContext : IDisposable
{
    private readonly ConcurrentDictionary<string, AssetIdentity> assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MinecraftFileVerificationSnapshot> verifiedAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> assetLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] managedRoots;

    public MinecraftDownloadOperationContext(string managedRoot)
        : this([managedRoot])
    {
    }

    public MinecraftDownloadOperationContext(IEnumerable<string> managedRoots)
    {
        ArgumentNullException.ThrowIfNull(managedRoots);
        var normalizedRoots = managedRoots
            .Select(root => string.IsNullOrWhiteSpace(root)
                ? throw new ArgumentException("A managed download root cannot be empty.", nameof(managedRoots))
                : Path.GetFullPath(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedRoots.Length == 0)
            throw new ArgumentException("At least one managed download root is required.", nameof(managedRoots));

        ManagedRoot = normalizedRoots[0];
        this.managedRoots = normalizedRoots
            .OrderByDescending(root => root.Length)
            .ToArray();
    }

    public string ManagedRoot { get; }
    internal DownloadHostHealthTracker HostHealth { get; } = new();

    public string ResolveManagedRoot(string destinationPath)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);
        foreach (var root in managedRoots)
        {
            if (MinecraftPathGuard.IsWithin(normalizedDestination, root))
                return root;
        }

        throw new InvalidDataException(
            $"Managed download escaped every allowed directory: {destinationPath}");
    }

    public void Dispose()
    {
        HostHealth.Clear();
    }

    public void RegisterAsset(string destinationPath, string sha1, long? size)
    {
        var normalized = Path.GetFullPath(destinationPath);
        var identity = new AssetIdentity(sha1, size);
        if (assets.TryGetValue(normalized, out var existing) && existing != identity)
            throw SafeExceptionLog.Describe(new InvalidDataException("Conflicting asset identities resolved to the same destination path."), "DownloadValidation", ("Reason", "Conflicting asset identities resolved to the same destination path."));
        assets[normalized] = identity;
    }

    public bool TryGetAsset(string destinationPath, string? sha1, long? size)
    {
        var normalized = Path.GetFullPath(destinationPath);
        return assets.TryGetValue(normalized, out var expected)
            && string.Equals(expected.Sha1, sha1, StringComparison.OrdinalIgnoreCase)
            && expected.Size == size;
    }

    public bool IsVerified(string destinationPath, DownloadIntegrityExpectation integrity)
    {
        using var lease = AcquireVerifiedFileLease(destinationPath, integrity);
        return lease is not null;
    }

    public void MarkVerified(string destinationPath, DownloadIntegrityExpectation integrity)
    {
        var key = CreateVerificationKey(destinationPath, integrity);
        if (WindowsFileSnapshot.TryCapture(destinationPath, out var snapshot))
            verifiedAssets[key] = snapshot;
        else
            verifiedAssets.TryRemove(key, out _);
    }

    public MinecraftVerifiedFileLease? AcquireVerifiedFileLease(
        string destinationPath,
        DownloadIntegrityExpectation integrity)
    {
        var key = CreateVerificationKey(destinationPath, integrity);
        if (!verifiedAssets.TryGetValue(key, out var expectedSnapshot))
            return null;

        var lease = WindowsFileSnapshot.TryAcquire(destinationPath);
        if (lease is not null && lease.Snapshot == expectedSnapshot)
            return lease;

        lease?.Dispose();
        verifiedAssets.TryRemove(key, out _);
        return null;
    }

    public async ValueTask<IDisposable> AcquireAssetLockAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var normalized = Path.GetFullPath(destinationPath);
        var semaphore = assetLocks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreLease(semaphore);
    }

    private static string CreateVerificationKey(string destinationPath, DownloadIntegrityExpectation integrity) =>
        $"{Path.GetFullPath(destinationPath)}|{integrity.ExpectedSize}|{integrity.Fingerprint}";

    private sealed record AssetIdentity(string Sha1, long? Size);

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref semaphore, null)?.Release();
    }
}
