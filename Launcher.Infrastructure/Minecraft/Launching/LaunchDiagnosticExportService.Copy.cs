/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Launcher.Application.Services;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class LaunchDiagnosticExportService
{
private static async Task<ExportOutcome> TryAddDiagnosticAsync(
        ZipArchive archive,
        LaunchDiagnosticReference diagnostic,
        string entryName,
        string stagingDirectory,
        LaunchDiagnosticExportLimits limits,
        long remainingTotalBytes,
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(stagingDirectory, $".launch-diagnostic-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!TryValidateSafePath(diagnostic.Path, out var validationReason))
                return CreateSkippedOutcome(diagnostic, validationReason);
            if (remainingTotalBytes <= 0)
                return CreateSkippedOutcome(diagnostic, "total-size-limit");

            long initialLength;
            DateTime initialLastWriteTimeUtc;
            DateTime initialCreationTimeUtc;
            WindowsFileIdentity initialIdentity;
            try
            {
                await using var source = new FileStream(
                    diagnostic.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    81920,
                    useAsync: true);
                if (!TryValidateSafePath(diagnostic.Path, out validationReason))
                    return CreateSkippedOutcome(diagnostic, validationReason);

                initialLength = source.Length;
                initialLastWriteTimeUtc = File.GetLastWriteTimeUtc(diagnostic.Path);
                initialCreationTimeUtc = File.GetCreationTimeUtc(diagnostic.Path);
                if (!TryGetFileIdentity(source.SafeFileHandle, out initialIdentity))
                    return CreateSkippedOutcome(diagnostic, "io-error");
                if (initialLength > limits.MaxSourceFileBytes)
                    return CreateSkippedOutcome(diagnostic, "file-too-large");
                if (initialLength > remainingTotalBytes)
                    return CreateSkippedOutcome(diagnostic, "total-size-limit");

                await using var staging = new FileStream(
                    stagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);
                await CopyRedactedTextAsync(source, staging, sensitiveValues, cancellationToken);
                await using var verificationSource = new FileStream(
                    diagnostic.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1,
                    useAsync: false);
                if (source.Length != initialLength
                    || File.GetLastWriteTimeUtc(diagnostic.Path) != initialLastWriteTimeUtc
                    || File.GetCreationTimeUtc(diagnostic.Path) != initialCreationTimeUtc
                    || !TryGetFileIdentity(verificationSource.SafeFileHandle, out var currentIdentity)
                    || currentIdentity != initialIdentity
                    || !TryValidateSafePath(diagnostic.Path, out validationReason))
                {
                    return CreateSkippedOutcome(
                        diagnostic,
                        validationReason == "unsafe-reparse-point" ? validationReason : "source-changed");
                }
            }
            catch (FileNotFoundException)
            {
                return CreateSkippedOutcome(diagnostic, "missing");
            }
            catch (DirectoryNotFoundException)
            {
                return CreateSkippedOutcome(diagnostic, "missing");
            }
            catch (UnauthorizedAccessException)
            {
                return CreateSkippedOutcome(diagnostic, "access-denied");
            }
            catch (IOException)
            {
                return CreateSkippedOutcome(diagnostic, "io-error");
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var stagedSource = new FileStream(
                stagingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            await using var destination = entry.Open();
            await stagedSource.CopyToAsync(destination, cancellationToken);
            return new ExportOutcome(
                diagnostic.Type,
                Path.GetFileName(diagnostic.Path),
                entryName,
                true,
                null,
                initialLength);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    private static async Task CopyRedactedTextAsync(
        Stream source,
        Stream destination,
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            source,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 81920,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 81920,
            leaveOpen: true);
        var buffer = new char[81920];
        var line = new StringBuilder();
        var redactor = new SensitiveLogText.LineRedactor(sensitiveValues);
        var previousWasCarriageReturn = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    if (character != '\n' || !previousWasCarriageReturn)
                        await writer.WriteAsync(redactor.RedactLine(line.ToString()));
                    line.Clear();
                    await writer.WriteAsync(character);
                    previousWasCarriageReturn = character == '\r';
                    continue;
                }

                previousWasCarriageReturn = false;
                line.Append(character);
            }
        }

        if (line.Length > 0)
            await writer.WriteAsync(redactor.RedactLine(line.ToString()));
        await writer.FlushAsync(cancellationToken);
    }
}
