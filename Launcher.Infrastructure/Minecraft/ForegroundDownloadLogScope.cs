/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Records the user-visible start and terminal result of one file in a foreground operation.
/// Attempt-level transport details remain owned by <see cref="MinecraftDownloadRequestExecutor"/>.
/// </summary>
internal sealed class ForegroundDownloadLogScope
{
    private readonly IReadOnlyDictionary<string, object?> context;
    public IDisposable RestoreContext() => DownloadDiagnostics.Restore(logger, context);
    private readonly object progressLock = new();
    private readonly ILogger logger;
    private readonly string operation;
    private readonly string fileName;
    private readonly string destinationPath;
    private readonly long? expectedBytes;
    private readonly int? position;
    private readonly int? total;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly Dictionary<(int Source, int Attempt), long> progressedBytes = [];
    private int sourceCount;
    private int terminalState;
    private long transferredBytes;

    public ForegroundDownloadLogScope(
        ILogger? logger,
        string operation,
        string fileName,
        string destinationPath,
        string? sourceUrl,
        long? expectedBytes = null,
        int? position = null,
        int? total = null)
    {
        this.logger = logger ?? NullLogger.Instance;
        using var operationScope = DownloadDiagnostics.BeginOperation(this.logger, operation);
        var fields = new Dictionary<string, object?>(DownloadDiagnostics.Capture()) { ["FileId"] = Guid.NewGuid().ToString("N") };
        context = fields;
        using var fileScope = RestoreContext();
        this.operation = operation;
        this.fileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(destinationPath) : fileName;
        this.destinationPath = Path.GetFullPath(destinationPath);
        this.expectedBytes = expectedBytes;
        this.position = position;
        this.total = total;

        this.logger.LogInformation(
            "Foreground file download started. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} SourceUrl={SourceUrl} ExpectedBytes={ExpectedBytes} Position={Position} Total={Total}",
            operation,
            this.fileName,
            this.destinationPath,
            SanitizeOptionalUrl(sourceUrl),
            expectedBytes,
            position,
            total);
    }

    public long TransferredBytes => Interlocked.Read(ref transferredBytes);

