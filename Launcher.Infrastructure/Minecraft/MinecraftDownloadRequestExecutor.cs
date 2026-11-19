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

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 按下载源优先级执行请求，并统一处理并发租约、重试、源切换和“确实不存在”的查询结果。
/// </summary>
internal sealed class MinecraftDownloadRequestExecutor
{
    internal const long MinimumSegmentedDownloadSize = 8L * 1024 * 1024;
    internal const long SegmentedSplitThreshold = 1024L * 1024;
    internal const int MaximumSegmentedWorkersPerFile = 8;
    internal static readonly TimeSpan SegmentedExpansionScanInterval = TimeSpan.FromMilliseconds(50);

    private readonly MinecraftDownloadTransport transport;
    private readonly ILogger logger;
    private readonly DownloadBandwidthLimiter? bandwidthLimiter;
    private readonly IImportConcurrencyLimiter limiter;
    private readonly DownloadConcurrencyCategory category;
    private readonly DownloadRetryOptions retryOptions;
    private readonly DownloadHostHealthTracker hostHealthTracker;
    private readonly DownloadHostConcurrencyController hostConcurrencyController;
    private readonly BmclApiRequestRateLimiter bmclApiRequestRateLimiter;
    private readonly Func<double> nextRetryJitter;
    private readonly TimeProvider timeProvider;
    private readonly SegmentedDownloadCoordinator segmentedDownloadCoordinator;
    private readonly long minimumSegmentedDownloadSize;
    private readonly AutomaticDownloadSourceSelector automaticSourceSelector;

    public MinecraftDownloadRequestExecutor(
        HttpClient httpClient,
        ILogger? logger = null,
        DownloadBandwidthLimiter? bandwidthLimiter = null,
        IImportConcurrencyLimiter? limiter = null,
        DownloadConcurrencyCategory category = DownloadConcurrencyCategory.Metadata,
        DownloadRetryOptions? retryOptions = null,
        DownloadHostHealthTracker? hostHealthTracker = null,
        DownloadHostConcurrencyController? hostConcurrencyController = null,
        Func<double>? nextRetryJitter = null,
        BmclApiRequestRateLimiter? bmclApiRequestRateLimiter = null,
        TimeProvider? timeProvider = null,
        SegmentedDownloadCoordinator? segmentedDownloadCoordinator = null,
        long minimumSegmentedDownloadSize = MinimumSegmentedDownloadSize,
        AutomaticDownloadSourceSelector? automaticSourceSelector = null)
    {
        if (minimumSegmentedDownloadSize <= SegmentedSplitThreshold)
            throw new ArgumentOutOfRangeException(nameof(minimumSegmentedDownloadSize));

        this.logger = logger ?? NullLogger.Instance;
        this.bandwidthLimiter = bandwidthLimiter;
        this.limiter = limiter ?? ImportConcurrencyLimiter.Shared;
        this.category = category;
        this.retryOptions = retryOptions ?? DownloadRetryOptions.Default;
        // A batch may provide a shared tracker; the default remains executor
        // scoped so unrelated operations never inherit a stale cooldown.
        this.hostHealthTracker = hostHealthTracker ?? new DownloadHostHealthTracker();
        this.hostConcurrencyController = hostConcurrencyController ?? DownloadHostConcurrencyController.Shared;
        this.bmclApiRequestRateLimiter = bmclApiRequestRateLimiter ?? BmclApiRequestRateLimiter.Shared;
        this.nextRetryJitter = nextRetryJitter ?? Random.Shared.NextDouble;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.segmentedDownloadCoordinator = segmentedDownloadCoordinator ?? SegmentedDownloadCoordinator.Shared;
        this.minimumSegmentedDownloadSize = minimumSegmentedDownloadSize;
        this.automaticSourceSelector = automaticSourceSelector ?? AutomaticDownloadSourceSelector.Shared;
        transport = new MinecraftDownloadTransport(
            httpClient,
            this.retryOptions,
            AcquireAdmissionAsync,
            TryAcquireOpportunisticAdmission,
            this.logger);
    }

    public async Task<T> ExecuteAsync<T>(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        bool logExhaustion = true)
    {
        var result = await ExecuteCoreAsync(
            originalUrl,
            preference,
            categoryHint,
            operation,
            noResultStatus: null,
            lookupMode: false,
            configureRequest: null,
            allowResponseStatus: null,
            sensitiveHeaders: null,
            speedMeter: null,
            executionMode: DownloadExecutionMode.PerSourceRetries,
            fileCandidates: null,
            cancellationToken: cancellationToken, logExhaustion: logExhaustion).ConfigureAwait(false);
        return result.Value!;
    }

    public Task<DownloadLookupResult<T>> ExecuteLookupAsync<T>(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Func<HttpStatusCode, bool> noResultStatus,
        CancellationToken cancellationToken,
        bool logExhaustion = true)
    {
        return ExecuteCoreAsync(
            originalUrl,
            preference,
            categoryHint,
            operation,
            noResultStatus,
            lookupMode: true,
            configureRequest: null,
            allowResponseStatus: null,
            sensitiveHeaders: null,
            speedMeter: null,
            executionMode: DownloadExecutionMode.PerSourceRetries,
            fileCandidates: null,
            cancellationToken: cancellationToken, logExhaustion: logExhaustion);
    }

