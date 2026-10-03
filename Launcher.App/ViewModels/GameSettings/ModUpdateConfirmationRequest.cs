/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.GameSettings;

public sealed class ModUpdateConfirmationRequest(string title, ModUpdateCandidate candidate)
{
    private readonly TaskCompletionSource<bool> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;

    public ModUpdateCandidate Candidate { get; } = candidate;

    public Task<bool> Completion => completion.Task;

    public void Resolve(bool confirmed) => completion.TrySetResult(confirmed);
}

public enum ModBulkUpdateConfirmationStage
{
    RiskConfirmation,
    Checking,
    Summary,
    Failed
}

public sealed class ModBulkUpdateConfirmationRequest
{
    private readonly TaskCompletionSource<bool> riskCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> summaryCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancellation = new();
    private Action? cancelChecking;

    public ModBulkUpdateConfirmationStage Stage { get; private set; } =
        ModBulkUpdateConfirmationStage.RiskConfirmation;

    public int UpdateCount { get; private set; }

    public int UpToDateCount { get; private set; }

    public int NotRecognizedCount { get; private set; }

    public int UnavailableCount { get; private set; }

    public int PartialProviderFailureCount { get; private set; }

    public int CheckCompletedCount { get; private set; }

    public int CheckTotalCount { get; private set; }

    public double CheckProgressPercent { get; private set; }

    public Task<bool> RiskCompletion => riskCompletion.Task;

    public Task<bool> SummaryCompletion => summaryCompletion.Task;

    public CancellationToken CancellationToken => cancellation.Token;

    public event Action? Changed;

    public event Action? Closed;

    public void Confirm()
    {
        if (Stage is ModBulkUpdateConfirmationStage.RiskConfirmation)
            riskCompletion.TrySetResult(true);
        else if (Stage is ModBulkUpdateConfirmationStage.Summary
            or ModBulkUpdateConfirmationStage.Failed)
            summaryCompletion.TrySetResult(true);
    }

    public void Cancel()
    {
        switch (Stage)
        {
            case ModBulkUpdateConfirmationStage.RiskConfirmation:
                cancellation.Cancel();
                riskCompletion.TrySetResult(false);
                break;
            case ModBulkUpdateConfirmationStage.Checking:
                cancellation.Cancel();
                cancelChecking?.Invoke();
                break;
            case ModBulkUpdateConfirmationStage.Summary:
                summaryCompletion.TrySetResult(false);
                break;
            case ModBulkUpdateConfirmationStage.Failed:
                summaryCompletion.TrySetResult(true);
                break;
        }
    }

    public void BeginChecking(int totalCount, Action cancel)
    {
        cancelChecking = cancel;
        if (cancellation.IsCancellationRequested)
        {
            cancelChecking();
            return;
        }

        CheckCompletedCount = 0;
        CheckTotalCount = Math.Max(0, totalCount);
        CheckProgressPercent = 0;
        Stage = ModBulkUpdateConfirmationStage.Checking;
        Changed?.Invoke();
    }

    public void ReportCheckingProgress(double batchPercent)
    {
        if (Stage is not ModBulkUpdateConfirmationStage.Checking)
            return;

        var progressPercent = Math.Clamp(batchPercent / 20d * 100d, 0, 100);
        var completedCount = CheckTotalCount == 0
            ? 0
            : Math.Min(
                CheckTotalCount,
                (int)Math.Floor(CheckTotalCount * progressPercent / 100d));
        if (Math.Abs(CheckProgressPercent - progressPercent) < 0.01
            && CheckCompletedCount == completedCount)
        {
            return;
        }

        CheckProgressPercent = progressPercent;
        CheckCompletedCount = completedCount;
        Changed?.Invoke();
    }

    public void ShowSummary(
        int updateCount,
        int upToDateCount,
        int notRecognizedCount,
        int unavailableCount,
        int partialProviderFailureCount)
    {
        cancelChecking = null;
        UpdateCount = updateCount;
        UpToDateCount = upToDateCount;
        NotRecognizedCount = notRecognizedCount;
        UnavailableCount = unavailableCount;
        PartialProviderFailureCount = partialProviderFailureCount;
        Stage = ModBulkUpdateConfirmationStage.Summary;
        Changed?.Invoke();
    }

    public void ShowFailure()
    {
        cancelChecking = null;
        Stage = ModBulkUpdateConfirmationStage.Failed;
        Changed?.Invoke();
    }

    public void Close() => Closed?.Invoke();
}
