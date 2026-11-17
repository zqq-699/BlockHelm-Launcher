/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Security.Cryptography;
using System.IO;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class DownloadIntegrityExpectation
{
    private readonly IReadOnlyDictionary<HashAlgorithmName, byte[]> requiredHashes;

    public DownloadIntegrityExpectation(
        long? expectedSize,
        IEnumerable<(HashAlgorithmName Algorithm, string Value)> hashes)
        : this(expectedSize, hashes, allowMissingHashes: false)
    {
    }

    private DownloadIntegrityExpectation(
        long? expectedSize,
        IEnumerable<(HashAlgorithmName Algorithm, string Value)> hashes,
        bool allowMissingHashes)
    {
        ExpectedSize = expectedSize;
        requiredHashes = hashes
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .ToDictionary(value => value.Algorithm, value => Convert.FromHexString(value.Value.Trim()));
        if (!allowMissingHashes && requiredHashes.Count == 0)
            throw SafeExceptionLog.Describe(new InvalidDataException("A resumable download requires at least one expected hash."), "DownloadValidation", ("Reason", "A resumable download requires at least one expected hash."));
    }

    public long? ExpectedSize { get; }
    public bool IsVerifiable => requiredHashes.Count > 0;
    public bool HasStrongHash => requiredHashes.Keys.Any(IsStrong);
    public string Fingerprint => string.Join(
        ";",
        requiredHashes.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key.Name}:{Convert.ToHexString(pair.Value)}"));

    public static DownloadIntegrityExpectation Sha1(string value, long? expectedSize) =>
        new(expectedSize, [(HashAlgorithmName.SHA1, value)]);

    public static DownloadIntegrityExpectation Unverified() =>
        new(expectedSize: null, hashes: [], allowMissingHashes: true);

    public IReadOnlyDictionary<HashAlgorithmName, IncrementalHash> CreateHashers() =>
        requiredHashes.Keys.ToDictionary(algorithm => algorithm, IncrementalHash.CreateHash);

    private IReadOnlyDictionary<string, object?> lastFailure = new Dictionary<string, object?>();
    public T DescribeFailure<T>(T exception) where T : Exception => SafeExceptionLog.Describe(exception, "FileIntegrity", lastFailure.Select(pair => (pair.Key, pair.Value)).ToArray());

    public bool Verify(IReadOnlyDictionary<HashAlgorithmName, IncrementalHash> hashers)
    {
        var valid = true;
        foreach (var pair in requiredHashes)
        {
            var actual = hashers.TryGetValue(pair.Key, out var hasher) ? hasher.GetHashAndReset() : null;
            if (actual is null || !CryptographicOperations.FixedTimeEquals(actual, pair.Value))
            {
                lastFailure = new Dictionary<string, object?> { ["HashAlgorithm"] = pair.Key.Name,
                    ["ExpectedHash"] = Convert.ToHexString(pair.Value), ["ActualHash"] = actual is null ? "NotChecked" : Convert.ToHexString(actual) };
                valid = false;
            }
        }
        return valid;
    }

    public async Task<bool> VerifyFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsVerifiable)
            return false;
        if (ExpectedSize.HasValue && new FileInfo(path).Length != ExpectedSize.Value)
        {
            lastFailure = new Dictionary<string, object?> { ["Artifact"] = path, ["ExpectedSize"] = ExpectedSize, ["ActualSize"] = new FileInfo(path).Length };
            return false;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hashers = CreateHashers();
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                foreach (var hasher in hashers.Values)
                    hasher.AppendData(buffer, 0, read);
            }
            return Verify(hashers);
        }
        finally
        {
            foreach (var hasher in hashers.Values)
                hasher.Dispose();
        }
    }

    public bool VerifyFile(string path, CancellationToken cancellationToken)
    {
        if (!IsVerifiable)
            return false;
        if (ExpectedSize.HasValue && new FileInfo(path).Length != ExpectedSize.Value)
        {
            lastFailure = new Dictionary<string, object?> { ["Artifact"] = path, ["ExpectedSize"] = ExpectedSize, ["ActualSize"] = new FileInfo(path).Length };
            return false;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        var hashers = CreateHashers();
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                foreach (var hasher in hashers.Values)
                    hasher.AppendData(buffer, 0, read);
            }
            return Verify(hashers);
        }
        finally
        {
            foreach (var hasher in hashers.Values)
                hasher.Dispose();
        }
    }

    private static bool IsStrong(HashAlgorithmName algorithm) => algorithm.Name is "SHA1" or "SHA256" or "SHA512";
}
