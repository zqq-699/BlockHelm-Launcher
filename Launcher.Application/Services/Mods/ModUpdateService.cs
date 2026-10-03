/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Application.Services;

public sealed class ModUpdateService : IModUpdateService
{
    private const int MaximumFingerprintConcurrency = 2;
    private const int MaximumUpdateConcurrency = 4;
    private readonly IReadOnlyList<IModUpdateProvider> providers;
    private readonly IModUpdateFileTransaction fileTransaction;
    private readonly ILogger<ModUpdateService> logger;

    public ModUpdateService(
        IEnumerable<IModUpdateProvider> providers,
        IModUpdateFileTransaction fileTransaction,
        ILogger<ModUpdateService>? logger = null)
    {
        this.providers = providers.ToArray();
        this.fileTransaction = fileTransaction;
        this.logger = logger ?? NullLogger<ModUpdateService>.Instance;
    }

    public async Task<ModUpdateCheckResult> CheckAsync(
        GameInstance instance,
        LocalMod mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var batch = await CheckManyAsync(instance, [mod], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return batch.Items.Count == 0
            ? new ModUpdateCheckResult(ModUpdateCheckStatus.Unavailable)
            : batch.Items[0].Result;
    }

    public async Task<ModUpdateBatchCheckResult> CheckManyAsync(
        GameInstance instance,
        IReadOnlyList<LocalMod> mods,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mods);
        if (mods.Count == 0)
            return new ModUpdateBatchCheckResult([], providers.Count);

        progress?.Report(new LauncherProgress(ModUpdateProgressStages.BatchChecking, string.Empty, 0));
        var states = new ModUpdateLocalFileState?[mods.Count];
        var results = new ModUpdateCheckResult?[mods.Count];
        var progressLock = new object();
        var completedChecks = 0;

        void ReportCompletedCheck()
        {
            lock (progressLock)
            {
                completedChecks++;
                progress?.Report(new LauncherProgress(
                    ModUpdateProgressStages.BatchChecking,
                    string.Empty,
                    Math.Min(20, completedChecks * 20d / mods.Count)));
            }
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, mods.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumFingerprintConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                try
                {
                    states[index] = await fileTransaction.CaptureAsync(mods[index], token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    results[index] = new ModUpdateCheckResult(ModUpdateCheckStatus.Unavailable);
                    logger.LogWarning(
                        exception,
                        "Failed to capture mod update fingerprint. FileName={FileName}",
                        System.IO.Path.GetFileName(mods[index].FullPath));
                    ReportCompletedCheck();
                }
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var capturedStates = states.Where(state => state is not null).Select(state => state!).ToArray();
        var reportedPathsByProvider = providers
            .Select(_ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var completedProviderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void ReportProviderCompleted(int providerIndex, string fullPath)
        {
            lock (progressLock)
            {
                if (!reportedPathsByProvider[providerIndex].Add(fullPath))
                    return;

                var providerCompletionCount = completedProviderCounts.TryGetValue(fullPath, out var count)
                    ? count + 1
                    : 1;
                completedProviderCounts[fullPath] = providerCompletionCount;
                if (providerCompletionCount != providers.Count)
                    return;

                completedChecks++;
                progress?.Report(new LauncherProgress(
                    ModUpdateProgressStages.BatchChecking,
                    string.Empty,
                    Math.Min(20, completedChecks * 20d / mods.Count)));
            }
        }

        if (providers.Count == 0)
        {
            foreach (var _ in capturedStates)
                ReportCompletedCheck();
        }

        var providerTasks = providers.Select((provider, providerIndex) =>
            QueryProviderAsync(
                provider,
                instance,
                capturedStates,
                new InlineProgress<string>(path => ReportProviderCompleted(providerIndex, path)),
                cancellationToken));
        var providerResults = await Task.WhenAll(providerTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        for (var index = 0; index < mods.Count; index++)
        {
            var localFile = states[index];
            if (localFile is null)
                continue;

            var itemProviderResults = providerResults
                .Select(result => result.TryGetValue(localFile.FullPath, out var item)
                    ? item
                    : new ModUpdateProviderResult(false, false))
                .ToArray();
            results[index] = SelectResult(localFile, itemProviderResults);
        }

        progress?.Report(new LauncherProgress(ModUpdateProgressStages.BatchChecking, string.Empty, 20));
        var items = mods.Select((mod, index) => new ModUpdateBatchCheckItem(
                System.IO.Path.GetFullPath(mod.FullPath),
                results[index] ?? new ModUpdateCheckResult(ModUpdateCheckStatus.Unavailable)))
            .ToArray();
        logger.LogInformation(
            "Mod update batch check completed. TotalCount={TotalCount} UpdateCount={UpdateCount} UpToDateCount={UpToDateCount} NotRecognizedCount={NotRecognizedCount} UnavailableCount={UnavailableCount}",
            items.Length,
            items.Count(item => item.Result.Status is ModUpdateCheckStatus.UpdateAvailable),
            items.Count(item => item.Result.Status is ModUpdateCheckStatus.UpToDate),
            items.Count(item => item.Result.Status is ModUpdateCheckStatus.NotRecognized),
            items.Count(item => item.Result.Status is ModUpdateCheckStatus.Unavailable));
        return new ModUpdateBatchCheckResult(items, providers.Count);
    }

    public Task<ModUpdateResult> UpdateAsync(
        GameInstance instance,
        ModUpdateCandidate candidate,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        fileTransaction.ApplyAsync(instance, candidate, progress, cancellationToken);

    public async Task<ModUpdateBatchResult> UpdateManyAsync(
        GameInstance instance,
        IReadOnlyList<ModUpdateCandidate> candidates,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            return new ModUpdateBatchResult([]);

        var results = new ModUpdateBatchItemResult?[candidates.Count];
        var percentages = new double[candidates.Count];
        var progressLock = new object();
        progress?.Report(new LauncherProgress(ModUpdateProgressStages.BatchUpdating, string.Empty, 20));
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumUpdateConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var candidate = candidates[index];
                var itemProgress = progress is null
                    ? null
                    : DownloadSpeedTaskProgress.Forward(progress, value =>
                    {
                        if (value.DownloadSpeedTelemetry is not null)
                        {
                            progress.Report(value);
                            return;
                        }

                        lock (progressLock)
                        {
                            if (value.Percent is { } percent)
                                percentages[index] = Math.Max(percentages[index], Math.Clamp(percent, 0, 100));
                            ReportOverallProgress(progress, percentages);
                        }
                    });

                try
                {
                    var result = await fileTransaction
                        .ApplyAsync(instance, candidate, itemProgress, token)
                        .ConfigureAwait(false);
                    results[index] = new ModUpdateBatchItemResult(candidate, result);
                    if (progress is not null)
                    {
                        lock (progressLock)
                        {
                            percentages[index] = 100;
                            ReportOverallProgress(progress, percentages);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (ModUpdateConflictException exception)
                {
                    var reason = exception.Reason switch
                    {
                        ModUpdateConflictReason.SourceChanged => ModUpdateBatchFailureReason.SourceChanged,
                        ModUpdateConflictReason.TargetExists => ModUpdateBatchFailureReason.TargetExists,
                        _ => ModUpdateBatchFailureReason.InvalidPath
                    };
                    results[index] = new ModUpdateBatchItemResult(
                        candidate,
                        FailureReason: reason,
                        FailurePath: exception.Path);
                    LogBatchUpdateFailure(candidate, exception, reason);
                    CompleteFailedProgress(progress, percentages, progressLock, index);
                }
                catch (ResourceProjectIntegrityException exception)
                {
                    results[index] = new ModUpdateBatchItemResult(
                        candidate,
                        FailureReason: ModUpdateBatchFailureReason.IntegrityFailed);
                    LogBatchUpdateFailure(candidate, exception, ModUpdateBatchFailureReason.IntegrityFailed);
                    CompleteFailedProgress(progress, percentages, progressLock, index);
                }
                catch (Exception exception)
                {
                    results[index] = new ModUpdateBatchItemResult(
                        candidate,
                        FailureReason: ModUpdateBatchFailureReason.Failed);
                    LogBatchUpdateFailure(candidate, exception, ModUpdateBatchFailureReason.Failed);
                    CompleteFailedProgress(progress, percentages, progressLock, index);
                }
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new LauncherProgress(ModUpdateProgressStages.BatchUpdating, string.Empty, 99));
        var completed = results.Where(result => result is not null).Select(result => result!).ToArray();
        logger.LogInformation(
            "Mod update batch completed. TotalCount={TotalCount} SuccessCount={SuccessCount} FailedCount={FailedCount}",
            completed.Length,
            completed.Count(item => item.Result is not null),
            completed.Count(item => item.FailureReason is not null));
        return new ModUpdateBatchResult(completed);
    }

    private async Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> QueryProviderAsync(
        IModUpdateProvider provider,
        GameInstance instance,
        IReadOnlyList<ModUpdateLocalFileState> localFiles,
        IProgress<string> completedFileProgress,
        CancellationToken cancellationToken)
    {
        if (localFiles.Count == 0)
            return new Dictionary<string, ModUpdateProviderResult>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return await provider
                .FindUpdatesAsync(instance, localFiles, cancellationToken, completedFileProgress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Mod update provider batch failed. Source={Source} Count={Count}",
                provider.Source,
                localFiles.Count);
            return localFiles.ToDictionary(
                state => state.FullPath,
                _ => new ModUpdateProviderResult(false, false),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                foreach (var localFile in localFiles)
                    completedFileProgress.Report(localFile.FullPath);
            }
        }
    }

    private ModUpdateCheckResult SelectResult(
        ModUpdateLocalFileState localFile,
        IReadOnlyList<ModUpdateProviderResult> results)
    {
        var successfulCount = results.Count(result => result.IsAvailable);
        var recognizedCount = results.Count(result => result.IsAvailable && result.IsRecognized);
        var candidate = results
            .Where(result => result.Candidate is not null)
            .Select(result => result.Candidate!)
            .Where(result => IsRelease(result.TargetVersion))
            .OrderByDescending(result => result.TargetVersion.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenBy(result => result.Source is ResourceProjectSource.Modrinth ? 0 : 1)
            .FirstOrDefault();
        var status = candidate is not null
            ? ModUpdateCheckStatus.UpdateAvailable
            : successfulCount == 0
                ? ModUpdateCheckStatus.Unavailable
                : recognizedCount > 0 && successfulCount == providers.Count
                    ? ModUpdateCheckStatus.UpToDate
                    : recognizedCount == 0 && successfulCount == providers.Count
                        ? ModUpdateCheckStatus.NotRecognized
                        : ModUpdateCheckStatus.Unavailable;

        logger.LogInformation(
            "Mod update check completed. FileName={FileName} Status={Status} SuccessfulProviderCount={SuccessfulProviderCount} RecognizedProviderCount={RecognizedProviderCount} TargetSource={TargetSource} TargetVersionId={TargetVersionId}",
            System.IO.Path.GetFileName(localFile.FullPath),
            status,
            successfulCount,
            recognizedCount,
            candidate?.Source,
            candidate?.TargetVersion.VersionId);
        return new ModUpdateCheckResult(status, candidate, successfulCount, recognizedCount);
    }

    private void LogBatchUpdateFailure(
        ModUpdateCandidate candidate,
        Exception exception,
        ModUpdateBatchFailureReason reason)
    {
        logger.LogWarning(
            exception,
            "Mod update batch item failed. FileName={FileName} Provider={Provider} VersionId={VersionId} Reason={Reason}",
            System.IO.Path.GetFileName(candidate.LocalFile.FullPath),
            candidate.Source,
            candidate.TargetVersion.VersionId,
            reason);
    }

    private static void CompleteFailedProgress(
        IProgress<LauncherProgress>? progress,
        double[] percentages,
        object progressLock,
        int index)
    {
        if (progress is null)
            return;

        lock (progressLock)
        {
            percentages[index] = 100;
            ReportOverallProgress(progress, percentages);
        }
    }

    private static void ReportOverallProgress(
        IProgress<LauncherProgress> progress,
        IReadOnlyCollection<double> percentages)
    {
        var overall = 20 + (percentages.Average() * 0.79);
        progress.Report(new LauncherProgress(
            ModUpdateProgressStages.BatchUpdating,
            string.Empty,
            Math.Min(99, overall)));
    }

    private static bool IsRelease(ResourceProjectVersion version) =>
        string.Equals(version.VersionType, "release", StringComparison.OrdinalIgnoreCase);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