    /// <summary>Starts the next lookup source when the preferred source is slow or fails. The operation must not publish shared state.</summary>
    internal async Task<DownloadLookupResult<T>> ExecuteStaggeredLookupAsync<T>(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Func<HttpStatusCode, bool> noResultStatus,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? fallbackDelay = null,
        IReadOnlyList<ResolvedDownloadRequest>? sourceCandidates = null,
        SpeedMeter? speedMeter = null,
        bool logExhaustion = true)
    {
        var candidates = sourceCandidates?.ToArray()
            ?? (await PrepareCandidatesAsync(ResolveFileCandidates([originalUrl], preference, categoryHint), preference, cancellationToken).ConfigureAwait(false)).ToArray();
        candidates = candidates.OrderBy(GetCandidateCooldown).ThenBy(IsCandidateDegraded).ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException("At least one lookup source is required.", nameof(sourceCandidates));
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pending.Token;
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<(ResolvedDownloadRequest Source, Task<DownloadLookupResult<T>> Task)>();
        var failures = new List<(ResolvedDownloadRequest Source, DownloadSourceRequestException Error)>();
        var noResults = 0;

        Task<DownloadLookupResult<T>> Start(ResolvedDownloadRequest source, Action? onFailure = null) =>
            ExecuteCoreAsync(originalUrl, preference, categoryHint, operation, noResultStatus,
                lookupMode: true, configureRequest: null, allowResponseStatus: null,
                sensitiveHeaders: null, speedMeter: speedMeter, executionMode: DownloadExecutionMode.PerSourceRetries,
                fileCandidates: null, cancellationToken: token, singleCandidate: source,
                onRecoverableFailure: onFailure, logExhaustion: false);

        var primary = Start(candidates[0], () => firstFailure.TrySetResult());
        tasks.Add((candidates[0], primary));
        var delay = candidates.Length > 1
            ? (fallbackDelay ?? (ct => Task.Delay(TimeSpan.FromMilliseconds(1500), ct)))(token)
            : Task.Delay(Timeout.InfiniteTimeSpan, token);
        var canceled = Task.Delay(Timeout.InfiniteTimeSpan, token);
        var nextCandidate = 1;
        try
        {
            while (tasks.Count > 0)
            {
                var wait = tasks.Select(item => (Task)item.Task).ToList();
                if (nextCandidate < candidates.Length)
                {
                    wait.Add(delay);
                    wait.Add(firstFailure.Task);
                }
                wait.Add(canceled);
                await Task.WhenAny(wait).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // In a tie, process the preferred source first.
                for (var index = 0; index < tasks.Count;)
                {
                    var item = tasks[index];
                    if (!item.Task.IsCompleted)
                    {
                        index++;
                        continue;
                    }
                    tasks.RemoveAt(index);
                    try
                    {
                        var result = await item.Task.ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (result.Found)
                        {
                            logger.LogInformation("Download lookup completed. ResourceCategory={ResourceCategory} FinalSource={FinalSource}",
                                item.Source.ResourceCategory, item.Source.ResolvedSourceKind);
                            return result;
                        }
                        noResults++;
                        firstFailure.TrySetResult();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (DownloadSourceRequestException exception)
                    {
                        failures.Add((item.Source, exception));
                        firstFailure.TrySetResult();
                    }
                }

                if (nextCandidate < candidates.Length && (delay.IsCompleted || firstFailure.Task.IsCompleted))
                {
                    logger.LogInformation("Starting fallback download lookup source. ResourceCategory={ResourceCategory} Source={Source} Trigger={Trigger}",
                        candidates[nextCandidate].ResourceCategory, candidates[nextCandidate].ResolvedSourceKind,
                        firstFailure.Task.IsCompleted ? "PreferredSourceFailedOrEmpty" : "PreferredSourceSlow");
                    tasks.Add((candidates[nextCandidate], Start(candidates[nextCandidate])));
                    nextCandidate++;
                }
            }

            if (noResults == candidates.Length)
                return DownloadLookupResult<T>.NotFound();

            var last = failures[^1];
            var allFailures = failures.SelectMany(item => item.Error.Failures).ToArray();
            if (logExhaustion)
                LogExhaustedRequest(last.Source, failures.SelectMany(item =>
                item.Error.Failures.OfType<DownloadAttemptException>()
                    .Select(error => new SourceFailure(item.Source.ResolvedSourceKind, error.Reason, error.StatusCode))).ToArray());
            throw new DownloadSourceRequestException(last.Source, last.Error, allFailures);
        }
        finally
        {
            logger.LogInformation("Remaining download lookup requests canceled. CancellationOrigin={CancellationOrigin}", cancellationToken.IsCancellationRequested ? "CallerCancellation" : "SupersededRequest");
            pending.Cancel();
            foreach (var item in tasks)
                _ = ObserveCompletionAsync(item.Task);
            _ = ObserveCompletionAsync(delay);
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* A canceled or losing source must not leak an unobserved exception. */ }
    }

    public Task<ResolvedDownloadRequest> DownloadFileAsync(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        string destinationPath,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken,
        Action<int, long, long?>? reportAttemptProgress = null,
        DownloadRequestHeaders? sensitiveHeaders = null,
        DownloadFileOptions? options = null,
        SpeedMeter? speedMeter = null,
        Action<long>? reportTransferredBytes = null) =>
        DownloadFileAsync(
            [originalUrl],
            preference,
            categoryHint,
            destinationPath,
            expectedSha1,
            expectedSize,
            cancellationToken,
            reportAttemptProgress,
            sensitiveHeaders,
            options,
            speedMeter,
            reportTransferredBytes);

    public Task<ResolvedDownloadRequest> DownloadFileAsync(
        IReadOnlyList<string> originalUrls,
        DownloadSourcePreference preference,
        string? categoryHint,
        string destinationPath,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken,
        Action<int, long, long?>? reportAttemptProgress = null,
        DownloadRequestHeaders? sensitiveHeaders = null,
        DownloadFileOptions? options = null,
        SpeedMeter? speedMeter = null,
        Action<long>? reportTransferredBytes = null)
    {
        var fileCandidates = ResolveFileCandidates(originalUrls, preference, categoryHint);
        var originalUrl = fileCandidates[0].OriginalUrl;

        // Some legacy installer metadata has no trusted hash. Preserve its existing
        // atomic one-shot behavior, but never persist it as a resumable part.
        if (string.IsNullOrWhiteSpace(expectedSha1))
        {
            var managedRoot = options?.ResolveManagedRoot(destinationPath);
            MinecraftDownloadFileWriter.PrepareDestination(destinationPath, expectedSha1, managedRoot);
            return DownloadUnverifiedAsync();

            async Task<ResolvedDownloadRequest> DownloadUnverifiedAsync()
            {
                fileCandidates = await PrepareCandidatesAsync(fileCandidates, preference, cancellationToken).ConfigureAwait(false);
            using var diagnosticFile = DownloadDiagnostics.BeginFile(logger);
            using var network = new DownloadNetworkObservation();
                var result = await ExecuteCoreAsync(
                    originalUrl,
                    preference,
                    categoryHint,
                    async (context, token) =>
                    {
                        await MinecraftDownloadFileWriter.WriteAsync(
                            context.Response,
                            destinationPath,
                            expectedSha1,
                            expectedSize,
                            context.AttemptNumber,
                            reportAttemptProgress,
                            token,
                            managedRoot,
                            reportTransferredBytes,
                            options?.ValidateUnverifiedFile).ConfigureAwait(false);
                        return context.Resolution;
                    },
                    noResultStatus: null,
                    lookupMode: false,
                    configureRequest: null,
                    allowResponseStatus: null,
                    sensitiveHeaders,
                    speedMeter: speedMeter,
                    executionMode: DownloadExecutionMode.FileSourceRounds,
                    fileCandidates: fileCandidates,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return result.Value! with { NetworkAttempts = network.Sends, FallbackUsed = result.Value!.ActualUrl != fileCandidates[0].ActualUrl };
            }
        }

        return DownloadFileCoreAsync();

        async Task<ResolvedDownloadRequest> DownloadFileCoreAsync()
        {
            using var diagnosticFile = DownloadDiagnostics.BeginFile(logger);
            using var network = new DownloadNetworkObservation();
            var integrity = DownloadIntegrityExpectation.Sha1(expectedSha1, expectedSize);
            await using var session = await DownloadDiagnostics.RunStageAsync(logger, "DownloadSessionAcquire", () => ResumableDownloadFileSession.AcquireAsync(
                destinationPath,
                integrity,
                cancellationToken,
                options), quiet: true).ConfigureAwait(false);
            if (session.IsComplete)
                return fileCandidates[0] with { NetworkAttempts = 0, FallbackUsed = false };

            fileCandidates = await PrepareCandidatesAsync(fileCandidates, preference, cancellationToken).ConfigureAwait(false);
            var segmented = await TryDownloadSegmentedAsync(
                fileCandidates,
                integrity,
                options,
                session,
                reportAttemptProgress,
                sensitiveHeaders,
                speedMeter,
                reportTransferredBytes,
                cancellationToken).ConfigureAwait(false);
            if (segmented.Resolution is not null)
                return segmented.Resolution with { NetworkAttempts = network.Sends, FallbackUsed = segmented.Resolution.ActualUrl != fileCandidates[0].ActualUrl };

            var sequentialProgress = OffsetAttemptProgress(reportAttemptProgress, segmented.AttemptCount);

            var result = await ExecuteCoreAsync(
                originalUrl,
                preference,
                categoryHint,
                async (context, token) =>
                {
                    await session.WriteAsync(
                        context.Response,
                        context.Resolution,
                        context.AttemptNumber,
                        sequentialProgress,
                        token,
                        reportTransferredBytes).ConfigureAwait(false);
                    return context.Resolution;
                },
                noResultStatus: null,
                lookupMode: false,
                configureRequest: (request, resolution) => session.ConfigureRequest(request, resolution),
                allowResponseStatus: status => status == HttpStatusCode.RequestedRangeNotSatisfiable,
                sensitiveHeaders: sensitiveHeaders,
                speedMeter: speedMeter,
                executionMode: DownloadExecutionMode.FileSourceRounds,
                fileCandidates: fileCandidates,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Value! with { NetworkAttempts = network.Sends, FallbackUsed = result.Value!.ActualUrl != fileCandidates[0].ActualUrl };
        }
    }

    public Task<ResolvedDownloadRequest> DownloadFileAsync(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        string destinationPath,
        DownloadIntegrityExpectation integrity,
        CancellationToken cancellationToken,
        DownloadRequestHeaders? sensitiveHeaders = null,
        Action<int, long, long?>? reportAttemptProgress = null,
        DownloadFileOptions? options = null,
        SpeedMeter? speedMeter = null,
        Action<long>? reportTransferredBytes = null) =>
        DownloadFileAsync(
            [originalUrl],
            preference,
            categoryHint,
            destinationPath,
            integrity,
            cancellationToken,
            sensitiveHeaders,
            reportAttemptProgress,
            options,
            speedMeter,
            reportTransferredBytes);

    public Task<ResolvedDownloadRequest> DownloadFileAsync(
        IReadOnlyList<string> originalUrls,
        DownloadSourcePreference preference,
        string? categoryHint,
        string destinationPath,
        DownloadIntegrityExpectation integrity,
        CancellationToken cancellationToken,
        DownloadRequestHeaders? sensitiveHeaders = null,
        Action<int, long, long?>? reportAttemptProgress = null,
        DownloadFileOptions? options = null,
        SpeedMeter? speedMeter = null,
        Action<long>? reportTransferredBytes = null)
    {
        var fileCandidates = ResolveFileCandidates(originalUrls, preference, categoryHint);
        var originalUrl = fileCandidates[0].OriginalUrl;
        return DownloadCoreAsync();

        async Task<ResolvedDownloadRequest> DownloadCoreAsync()
        {
            using var diagnosticFile = DownloadDiagnostics.BeginFile(logger);
            using var network = new DownloadNetworkObservation();
            await using var session = await DownloadDiagnostics.RunStageAsync(logger, "DownloadSessionAcquire", () => ResumableDownloadFileSession.AcquireAsync(
                destinationPath,
                integrity,
                cancellationToken,
                options), quiet: true).ConfigureAwait(false);
            if (session.IsComplete)
                return fileCandidates[0] with { NetworkAttempts = 0, FallbackUsed = false };

            fileCandidates = await PrepareCandidatesAsync(fileCandidates, preference, cancellationToken).ConfigureAwait(false);
            var segmented = await TryDownloadSegmentedAsync(
                fileCandidates,
                integrity,
                options,
                session,
                reportAttemptProgress,
                sensitiveHeaders,
                speedMeter,
                reportTransferredBytes,
                cancellationToken).ConfigureAwait(false);
            if (segmented.Resolution is not null)
                return segmented.Resolution with { NetworkAttempts = network.Sends, FallbackUsed = segmented.Resolution.ActualUrl != fileCandidates[0].ActualUrl };

            var sequentialProgress = OffsetAttemptProgress(reportAttemptProgress, segmented.AttemptCount);

            var result = await ExecuteCoreAsync(
                originalUrl,
                preference,
                categoryHint,
                async (context, token) =>
                {
                    await session.WriteAsync(
                        context.Response,
                        context.Resolution,
                        context.AttemptNumber,
                        sequentialProgress,
                        token,
                        reportTransferredBytes).ConfigureAwait(false);
                    return context.Resolution;
                },
                noResultStatus: null,
                lookupMode: false,
                configureRequest: (request, resolution) => session.ConfigureRequest(request, resolution),
                allowResponseStatus: status => status == HttpStatusCode.RequestedRangeNotSatisfiable,
                sensitiveHeaders,
                speedMeter: speedMeter,
                executionMode: DownloadExecutionMode.FileSourceRounds,
                fileCandidates: fileCandidates,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Value! with { NetworkAttempts = network.Sends, FallbackUsed = result.Value!.ActualUrl != fileCandidates[0].ActualUrl };
        }
    }

    public Task<ResolvedDownloadRequest> DownloadUnverifiedFileAsync(
        string originalUrl,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<int, long, long?>? reportAttemptProgress = null,
        SpeedMeter? speedMeter = null,
        Action<long>? reportTransferredBytes = null)
    {
        return DownloadFileAsync(
            originalUrl,
            DownloadSourcePreference.Official,
            "ThirdParty",
            destinationPath,
            DownloadIntegrityExpectation.Unverified(),
            cancellationToken,
            reportAttemptProgress: reportAttemptProgress,
            options: new DownloadFileOptions(
                AllowUnverifiedSegmentedDownload: true,
                UseHiddenTemporaryFile: false),
            speedMeter: speedMeter,
            reportTransferredBytes: reportTransferredBytes);
    }

    private async Task<SegmentedDownloadAttemptResult> TryDownloadSegmentedAsync(
        IReadOnlyList<ResolvedDownloadRequest> candidates,
        DownloadIntegrityExpectation integrity,
        DownloadFileOptions? options,
        ResumableDownloadFileSession session,
        Action<int, long, long?>? reportAttemptProgress,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        Action<long>? reportTransferredBytes,
        CancellationToken cancellationToken)
    {
        if (!ShouldUseSegmentedDownload(candidates, integrity, options))
            return default;

        var attemptCount = 0;
        var failures = new List<DownloadAttemptException>();
        foreach (var resolution in candidates.Where(IsSegmentableCandidate))
        {
            attemptCount++;
            session.ResetSegmentedDownload();
            try
            {
                var completed = await TryDownloadSegmentedSourceAsync(
                    resolution,
                    integrity,
                    session,
                    OffsetAttemptProgress(reportAttemptProgress, attemptCount - 1),
                    sensitiveHeaders,
                    speedMeter,
                    reportTransferredBytes,
                    cancellationToken).ConfigureAwait(false);
                if (completed)
                    return new SegmentedDownloadAttemptResult(attemptCount, resolution);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                session.ResetSegmentedDownload();
                throw;
            }
            catch (Exception exception)
            {
                var failure = ClassifyException(exception);
                failures.Add(failure);
                session.ResetSegmentedDownload();
                logger.LogWarning(
                    exception,
                    "Segmented source stopped. ResolvedSourceKind={ResolvedSourceKind} ActualUrl={ActualUrl} FailureReason={FailureReason} FailureDisposition={FailureDisposition} StatusCode={StatusCode} FinalHost={FinalHost}",
                    resolution.ResolvedSourceKind,
                    DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl),
                    failure.Reason,
                    failure.Disposition,
                    failure.StatusCode is null ? null : (int)failure.StatusCode.Value,
                    failure.FinalHost);
                if (failure.Disposition is DownloadFailureDisposition.Abort)
                    throw failure;
            }
        }

        if (attemptCount > 0)
        {
            logger.LogInformation(
                "Segmented download exhausted all compatible candidates and will use the existing single-stream path. CandidateCount={CandidateCount} FailureReasons={FailureReasons} FinalFailureReason={FinalFailureReason} FinalStatusCode={FinalStatusCode} FinalHost={FinalHost}",
                candidates.Count(IsSegmentableCandidate),
                failures.Select(failure => failure.Reason).Distinct().Order().ToArray(),
                failures.LastOrDefault()?.Reason,
                failures.LastOrDefault()?.StatusCode is { } status ? (int)status : null,
                failures.LastOrDefault()?.FinalHost);
        }
        return new SegmentedDownloadAttemptResult(attemptCount, null);
    }

    private async Task<bool> TryDownloadSegmentedSourceAsync(
        ResolvedDownloadRequest resolution,
        DownloadIntegrityExpectation integrity,
        ResumableDownloadFileSession session,
        Action<int, long, long?>? reportAttemptProgress,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        Action<long>? reportTransferredBytes,
        CancellationToken cancellationToken)
    {
        using var segmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestedProbeEnd = integrity.ExpectedSize.HasValue
            ? Math.Min(integrity.ExpectedSize.Value - 1, SegmentedSplitThreshold - 1)
            : SegmentedSplitThreshold - 1;
        var probeRange = new DownloadSegmentRange(
            0,
            requestedProbeEnd,
            integrity.ExpectedSize,
            ChunkId: 0,
            OpenEnded: true);
        var probeAccepted = new TaskCompletionSource<SegmentedProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = 0;
        string? strongETag = null;

        var probeTask = ExecuteSegmentAttemptWithRetriesAsync(
            resolution,
            probeRange,
            async (context, token) =>
            {
                if (context.Response.StatusCode == HttpStatusCode.OK)
                {
                    if (Volatile.Read(ref prepared) != 0)
                        throw new SegmentedDownloadNotSupportedException("The source ignored Range after accepting an earlier partial response.");
                    probeAccepted.TrySetResult(SegmentedProbeResult.Sequential(context.Transport.FinalHost));
                    await session.WriteAsync(
                        context.Response,
                        context.Resolution,
                        attemptNumber: 1,
                        reportAttemptProgress,
                        token,
                        reportTransferredBytes).ConfigureAwait(false);
                    return;
                }

                var validated = ValidateSegmentResponse(context.Response, probeRange, strongETag);
                var responseStrongETag = GetStrongETag(context.Response);
                if (!integrity.IsVerifiable
                    && (validated.TotalLength < minimumSegmentedDownloadSize
                        || string.IsNullOrWhiteSpace(responseStrongETag)))
                {
                    throw new SegmentedDownloadNotSupportedException(
                        "An unverified segmented download requires a sufficiently large response with a strong ETag.");
                }
                if (Interlocked.CompareExchange(ref prepared, 1, 0) == 0)
                {
                    session.PrepareSegmentedDownload(validated.TotalLength);
                    strongETag = responseStrongETag;
                    probeAccepted.TrySetResult(new SegmentedProbeResult(
                        false,
                        validated,
                        context.Transport.FinalUri,
                        context.Transport.FinalHost,
                        context.Transport.HostSnapshot));
                }

                await session.WriteSegmentAsync(
                    context.Response.Content,
                    validated.Start,
                    validated.End,
                    attemptNumber: 1,
                    reportAttemptProgress,
                    token,
                    allowTrailingContent: true,
                    reportTransferredBytes: reportTransferredBytes).ConfigureAwait(false);
            },
            strongETagProvider: () => strongETag,
            sensitiveHeaders,
            speedMeter,
            segmentCancellation.Token);

        await Task.WhenAny(probeAccepted.Task, probeTask).ConfigureAwait(false);
        if (!probeAccepted.Task.IsCompletedSuccessfully)
            await probeTask.ConfigureAwait(false);

        var probe = await probeAccepted.Task.ConfigureAwait(false);
        if (probe.IsSequential)
        {
            await probeTask.ConfigureAwait(false);
            logger.LogDebug(
                "Segmented probe was ignored and the same HTTP 200 response completed the file. ResolvedSourceKind={ResolvedSourceKind} FinalHost={FinalHost}",
                resolution.ResolvedSourceKind,
                probe.FinalHost);
            return true;
        }

        var firstRange = probe.Range
            ?? throw SafeExceptionLog.Describe(new InvalidOperationException("The segmented probe did not provide a validated range."), "DownloadValidation", ("Reason", "The segmented probe did not provide a validated range."));
        var totalLength = firstRange.TotalLength;
        var remainingStart = firstRange.End + 1;
        if (remainingStart >= totalLength)
        {
            await probeTask.ConfigureAwait(false);
            await session.CompleteSegmentedDownloadAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var adaptiveSession = new AdaptiveSegmentDownloadSession(
            remainingStart,
            totalLength,
            SegmentedSplitThreshold);
        var pinnedResolution = PinFinalHttpsResolution(resolution, probe.FinalUri);
        var hostOrigin = DownloadHostConcurrencyController.NormalizeOrigin(probe.FinalUri!);
        var usefulWorkerCount = CalculateUsefulSegmentedWorkers(totalLength - remainingStart);
        var scheduling = segmentedDownloadCoordinator.Register(
            hostOrigin,
            usefulWorkerCount,
            () =>
            {
                var snapshot = GetGlobalConcurrencySnapshot();
                return new SegmentedGlobalConcurrencySnapshot(
                    snapshot.ActiveCount,
                    snapshot.WaitingCount,
                    snapshot.CurrentTarget);
            },
            hostConcurrencyController.GetSnapshot);
        const int initialWorkerCount = 1;
        var workerTasks = new List<Task>(usefulWorkerCount);
        var liveWorkers = 0;
        var peakWorkers = 0;
        var expansionCount = 0;
        string? lastExpansionSkipState = null;

        int SpawnWorker(
            bool additional,
            AdaptiveDownloadSegment initialSegment,
            DownloadHostConcurrencyController.DownloadAdmissionLease? preacquiredAdmission = null,
            Task? prerequisite = null)
        {
            var current = Interlocked.Increment(ref liveWorkers);
            UpdateMaximum(ref peakWorkers, current);
            workerTasks.Add(RunAdaptiveSegmentWorkerLaneAsync(
                prerequisite,
                pinnedResolution,
                adaptiveSession,
                initialSegment,
                session,
                reportAttemptProgress,
                reportTransferredBytes,
                strongETag,
                sensitiveHeaders,
                speedMeter,
                additional,
                scheduling,
                preacquiredAdmission,
                retirementReserved =>
                {
                    Interlocked.Decrement(ref liveWorkers);
                    if (additional)
                        scheduling.ReleaseAdditionalWorker(retirementReserved);
                    else
                        scheduling.ReleaseBaselineWorker();
                },
                segmentCancellation.Token));
            return current;
        }

        var initialGlobalSnapshot = GetGlobalConcurrencySnapshot();
        var initialHostSnapshot = hostConcurrencyController.GetSnapshot(hostOrigin);
        var initialSchedulingSnapshot = scheduling.Snapshot;
        logger.LogDebug(
            "Segmented download registered. SessionId={SessionId} ResolvedSourceKind={ResolvedSourceKind} FileSize={FileSize} InitialWorkerCount={InitialWorkerCount} InitialTargetWorkerCount={InitialTargetWorkerCount} ActiveFileCount={ActiveFileCount} InitialChunkCount={InitialChunkCount} SplitThreshold={SplitThreshold} GlobalActive={GlobalActive} GlobalWaiting={GlobalWaiting} GlobalTarget={GlobalTarget} HostActive={HostActive} HostWaiting={HostWaiting} HostTarget={HostTarget} FinalHost={FinalHost} StrongETag={HasStrongETag}",
            scheduling.SessionId,
            resolution.ResolvedSourceKind,
            totalLength,
            initialWorkerCount,
            initialSchedulingSnapshot.TargetWorkerCount,
            initialSchedulingSnapshot.ActiveSessionCount,
            adaptiveSession.TotalChunkCount + 1,
            SegmentedSplitThreshold,
            initialGlobalSnapshot.ActiveCount,
            initialGlobalSnapshot.WaitingCount,
            initialGlobalSnapshot.CurrentTarget,
            initialHostSnapshot.ActiveCount,
            initialHostSnapshot.WaitingCount,
            initialHostSnapshot.CurrentTarget,
            probe.FinalHost,
            strongETag is not null);

        try
        {
            if (!adaptiveSession.TryTake(out var baselineSegment))
                throw SafeExceptionLog.Describe(new InvalidOperationException("The adaptive segmented session did not contain baseline work."), "DownloadValidation", ("Reason", "The adaptive segmented session did not contain baseline work."));
            SpawnWorker(
                additional: false,
                baselineSegment!,
                prerequisite: probeTask);

            while (!adaptiveSession.IsComplete || workerTasks.Any(task => !task.IsCompleted))
            {
                var failedWorker = workerTasks.FirstOrDefault(task => task.IsFaulted);
                if (failedWorker is not null)
                    await failedWorker.ConfigureAwait(false);

                if (!adaptiveSession.IsComplete)
                {
                    var addedWorker = false;
                    string? expansionBlockReason = null;
                    while (scheduling.TryReserveAdditionalWorker(out var schedule))
                    {
                        var opportunityAdmission = TryAcquireOpportunisticAdmission(probe.FinalUri!);
                        if (opportunityAdmission is null)
                        {
                            scheduling.CancelAdditionalWorkerReservation();
                            expansionBlockReason = "NoIdleAdmission";
                            break;
                        }

                        if (!adaptiveSession.TryTakeQueuedOrSplit(
                                out var workerSegment,
                                out var split))
                        {
                            opportunityAdmission.Dispose();
                            scheduling.CancelAdditionalWorkerReservation();
                            expansionBlockReason = "NoAvailableOrSplittableRange";
                            break;
                        }
                        if (split is { } createdSplit)
                        {
                            logger.LogDebug(
                                "Segmented range dynamically split. SessionId={SessionId} SourceChunkId={SourceChunkId} SourceStart={SourceStart} SourceOriginalEnd={SourceOriginalEnd} SourceRetainedEnd={SourceRetainedEnd} NewChunkId={NewChunkId} NewStart={NewStart} NewEnd={NewEnd} SplitKind={SplitKind}",
                                scheduling.SessionId,
                                createdSplit.SourceChunkId,
                                createdSplit.SourceStart,
                                createdSplit.SourceOriginalEnd,
                                createdSplit.SourceRetainedEnd,
                                createdSplit.NewChunkId,
                                createdSplit.NewStart,
                                createdSplit.NewEnd,
                                createdSplit.SourceWasActive ? "InFlightTail" : "QueuedRange");
                        }

                        var previousWorkers = Volatile.Read(ref liveWorkers);
                        int currentWorkers;
                        scheduling.ConfirmAdditionalWorkerActivated();
                        try
                        {
                            currentWorkers = SpawnWorker(
                                additional: true,
                                workerSegment!,
                                opportunityAdmission);
                            opportunityAdmission = null;
                        }
                        catch
                        {
                            adaptiveSession.Return(workerSegment!);
                            opportunityAdmission?.Dispose();
                            scheduling.CancelAdditionalWorkerReservation(activated: true);
                            throw;
                        }
                        schedule = scheduling.Snapshot;
                        addedWorker = true;
                        expansionCount++;
                        logger.LogDebug(
                            "Segmented download worker activated by fair scheduler. SessionId={SessionId} PreviousWorkerCount={PreviousWorkerCount} CurrentWorkerCount={CurrentWorkerCount} TargetWorkerCount={TargetWorkerCount} ActiveFileCount={ActiveFileCount} GlobalSegmentedWorkerCount={GlobalSegmentedWorkerCount} GlobalPeakSegmentedWorkerCount={GlobalPeakSegmentedWorkerCount} WorkSource={WorkSource} QueuedChunkCount={QueuedChunkCount} ActiveChunkCount={ActiveChunkCount} ExpansionCount={ExpansionCount} FairnessRounds={FairnessRounds} GlobalActive={GlobalActive} GlobalWaiting={GlobalWaiting} GlobalTarget={GlobalTarget} HostActive={HostActive} HostWaiting={HostWaiting} HostTarget={HostTarget} FinalHost={FinalHost}",
                            scheduling.SessionId,
                            previousWorkers,
                            currentWorkers,
                            schedule.TargetWorkerCount,
                            schedule.ActiveSessionCount,
                            schedule.GlobalSegmentedWorkerCount,
                            schedule.GlobalPeakSegmentedWorkerCount,
                            split.HasValue ? "SplitRange" : "QueuedRange",
                            adaptiveSession.QueuedCount,
                            adaptiveSession.ActiveCount,
                            expansionCount,
                            schedule.FairnessRounds,
                            schedule.Global.ActiveCount,
                            schedule.Global.WaitingCount,
                            schedule.Global.CurrentTarget,
                            schedule.Host.ActiveCount,
                            schedule.Host.WaitingCount,
                            schedule.Host.CurrentTarget,
                            probe.FinalHost);
                    }

                    if (!addedWorker)
                    {
                        var schedule = scheduling.Snapshot;
                        var reason = expansionBlockReason
                            ?? (schedule.Global.WaitingCount > 0
                            ? "GlobalOrdinaryWaiter"
                            : schedule.Host.WaitingCount > 0
                                ? "HostOrdinaryWaiter"
                                : schedule.LiveWorkerCount >= schedule.TargetWorkerCount
                                    ? "FairTargetReached"
                                    : "NoAvailableOrSplittableRange");
                        var skipState = string.Join(
                            '|',
                            reason,
                            schedule.LiveWorkerCount,
                            schedule.TargetWorkerCount,
                            schedule.ActiveSessionCount,
                            schedule.Global.ActiveCount,
                            schedule.Global.WaitingCount,
                            schedule.Global.CurrentTarget,
                            schedule.Host.ActiveCount,
                            schedule.Host.WaitingCount,
                            schedule.Host.CurrentTarget);
                        if (!string.Equals(skipState, lastExpansionSkipState, StringComparison.Ordinal))
                        {
                            lastExpansionSkipState = skipState;
                            logger.LogTrace(
                                "Segmented fair scheduler did not add a worker. SessionId={SessionId} Reason={Reason} LiveWorkerCount={LiveWorkerCount} TargetWorkerCount={TargetWorkerCount} ActiveFileCount={ActiveFileCount} GlobalActive={GlobalActive} GlobalWaiting={GlobalWaiting} GlobalTarget={GlobalTarget} HostActive={HostActive} HostWaiting={HostWaiting} HostTarget={HostTarget} FinalHost={FinalHost}",
                                scheduling.SessionId,
                                reason,
                                schedule.LiveWorkerCount,
                                schedule.TargetWorkerCount,
                                schedule.ActiveSessionCount,
                                schedule.Global.ActiveCount,
                                schedule.Global.WaitingCount,
                                schedule.Global.CurrentTarget,
                                schedule.Host.ActiveCount,
                                schedule.Host.WaitingCount,
                                schedule.Host.CurrentTarget,
                                probe.FinalHost);
                        }
                    }
                    else
                    {
                        lastExpansionSkipState = null;
                    }
                }

                if (!adaptiveSession.IsComplete || workerTasks.Any(task => !task.IsCompleted))
                {
                    await Task.Delay(
                        SegmentedExpansionScanInterval,
                        timeProvider,
                        segmentCancellation.Token).ConfigureAwait(false);
                }
            }

            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            await session.CompleteSegmentedDownloadAsync(cancellationToken).ConfigureAwait(false);
            var completedSchedulingSnapshot = scheduling.Snapshot;
            logger.LogDebug(
                "Segmented download completed. SessionId={SessionId} ResolvedSourceKind={ResolvedSourceKind} FileSize={FileSize} InitialWorkerCount={InitialWorkerCount} PeakWorkerCount={PeakWorkerCount} ExpansionCount={ExpansionCount} CompletedChunkCount={CompletedChunkCount} ActiveFileCount={ActiveFileCount} GlobalPeakSegmentedWorkerCount={GlobalPeakSegmentedWorkerCount} AdditionalWorkersGranted={AdditionalWorkersGranted} AdditionalWorkersReturned={AdditionalWorkersReturned} FairnessRounds={FairnessRounds} FinalHost={FinalHost}",
                scheduling.SessionId,
                resolution.ResolvedSourceKind,
                totalLength,
                initialWorkerCount,
                Volatile.Read(ref peakWorkers),
                expansionCount,
                adaptiveSession.CompletedChunks + 1,
                completedSchedulingSnapshot.ActiveSessionCount,
                completedSchedulingSnapshot.GlobalPeakSegmentedWorkerCount,
                completedSchedulingSnapshot.AdditionalWorkersGranted,
                completedSchedulingSnapshot.AdditionalWorkersReturned,
                completedSchedulingSnapshot.FairnessRounds,
                probe.FinalHost);
            return true;
        }
        catch
        {
            segmentCancellation.Cancel();
            await ObserveSegmentTasksAsync(workerTasks).ConfigureAwait(false);
            throw;
        }
        finally
        {
            var finalSchedulingSnapshot = scheduling.Snapshot;
            scheduling.Dispose();
            logger.LogTrace(
                "Segmented download unregistered. SessionId={SessionId} RemainingActiveFileCount={ActiveFileCount} LiveWorkerCount={LiveWorkerCount} TargetWorkerCount={TargetWorkerCount} AdditionalWorkersGranted={AdditionalWorkersGranted} AdditionalWorkersReturned={AdditionalWorkersReturned} FinalHost={FinalHost}",
                scheduling.SessionId,
                Math.Max(0, finalSchedulingSnapshot.ActiveSessionCount - 1),
                finalSchedulingSnapshot.LiveWorkerCount,
                finalSchedulingSnapshot.TargetWorkerCount,
                finalSchedulingSnapshot.AdditionalWorkersGranted,
                finalSchedulingSnapshot.AdditionalWorkersReturned,
                probe.FinalHost);
        }
    }

    private async Task RunAdaptiveSegmentWorkerLaneAsync(
        Task? prerequisite,
        ResolvedDownloadRequest resolution,
        AdaptiveSegmentDownloadSession adaptiveSession,
        AdaptiveDownloadSegment initialSegment,
        ResumableDownloadFileSession session,
        Action<int, long, long?>? reportAttemptProgress,
        Action<long>? reportTransferredBytes,
        string? strongETag,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        bool additional,
        SegmentedDownloadCoordinator.Registration scheduling,
        DownloadHostConcurrencyController.DownloadAdmissionLease? preacquiredAdmission,
        Action<bool> workerCompleted,
        CancellationToken cancellationToken)
    {
        var retirementReserved = false;
        try
        {
            if (prerequisite is not null)
                await prerequisite.ConfigureAwait(false);
            retirementReserved = await DownloadAdaptiveSegmentWorkerAsync(
                resolution,
                adaptiveSession,
                initialSegment,
                session,
                reportAttemptProgress,
                reportTransferredBytes,
                strongETag,
                sensitiveHeaders,
                speedMeter,
                additional,
                scheduling,
                preacquiredAdmission,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            workerCompleted(retirementReserved);
        }
    }

    private async Task<bool> DownloadAdaptiveSegmentWorkerAsync(
        ResolvedDownloadRequest resolution,
        AdaptiveSegmentDownloadSession adaptiveSession,
        AdaptiveDownloadSegment initialSegment,
        ResumableDownloadFileSession session,
        Action<int, long, long?>? reportAttemptProgress,
        Action<long>? reportTransferredBytes,
        string? strongETag,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        bool additional,
        SegmentedDownloadCoordinator.Registration scheduling,
        DownloadHostConcurrencyController.DownloadAdmissionLease? preacquiredAdmission,
        CancellationToken cancellationToken)
    {
        AdaptiveDownloadSegment? segment = initialSegment;
        try
        {
            while (segment is not null || adaptiveSession.TryTake(out segment))
            {
                var attemptedRequest = false;
                while (segment!.TryGetAttemptRange(out _))
                {
                    if (additional
                        && attemptedRequest
                        && scheduling.TryBeginAdditionalWorkerRetirement(out _))
                    {
                        adaptiveSession.Return(segment);
                        return true;
                    }

                    (DownloadSegmentRange Requested, ValidatedSegmentRange Validated) result;
                    try
                    {
                        var reservedAdmission = preacquiredAdmission;
                        preacquiredAdmission = null;
                        result = await ExecuteSegmentAttemptWithRetriesAsync(
                            resolution,
                            () =>
                            {
                                if (!segment.TryGetAttemptRange(out var current))
                                    throw new AdaptiveSegmentAlreadyCompleteException();
                                return new DownloadSegmentRange(
                                    current.Start,
                                    current.End,
                                    current.TotalLength,
                                    current.ChunkId);
                            },
                            async (context, requestedRange, token) =>
                            {
                                var responseRange = ValidateSegmentResponse(
                                    context.Response,
                                    requestedRange,
                                    strongETag) with
                                {
                                    FinalHost = context.Transport.FinalHost
                                };
                                await session.WriteSegmentAsync(
                                    context.Response.Content,
                                    responseRange.Start,
                                    responseRange.End,
                                    attemptNumber: 1,
                                    reportAttemptProgress,
                                    token,
                                    allowTrailingContent: true,
                                    adaptiveSegment: segment,
                                    reportTransferredBytes: reportTransferredBytes).ConfigureAwait(false);
                                return (Requested: requestedRange, Validated: responseRange);
                            },
                            () => strongETag,
                            sensitiveHeaders,
                            speedMeter,
                            cancellationToken,
                            opportunisticAdmission: additional,
                            preacquiredAdmission: reservedAdmission).ConfigureAwait(false);
                    }
                    catch (OpportunisticDownloadAdmissionUnavailableException)
                    {
                        adaptiveSession.Return(segment);
                        return false;
                    }
                    catch (AdaptiveSegmentAlreadyCompleteException)
                    {
                        break;
                    }
                    attemptedRequest = true;

                    if (result.Validated.End < result.Requested.End && !segment.IsComplete)
                    {
                        logger.LogTrace(
                            "Segmented response returned a legal short range; the remaining suffix will continue from the adaptive cursor. ChunkId={ChunkId} RangeStart={RangeStart} RequestedEnd={RequestedEnd} ReturnedEnd={ReturnedEnd} NextOffset={NextOffset} LogicalEnd={LogicalEnd} FinalHost={FinalHost}",
                            segment.ChunkId,
                            result.Requested.Start,
                            result.Requested.End,
                            result.Validated.End,
                            segment.NextOffset,
                            segment.LogicalEnd,
                            result.Validated.FinalHost);
                    }
                }

                adaptiveSession.Complete(segment);
                segment = null;
                if (additional
                    && scheduling.TryBeginAdditionalWorkerRetirement(out _))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            preacquiredAdmission?.Dispose();
        }
    }

    private async Task<T> ExecuteSegmentAttemptWithRetriesAsync<T>(
        ResolvedDownloadRequest resolution,
        Func<DownloadSegmentRange> rangeProvider,
        Func<DownloadAttemptContext, DownloadSegmentRange, CancellationToken, Task<T>> operation,
        Func<string?> strongETagProvider,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        CancellationToken cancellationToken,
        bool opportunisticAdmission = false,
        DownloadHostConcurrencyController.DownloadAdmissionLease? preacquiredAdmission = null)
    {
        await using var ownedAdmission = preacquiredAdmission;
        for (var attempt = 1; attempt <= retryOptions.MaxAttemptsPerSource; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = rangeProvider();
            try
            {
                var reservedAdmission = attempt == 1 ? ownedAdmission : null;
                return await ExecuteAttemptAsync(
                    resolution,
                    attempt,
                    (context, token) => operation(context, range, token),
                    noResultStatus: null,
                    cancellationToken,
                    configureRequest: (request, _) => ConfigureSegmentRequest(request, range, strongETagProvider()),
                    allowResponseStatus: null,
                    sensitiveHeaders,
                    speedMeter,
                    enableSlowBodyWatchdog: true,
                    opportunisticAdmission,
                    preacquiredAdmission: reservedAdmission).ConfigureAwait(false);
            }
            catch (OpportunisticDownloadAdmissionUnavailableException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = ClassifyException(exception);
                if (failure.IsHostThrottled)
                    failure.WithDisposition(DownloadFailureDisposition.SwitchSource);
                RecordHostResult(resolution, failure);
                hostHealthTracker.RecordFailure(
                    resolution.ResolvedSourceKind,
                    failure.FinalHost ?? GetCandidateHost(resolution),
                    failure.Reason,
                    failure.StatusCode);
                var retry = failure.Disposition is DownloadFailureDisposition.RetryCurrentSource
                    && attempt < retryOptions.MaxAttemptsPerSource;
                logger.LogWarning(
                    failure,
                    "Segmented range attempt failed. ResolvedSourceKind={ResolvedSourceKind} ChunkId={ChunkId} RangeStart={RangeStart} RangeEnd={RangeEnd} TotalLength={TotalLength} Attempt={Attempt} FailureReason={FailureReason} FailureDisposition={FailureDisposition} StatusCode={StatusCode} FinalHost={FinalHost} Action={Action}",
                    resolution.ResolvedSourceKind,
                    range.ChunkId,
                    range.Start,
                    range.End,
                    range.TotalLength,
                    attempt,
                    failure.Reason,
                    failure.Disposition,
                    failure.StatusCode is null ? null : (int)failure.StatusCode.Value,
                    failure.FinalHost,
                    retry ? "RetryRange" : failure.Disposition is DownloadFailureDisposition.Abort ? "Abort" : "StopSource");
                if (!retry)
                    throw failure;
                await Task.Delay(GetRetryDelay(failure, attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw SafeExceptionLog.Describe(new InvalidOperationException("The segmented range retry loop exited unexpectedly."), "DownloadValidation", ("Reason", "The segmented range retry loop exited unexpectedly."));
    }

    private Task<T> ExecuteSegmentAttemptWithRetriesAsync<T>(
        ResolvedDownloadRequest resolution,
        DownloadSegmentRange range,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Func<string?> strongETagProvider,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        CancellationToken cancellationToken) =>
        ExecuteSegmentAttemptWithRetriesAsync(
            resolution,
            () => range,
            (context, _, token) => operation(context, token),
            strongETagProvider,
            sensitiveHeaders,
            speedMeter,
            cancellationToken);

    private Task ExecuteSegmentAttemptWithRetriesAsync(
        ResolvedDownloadRequest resolution,
        DownloadSegmentRange range,
        Func<DownloadAttemptContext, CancellationToken, Task> operation,
        Func<string?> strongETagProvider,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        CancellationToken cancellationToken) =>
        ExecuteSegmentAttemptWithRetriesAsync(
            resolution,
            range,
            async (context, token) =>
            {
                await operation(context, token).ConfigureAwait(false);
                return true;
            },
            strongETagProvider,
            sensitiveHeaders,
            speedMeter,
            cancellationToken);

    private bool ShouldUseSegmentedDownload(
        IReadOnlyList<ResolvedDownloadRequest> candidates,
        DownloadIntegrityExpectation integrity,
        DownloadFileOptions? options)
    {
        if (!integrity.HasStrongHash
            && !(options?.AllowUnverifiedSegmentedDownload == true && !integrity.IsVerifiable)
            || integrity.ExpectedSize.HasValue && integrity.ExpectedSize.Value < minimumSegmentedDownloadSize)
            return false;
        if ((options?.PersistenceMode ?? DownloadPersistenceMode.TaskScopedResumable)
            is not DownloadPersistenceMode.TaskScopedResumable)
            return false;
        if (GetGlobalConcurrencySnapshot().CurrentTarget < 2)
            return false;
        // Do not skip a preferred single-stream mirror to probe a later source.
        return candidates.Count > 0 && IsSegmentableCandidate(candidates[0])
            && GetCandidateCooldown(candidates[0]) <= TimeSpan.Zero && !IsCandidateDegraded(candidates[0]);
    }

    private static int CalculateUsefulSegmentedWorkers(long remainingLength) =>
        (int)Math.Min(
            MaximumSegmentedWorkersPerFile,
            Math.Max(
                1d,
                Math.Ceiling(
                    remainingLength
                    / (SegmentedSplitThreshold * AdaptiveSegmentDownloadSession.TailSplitFraction))));

    private sealed class AdaptiveSegmentAlreadyCompleteException : Exception;

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (current >= value
                || Interlocked.CompareExchange(ref maximum, value, current) == current)
            {
                return;
            }
        }
    }

    private static void ConfigureSegmentRequest(
        HttpRequestMessage request,
        DownloadSegmentRange range,
        string? strongETag)
    {
        request.Headers.Range = new RangeHeaderValue(
            range.Start,
            range.OpenEnded ? null : range.End);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (!string.IsNullOrWhiteSpace(strongETag))
            request.Headers.TryAddWithoutValidation("If-Range", strongETag);
    }

    private static ValidatedSegmentRange ValidateSegmentResponse(
        HttpResponseMessage response,
        DownloadSegmentRange expected,
        string? strongETag)
    {
        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent
            || response.Content.Headers.ContentEncoding.Count != 0
            || contentRange is null
            || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From != expected.Start
            || !contentRange.To.HasValue
            || contentRange.To.Value < expected.Start
            || !expected.OpenEnded && contentRange.To.Value > expected.End
            || !contentRange.Length.HasValue
            || contentRange.Length.Value <= contentRange.To.Value
            || expected.TotalLength.HasValue && contentRange.Length.Value != expected.TotalLength.Value)
        {
            throw new SegmentedDownloadNotSupportedException("The partial response did not describe the requested byte range.");
        }

        var actualLength = contentRange.To.Value - expected.Start + 1;
        if (response.Content.Headers.ContentLength.HasValue
            && response.Content.Headers.ContentLength.Value != actualLength)
        {
            throw new SegmentedDownloadNotSupportedException("The partial response Content-Length did not match Content-Range.");
        }

        var responseETag = GetStrongETag(response);
        if (strongETag is not null
            && !string.Equals(strongETag, responseETag, StringComparison.Ordinal))
        {
            throw new SegmentedDownloadNotSupportedException("The source representation changed during segmented download.");
        }

        return new ValidatedSegmentRange(
            expected.Start,
            expected.OpenEnded ? Math.Min(contentRange.To.Value, expected.End) : contentRange.To.Value,
            contentRange.Length.Value,
            null);
    }

    private static string? GetStrongETag(HttpResponseMessage response)
    {
        var entityTag = response.Headers.ETag;
        return entityTag is not null && !entityTag.IsWeak ? entityTag.ToString() : null;
    }

    private static bool IsSegmentableCandidate(ResolvedDownloadRequest resolution)
    {
        if (!Uri.TryCreate(resolution.ActualUrl, UriKind.Absolute, out var uri))
            return false;
        return !uri.Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            && !resolution.ResolvedSourceKind.StartsWith("BmclApi", StringComparison.Ordinal);
    }

    private static ResolvedDownloadRequest PinFinalHttpsResolution(
        ResolvedDownloadRequest resolution,
        Uri? finalUri)
    {
        if (finalUri is null
            || !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return resolution;
        }
        return resolution with { ActualUrl = finalUri.AbsoluteUri };
    }

    private static async Task ObserveSegmentTasksAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static Action<int, long, long?>? OffsetAttemptProgress(
        Action<int, long, long?>? progress,
        int offset)
    {
        if (progress is null || offset == 0)
            return progress;
        return (attempt, progressedBytes, totalBytes) =>
            progress(attempt + offset, progressedBytes, totalBytes);
    }

    private readonly record struct DownloadSegmentRange(
        long Start,
        long End,
        long? TotalLength,
        int ChunkId,
        bool OpenEnded = false);
    private readonly record struct ValidatedSegmentRange(
        long Start,
        long End,
        long TotalLength,
        string? FinalHost);
    private readonly record struct SegmentedProbeResult(
        bool IsSequential,
        ValidatedSegmentRange? Range,
        Uri? FinalUri,
        string? FinalHost,
        DownloadHostConcurrencySnapshot? HostSnapshot)
    {
        public static SegmentedProbeResult Sequential(string finalHost) =>
            new(true, null, null, finalHost, null);
    }
    private readonly record struct SegmentedDownloadAttemptResult(
        int AttemptCount,
        ResolvedDownloadRequest? Resolution);
    private sealed class SegmentedDownloadNotSupportedException(string message)
        : DownloadAttemptException(
            DownloadFailureDisposition.SwitchSource,
            DownloadFailureReason.InvalidContent,
            message);

    /// <summary>
    /// 按解析后的候选源和每源重试次数执行操作，并汇总所有可恢复失败用于最终诊断。
    /// </summary>
    private async Task<DownloadLookupResult<T>> ExecuteCoreAsync<T>(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Func<HttpStatusCode, bool>? noResultStatus,
        bool lookupMode,
        Action<HttpRequestMessage, ResolvedDownloadRequest>? configureRequest,
        Func<HttpStatusCode, bool>? allowResponseStatus,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        DownloadExecutionMode executionMode,
        IReadOnlyList<ResolvedDownloadRequest>? fileCandidates,
        CancellationToken cancellationToken,
        ResolvedDownloadRequest? singleCandidate = null,
        Action? onRecoverableFailure = null,
        bool logExhaustion = true)
    {
        using var networkObservation = DownloadNetworkObservation.Current is null ? new DownloadNetworkObservation() : null;
        if (executionMode is DownloadExecutionMode.FileSourceRounds)
        {
            if (lookupMode || noResultStatus is not null)
                throw SafeExceptionLog.Describe(new InvalidOperationException("File source rounds do not support lookup requests."), "DownloadValidation", ("Reason", "File source rounds do not support lookup requests."));

            return await ExecuteFileSourceRoundsAsync(
                originalUrl,
                fileCandidates ?? throw new InvalidOperationException("File source rounds require resolved candidates."),
                operation,
                configureRequest,
                allowResponseStatus,
                sensitiveHeaders,
                speedMeter,
                cancellationToken).ConfigureAwait(false);
        }

        using var diagnosticRequest = DownloadDiagnostics.BeginRequest(logger);
        // 候选顺序已经包含用户偏好和镜像回退策略；每个源内部重试耗尽后才切换下一源。
        var candidates = singleCandidate is null
            ? (await PrepareCandidatesAsync(ResolveFileCandidates([originalUrl], preference, categoryHint), preference, cancellationToken).ConfigureAwait(false)).ToList()
            : [singleCandidate];
        candidates = candidates.OrderBy(GetCandidateCooldown).ThenBy(IsCandidateDegraded).ToList();
        var failures = new List<Exception>();
        var sourceFailures = new List<SourceFailure>();
        var noResultSourceCount = 0;
        ResolvedDownloadRequest? lastResolution = null;

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var resolution = candidates[candidateIndex];
            DownloadNetworkObservation.SelectCandidate(resolution.ActualUrl);
            lastResolution = resolution;
            var sourceReportedNoResult = false;
            var slowDisconnectCount = 0;
            int? slowWatchdogDisabledAttempt = null;
            var mirrorForbiddenRetried = false;

            for (var attempt = 1; attempt <= retryOptions.MaxAttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var value = await ExecuteAttemptAsync(
                        resolution,
                        attempt,
                        operation,
                        noResultStatus,
                        cancellationToken,
                        configureRequest,
                        allowResponseStatus,
                        sensitiveHeaders,
                        speedMeter,
                        enableSlowBodyWatchdog: slowWatchdogDisabledAttempt != attempt).ConfigureAwait(false);
                    hostHealthTracker.RecordSuccess(resolution.ResolvedSourceKind, GetCandidateHost(resolution));
                    LogResolvedRequest(resolution, attempt);
                    LogRecoveredRequest(resolution, attempt, failures);
                    return DownloadLookupResult<T>.Success(value);
                }
                catch (DownloadNoResultException exception)
                {
                    sourceReportedNoResult = true;
                    onRecoverableFailure?.Invoke();
                    logger.LogDebug(
                        exception,
                        "Download source explicitly reported no result. RequestedSourcePreference={RequestedSourcePreference} ResourceCategory={ResourceCategory} OriginalUrl={OriginalUrl} ActualUrl={ActualUrl} ResolvedSourceKind={ResolvedSourceKind} Attempt={Attempt}",
                        preference,
                        resolution.ResourceCategory,
                        DownloadUriLogSanitizer.Sanitize(resolution.OriginalUrl),
                        DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl),
                        resolution.ResolvedSourceKind,
                        attempt);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var failure = ClassifyException(exception);
                    if (failure.IsTransientMirrorForbidden)
                    {
                        if (mirrorForbiddenRetried) failure.WithDisposition(DownloadFailureDisposition.SwitchSource);
                        mirrorForbiddenRetried = true;
                    }
                    if (failure.Reason is DownloadFailureReason.BodyTooSlow)
                    {
                        slowDisconnectCount++;
                        if (slowDisconnectCount >= 2)
                        {
                            if (candidateIndex + 1 < candidates.Count)
                            {
                                failure.WithDisposition(DownloadFailureDisposition.SwitchSource);
                            }
                            else if (attempt < retryOptions.MaxAttemptsPerSource)
                            {
                                slowWatchdogDisabledAttempt = attempt + 1;
                                failure.WithDisposition(DownloadFailureDisposition.RetryCurrentSource);
                                logger.LogDebug(
                                    "Download slow-body watchdog disabled for the final fallback attempt. RequestedSourcePreference={RequestedSourcePreference} ResourceCategory={ResourceCategory} ActualUrl={ActualUrl} ResolvedSourceKind={ResolvedSourceKind} Attempt={Attempt}",
                                    preference,
                                    resolution.ResourceCategory,
                                    DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl),
                                    resolution.ResolvedSourceKind,
                                    attempt + 1);
                            }
                        }
                    }
                    RecordHostResult(resolution, failure);
                    hostHealthTracker.RecordFailure(
                        resolution.ResolvedSourceKind,
                        failure.FinalHost ?? GetCandidateHost(resolution),
                        failure.Reason,
                        failure.StatusCode);
                    // Abort 表示请求本身或本地写入不可通过换源修复；其余分类决定重试当前源还是直接换源。
                    if (failure.Disposition is DownloadFailureDisposition.Abort)
                    {
                        LogFailure(resolution, attempt, failure, retryCurrentSource: false);
                        throw failure;
                    }

                    onRecoverableFailure?.Invoke();
                    failures.Add(failure);
                    sourceFailures.Add(new SourceFailure(resolution.ResolvedSourceKind, failure.Reason, failure.StatusCode));
                    var retryCurrentSource = failure.Disposition is DownloadFailureDisposition.RetryCurrentSource
                        && attempt < retryOptions.MaxAttemptsPerSource
                        && !(failure.StatusCode is HttpStatusCode.TooManyRequests
                            && candidates.Skip(candidateIndex + 1).Any(candidate => GetCandidateCooldown(candidate) <= TimeSpan.Zero));
                    LogFailure(resolution, attempt, failure, retryCurrentSource);

                    if (!retryCurrentSource)
                        break;

                    var retryDelay = GetRetryDelay(failure, attempt);
                    logger.LogInformation("Download retry scheduled. RetryDelay={RetryDelay} Attempt={Attempt}", retryDelay, attempt);
                    if (!failure.IsHostThrottled)
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (sourceReportedNoResult)
                noResultSourceCount++;
        }

        if (lookupMode && noResultSourceCount == candidates.Count)
            return DownloadLookupResult<T>.NotFound();

        var finalResolution = lastResolution
            ?? throw new InvalidOperationException($"No download candidates were available for {originalUrl}.");
        var finalException = failures.LastOrDefault()
            ?? new InvalidOperationException($"No download source returned a usable result for {originalUrl}.");
        if (logExhaustion)
            LogExhaustedRequest(finalResolution, sourceFailures);
        throw new DownloadSourceRequestException(finalResolution, finalException, failures);
    }

    private async Task<DownloadLookupResult<T>> ExecuteFileSourceRoundsAsync<T>(
        string originalUrl,
        IReadOnlyList<ResolvedDownloadRequest> candidates,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Action<HttpRequestMessage, ResolvedDownloadRequest>? configureRequest,
        Func<HttpStatusCode, bool>? allowResponseStatus,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        CancellationToken cancellationToken)
    {
        using var diagnosticRequest = DownloadDiagnostics.BeginRequest(logger);
        var activeCandidateIndexes = Enumerable.Range(0, candidates.Count).ToList();
        var failures = new List<Exception>();
        var sourceFailures = new List<SourceFailure>();
        var maxAttemptsPerFileSource = Math.Max(1, retryOptions.MaxFileAttemptsPerSource);
        var attempts = new int[candidates.Count];
        var mirrorForbiddenRetried = new HashSet<int>();
        ResolvedDownloadRequest? lastResolution = null;

        for (var round = 1; activeCandidateIndexes.Count > 0; round++)
        {
            activeCandidateIndexes = activeCandidateIndexes.OrderBy(index => GetCandidateCooldown(candidates[index]))
                .ThenBy(index => IsCandidateDegraded(candidates[index])).ToList();
            var nextRoundCandidateIndexes = new List<int>();
            var nextRoundFailures = new List<DownloadAttemptException>();

            for (var position = 0; position < activeCandidateIndexes.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateIndex = activeCandidateIndexes[position];
                var resolution = candidates[candidateIndex];
                var cooldown = GetCandidateCooldown(resolution);
                if (cooldown > TimeSpan.Zero && activeCandidateIndexes.Any(index => index != candidateIndex
                    && GetCandidateCooldown(candidates[index]) <= TimeSpan.Zero))
                {
                    // Defer this candidate without spending its attempt budget.
                    nextRoundCandidateIndexes.Add(candidateIndex);
                    logger.LogInformation("Download source deferred during host cooldown. SourceKind={SourceKind} CooldownMs={CooldownMs}",
                        resolution.ResolvedSourceKind, cooldown.TotalMilliseconds);
                    continue;
                }
                DownloadNetworkObservation.SelectCandidate(resolution.ActualUrl);
                lastResolution = resolution;

                try
                {
                    var attempt = ++attempts[candidateIndex];
                    var value = await ExecuteAttemptAsync(
                        resolution,
                        attempt,
                        operation,
                        noResultStatus: null,
                        cancellationToken: cancellationToken,
                        configureRequest: configureRequest,
                        allowResponseStatus: allowResponseStatus,
                        sensitiveHeaders: sensitiveHeaders,
                        speedMeter: speedMeter,
                        enableSlowBodyWatchdog: true).ConfigureAwait(false);
                    hostHealthTracker.RecordSuccess(resolution.ResolvedSourceKind, GetCandidateHost(resolution));
                    LogResolvedRequest(resolution, attempt);
                    LogRecoveredRequest(resolution, attempt, failures);
                    return DownloadLookupResult<T>.Success(value);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var failure = ClassifyException(exception);
                    if (failure.IsTransientMirrorForbidden && !mirrorForbiddenRetried.Add(candidateIndex))
                        failure.WithDisposition(DownloadFailureDisposition.SwitchSource);
                    RecordHostResult(resolution, failure);
                    hostHealthTracker.RecordFailure(
                        resolution.ResolvedSourceKind,
                        failure.FinalHost ?? GetCandidateHost(resolution),
                        failure.Reason,
                        failure.StatusCode);
                    if (failure.Disposition is DownloadFailureDisposition.Abort)
                    {
                        LogFailure(resolution, attempts[candidateIndex], failure, retryCurrentSource: false);
                        throw failure;
                    }

                    failures.Add(failure);
                    sourceFailures.Add(new SourceFailure(resolution.ResolvedSourceKind, failure.Reason, failure.StatusCode));
                    var retryInLaterRound = failure.Disposition is DownloadFailureDisposition.RetryCurrentSource
                        && attempts[candidateIndex] < maxAttemptsPerFileSource;
                    if (retryInLaterRound)
                    {
                        nextRoundCandidateIndexes.Add(candidateIndex);
                        nextRoundFailures.Add(failure);
                    }

                    LogFailure(
                        resolution,
                        attempts[candidateIndex],
                        failure,
                        retryCurrentSource: false,
                        sourceRound: round,
                        retryInLaterRound: retryInLaterRound,
                        remainingCandidateCount: activeCandidateIndexes.Count - position - 1);
                }
            }

            if (nextRoundCandidateIndexes.Count == 0)
                break;

            var retryDelay = nextRoundFailures
                .Where(failure => !failure.IsHostThrottled)
                .Select(failure => GetRetryDelay(failure, round))
                .DefaultIfEmpty(TimeSpan.Zero)
                .Min();
            logger.LogInformation(
                "File download source round exhausted. OriginalUrl={OriginalUrl} CompletedRound={CompletedRound} MaxFileAttemptsPerSource={MaxFileAttemptsPerSource} RetryCandidateCount={RetryCandidateCount} RetryDelay={RetryDelay}",
                DownloadUriLogSanitizer.Sanitize(originalUrl),
                round,
                maxAttemptsPerFileSource,
                nextRoundCandidateIndexes.Count,
                retryDelay);
            if (retryDelay > TimeSpan.Zero)
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            activeCandidateIndexes = nextRoundCandidateIndexes;
        }

        var finalResolution = lastResolution
            ?? throw new InvalidOperationException($"No download candidates were available for {originalUrl}.");
        var finalException = failures.LastOrDefault()
            ?? new InvalidOperationException($"No download source returned a usable result for {originalUrl}.");
        LogExhaustedRequest(finalResolution, sourceFailures);
        throw new DownloadSourceRequestException(finalResolution, finalException, failures);
    }

    private static IReadOnlyList<ResolvedDownloadRequest> ResolveFileCandidates(
        IReadOnlyList<string> originalUrls,
        DownloadSourcePreference preference,
        string? categoryHint)
    {
        ArgumentNullException.ThrowIfNull(originalUrls);
        var candidates = new List<ResolvedDownloadRequest>();
        var seenActualUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var originalUrl in originalUrls.Where(url => !string.IsNullOrWhiteSpace(url)))
        {
            foreach (var candidate in MinecraftDownloadSourceResolver.EnumerateRequests(originalUrl,
                preference is DownloadSourcePreference.Auto ? DownloadSourcePreference.Official : preference, categoryHint))
            {
                if (seenActualUrls.Add(candidate.ActualUrl))
                    candidates.Add(candidate with { RequestedSourcePreference = preference });
            }
        }

        if (candidates.Count == 0)
            throw new ArgumentException("At least one file download URL is required.", nameof(originalUrls));
        return candidates;
    }

    private async Task<IReadOnlyList<ResolvedDownloadRequest>> PrepareCandidatesAsync(
        IReadOnlyList<ResolvedDownloadRequest> candidates, DownloadSourcePreference preference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (preference is DownloadSourcePreference.Auto && candidates.Count > 1
            && candidates.Any(candidate => candidate.ResolvedSourceKind.StartsWith("BmclApi", StringComparison.Ordinal)))
        {
            var selected = await automaticSourceSelector.SelectAsync(logger, token).ConfigureAwait(false);
            candidates = candidates.OrderBy(candidate =>
                candidate.ResolvedSourceKind.StartsWith("BmclApi", StringComparison.Ordinal) != (selected.Preference is DownloadSourcePreference.BmclApi)).ToArray();
            logger.LogDebug("Automatic download candidates prepared. Region={Region} Reason={Reason} PreferredSource={PreferredSource}",
                selected.Region, selected.Reason, candidates[0].ResolvedSourceKind);
        }
        return candidates.OrderBy(GetCandidateCooldown).ThenBy(IsCandidateDegraded).ToArray();
    }

    /// <summary>
    /// 在并发租约内完成单次 HTTP 响应和调用方操作，并把状态码转换为统一失败类型。
    /// </summary>
    private async Task<T> ExecuteAttemptAsync<T>(
        ResolvedDownloadRequest resolution,
        int attempt,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Func<HttpStatusCode, bool>? noResultStatus,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage, ResolvedDownloadRequest>? configureRequest,
        Func<HttpStatusCode, bool>? allowResponseStatus,
        DownloadRequestHeaders? sensitiveHeaders,
        SpeedMeter? speedMeter,
        bool enableSlowBodyWatchdog,
        bool opportunisticAdmission = false,
        DownloadHostConcurrencyController.DownloadAdmissionLease? preacquiredAdmission = null)
    {
        DownloadNetworkObservation.SelectCandidate(resolution.ActualUrl);
        using var attemptScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Attempt"] = attempt, ["SourceKind"] = resolution.ResolvedSourceKind,
            ["ResourceCategory"] = resolution.ResourceCategory
        });
        // 租约覆盖响应处理，而不只是发送请求，防止大量响应体同时占用网络与磁盘。
        await using var reservedAdmission = preacquiredAdmission;
        await using var transportResult = await transport.SendAsync(
            resolution.ActualUrl,
            cancellationToken,
            configureRequest is null ? null : request => configureRequest(request, resolution),
            sensitiveHeaders,
            applyColdStartJitter: attempt == 1,
            opportunisticAdmission,
            preacquiredAdmission: reservedAdmission).ConfigureAwait(false);
        using var response = transportResult.Response;
        var globalConcurrency = GetGlobalConcurrencySnapshot();
        var hostConcurrency = transportResult.HostSnapshot;

        if (noResultStatus?.Invoke(response.StatusCode) is true)
        {
            throw new DownloadNoResultException(
                $"The source returned HTTP {(int)response.StatusCode} for a lookup request.");
        }

        if (!response.IsSuccessStatusCode && allowResponseStatus?.Invoke(response.StatusCode) is not true)
        {
            var origin = transportResult.AdmissionOrigin ?? DownloadHostConcurrencyController.NormalizeOrigin(transportResult.FinalUri);
            var failure = CreateStatusFailure(response, transportResult.FinalUri)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(origin);
            // Publish cooldown before releasing this response's admission lease,
            // so queued requests recheck it before sending more traffic.
            if (failure.IsHostThrottled)
                hostConcurrencyController.DeferRequests(origin, failure.RetryAfter ?? retryOptions.ThrottleRetryDelay);
            throw failure;
        }

        var telemetry = new DownloadAttemptTelemetry();
        using var heartbeat = new Timer(_ => logger.LogInformation("Download body still running. Host={Host} Attempt={Attempt} DownloadedBytes={DownloadedBytes} BodyDuration={BodyDuration}", transportResult.FinalHost, attempt, telemetry.BodyBytes, telemetry.BodyDuration), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        try
        {
        telemetry.StartBody();
        await DownloadResponseThrottler.ApplyAsync(
            response,
            bandwidthLimiter,
            cancellationToken,
            bodyIdleTimeout: retryOptions.BodyIdleTimeout,
            firstByteTimeout: retryOptions.FirstByteTimeout,
            slowBodyReadThreshold: enableSlowBodyWatchdog ? retryOptions.SlowBodyReadThreshold : null,
            minimumBodyBytesPerSecond: retryOptions.MinimumBodyBytesPerSecond,
            timeProvider: timeProvider,
            reportBodyBytes: bytes =>
            {
                telemetry.ReportBodyBytes(bytes);
            },
            speedMeter: speedMeter).ConfigureAwait(false);
            var value = await operation(
                new DownloadAttemptContext(resolution, response, transportResult, attempt),
                cancellationToken).ConfigureAwait(false);
            logger.LogTrace(
                "Download transport completed. RequestedSourcePreference={RequestedSourcePreference} ResolvedSourceKind={ResolvedSourceKind} OriginalUrl={OriginalUrl} ActualUrl={ActualUrl} FinalHost={FinalHost} RedirectCount={RedirectCount} Attempt={Attempt} StatusCode={StatusCode} ResponseHeadersDuration={ResponseHeadersDuration} ResponseBodyDuration={ResponseBodyDuration} ContentLength={ContentLength} DownloadedBytes={DownloadedBytes} AverageBytesPerSecond={AverageBytesPerSecond} GlobalActive={GlobalActive} GlobalWaiting={GlobalWaiting} GlobalTarget={GlobalTarget} HostOrigin={HostOrigin} HostActive={HostActive} HostWaiting={HostWaiting} HostTarget={HostTarget}",
                resolution.RequestedSourcePreference,
                resolution.ResolvedSourceKind,
                DownloadUriLogSanitizer.Sanitize(transportResult.OriginalUri),
                DownloadUriLogSanitizer.Sanitize(transportResult.FinalUri),
                transportResult.FinalHost,
                transportResult.RedirectCount,
                attempt,
                (int)response.StatusCode,
                transportResult.ResponseHeadersDuration,
                telemetry.BodyDuration,
                response.Content.Headers.ContentLength,
                telemetry.BodyBytes,
                telemetry.AverageBytesPerSecond,
                globalConcurrency.ActiveCount,
                globalConcurrency.WaitingCount,
                globalConcurrency.CurrentTarget,
                transportResult.AdmissionOrigin,
                hostConcurrency?.ActiveCount ?? 0,
                hostConcurrency?.WaitingCount ?? 0,
                hostConcurrency?.CurrentTarget ?? 0);
            RecordHostResult(transportResult.AdmissionOrigin, failureReason: null, statusCode: null);
            hostHealthTracker.RecordSuccess(resolution.ResolvedSourceKind, transportResult.FinalHost);
            return value;
        }
        catch (DownloadNoResultException)
        {
            throw;
        }
        catch (DownloadAttemptException exception)
        {
            var slowFailure = exception as DownloadBodyTooSlowException;
            logger.LogWarning(
                exception,
                "Download transport failed after response headers. FinalHost={FinalHost} Attempt={Attempt} FailureReason={FailureReason} StatusCode={StatusCode} ResponseHeadersDuration={ResponseHeadersDuration} ResponseBodyDuration={ResponseBodyDuration} DownloadedBytes={DownloadedBytes} AverageBytesPerSecond={AverageBytesPerSecond} SlowReadDuration={SlowReadDuration} SlowReadBytes={SlowReadBytes} SlowReadBytesPerSecond={SlowReadBytesPerSecond} GlobalActive={GlobalActive} GlobalWaiting={GlobalWaiting} GlobalTarget={GlobalTarget} HostOrigin={HostOrigin} HostActive={HostActive} HostWaiting={HostWaiting} HostTarget={HostTarget}",
                transportResult.FinalHost,
                attempt,
                exception.Reason,
                (int)response.StatusCode,
                transportResult.ResponseHeadersDuration,
                telemetry.BodyDuration,
                telemetry.BodyBytes,
                telemetry.AverageBytesPerSecond,
                slowFailure?.ReadDuration,
                slowFailure?.BytesRead,
                slowFailure?.BytesPerSecond,
                globalConcurrency.ActiveCount,
                globalConcurrency.WaitingCount,
                globalConcurrency.CurrentTarget,
                transportResult.AdmissionOrigin,
                hostConcurrency?.ActiveCount ?? 0,
                hostConcurrency?.WaitingCount ?? 0,
                hostConcurrency?.CurrentTarget ?? 0);
            throw exception
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new DownloadBodyInterruptedException("The response body read timed out.", exception)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
        catch (JsonException exception)
        {
            throw new DownloadContentValidationException("The response body is not valid JSON.", exception)
                .WithDetail("ExpectedFormat", "JSON").WithDetail("ActualContentType", response.Content.Headers.ContentType?.MediaType)
                .WithDetail("DownloadedBytes", telemetry.BodyBytes)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
        catch (XmlException exception)
        {
            throw new DownloadContentValidationException("The response body is not valid XML.", exception)
                .WithDetail("ExpectedFormat", "XML").WithDetail("ActualContentType", response.Content.Headers.ContentType?.MediaType)
                .WithDetail("DownloadedBytes", telemetry.BodyBytes)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadBodyInterruptedException("The response body network read failed.", exception)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
        catch (IOException exception)
        {
            throw new DownloadBodyInterruptedException("The response body ended unexpectedly.", exception)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
    }

    private DownloadAttemptException CreateStatusFailure(HttpResponseMessage response, Uri finalUri)
    {
        var statusCode = response.StatusCode;
        var value = (int)statusCode;
        var mirrorForbidden = statusCode is HttpStatusCode.Forbidden
            && finalUri.Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase);
        var retry = statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || mirrorForbidden
            || value is >= 500 and <= 599
            || value is < 400 or > 499;
        return new DownloadAttemptException(
            retry
                ? DownloadFailureDisposition.RetryCurrentSource
                : DownloadFailureDisposition.SwitchSource,
            DownloadFailureReason.HttpStatus,
            $"The source returned HTTP {value} ({statusCode}).",
            statusCode: statusCode,
            retryAfter: statusCode is HttpStatusCode.TooManyRequests || mirrorForbidden
                ? ParseRetryAfter(response) ?? retryOptions.ThrottleRetryDelay : null)
        { IsTransientMirrorForbidden = mirrorForbidden };
    }

    internal TimeSpan GetRetryDelay(DownloadAttemptException failure, int attempt)
    {
        if (failure.IsHostThrottled && failure.RetryAfter.HasValue)
            return failure.RetryAfter.Value;

        var multiplier = Math.Pow(2, Math.Max(attempt - 1, 0));
        var milliseconds = Math.Min(
            retryOptions.RetryDelay.TotalMilliseconds * multiplier,
            retryOptions.MaximumRetryDelay.TotalMilliseconds);
        var jitterMilliseconds = 300 * Math.Clamp(nextRetryJitter(), 0, 1);
        return TimeSpan.FromMilliseconds(Math.Min(
            retryOptions.MaximumRetryDelay.TotalMilliseconds,
            milliseconds + jitterMilliseconds));
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// 将网络、响应体、本地文件和内容校验异常分类为终止、重试当前源或切换源。
    /// </summary>
    private static DownloadAttemptException ClassifyException(Exception exception)
    {
        if (exception is DownloadAttemptException downloadAttemptException)
            return downloadAttemptException;

        if (exception is JsonException or XmlException)
        {
            return new DownloadContentValidationException(
                "The response content could not be parsed.",
                exception);
        }

        if (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return new DownloadAttemptException(
                DownloadFailureDisposition.RetryCurrentSource,
                DownloadFailureReason.Network,
                "The download attempt failed due to a transient network error.",
                exception);
        }

        return new DownloadAttemptException(
            DownloadFailureDisposition.Abort,
            DownloadFailureReason.LocalFileSystem,
            "The download attempt failed due to a non-network error.",
            exception);
    }

    private ValueTask<IImportConcurrencyLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        return category switch
        {
            DownloadConcurrencyCategory.Metadata => limiter.AcquireMetadataSlotAsync(cancellationToken),
            DownloadConcurrencyCategory.Modpack => limiter.AcquireModpackDownloadSlotAsync(cancellationToken),
            DownloadConcurrencyCategory.Runtime => limiter.AcquireRuntimeDownloadSlotAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported download concurrency category.")
        };
    }

    private ValueTask<DownloadHostConcurrencyController.DownloadAdmissionLease> AcquireAdmissionAsync(
        Uri uri,
        bool applyColdStartJitter,
        CancellationToken cancellationToken)
    {
        return hostConcurrencyController.AcquireAsync(
            uri,
            token => AcquireRateLimitedLeaseAsync(uri, token),
            applyColdStartJitter,
            cancellationToken);
    }

    private DownloadHostConcurrencyController.DownloadAdmissionLease?
        TryAcquireOpportunisticAdmission(Uri uri)
    {
        return hostConcurrencyController.TryAcquireAvailable(
            uri,
            () => limiter.TryAcquireAvailableDownloadSlot(out var lease)
                ? lease
                : null);
    }

    private async ValueTask<IImportConcurrencyLease> AcquireRateLimitedLeaseAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        await bmclApiRequestRateLimiter.WaitAsync(uri, cancellationToken).ConfigureAwait(false);
        return await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RecordHostResult(
        ResolvedDownloadRequest resolution,
        DownloadAttemptException failure)
    {
        if (failure is DownloadCooldownWaitException)
        {
            logger.LogWarning(failure,
                "Download source cooldown wait budget exceeded; trying another source if available. HostOrigin={HostOrigin} RemainingCooldownMs={RemainingCooldownMs}",
                failure.FinalOrigin, failure.RetryAfter?.TotalMilliseconds);
            return; // No new HTTP response: do not extend cooldown or lower concurrency again.
        }
        var origin = failure.FinalOrigin;
        if (string.IsNullOrWhiteSpace(origin)
            && Uri.TryCreate(resolution.ActualUrl, UriKind.Absolute, out var uri))
        {
            origin = DownloadHostConcurrencyController.NormalizeOrigin(uri);
        }
        if (failure.IsHostThrottled && !string.IsNullOrWhiteSpace(origin))
        {
            var delay = failure.RetryAfter ?? retryOptions.ThrottleRetryDelay;
            hostConcurrencyController.DeferRequests(origin, delay);
            if (delay > DownloadHostConcurrencyController.MaximumCooldownWait)
            {
                failure.WithDisposition(DownloadFailureDisposition.SwitchSource)
                    .WithDetail("Reason", "HostCooldownWaitBudgetExceeded")
                    .WithDetail("CooldownWaitBudgetMs", DownloadHostConcurrencyController.MaximumCooldownWait.TotalMilliseconds);
                logger.LogWarning("Download host cooldown exceeds wait budget; trying another source if available. HostOrigin={HostOrigin} StatusCode={StatusCode} ServerRetryAfterMs={ServerRetryAfterMs} CooldownWaitBudgetMs={CooldownWaitBudgetMs}",
                    origin, (int?)failure.StatusCode, delay.TotalMilliseconds, DownloadHostConcurrencyController.MaximumCooldownWait.TotalMilliseconds);
            }
            else
                logger.LogInformation("Download host cooldown applied. HostOrigin={HostOrigin} StatusCode={StatusCode} CooldownMs={CooldownMs}",
                    origin, (int?)failure.StatusCode, delay.TotalMilliseconds);
        }
        RecordHostResult(origin, failure.Reason, failure.IsTransientMirrorForbidden ? HttpStatusCode.TooManyRequests : failure.StatusCode);
    }

    private void RecordHostResult(
        string? origin,
        DownloadFailureReason? failureReason,
        HttpStatusCode? statusCode)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return;
        var adjustment = hostConcurrencyController.RecordResult(origin, failureReason, statusCode);
        if (adjustment is null)
            return;
        logger.LogDebug(
            "Download host concurrency adjusted. HostOrigin={HostOrigin} PreviousTarget={PreviousTarget} CurrentTarget={CurrentTarget} AdjustmentReason={AdjustmentReason} SuccessCount={SuccessCount} FailureCount={FailureCount}",
            adjustment.Origin,
            adjustment.PreviousTarget,
            adjustment.CurrentTarget,
            adjustment.Reason,
            adjustment.Successes,
            adjustment.Failures);
    }

    private (int ActiveCount, int WaitingCount, int CurrentTarget) GetGlobalConcurrencySnapshot()
    {
        var snapshot = limiter.DownloadSnapshot;
        return (snapshot.ActiveCount, snapshot.WaitingCount, snapshot.CurrentTarget);
    }

    private sealed class DownloadAttemptTelemetry
    {
        private readonly Stopwatch bodyStopwatch = new();
        private long bodyBytes;

        public long BodyBytes => Interlocked.Read(ref bodyBytes);
        public TimeSpan BodyDuration => bodyStopwatch.Elapsed;
        public double AverageBytesPerSecond => BodyDuration.TotalSeconds <= 0
            ? 0
            : BodyBytes / BodyDuration.TotalSeconds;

        public void StartBody() => bodyStopwatch.Start();
        public void ReportBodyBytes(long bytes) => Interlocked.Add(ref bodyBytes, bytes);
    }

    private static string GetCandidateHost(ResolvedDownloadRequest resolution) =>
        Uri.TryCreate(resolution.ActualUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private TimeSpan GetCandidateCooldown(ResolvedDownloadRequest resolution) =>
        Uri.TryCreate(resolution.ActualUrl, UriKind.Absolute, out var uri)
            ? hostConcurrencyController.GetRemainingCooldown(uri) : TimeSpan.Zero;

    // Health only affects ordering. Even a degraded or redirected host may be
    // the last working source, so it must still receive a real request.
    private bool IsCandidateDegraded(ResolvedDownloadRequest resolution) =>
        hostHealthTracker.ShouldAvoid(resolution.ResolvedSourceKind, GetCandidateHost(resolution));

    private void LogResolvedRequest(ResolvedDownloadRequest resolution, int attempt)
    {
        logger.LogDebug(
            "Download resource completed. RequestedSourcePreference={RequestedSourcePreference} ResourceCategory={ResourceCategory} OriginalUrl={OriginalUrl} ActualUrl={ActualUrl} ResolvedSourceKind={ResolvedSourceKind} Attempt={Attempt}",
            resolution.RequestedSourcePreference,
            resolution.ResourceCategory,
            DownloadUriLogSanitizer.Sanitize(resolution.OriginalUrl),
            DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl),
            resolution.ResolvedSourceKind,
            attempt);
    }

    private void LogRecoveredRequest(
        ResolvedDownloadRequest resolution,
        int attempt,
        IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
            return;

        var failureReasons = failures
            .OfType<DownloadAttemptException>()
            .Select(failure => failure.Reason)
            .Distinct()
            .Order()
            .ToArray();
        logger.LogWarning(
            "Download recovered after retry or source fallback. ResourceCategory={ResourceCategory} FinalSource={FinalSource} Attempt={Attempt} RecoveredFailureCount={RecoveredFailureCount} FailureReasons={FailureReasons}",
            resolution.ResourceCategory,
            resolution.ResolvedSourceKind,
            attempt,
            failures.Count,
            failureReasons);
    }

    private void LogExhaustedRequest(
        ResolvedDownloadRequest resolution,
        IReadOnlyList<SourceFailure> failures)
    {
        var summary = string.Join("; ", failures
            .GroupBy(failure => (failure.SourceKind, failure.Reason, failure.StatusCode))
            .Select(group =>
                $"{group.Key.SourceKind}: {group.Key.Reason}"
                + (group.Key.StatusCode is { } status ? $" (HTTP {(int)status})" : string.Empty)
                + $" x{group.Count()}"));
        logger.LogError(
            "Download exhausted all sources. ResourceCategory={ResourceCategory} OriginalUrl={OriginalUrl} FailureCount={FailureCount} FailureSummary={FailureSummary}",
            resolution.ResourceCategory,
            DownloadUriLogSanitizer.Sanitize(resolution.OriginalUrl),
            failures.Count,
            summary);
    }

    private readonly record struct SourceFailure(
        string SourceKind,
        DownloadFailureReason Reason,
        HttpStatusCode? StatusCode);

    private void LogFailure(
        ResolvedDownloadRequest resolution,
        int attempt,
        DownloadAttemptException failure,
        bool retryCurrentSource,
        int? sourceRound = null,
        bool retryInLaterRound = false,
        int remainingCandidateCount = 0)
    {
        failure.WithDetail("NetworkAttempts", DownloadNetworkObservation.Current?.Sends);
        var concurrency = GetGlobalConcurrencySnapshot();
        failure.WithDetail("GlobalActive", concurrency.ActiveCount).WithDetail("GlobalWaiting", concurrency.WaitingCount)
            .WithDetail("GlobalTarget", concurrency.CurrentTarget);
        var slowFailure = failure as DownloadBodyTooSlowException;
        logger.LogWarning(
            failure,
            "Download resource attempt failed. RequestedSourcePreference={RequestedSourcePreference} ResourceCategory={ResourceCategory} OriginalUrl={OriginalUrl} ActualUrl={ActualUrl} ResolvedSourceKind={ResolvedSourceKind} FinalHost={FinalHost} Attempt={Attempt} SourceRound={SourceRound} FailureReason={FailureReason} FailureDisposition={FailureDisposition} StatusCode={StatusCode} RetryCurrentSource={RetryCurrentSource} RetryInLaterRound={RetryInLaterRound} RemainingCandidateCount={RemainingCandidateCount} SlowReadDuration={SlowReadDuration} SlowReadBytes={SlowReadBytes} SlowReadBytesPerSecond={SlowReadBytesPerSecond}",
            resolution.RequestedSourcePreference,
            resolution.ResourceCategory,
            DownloadUriLogSanitizer.Sanitize(resolution.OriginalUrl),
            DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl),
            resolution.ResolvedSourceKind,
            failure.FinalHost,
            attempt,
            sourceRound,
            failure.Reason,
            failure.Disposition,
            failure.StatusCode is null ? null : (int)failure.StatusCode.Value,
            retryCurrentSource,
            retryInLaterRound,
            remainingCandidateCount,
            slowFailure?.ReadDuration,
            slowFailure?.BytesRead,
            slowFailure?.BytesPerSecond);
    }

    private enum DownloadExecutionMode
    {
        PerSourceRetries,
        FileSourceRounds
    }

    internal sealed class DownloadSourceRequestException : Exception, ISafeDiagnosticException
    {
        public DownloadSourceRequestException(
            ResolvedDownloadRequest resolution,
            Exception innerException,
            IReadOnlyList<Exception>? failures = null)
            : base($"Download request failed for {DownloadUriLogSanitizer.Sanitize(resolution.ActualUrl)}", innerException)
        {
            Resolution = resolution;
            Failures = failures ?? [innerException];
        }

        public string DiagnosticCode => "DownloadSourcesExhausted";
        public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>
        {
            ["SourceKind"] = Resolution.ResolvedSourceKind,
            ["NetworkAttempts"] = NetworkAttempts,
            ["FallbackUsed"] = FallbackUsed,
            ["AttemptFailures"] = Failures.Select(exception => SafeExceptionLog.Format(exception, includeStack: false)).ToArray()
        };
        public int NetworkAttempts { get; } = DownloadNetworkObservation.Current?.Sends ?? 0;
        public bool FallbackUsed { get; } = DownloadNetworkObservation.Current?.CandidateCount > 1;
        public ResolvedDownloadRequest Resolution { get; }
        public IReadOnlyList<Exception> Failures { get; }
    }

}