    public void ReportTransferredBytes(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref transferredBytes, bytes);
    }

    public int AttemptCount
    {
        get
        {
            lock (progressLock)
                return progressedBytes.Count;
        }
    }

    /// <summary>
    /// Creates a progress callback for one source candidate. Call this once for each outer
    /// fallback URL; executor-internal retries are distinguished by their attempt number.
    /// </summary>
    public Action<int, long, long?> BeginSource(Action<int, long, long?>? downstream = null)
    {
        var source = Interlocked.Increment(ref sourceCount);
        return (attempt, currentBytes, totalBytes) =>
        {
            RecordProgress(source, attempt, currentBytes);
            downstream?.Invoke(attempt, currentBytes, totalBytes);
        };
    }

    public void Complete(ResolvedDownloadRequest resolution, bool? reused = null)
    {
        using var diagnosticContext = RestoreContext();
        if (Interlocked.Exchange(ref terminalState, 1) != 0)
            return;

        stopwatch.Stop();
        var attempts = resolution.NetworkAttempts ?? AttemptCount;
        var bytes = TransferredBytes;
        var wasReused = reused ?? bytes == 0;
        var fallbackUsed = resolution.FallbackUsed ?? (Volatile.Read(ref sourceCount) > 1
            || !string.Equals(resolution.OriginalUrl, resolution.ActualUrl, StringComparison.OrdinalIgnoreCase));
        logger.LogInformation(
            "Foreground file download completed. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} Status={Status} TransferredBytes={TransferredBytes} ExpectedBytes={ExpectedBytes} DurationMs={DurationMs} FinalSourceUrl={FinalSourceUrl} SourceKind={SourceKind} Attempts={Attempts} FallbackUsed={FallbackUsed} Position={Position} Total={Total}",
            operation,
            fileName,
            destinationPath,
            wasReused ? "Reused" : "Downloaded",
            bytes,
            expectedBytes,
            stopwatch.ElapsedMilliseconds,
            DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl),
            resolution.ResolvedSourceKind,
            attempts,
            fallbackUsed,
            position,
            total);
    }

    public void CompleteWithoutDownload(string status, string? finalSourceUrl = null)
    {
        using var diagnosticContext = RestoreContext();
        if (Interlocked.Exchange(ref terminalState, 1) != 0)
            return;

        stopwatch.Stop();
        logger.LogInformation(
            "Foreground file download completed. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} Status={Status} TransferredBytes={TransferredBytes} ExpectedBytes={ExpectedBytes} DurationMs={DurationMs} FinalSourceUrl={FinalSourceUrl} Attempts={Attempts} FallbackUsed={FallbackUsed} Position={Position} Total={Total}",
            operation,
            fileName,
            destinationPath,
            status,
            TransferredBytes,
            expectedBytes,
            stopwatch.ElapsedMilliseconds,
            SanitizeOptionalUrl(finalSourceUrl),
            AttemptCount,
            Volatile.Read(ref sourceCount) > 1,
            position,
            total);
    }

    public void Fail(Exception exception, string? finalSourceUrl = null)
    {
        using var diagnosticContext = RestoreContext();
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref terminalState, 1) != 0)
            return;

        stopwatch.Stop();
        logger.LogWarning(
            exception,
            "Foreground file download failed. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} TransferredBytes={TransferredBytes} ExpectedBytes={ExpectedBytes} DurationMs={DurationMs} FinalSourceUrl={FinalSourceUrl} Attempts={Attempts} FallbackUsed={FallbackUsed} FailureType={FailureType} Position={Position} Total={Total}",
            operation,
            fileName,
            destinationPath,
            TransferredBytes,
            expectedBytes,
            stopwatch.ElapsedMilliseconds,
            SanitizeOptionalUrl(finalSourceUrl),
            FindSourceFailure(exception)?.NetworkAttempts ?? AttemptCount,
            FindSourceFailure(exception)?.FallbackUsed ?? (Volatile.Read(ref sourceCount) > 1),
            exception.GetType().Name,
            position,
            total);
        logger.LogDebug(
            "Foreground file download failure details. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} FailureType={FailureType}",
            operation,
            fileName,
            destinationPath,
            exception.GetType().Name);
    }

    public void Defer(Exception exception, string status, string? finalSourceUrl = null)
    {
        using var diagnosticContext = RestoreContext();
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref terminalState, 1) != 0)
            return;

        stopwatch.Stop();
        logger.LogWarning(
            exception,
            "Foreground file download deferred. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} Status={Status} TransferredBytes={TransferredBytes} ExpectedBytes={ExpectedBytes} DurationMs={DurationMs} FinalSourceUrl={FinalSourceUrl} Attempts={Attempts} FallbackUsed={FallbackUsed} FailureType={FailureType} Position={Position} Total={Total}",
            operation,
            fileName,
            destinationPath,
            status,
            TransferredBytes,
            expectedBytes,
            stopwatch.ElapsedMilliseconds,
            SanitizeOptionalUrl(finalSourceUrl),
            FindSourceFailure(exception)?.NetworkAttempts ?? AttemptCount,
            FindSourceFailure(exception)?.FallbackUsed ?? (Volatile.Read(ref sourceCount) > 1),
            exception.GetType().Name,
            position,
            total);
        logger.LogDebug(
            "Foreground file download deferral details. Operation={Operation} FileName={FileName} DestinationPath={DestinationPath} FailureType={FailureType}",
            operation,
            fileName,
            destinationPath,
            exception.GetType().Name);
    }

    private static MinecraftDownloadRequestExecutor.DownloadSourceRequestException? FindSourceFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is MinecraftDownloadRequestExecutor.DownloadSourceRequestException sourceFailure) return sourceFailure;
        return null;
    }

    private void RecordProgress(int source, int attempt, long currentBytes)
    {
        if (currentBytes < 0)
            return;

        lock (progressLock)
        {
            var key = (source, Math.Max(1, attempt));
            progressedBytes.TryGetValue(key, out var previousBytes);
            if (currentBytes <= previousBytes)
                return;

            progressedBytes[key] = currentBytes;
        }
    }

    private static string SanitizeOptionalUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<unresolved>" : DownloadUriLogSanitizer.Sanitize(value);
}
