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

using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 在本地资源补全流程之间共享文件指纹，并将同一文件身份的并发计算合并为一个任务。
/// </summary>
public sealed class LocalFileFingerprintService
{
    private const int BufferSize = 81920;
    private const int MaximumCachedEntries = 4096;
    private const int TargetCachedEntries = 3072;

    private readonly object cacheLock = new();
    private readonly Dictionary<string, FingerprintCacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, Stream> openRead;

    public LocalFileFingerprintService()
        : this(OpenFile)
    {
    }

    internal LocalFileFingerprintService(Func<string, Stream> openRead)
    {
        this.openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
    }

    internal async Task<LocalFileFingerprint> GetFingerprintAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var identity = CreateIdentity(path);
        FingerprintCacheEntry entry;
        lock (cacheLock)
        {
            if (!entries.TryGetValue(identity.StablePath, out entry!)
                || !entry.Identity.Matches(identity)
                || entry.Computation.IsFaulted
                || entry.Computation.IsCanceled
                || entry.WaiterCount == 0 && !entry.Computation.IsCompleted)
            {
                entry = new FingerprintCacheEntry(identity, ComputeFingerprintAsync);
                entries[identity.StablePath] = entry;
                TrimCacheLocked();
            }

            entry.WaiterCount++;
            entry.LastUsedAtUtc = DateTimeOffset.UtcNow;
        }

        try
        {
            // 调用方只取消自己的等待，不取消共享计算，避免一个隐藏分区中断另一个仍可见分区的指纹任务。
            return await entry.Computation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            lock (cacheLock)
            {
                if (entries.TryGetValue(identity.StablePath, out var current)
                    && ReferenceEquals(current, entry))
                {
                    entries.Remove(identity.StablePath);
                }
            }

            throw;
        }
        finally
        {
            lock (cacheLock)
            {
                entry.WaiterCount--;
                if (entry.WaiterCount == 0 && !entry.Computation.IsCompleted)
                    entry.Cancel();
            }
        }
    }

    internal Task<LocalFileFingerprint> GetFreshFingerprintAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var identity = CreateIdentity(path);
        return ComputeFingerprintAsync(identity.ReadPath, cancellationToken);
    }

    internal void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var fullPath = Path.GetFullPath(path);
        var stablePath = fullPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? fullPath[..^".disabled".Length]
            : fullPath;
        lock (cacheLock)
            entries.Remove(stablePath);
    }

    private async Task<LocalFileFingerprint> ComputeFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = openRead(path);
        if (!stream.CanSeek)
            throw new NotSupportedException("Local resource fingerprint streams must support seeking.");

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long filteredLength = 0;
            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                sha1.AppendData(buffer, 0, read);
                for (var index = 0; index < read; index++)
                {
                    if (!IsCurseForgeWhitespace(buffer[index]))
                        filteredLength++;
                }
            }

            var sha1Text = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            stream.Seek(0, SeekOrigin.Begin);
            var curseForgeFingerprint = await ComputeCurseForgeMurmurHash2Async(
                    stream,
                    filteredLength,
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            return new LocalFileFingerprint(sha1Text, curseForgeFingerprint);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<long> ComputeCurseForgeMurmurHash2Async(
        Stream stream,
        long filteredLength,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        const uint seed = 1;
        const uint m = 0x5bd1e995;
        const int r = 24;

        var hash = seed ^ unchecked((uint)filteredLength);
        var pending = 0u;
        var pendingCount = 0;
        while (true)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (IsCurseForgeWhitespace(value))
                    continue;

                pending |= (uint)value << (pendingCount * 8);
                pendingCount++;
                if (pendingCount != 4)
                    continue;

                var block = pending * m;
                block ^= block >> r;
                block *= m;
                hash *= m;
                hash ^= block;
                pending = 0;
                pendingCount = 0;
            }
        }

        if (pendingCount > 0)
        {
            hash ^= pending;
            hash *= m;
        }

        hash ^= hash >> 13;
        hash *= m;
        hash ^= hash >> 15;
        return hash;
    }

    private void TrimCacheLocked()
    {
        if (entries.Count <= MaximumCachedEntries)
            return;

        foreach (var pair in entries
                     .Where(pair => pair.Value.Computation.IsCompleted)
                     .OrderBy(pair => File.Exists(pair.Value.Identity.ReadPath))
                     .ThenBy(pair => pair.Value.LastUsedAtUtc)
                     .Take(entries.Count - TargetCachedEntries)
                     .ToArray())
        {
            entries.Remove(pair.Key);
        }
    }

    private static LocalFileFingerprintIdentity CreateIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("Local resource file was not found.", path);

        var fullPath = file.FullName;
        var stablePath = fullPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? fullPath[..^".disabled".Length]
            : fullPath;
        return new LocalFileFingerprintIdentity(
            Path.GetFullPath(stablePath),
            fullPath,
            file.Length,
            file.LastWriteTimeUtc.Ticks);
    }

    private static FileStream OpenFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        BufferSize,
        useAsync: true);

    private static bool IsCurseForgeWhitespace(byte value) => value is 0x09 or 0x0a or 0x0d or 0x20;

    private sealed record LocalFileFingerprintIdentity(
        string StablePath,
        string ReadPath,
        long Length,
        long LastWriteTimeUtcTicks)
    {
        public bool Matches(LocalFileFingerprintIdentity other) =>
            string.Equals(StablePath, other.StablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadPath, other.ReadPath, StringComparison.OrdinalIgnoreCase)
            && Length == other.Length
            && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
    }

    private sealed class FingerprintCacheEntry
    {
        private readonly CancellationTokenSource cancellation = new();

        public FingerprintCacheEntry(
            LocalFileFingerprintIdentity identity,
            Func<string, CancellationToken, Task<LocalFileFingerprint>> compute)
        {
            Identity = identity;
            Computation = compute(identity.ReadPath, cancellation.Token);
            _ = Computation.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public LocalFileFingerprintIdentity Identity { get; }

        public Task<LocalFileFingerprint> Computation { get; }

        public DateTimeOffset LastUsedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public int WaiterCount { get; set; }

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

internal sealed record LocalFileFingerprint(
    string Sha1,
    long CurseForgeFingerprint);
