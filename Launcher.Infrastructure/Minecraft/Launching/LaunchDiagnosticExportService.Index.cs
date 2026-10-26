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
private static async Task WriteIndexAsync(
        ZipArchive archive,
        LaunchDiagnosticExportRequest request,
        IReadOnlyList<ExportOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("report-index.txt", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync($"CreatedAtUtc: {DateTimeOffset.UtcNow:O}");
        await writer.WriteLineAsync($"InstanceName: {LaunchDiagnosticRedactor.Redact(NormalizeIndexValue(request.InstanceName), request.SensitiveValues)}");
        await writer.WriteLineAsync($"VersionName: {LaunchDiagnosticRedactor.Redact(NormalizeIndexValue(request.VersionName), request.SensitiveValues)}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("[Diagnostics]");
        foreach (var outcome in outcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(LaunchDiagnosticRedactor.Redact(
                $"Type={outcome.Type}; Status={(outcome.IsExported ? "exported" : "skipped")}; Sanitization={(outcome.IsExported ? "pattern-redacted" : "none")}; FileName={NormalizeIndexValue(outcome.FileName)}; Entry={NormalizeIndexValue(outcome.EntryName ?? "none")}; Reason={NormalizeIndexValue(outcome.Reason ?? "none")}",
                request.SensitiveValues));
        }
    }

    private static ExportOutcome CreateSkippedOutcome(
        LaunchDiagnosticReference diagnostic,
        string reason)
    {
        return new ExportOutcome(
            diagnostic.Type,
            Path.GetFileName(diagnostic.Path),
            null,
            false,
            reason,
            0);
    }

    private static string ResolveUniqueEntryName(
        LaunchDiagnosticReference diagnostic,
        ISet<string> usedEntryNames)
    {
        var fileName = SanitizeFileName(Path.GetFileName(diagnostic.Path));
        var directory = diagnostic.Type switch
        {
            LaunchDiagnosticType.MinecraftCrashReport => "minecraft/crash-reports",
            LaunchDiagnosticType.JvmCrashReport => "jvm",
            LaunchDiagnosticType.MinecraftLatestLog => "minecraft/logs",
            LaunchDiagnosticType.CapturedOutput => "launcher/captured-output",
            _ => "launcher/diagnostics"
        };
        var candidate = $"{directory}/{fileName}";
        if (usedEntryNames.Add(candidate))
            return candidate;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            candidate = $"{directory}/{baseName} ({index}){extension}";
            if (usedEntryNames.Add(candidate))
                return candidate;
        }
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "diagnostic.log";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(fileName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "diagnostic.log" : sanitized;
    }

    private static string SafeFileName(string? path)
    {
        try
        {
            return SanitizeFileName(Path.GetFileName(path));
        }
        catch (ArgumentException)
        {
            return "diagnostic.log";
        }
    }
}
