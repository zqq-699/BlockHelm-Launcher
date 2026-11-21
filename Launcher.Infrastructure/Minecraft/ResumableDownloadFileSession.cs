/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// A resumable session whose state only exists for one top-level download. No
/// part metadata or lock file survives the task that owns it.
/// </summary>
internal sealed class ResumableDownloadFileSession : IAsyncDisposable
{
    private const int BufferSize = 81920;
    private readonly string destinationPath;
    private readonly string temporaryPath;
    private readonly DownloadIntegrityExpectation integrity;
    private readonly DownloadPersistenceMode persistenceMode;
    private readonly MinecraftDownloadOperationContext? operationContext;
    private readonly string? managedRoot;
    private readonly bool allowUnverifiedSegmentedDownload;
    private readonly bool useHiddenTemporaryFile;
    private readonly IDisposable? assetLock;
    private string? sourceCandidateKey;
    private string? strongETag;
    private string? lastModified;
    private long? knownTotalLength;
    private FileStream? segmentedDestination;
    private long segmentedBytes;
    private readonly object segmentedProgressLock = new();
    private readonly List<SegmentedWrittenInterval> segmentedWrittenIntervals = [];
    private bool disposed;

    public long SegmentedBytesWritten => Interlocked.Read(ref segmentedBytes);

    private ResumableDownloadFileSession(
        string destinationPath,
        string temporaryPath,
        DownloadIntegrityExpectation integrity,
        DownloadPersistenceMode persistenceMode,
        MinecraftDownloadOperationContext? operationContext,
        string? managedRoot,
        bool allowUnverifiedSegmentedDownload,
        bool useHiddenTemporaryFile,
        IDisposable? assetLock)
    {
        this.destinationPath = destinationPath;
        this.temporaryPath = temporaryPath;
        this.integrity = integrity;
        this.persistenceMode = persistenceMode;
        this.operationContext = operationContext;
        this.managedRoot = managedRoot;
        this.allowUnverifiedSegmentedDownload = allowUnverifiedSegmentedDownload;
        this.useHiddenTemporaryFile = useHiddenTemporaryFile;
        this.assetLock = assetLock;
    }

    public bool IsComplete { get; private set; }
    public bool IsResumeRequested { get; private set; }

