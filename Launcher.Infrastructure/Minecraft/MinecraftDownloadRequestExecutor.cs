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
    internal const long SegmentedSplitThreshold = 256L * 1024;
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
        long minimumSegmentedDownloadSize = MinimumSegmentedDownloadSize)
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
        transport = new MinecraftDownloadTransport(
            httpClient,
            this.retryOptions,
            AcquireAdmissionAsync,
            TryAcquireOpportunisticAdmission);
    }

    public async Task<T> ExecuteAsync<T>(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
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
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Value!;
    }

    public Task<DownloadLookupResult<T>> ExecuteLookupAsync<T>(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint,
        Func<DownloadAttemptContext, CancellationToken, Task<T>> operation,
        Func<HttpStatusCode, bool> noResultStatus,
        CancellationToken cancellationToken)
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
            cancellationToken: cancellationToken);
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
                            reportTransferredBytes).ConfigureAwait(false);
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
                return result.Value!;
            }
        }

        return DownloadFileCoreAsync();

        async Task<ResolvedDownloadRequest> DownloadFileCoreAsync()
        {
            var integrity = DownloadIntegrityExpectation.Sha1(expectedSha1, expectedSize);
            await using var session = await ResumableDownloadFileSession.AcquireAsync(
                destinationPath,
                integrity,
                cancellationToken,
                options).ConfigureAwait(false);
            if (session.IsComplete)
                return fileCandidates[0];

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
                return segmented.Resolution;

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
            return result.Value!;
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
            await using var session = await ResumableDownloadFileSession.AcquireAsync(
                destinationPath,
                integrity,
                cancellationToken,
                options).ConfigureAwait(false);
            if (session.IsComplete)
                return fileCandidates[0];

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
                return segmented.Resolution;

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
            return result.Value!;
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
                logger.LogDebug(
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
            logger.LogDebug(
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
            ?? throw new InvalidOperationException("The segmented probe did not provide a validated range.");
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
                throw new InvalidOperationException("The adaptive segmented session did not contain baseline work.");
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
                                    throw new InvalidOperationException("The adaptive segmented range completed before its request was created.");
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
                RecordHostResult(resolution, failure);
                hostHealthTracker.RecordFailure(
                    resolution.ResolvedSourceKind,
                    failure.FinalHost ?? GetCandidateHost(resolution),
                    failure.Reason,
                    failure.StatusCode);
                var retry = failure.Disposition is DownloadFailureDisposition.RetryCurrentSource
                    && attempt < retryOptions.MaxAttemptsPerSource;
                logger.LogDebug(
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

        throw new InvalidOperationException("The segmented range retry loop exited unexpectedly.");
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
        return candidates.Any(IsSegmentableCandidate);
    }

    private static int CalculateUsefulSegmentedWorkers(long remainingLength) =>
        (int)Math.Min(
            int.MaxValue,
            Math.Max(
                1d,
                Math.Ceiling(
                    remainingLength
                    / (SegmentedSplitThreshold * AdaptiveSegmentDownloadSession.TailSplitFraction))));

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
        CancellationToken cancellationToken)
    {
        if (executionMode is DownloadExecutionMode.FileSourceRounds)
        {
            if (lookupMode || noResultStatus is not null)
                throw new InvalidOperationException("File source rounds do not support lookup requests.");

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

        // 候选顺序已经包含用户偏好和镜像回退策略；每个源内部重试耗尽后才切换下一源。
        var candidates = MinecraftDownloadSourceResolver
            .EnumerateRequests(originalUrl, preference, categoryHint)
            .ToList();
        var failures = new List<Exception>();
        var noResultSourceCount = 0;
        ResolvedDownloadRequest? lastResolution = null;

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var resolution = candidates[candidateIndex];
            lastResolution = resolution;
            var sourceReportedNoResult = false;
            var slowDisconnectCount = 0;
            int? slowWatchdogDisabledAttempt = null;

            for (var attempt = 1; attempt <= retryOptions.MaxAttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (attempt == 1
                    && !resolution.ResolvedSourceKind.StartsWith("BmclApi", StringComparison.Ordinal)
                    && hostHealthTracker.ShouldAvoid(resolution.ResolvedSourceKind, GetCandidateHost(resolution)))
                {
                    var avoided = new DownloadAttemptException(
                        DownloadFailureDisposition.SwitchSource,
                        DownloadFailureReason.Network,
                        "The download host is temporarily avoided after repeated transient failures.");
                    failures.Add(avoided);
                    LogFailure(resolution, attempt, avoided, retryCurrentSource: false);
                    break;
                }

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
                        throw failure;

                    failures.Add(failure);
                    var retryCurrentSource = failure.Disposition is DownloadFailureDisposition.RetryCurrentSource
                        && attempt < retryOptions.MaxAttemptsPerSource;
                    LogFailure(resolution, attempt, failure, retryCurrentSource);

                    if (!retryCurrentSource)
                        break;

                    await Task.Delay(GetRetryDelay(failure, attempt), cancellationToken).ConfigureAwait(false);
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
        var activeCandidateIndexes = Enumerable.Range(0, candidates.Count).ToList();
        var failures = new List<Exception>();
        var maxRounds = Math.Max(1, retryOptions.MaxFileSourceRounds);
        ResolvedDownloadRequest? lastResolution = null;

        for (var round = 1; round <= maxRounds && activeCandidateIndexes.Count > 0; round++)
        {
            var nextRoundCandidateIndexes = new List<int>();
            var nextRoundFailures = new List<DownloadAttemptException>();

            for (var position = 0; position < activeCandidateIndexes.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateIndex = activeCandidateIndexes[position];
                var resolution = candidates[candidateIndex];
                lastResolution = resolution;

                if (round == 1
                    && !resolution.ResolvedSourceKind.StartsWith("BmclApi", StringComparison.Ordinal)
                    && hostHealthTracker.ShouldAvoid(resolution.ResolvedSourceKind, GetCandidateHost(resolution)))
                {
                    var avoided = new DownloadAttemptException(
                        DownloadFailureDisposition.RetryCurrentSource,
                        DownloadFailureReason.Network,
                        "The download host is temporarily avoided after repeated transient failures.");
                    failures.Add(avoided);
                    var retryInLaterRound = round < maxRounds;
                    if (retryInLaterRound)
                    {
                        nextRoundCandidateIndexes.Add(candidateIndex);
                        nextRoundFailures.Add(avoided);
                    }
                    LogFailure(
                        resolution,
                        round,
                        avoided,
                        retryCurrentSource: false,
                        sourceRound: round,
                        retryInLaterRound: retryInLaterRound,
                        remainingCandidateCount: activeCandidateIndexes.Count - position - 1);
                    continue;
                }

                try
                {
                    var value = await ExecuteAttemptAsync(
                        resolution,
                        round,
                        operation,
                        noResultStatus: null,
                        cancellationToken: cancellationToken,
                        configureRequest: configureRequest,
                        allowResponseStatus: allowResponseStatus,
                        sensitiveHeaders: sensitiveHeaders,
                        speedMeter: speedMeter,
                        enableSlowBodyWatchdog: true).ConfigureAwait(false);
                    hostHealthTracker.RecordSuccess(resolution.ResolvedSourceKind, GetCandidateHost(resolution));
                    LogResolvedRequest(resolution, round);
                    LogRecoveredRequest(resolution, round, failures);
                    return DownloadLookupResult<T>.Success(value);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var failure = ClassifyException(exception);
                    RecordHostResult(resolution, failure);
                    hostHealthTracker.RecordFailure(
                        resolution.ResolvedSourceKind,
                        failure.FinalHost ?? GetCandidateHost(resolution),
                        failure.Reason,
                        failure.StatusCode);
                    if (failure.Disposition is DownloadFailureDisposition.Abort)
                        throw failure;

                    failures.Add(failure);
                    var retryInLaterRound = failure.Disposition is DownloadFailureDisposition.RetryCurrentSource
                        && round < maxRounds;
                    if (retryInLaterRound)
                    {
                        nextRoundCandidateIndexes.Add(candidateIndex);
                        nextRoundFailures.Add(failure);
                    }

                    LogFailure(
                        resolution,
                        round,
                        failure,
                        retryCurrentSource: false,
                        sourceRound: round,
                        retryInLaterRound: retryInLaterRound,
                        remainingCandidateCount: activeCandidateIndexes.Count - position - 1);
                }
            }

            if (round >= maxRounds || nextRoundCandidateIndexes.Count == 0)
                break;

            var retryDelay = nextRoundFailures
                .Select(failure => GetRetryDelay(failure, round))
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();
            logger.LogDebug(
                "File download source round exhausted. OriginalUrl={OriginalUrl} CompletedRound={CompletedRound} MaxRounds={MaxRounds} RetryCandidateCount={RetryCandidateCount} RetryDelay={RetryDelay}",
                DownloadUriLogSanitizer.Sanitize(originalUrl),
                round,
                maxRounds,
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
            foreach (var candidate in MinecraftDownloadSourceResolver.EnumerateRequests(originalUrl, preference, categoryHint))
            {
                if (seenActualUrls.Add(candidate.ActualUrl))
                    candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
            throw new ArgumentException("At least one file download URL is required.", nameof(originalUrls));
        return candidates;
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

        if (!string.Equals(GetCandidateHost(resolution), transportResult.FinalHost, StringComparison.OrdinalIgnoreCase)
            && hostHealthTracker.ShouldAvoid(resolution.ResolvedSourceKind, transportResult.FinalHost))
        {
            throw new DownloadAttemptException(
                DownloadFailureDisposition.SwitchSource,
                DownloadFailureReason.Network,
                "The redirected download host is temporarily avoided after a degraded transfer.")
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }

        if (noResultStatus?.Invoke(response.StatusCode) is true)
        {
            throw new DownloadNoResultException(
                $"The source returned HTTP {(int)response.StatusCode} for a lookup request.");
        }

        if (!response.IsSuccessStatusCode && allowResponseStatus?.Invoke(response.StatusCode) is not true)
        {
            throw CreateStatusFailure(response)
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }

        var telemetry = new DownloadAttemptTelemetry();
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
            logger.LogDebug(
                exception,
                "Download transport failed after response headers. FinalHost={FinalHost} Attempt={Attempt} FailureReason={FailureReason} ResponseHeadersDuration={ResponseHeadersDuration} ResponseBodyDuration={ResponseBodyDuration} DownloadedBytes={DownloadedBytes} AverageBytesPerSecond={AverageBytesPerSecond} SlowReadDuration={SlowReadDuration} SlowReadBytes={SlowReadBytes} SlowReadBytesPerSecond={SlowReadBytesPerSecond} GlobalActive={GlobalActive} GlobalWaiting={GlobalWaiting} GlobalTarget={GlobalTarget} HostOrigin={HostOrigin} HostActive={HostActive} HostWaiting={HostWaiting} HostTarget={HostTarget}",
                transportResult.FinalHost,
                attempt,
                exception.Reason,
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
                .WithFinalHost(transportResult.FinalHost)
                .WithFinalOrigin(transportResult.AdmissionOrigin);
        }
        catch (XmlException exception)
        {
            throw new DownloadContentValidationException("The response body is not valid XML.", exception)
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

    private static DownloadAttemptException CreateStatusFailure(HttpResponseMessage response)
    {
        var statusCode = response.StatusCode;
        var value = (int)statusCode;
        var retry = statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || value is >= 500 and <= 599
            || value is < 400 or > 499;
        return new DownloadAttemptException(
            retry
                ? DownloadFailureDisposition.RetryCurrentSource
                : DownloadFailureDisposition.SwitchSource,
            DownloadFailureReason.HttpStatus,
            $"The source returned HTTP {value} ({statusCode}).",
            statusCode: statusCode,
            retryAfter: statusCode == HttpStatusCode.TooManyRequests ? ParseRetryAfter(response) : null);
    }

    internal TimeSpan GetRetryDelay(DownloadAttemptException failure, int attempt)
    {
        if (failure.StatusCode == HttpStatusCode.TooManyRequests && failure.RetryAfter.HasValue)
            return failure.RetryAfter.Value > retryOptions.MaximumRetryAfter
                ? retryOptions.MaximumRetryAfter
                : failure.RetryAfter.Value;

        var multiplier = Math.Pow(2, Math.Max(attempt - 1, 0));
        var milliseconds = Math.Min(
            retryOptions.RetryDelay.TotalMilliseconds * multiplier,
            retryOptions.MaximumRetryDelay.TotalMilliseconds);
        var jitterMilliseconds = 2000 * Math.Clamp(nextRetryJitter(), 0, 1);
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
        var origin = failure.FinalOrigin;
        if (string.IsNullOrWhiteSpace(origin)
            && Uri.TryCreate(resolution.ActualUrl, UriKind.Absolute, out var uri))
        {
            origin = DownloadHostConcurrencyController.NormalizeOrigin(uri);
        }
        RecordHostResult(origin, failure.Reason, failure.StatusCode);
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

    private void LogFailure(
        ResolvedDownloadRequest resolution,
        int attempt,
        DownloadAttemptException failure,
        bool retryCurrentSource,
        int? sourceRound = null,
        bool retryInLaterRound = false,
        int remainingCandidateCount = 0)
    {
        var slowFailure = failure as DownloadBodyTooSlowException;
        logger.LogDebug(
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

    internal sealed class DownloadSourceRequestException : Exception
    {
        public DownloadSourceRequestException(
            ResolvedDownloadRequest resolution,
            Exception innerException,
            IReadOnlyList<Exception>? failures = null)
            : base($"Download request failed for {resolution.ActualUrl}", innerException)
        {
            Resolution = resolution;
            Failures = failures ?? [innerException];
        }

        public ResolvedDownloadRequest Resolution { get; }
        public IReadOnlyList<Exception> Failures { get; }
    }

}
