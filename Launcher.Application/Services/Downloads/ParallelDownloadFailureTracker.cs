/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.Extensions.Logging;

namespace Launcher.Application.Services;

internal sealed class ParallelDownloadFailureTracker(ILogger logger, CancellationToken outerCancellation, Action cancel)
{
    private string? primaryFailure;

    public async Task<T> RunAsync<T>(string branch, Func<Task<T>> action)
    {
        using var scope = DownloadDiagnostics.BeginBranch(logger, branch);
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Download branch canceled. Branch={Branch} CancellationOrigin={CancellationOrigin} PrimaryFailureId={PrimaryFailureId}",
                branch, Volatile.Read(ref primaryFailure) is not null ? "UpstreamFailure" : outerCancellation.IsCancellationRequested ? "CallerCancellation" : "InternalCancellation", primaryFailure);
            throw;
        }
        catch (Exception exception)
        {
            var failureId = SafeExceptionLog.FailureId(exception);
            var primary = Interlocked.CompareExchange(ref primaryFailure, failureId, null) ?? failureId;
            logger.LogError(exception, "Download branch failed before cancellation. Branch={Branch} FailureId={FailureId} PrimaryFailureId={PrimaryFailureId} IsPrimaryFailure={IsPrimaryFailure}",
                branch, failureId, primary, primary == failureId);
            try { cancel(); }
            catch (Exception cancellationException)
            {
                logger.LogWarning(cancellationException, "Download branch cancellation callback failed. PrimaryFailureId={PrimaryFailureId}", primary);
            }
            throw;
        }
    }
}