    public static Task<ResumableDownloadFileSession> AcquireAsync(
        string destinationPath,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken,
        DownloadFileOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1) || !MinecraftFileIntegrity.IsSha1(expectedSha1))
            throw SafeExceptionLog.Describe(new InvalidDataException("A resumable game download requires a valid SHA-1 value."), "DownloadValidation", ("Reason", "A resumable game download requires a valid SHA-1 value."));
        return AcquireAsync(destinationPath, DownloadIntegrityExpectation.Sha1(expectedSha1, expectedSize),
            cancellationToken, options);
    }

    public static async Task<ResumableDownloadFileSession> AcquireAsync(
        string destinationPath,
        DownloadIntegrityExpectation integrity,
        CancellationToken cancellationToken,
        DownloadFileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(integrity);
        options ??= new DownloadFileOptions();
        var normalizedDestination = Path.GetFullPath(destinationPath);
        var managedRoot = options.ResolveManagedRoot(normalizedDestination);
        if (!string.IsNullOrWhiteSpace(managedRoot))
        {
            managedRoot = Path.GetFullPath(managedRoot);
            MinecraftPathGuard.EnsureSafeFileDestination(
                normalizedDestination,
                managedRoot,
                "Managed download");
        }
        var destinationDirectory = Path.GetDirectoryName(normalizedDestination)
            ?? throw SafeExceptionLog.Describe(new InvalidOperationException("Download destination has no parent directory."), "DownloadValidation", ("Reason", "Download destination has no parent directory."));
        if (managedRoot is null)
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        else
        {
            MinecraftPathGuard.EnsureSafeDirectory(
                destinationDirectory,
                managedRoot,
                "Managed download directory");
            MinecraftPathGuard.EnsureSafeFileDestination(
                normalizedDestination,
                managedRoot,
                "Managed download");
        }

        IDisposable? assetLock = null;
        try
        {
            if (options.OperationContext is { } context
                && (options.PersistenceMode is DownloadPersistenceMode.LightweightAtomic || context.HasRegisteredAsset(normalizedDestination)))
            {
                assetLock = await context.AcquireAssetLockAsync(normalizedDestination, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(normalizedDestination))
            {
                using var verifiedLease = assetLock is not null
                    ? options.OperationContext?.AcquireVerifiedFileLease(normalizedDestination, integrity) : null;
                if (verifiedLease is not null || await integrity.VerifyFileAsync(normalizedDestination, cancellationToken).ConfigureAwait(false))
                {
                    options.OperationContext?.MarkVerified(normalizedDestination, integrity);
                    return new ResumableDownloadFileSession(
                        normalizedDestination, string.Empty, integrity, options.PersistenceMode,
                        options.OperationContext, managedRoot, options.AllowUnverifiedSegmentedDownload,
                        options.UseHiddenTemporaryFile, assetLock) { IsComplete = true };
                }
            }

            DeleteAbandonedTemporaryFiles(
                destinationDirectory,
                Path.GetFileName(normalizedDestination),
                managedRoot,
                options.UseHiddenTemporaryFile);
            var temporaryFileName = options.UseHiddenTemporaryFile
                ? $".{Path.GetFileName(normalizedDestination)}.bhl-pending-{Guid.NewGuid():N}.tmp"
                : $"{Path.GetFileName(normalizedDestination)}.bhl-pending-{Guid.NewGuid():N}.tmp";
            var temporaryPath = Path.Combine(destinationDirectory, temporaryFileName);

            return new ResumableDownloadFileSession(
                normalizedDestination, temporaryPath, integrity, options.PersistenceMode,
                options.OperationContext, managedRoot, options.AllowUnverifiedSegmentedDownload,
                options.UseHiddenTemporaryFile, assetLock);
        }
        catch
        {
            assetLock?.Dispose();
            throw;
        }
    }

    public void ConfigureRequest(HttpRequestMessage request, ResolvedDownloadRequest resolution)
    {
        IsResumeRequested = false;
        if (persistenceMode is not DownloadPersistenceMode.TaskScopedResumable)
            return;

        var existingLength = GetTemporaryLength();
        var candidateKey = CreateSourceCandidateKey(resolution);
        if (existingLength > 0 && !string.Equals(sourceCandidateKey, candidateKey, StringComparison.Ordinal))
        {
            DiscardTemporaryForSourceSwitch();
            existingLength = 0;
        }
        if (existingLength <= 0 || knownTotalLength is null || existingLength >= knownTotalLength
            || sourceCandidateKey != candidateKey
            || string.IsNullOrWhiteSpace(strongETag) && string.IsNullOrWhiteSpace(lastModified))
            return;

        request.Headers.Range = new RangeHeaderValue(existingLength, null);
        request.Headers.TryAddWithoutValidation("If-Range", strongETag ?? lastModified);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        IsResumeRequested = true;
    }

    public void PrepareSegmentedDownload(long totalLength)
    {
        if (persistenceMode is not DownloadPersistenceMode.TaskScopedResumable)
            throw SafeExceptionLog.Describe(new InvalidOperationException("Segmented downloads require task-scoped resumable storage."), "DownloadValidation", ("Reason", "Segmented downloads require task-scoped resumable storage."));
        if (totalLength <= 0
            || integrity.ExpectedSize.HasValue && integrity.ExpectedSize.Value != totalLength)
        {
            throw SafeExceptionLog.Describe(new InvalidDataException("The segmented download total length did not match the trusted size."), "DownloadValidation", ("Reason", "The segmented download total length did not match the trusted size."));
        }
        if (!integrity.HasStrongHash
            && !(allowUnverifiedSegmentedDownload && !integrity.IsVerifiable))
            throw SafeExceptionLog.Describe(new InvalidDataException("Segmented downloads require a strong expected hash."), "DownloadValidation", ("Reason", "Segmented downloads require a strong expected hash."));

        ResetSegmentedDownload();
        try
        {
            EnsureManagedDestinationIsSafe();
            segmentedDestination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            segmentedDestination.SetLength(totalLength);
            if (useHiddenTemporaryFile)
                TryMarkTemporaryHidden(temporaryPath);
            knownTotalLength = totalLength;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            segmentedDestination?.Dispose();
            segmentedDestination = null;
            DeleteTemporary();
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to prepare the segmented download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to prepare the segmented download temporary file."));
        }
    }

    public async Task WriteSegmentAsync(
        HttpContent content,
        long start,
        long end,
        int attemptNumber,
        Action<int, long, long?>? reportAttemptProgress,
        CancellationToken cancellationToken,
        bool allowTrailingContent = false,
        AdaptiveDownloadSegment? adaptiveSegment = null,
        Action<long>? reportTransferredBytes = null)
    {
        var destination = segmentedDestination
            ?? throw SafeExceptionLog.Describe(new InvalidOperationException("The segmented download temporary file was not prepared."), "DownloadValidation", ("Reason", "The segmented download temporary file was not prepared."));
        if (start < 0 || end < start || knownTotalLength is null || end >= knownTotalLength.Value)
            throw SafeExceptionLog.Describe(new InvalidDataException("The segmented download range was invalid."), "DownloadValidation", ("Reason", "The segmented download range was invalid."));

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var offset = start;
        var intentionallyTruncated = false;
        try
        {
            while (offset <= end)
            {
                var currentEnd = adaptiveSegment is null
                    ? end
                    : Math.Min(end, adaptiveSegment.LogicalEnd);
                if (offset > currentEnd)
                {
                    intentionallyTruncated = true;
                    break;
                }

                var requested = (int)Math.Min(buffer.Length, currentEnd - offset + 1);
                var read = await ReadNetworkAsync(source, buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw SafeExceptionLog.Describe(new DownloadBodyInterruptedException("A segmented response ended before its requested range was complete."), "DownloadBodyInterruptedException", ("Reason", "A segmented response ended before its requested range was complete."));

                reportTransferredBytes?.Invoke(read);
                var accepted = adaptiveSegment?.ReserveWrite(offset, read) ?? read;
                if (accepted == 0)
                {
                    intentionallyTruncated = true;
                    break;
                }

                try
                {
                    await RandomAccess.WriteAsync(
                        destination.SafeFileHandle,
                        buffer.AsMemory(0, accepted),
                        offset,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to write a segmented download range.", exception), "DownloadLocalFileException", ("Reason", "Failed to write a segmented download range."));
                }
                offset += accepted;

                lock (segmentedProgressLock)
                {
                    var newlyWritten = RecordSegmentedWrite(offset - accepted, offset - 1);
                    var written = Interlocked.Add(ref segmentedBytes, newlyWritten);
                    reportAttemptProgress?.Invoke(attemptNumber, written, knownTotalLength);
                }

                if (accepted < read)
                {
                    intentionallyTruncated = true;
                    break;
                }
            }

            intentionallyTruncated |= adaptiveSegment?.WasShortenedFrom(end) == true;
            if (!allowTrailingContent
                && !intentionallyTruncated
                && await ReadNetworkAsync(source, buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw SafeExceptionLog.Describe(new DownloadContentValidationException("A segmented response exceeded its requested range."), "DownloadContentValidationException", ("Reason", "A segmented response exceeded its requested range."));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CompleteSegmentedDownloadAsync(CancellationToken cancellationToken)
    {
        var destination = segmentedDestination
            ?? throw SafeExceptionLog.Describe(new InvalidOperationException("The segmented download temporary file was not prepared."), "DownloadValidation", ("Reason", "The segmented download temporary file was not prepared."));
        try
        {
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            await destination.DisposeAsync().ConfigureAwait(false);
            segmentedDestination = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to flush the segmented download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to flush the segmented download temporary file."));
        }

        if (knownTotalLength is null || SegmentedBytesWritten != knownTotalLength.Value)
            throw SafeExceptionLog.Describe(new DownloadBodyInterruptedException("The segmented download did not write the expected number of bytes."), "DownloadBodyInterruptedException", ("Reason", "The segmented download did not write the expected number of bytes."), ("Artifact", temporaryPath), ("ExpectedBytes", knownTotalLength), ("ActualBytes", SegmentedBytesWritten));
        if (integrity.IsVerifiable
            && !await VerifyTemporaryAsync(cancellationToken).ConfigureAwait(false))
            throw integrity.DescribeFailure(new DownloadHashMismatchException("The segmented download did not match its expected hash."));

        Publish(cancellationToken);
    }

    public void ResetSegmentedDownload()
    {
        segmentedDestination?.Dispose();
        segmentedDestination = null;
        Interlocked.Exchange(ref segmentedBytes, 0);
        segmentedWrittenIntervals.Clear();
        knownTotalLength = null;
        sourceCandidateKey = null;
        strongETag = null;
        lastModified = null;
        IsResumeRequested = false;
        DeleteTemporary();
    }

    private long RecordSegmentedWrite(long start, long end)
    {
        var mergedStart = start;
        var mergedEnd = end;
        var alreadyWritten = 0L;
        var insertAt = 0;

        while (insertAt < segmentedWrittenIntervals.Count
               && segmentedWrittenIntervals[insertAt].End < start - 1)
        {
            insertAt++;
        }

        var removeAt = insertAt;
        while (removeAt < segmentedWrittenIntervals.Count
               && segmentedWrittenIntervals[removeAt].Start <= end + 1)
        {
            var existing = segmentedWrittenIntervals[removeAt];
            var overlapStart = Math.Max(start, existing.Start);
            var overlapEnd = Math.Min(end, existing.End);
            if (overlapStart <= overlapEnd)
                alreadyWritten += overlapEnd - overlapStart + 1;
            mergedStart = Math.Min(mergedStart, existing.Start);
            mergedEnd = Math.Max(mergedEnd, existing.End);
            removeAt++;
        }

        if (removeAt > insertAt)
            segmentedWrittenIntervals.RemoveRange(insertAt, removeAt - insertAt);
        segmentedWrittenIntervals.Insert(insertAt, new SegmentedWrittenInterval(mergedStart, mergedEnd));
        return end - start + 1 - alreadyWritten;
    }

    private readonly record struct SegmentedWrittenInterval(long Start, long End);

    public async Task WriteAsync(
        HttpResponseMessage response,
        ResolvedDownloadRequest resolution,
        int attemptNumber,
        Action<int, long, long?>? reportAttemptProgress,
        CancellationToken cancellationToken,
        Action<long>? reportTransferredBytes = null)
    {
        if (IsComplete)
            return;

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (await VerifyTemporaryAsync(cancellationToken).ConfigureAwait(false))
            {
                Publish(cancellationToken);
                return;
            }
            DeleteTemporary();
            throw SafeExceptionLog.Describe(new DownloadContentValidationException("The server rejected the range request and the task-scoped file is not complete."), "DownloadContentValidationException", ("Reason", "The server rejected the range request and the task-scoped file is not complete."));
        }

        var candidateKey = CreateSourceCandidateKey(resolution);
        var resumed = IsResumeRequested && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumed && !ValidatePartialResponse(response, candidateKey))
        {
            DeleteTemporary();
            throw SafeExceptionLog.Describe(new DownloadContentValidationException("The partial response did not match this task-scoped download session."), "DownloadContentValidationException", ("Reason", "The partial response did not match this task-scoped download session."));
        }
        if (!resumed)
            DeleteTemporary();

        var existingLength = resumed ? GetTemporaryLength() : 0;
        var totalLength = ResolveTotalLength(response, existingLength, resumed);
        knownTotalLength = totalLength;
        sourceCandidateKey = candidateKey;
        strongETag = GetStrongETag(response);
        lastModified = GetLastModified(response);

        var hashers = integrity.CreateHashers();
        try
        {
            if (existingLength > 0)
                await AppendExistingTemporaryToHashAsync(hashers.Values, cancellationToken).ConfigureAwait(false);

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            EnsureManagedDestinationIsSafe();
            await using var destination = OpenTemporaryFile(resumed);
            if (useHiddenTemporaryFile)
                TryMarkTemporaryHidden(temporaryPath);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            var persisted = existingLength;
            try
            {
                while (true)
                {
                    var read = await ReadNetworkAsync(source, buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    reportTransferredBytes?.Invoke(read);
                    await WriteLocalAsync(destination, buffer, read, cancellationToken).ConfigureAwait(false);
                    foreach (var hasher in hashers.Values)
                        hasher.AppendData(buffer, 0, read);
                    persisted += read;
                    reportAttemptProgress?.Invoke(attemptNumber, persisted, integrity.ExpectedSize ?? totalLength);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await FlushLocalAsync(destination, cancellationToken).ConfigureAwait(false);
            ValidateCompletedLength(persisted, totalLength);
            if (integrity.IsVerifiable && !integrity.Verify(hashers))
            {
                DeleteTemporary();
                throw integrity.DescribeFailure(new DownloadHashMismatchException("The downloaded file did not match its expected hash."));
            }
        }
        finally
        {
            foreach (var hasher in hashers.Values)
                hasher.Dispose();
        }

        Publish(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        segmentedDestination?.Dispose();
        segmentedDestination = null;
        DeleteTemporary();
        assetLock?.Dispose();
        await Task.CompletedTask;
    }

    private bool ValidatePartialResponse(HttpResponseMessage response, string candidateKey)
    {
        var range = response.Content.Headers.ContentRange;
        var length = GetTemporaryLength();
        if (sourceCandidateKey != candidateKey || response.Content.Headers.ContentEncoding.Count != 0
            || range is null || range.Unit != "bytes" || range.From != length || range.To is null || range.Length is null
            || knownTotalLength != range.Length || response.Content.Headers.ContentLength != range.To - range.From + 1)
            return false;
        return strongETag is not null
            ? string.Equals(strongETag, GetStrongETag(response), StringComparison.Ordinal)
            : string.Equals(lastModified, GetLastModified(response), StringComparison.Ordinal);
    }

    private long? ResolveTotalLength(HttpResponseMessage response, long existingLength, bool resumed)
    {
        var total = resumed ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
        if (total is null)
            return integrity.ExpectedSize;
        if (integrity.ExpectedSize.HasValue && integrity.ExpectedSize.Value != total.Value)
            throw SafeExceptionLog.Describe(new DownloadContentValidationException("The response total length did not match the expected file size."), "DownloadContentValidationException", ("Reason", "The response total length did not match the expected file size."), ("Artifact", temporaryPath), ("ExpectedBytes", integrity.ExpectedSize), ("ActualBytes", total.Value));
        return total;
    }

    private void ValidateCompletedLength(long persisted, long? totalLength)
    {
        var required = integrity.ExpectedSize ?? totalLength;
        if (required.HasValue && persisted != required.Value)
            throw SafeExceptionLog.Describe(new DownloadBodyInterruptedException("The response body length did not match the expected file size."), "DownloadBodyInterruptedException", ("Reason", "The response body length did not match the expected file size."), ("Artifact", temporaryPath), ("ExpectedBytes", required.Value), ("ActualBytes", persisted));
    }

    private async Task<bool> VerifyTemporaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(temporaryPath)
                && await integrity.VerifyFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to verify the download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to verify the download temporary file."));
        }
    }

    private async Task AppendExistingTemporaryToHashAsync(IEnumerable<IncrementalHash> hashers, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    foreach (var hasher in hashers)
                        hasher.AppendData(buffer, 0, read);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to read the download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to read the download temporary file."));
        }
    }

    private void Publish(CancellationToken cancellationToken)
    {
        try
        {
            using var publishLock = AcquireFinalPublishLock(destinationPath, cancellationToken);
            EnsureManagedDestinationIsSafe();
            if (File.Exists(destinationPath)
                && integrity.VerifyFile(destinationPath, cancellationToken))
            {
                DeleteTemporary();
                operationContext?.MarkVerified(destinationPath, integrity);
                IsComplete = true;
                return;
            }
            if (useHiddenTemporaryFile)
                ClearTemporaryHiddenAttribute();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            operationContext?.MarkVerified(destinationPath, integrity);
            IsComplete = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to atomically publish the completed download.", exception), "DownloadLocalFileException", ("Reason", "Failed to atomically publish the completed download."));
        }
    }

    private void EnsureManagedDestinationIsSafe()
    {
        if (managedRoot is null)
            return;
        MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, managedRoot, "Managed download");
        MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, temporaryPath, "Managed download temporary file");
    }

    private long GetTemporaryLength()
    {
        try
        {
            return File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to inspect the download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to inspect the download temporary file."));
        }
    }

    private void DiscardTemporaryForSourceSwitch()
    {
        try
        {
            if (managedRoot is not null)
                MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, temporaryPath, "Managed download source switch");
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to discard an incompatible partial download.", exception), "DownloadLocalFileException", ("Reason", "Failed to discard an incompatible partial download."));
        }
    }

    private FileStream OpenTemporaryFile(bool resumed)
    {
        try
        {
            return new FileStream(
                temporaryPath,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to open the download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to open the download temporary file."));
        }
    }

    private async Task WriteLocalAsync(FileStream destination, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        try
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to write the download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to write the download temporary file."));
        }
    }

    private async Task FlushLocalAsync(FileStream destination, CancellationToken cancellationToken)
    {
        try
        {
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            await destination.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SafeExceptionLog.Describe(new DownloadLocalFileException("Failed to flush the download temporary file.", exception), "DownloadLocalFileException", ("Reason", "Failed to flush the download temporary file."));
        }
    }

    private void DeleteTemporary()
    {
        try
        {
            if (string.IsNullOrEmpty(temporaryPath) || !File.Exists(temporaryPath))
                return;
            if (managedRoot is not null)
                MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, temporaryPath, "Managed download cleanup");
            File.Delete(temporaryPath);
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void DeleteAbandonedTemporaryFiles(
        string directory,
        string destinationFileName,
        string? managedRoot,
        bool useHiddenTemporaryFile)
    {
        if (managedRoot is not null)
            MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, directory, "Managed download cleanup directory");
        var pattern = useHiddenTemporaryFile
            ? $".{destinationFileName}.bhl-pending-*.tmp"
            : $"{destinationFileName}.bhl-pending-*.tmp";
        foreach (var candidate in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (managedRoot is not null)
                    MinecraftPathGuard.EnsureNoReparsePoints(managedRoot, candidate, "Managed download cleanup file");
                using var lease = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                lease.Close();
                File.Delete(candidate);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryMarkTemporaryHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ClearTemporaryHiddenAttribute()
    {
        try
        {
            File.SetAttributes(temporaryPath, File.GetAttributes(temporaryPath) & ~FileAttributes.Hidden);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static string CreateSourceCandidateKey(ResolvedDownloadRequest resolution) { var uri = new Uri(resolution.ActualUrl, UriKind.Absolute); return $"{resolution.ResolvedSourceKind}:{uri.Host.ToLowerInvariant()}:{uri.AbsolutePath}"; }
    private static string? GetStrongETag(HttpResponseMessage response) { var tag = response.Headers.ETag?.Tag; return tag is not null && !response.Headers.ETag!.IsWeak ? tag : null; }
    private static string? GetLastModified(HttpResponseMessage response) => response.Content.Headers.LastModified?.ToString("R");

    private static Task<int> ReadNetworkAsync(Stream source, byte[] buffer, CancellationToken cancellationToken) =>
        ReadNetworkAsync(source, buffer.AsMemory(0, buffer.Length), cancellationToken);

    private static async Task<int> ReadNetworkAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { return await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
        { throw SafeExceptionLog.Describe(new DownloadBodyInterruptedException("The response body was interrupted while downloading a file.", exception), "DownloadBodyInterruptedException", ("Reason", "The response body was interrupted while downloading a file.")); }
    }

    private static IDisposable AcquireFinalPublishLock(string path, CancellationToken cancellationToken)
    {
        var mutex = new Mutex(false, GetFinalPublishMutexName(path));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(50)))
                        return new PublishMutexLease(mutex);
                }
                catch (AbandonedMutexException)
                {
                    return new PublishMutexLease(mutex);
                }
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    internal static string GetFinalPublishMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            normalizedPath = normalizedPath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath)));
        return $"Local\\BlockHelm.Download.Publish.{hash}";
    }

    private sealed class PublishMutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? mutex = mutex;

        public void Dispose()
        {
            var ownedMutex = Interlocked.Exchange(ref mutex, null);
            if (ownedMutex is null)
                return;
            ownedMutex.ReleaseMutex();
            ownedMutex.Dispose();
        }
    }
}
