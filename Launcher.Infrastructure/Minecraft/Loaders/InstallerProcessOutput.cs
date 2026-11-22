/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class InstallerProcessException(int exitCode, bool unsupportedInstallClient, string stdout, string stderr, string? firstError = null)
    : InvalidOperationException("The loader installer process failed."), ISafeDiagnosticException
{
    public bool UnsupportedInstallClient { get; } = unsupportedInstallClient;
    public string DiagnosticCode => "LoaderInstallerExit";
    public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>
    {
        ["ExitCode"] = exitCode, ["UnsupportedInstallClient"] = UnsupportedInstallClient,
        ["FirstInstallerError"] = firstError is null ? null : BoundTail(firstError),
        ["StdoutTail"] = BoundTail(stdout), ["StderrTail"] = BoundTail(stderr)
    };

    internal static string BoundTail(string value)
    {
        const int maxSerializedBytes = 12 * 1024;
        const string marker = "[tail truncated] ";
        // Spaces remain visible after JSON escaping and keep the final log
        // redactor from treating the next line as part of a credential value.
        var safe = SensitiveLogText.Redact(value).Replace("\r\n", " \n ").Replace("\r", " \n ").Replace("\n", " \n ");
        if (value.Length <= maxSerializedBytes && Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe)) <= maxSerializedBytes)
            return safe;
        var low = 0;
        var high = safe.Length;
        while (low < high)
        {
            var length = low + (high - low + 1) / 2;
            var candidate = marker + safe[^length..];
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(candidate)) <= maxSerializedBytes) low = length;
            else high = length - 1;
        }
        var start = safe.Length - low;
        if (start < safe.Length && char.IsLowSurrogate(safe[start])) start++;
        return marker + safe[start..];
    }
}

internal sealed record InstallerProcessResult(int ProcessId, string Stdout, string Stderr, string? FirstError)
{
    public void LogAfterFailure(ILogger logger) => logger.LogWarning(
        "Loader installation failed after the installer exited successfully. ProcessId={ProcessId} ExitCode=0 FirstInstallerError={FirstInstallerError} StdoutTail={StdoutTail} StderrTail={StderrTail}",
        ProcessId, FirstError is null ? null : InstallerProcessException.BoundTail(FirstError),
        InstallerProcessException.BoundTail(Stdout), InstallerProcessException.BoundTail(Stderr));
}

/// <summary>Bounded, streaming capture. Each pipe owns its redaction state.</summary>
internal sealed class InstallerProcessOutput(ILogger? logger, int processId, IReadOnlyList<string>? sensitiveValues = null)
{
    private const int MaximumLineBytes = 16 * 1024;
    private const int TailLineLimit = 100;
    private long outputLines;
    private long lastOutput = Stopwatch.GetTimestamp();
    private int unsupported;
    private string? phase;
    private string? firstError;
    public bool UnsupportedInstallClient => Volatile.Read(ref unsupported) != 0;
    public TimeSpan SinceLastOutput => Stopwatch.GetElapsedTime(Volatile.Read(ref lastOutput));
    public long OutputLines => Interlocked.Read(ref outputLines);
    public string? Phase => Volatile.Read(ref phase);
    public string? FirstError => Volatile.Read(ref firstError);

    public async Task<string> PumpAsync(StreamReader reader, string stream)
    {
        var redactor = new SensitiveLogText.LineRedactor(sensitiveValues ?? []);
        var tail = new Queue<string>();
        var line = new StringBuilder();
        var buffer = new char[4096];
        var lineBytes = 0;
        var truncated = false;
        var tailTruncated = false;
        var suppressionReported = false;
        var previousCr = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var character = buffer[i];
                if (character is '\r' or '\n')
                {
                    if (character != '\n' || !previousCr) Emit();
                    previousCr = character == '\r';
                    continue;
                }
                previousCr = false;
                var characterBytes = character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                if (lineBytes + characterBytes <= MaximumLineBytes)
                {
                    line.Append(character);
                    lineBytes += characterBytes;
                }
                else truncated = true;
            }
        }
        if (line.Length > 0 || truncated) Emit();
        var output = string.Join(Environment.NewLine, tail);
        return tailTruncated ? "[earlier output omitted]" + Environment.NewLine + output : output;

        void Emit()
        {
            var raw = line.ToString();
            if (raw.Contains(ForgeInstallerRunner.InstallClientUnrecognizedOptionMessage, StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref unsupported, 1);
            if (truncated)
            {
                redactor.SuppressRemainder();
                if (!suppressionReported)
                    logger?.LogWarning("Loader installer output truncated; remaining stream suppressed for redaction safety. ProcessId={ProcessId} Stream={Stream} LineLimit={LineLimit}", processId, stream, MaximumLineBytes);
                suppressionReported = true;
            }
            var safe = redactor.RedactLine(raw);
            Volatile.Write(ref lastOutput, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref outputLines);
            RecordMilestone(safe.Trim(), stream);
            tail.Enqueue(safe);
            if (tail.Count > TailLineLimit)
            {
                tail.Dequeue();
                tailTruncated = true;
            }
            line.Clear();
            lineBytes = 0;
            truncated = false;
        }
    }

    private void RecordMilestone(string line, string stream)
    {
        var nextPhase = line switch
        {
            "Extracting json" or "Extracting JSON" => "ExtractingProfile",
            "Downloading libraries" => "DownloadingLibraries",
            "Building Processors" => "BuildingProcessors",
            "Task: DOWNLOAD_MOJMAPS" => "DownloadingMappings",
            "Task: MERGE_MAPPING" => "MergingMappings",
            "Remapping final jar" or "Remapping jar... 50%" => "RemappingJar",
            _ when line.StartsWith("Patching ", StringComparison.Ordinal) => "PatchingJar",
            "Processing Complete" => "ProcessingComplete",
            "Injecting profile" => "InjectingProfile",
            _ => null
        };
        if (nextPhase is not null && Interlocked.Exchange(ref phase, nextPhase) != nextPhase)
            logger?.LogInformation("Loader installer phase changed. ProcessId={ProcessId} Phase={InstallerPhase}", processId, nextPhase);

        if (line.StartsWith("Exception in thread", StringComparison.Ordinal)
            || line.StartsWith("Caused by:", StringComparison.Ordinal)
            || line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.CompareExchange(ref firstError, line, null) is null)
                logger?.LogWarning("Loader installer reported an error. ProcessId={ProcessId} Stream={Stream} InstallerError={InstallerError}",
                    processId, stream, InstallerProcessException.BoundTail(line));
        }
    }
}
