/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Buffers;
using System.IO;
using System.Security.Cryptography;

using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal static class AtomicSharedFilePublisher
{
    private const int BufferSize = 128 * 1024;

    public static async Task<string> PublishCopyAsync(
        string sourcePath,
        string destinationPath,
        string? expectedSha1,
        CancellationToken cancellationToken = default,
        string? managedRoot = null, ILogger? logger = null)
    {
        var result = await PublishCoreAsync(
            sourcePath,
            destinationPath,
            expectedSha1,
            replaceExisting: false,
            cancellationToken,
            managedRoot, logger).ConfigureAwait(false);
        return result.Sha1;
    }

    public static Task<SharedFilePublishResult> PublishVerifiedReplacementAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha1,
        CancellationToken cancellationToken = default,
        string? managedRoot = null, ILogger? logger = null)
    {
        if (!MinecraftFileIntegrity.IsSha1(expectedSha1))
            throw new ArgumentException("A valid trusted SHA-1 is required when replacing shared content.", nameof(expectedSha1));

        return PublishCoreAsync(
            sourcePath,
            destinationPath,
            expectedSha1,
            replaceExisting: true,
            cancellationToken,
            managedRoot, logger);
    }

    private static async Task<SharedFilePublishResult> PublishCoreAsync(
        string sourcePath,
        string destinationPath,
        string? expectedSha1,
        bool replaceExisting,
        CancellationToken cancellationToken,
        string? managedRoot, ILogger? logger)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
            throw SafeExceptionLog.Describe(new FileNotFoundException("The shared runtime source file is missing.", sourcePath), "SharedSourceMissing", ("Artifact", sourcePath));
        if (!string.IsNullOrWhiteSpace(managedRoot))
            MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, managedRoot, "Shared runtime destination");

        var destinationExisted = File.Exists(destinationPath);
        if (destinationExisted)
        {
            if (!string.IsNullOrWhiteSpace(managedRoot))
                MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, managedRoot, "Shared runtime destination");
            if (!replaceExisting)
            {
                var comparison = await CompareFilesAsync(sourcePath, destinationPath, expectedSha1, cancellationToken).ConfigureAwait(false);
                if (comparison.Matches)
                    return new SharedFilePublishResult(comparison.FirstSha1!, SharedFilePublishDisposition.AlreadyMatched);
                throw comparison.CreateConflict($"Shared runtime destination contains different content: {destinationPath}", sourcePath, destinationPath);
            }
        }

        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw SafeExceptionLog.Describe(new IOException($"Shared runtime destination has no parent directory: {destinationPath}"), "SharedDestinationInvalid", ("Artifact", destinationPath));
        if (string.IsNullOrWhiteSpace(managedRoot))
        {
            Directory.CreateDirectory(parent);
        }
        else
        {
            MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, managedRoot, "Shared runtime destination");
            MinecraftPathGuard.EnsureSafeDirectory(parent, managedRoot, "Shared runtime directory");
            MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, managedRoot, "Shared runtime destination");
        }
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        var published = false;

        void EnsureSafe()
        {
            if (string.IsNullOrWhiteSpace(managedRoot))
                return;
            MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, managedRoot, "Shared runtime destination");
            MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, temporaryPath, "Shared runtime temporary file");
        }

        try
        {
            EnsureSafe();
            var actualSha1 = await CopyAndFlushAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedSha1)
                && !string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw SafeExceptionLog.Describe(new InvalidDataException($"Shared runtime source SHA-1 did not match for {sourcePath}."), "SharedSourceHashMismatch", ("Artifact", sourcePath), ("HashAlgorithm", "SHA1"), ("ExpectedHash", expectedSha1), ("ActualHash", actualSha1));
            }

            if (replaceExisting)
            {
                EnsureSafe();
                if (File.Exists(destinationPath)
                    && (await CompareFilesAsync(temporaryPath, destinationPath, actualSha1, cancellationToken).ConfigureAwait(false)).Matches)
                {
                    return new SharedFilePublishResult(actualSha1, SharedFilePublishDisposition.AlreadyMatched);
                }

                var replacesExisting = File.Exists(destinationPath);
                if (replacesExisting
                    && (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw SafeExceptionLog.Describe(new InvalidDataException($"Shared runtime destination is a reparse point: {destinationPath}"), "SharedDestinationReparsePoint", ("Artifact", destinationPath));
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
                published = true;
                return new SharedFilePublishResult(
                    actualSha1,
                    replacesExisting
                        ? SharedFilePublishDisposition.Replaced
                        : SharedFilePublishDisposition.Created);
            }
            else
            {
                EnsureSafe();
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                    published = true;
                    return new SharedFilePublishResult(actualSha1, SharedFilePublishDisposition.Created);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    var comparison = await CompareFilesAsync(temporaryPath, destinationPath, actualSha1, cancellationToken).ConfigureAwait(false);
                    if (!comparison.Matches)
                        throw comparison.CreateConflict($"Concurrent shared runtime publication produced different content: {destinationPath}", temporaryPath, destinationPath);
                    return new SharedFilePublishResult(actualSha1, SharedFilePublishDisposition.AlreadyMatched);
                }
            }
        }

        finally
        {
            if (!published)
                TryDelete(temporaryPath, managedRoot, logger);
        }
    }

    public static string ComputeSha1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA1.HashData(stream));
    }

    private static async Task<string> CopyAndFlushAsync(
        string sourcePath,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sha1.AppendData(buffer, 0, read);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return Convert.ToHexString(sha1.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<SharedFileComparison> CompareFilesAsync(
        string firstPath,
        string secondPath,
        string? knownFirstSha1,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (first.Length != second.Length)
            return new(false, first.Length, second.Length, knownFirstSha1, null);

        var firstSha1 = string.IsNullOrWhiteSpace(knownFirstSha1)
            ? await Task.Run(() => ComputeSha1(firstPath), cancellationToken).ConfigureAwait(false)
            : knownFirstSha1;
        var secondSha1 = await Task.Run(() => ComputeSha1(secondPath), cancellationToken).ConfigureAwait(false);
        return new(string.Equals(firstSha1, secondSha1, StringComparison.OrdinalIgnoreCase), first.Length, second.Length, firstSha1, secondSha1);
    }

    private sealed record SharedFileComparison(bool Matches, long FirstSize, long SecondSize, string? FirstSha1, string? SecondSha1)
    {
        public IOException CreateConflict(string message, string source, string destination) => SafeExceptionLog.Describe(
            new IOException(message), "SharedPublicationConflict", ("Source", source), ("Artifact", destination),
            ("ExpectedBytes", FirstSize), ("ActualBytes", SecondSize), ("HashAlgorithm", "SHA1"),
            ("ExpectedHash", FirstSha1 ?? "NotChecked"), ("ActualHash", SecondSha1 ?? "NotChecked"));
    }

    private static void TryDelete(string path, string? managedRoot, ILogger? logger)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(managedRoot))
                MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, path, "Shared runtime cleanup");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Shared publication temporary file cleanup failed. Artifact={Artifact} Stage=Cleanup", path);
        }
    }
}

internal sealed record SharedFilePublishResult(string Sha1, SharedFilePublishDisposition Disposition);

internal enum SharedFilePublishDisposition
{
    Created,
    AlreadyMatched,
    Replaced
}
