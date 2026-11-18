/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using SharpCompress.Common;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Readers;
using ZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record ProcessorOutputInspection(
    string RelativePath,
    long Size,
    string Sha1,
    GameFileVerificationLevel VerificationLevel);

internal static class ProcessorOutputInspector
{
    public static async Task<ProcessorOutputInspection> InspectAsync(
        string path, ForgeProcessorOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw Invalid(output.RelativePath, "Missing");

        // Keep the same file open for hashing and archive validation; writers must
        // not change the bytes that are accepted as this phase's output.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous);
        var size = stream.Length;
        if (size == 0)
            throw Invalid(output.RelativePath, "Empty");
        var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (MinecraftFileIntegrity.IsSha1(output.TrustedSha1)
            && string.Equals(sha1, output.TrustedSha1, StringComparison.OrdinalIgnoreCase))
        {
            // The trusted digest is sufficient; an additional archive parser
            // must not reject the installer's declared bytes for compatibility.
            return new ProcessorOutputInspection(output.RelativePath, size, sha1, GameFileVerificationLevel.HashVerified);
        }

        if (Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            stream.Position = 0;
            await ValidateArchiveAsync(stream, output.RelativePath, cancellationToken).ConfigureAwait(false);
        }

        return new ProcessorOutputInspection(output.RelativePath, size, sha1, GameFileVerificationLevel.SizeVerified);
    }

    private static async Task ValidateArchiveAsync(Stream stream, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            var hasContent = false;
            try
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entry.IsComplete || entry.IsEncrypted)
                        throw new InvalidDataException("Generated archive entry is incomplete or encrypted.");
                    using var content = entry.OpenEntryStream();
                    var crc = new CRC32();
                    long length = 0;
                    int read;
                    while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        length += read;
                        if (length > entry.Size)
                            throw new InvalidDataException("Generated archive entry exceeded its declared length.");
                        crc.SlurpBlock(buffer, 0, read);
                    }
                    if (length != entry.Size || unchecked((uint)crc.Crc32Result) != unchecked((uint)entry.Crc))
                        throw new InvalidDataException("Generated archive entry length or CRC does not match.");
                    hasContent |= length > 0;
                }
                if (!hasContent)
                    throw new InvalidDataException("Generated archive contains no file content.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArchiveException or NotSupportedException)
        {
            throw Invalid(relativePath, "InvalidArchive", exception);
        }
    }

    private static InvalidDataException Invalid(string relativePath, string status, Exception? inner = null) =>
        SafeExceptionLog.Describe(new InvalidDataException("Loader processor output is missing or invalid.", inner),
            "ProcessorOutputInvalid", ("Artifact", relativePath), ("IntegrityStatus", status));
}
