/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Resources;
using Launcher.App.ViewModels.GameSettings;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class GameSettingsDialogsModBulkUpdateTests
{
    [Fact]
    public async Task BulkDialogMovesFromRiskToCheckingToSummary()
    {
        var dialogs = new GameSettingsDialogsViewModel(null!, null!, null!);
        var request = new ModBulkUpdateConfirmationRequest();

        dialogs.OpenModBulkUpdate(request);

        Assert.True(dialogs.IsModBulkUpdateDialogOpen);
        Assert.Equal(Strings.Dialog_ModUpdateRiskWarning, dialogs.ModBulkUpdateDialogMessage);
        Assert.True(dialogs.CanConfirmModBulkUpdate);

        dialogs.ConfirmModBulkUpdateDialogCommand.Execute(null);
        Assert.True(await request.RiskCompletion);

        request.BeginChecking(10, () => { });
        request.ReportCheckingProgress(10);
        Assert.Equal(Strings.Status_ModBulkUpdateChecking, dialogs.ModBulkUpdateDialogMessage);
        Assert.False(dialogs.CanConfirmModBulkUpdate);
        Assert.True(dialogs.IsModBulkUpdateChecking);
        Assert.Equal(50, dialogs.ModBulkUpdateCheckingProgress);
        Assert.Equal("5/10", dialogs.ModBulkUpdateCheckingCountText);

        request.ShowSummary(3, 4, 5, 6, 1);
        Assert.True(dialogs.CanConfirmModBulkUpdate);
        Assert.Contains(Strings.Dialog_ModUpdateRiskWarning, dialogs.ModBulkUpdateDialogMessage, StringComparison.Ordinal);
        Assert.Contains("3", dialogs.ModBulkUpdateDialogMessage, StringComparison.Ordinal);

        dialogs.ConfirmModBulkUpdateDialogCommand.Execute(null);

        Assert.True(await request.SummaryCompletion);
        Assert.False(dialogs.IsModBulkUpdateDialogOpen);
    }

    [Fact]
    public void CancelButtonCancelsAnActiveBulkCheckAndClosesDialog()
    {
        var dialogs = new GameSettingsDialogsViewModel(null!, null!, null!);
        var request = new ModBulkUpdateConfirmationRequest();
        var canceled = false;
        dialogs.OpenModBulkUpdate(request);
        dialogs.ConfirmModBulkUpdateDialogCommand.Execute(null);
        request.BeginChecking(10, () => canceled = true);

        dialogs.CancelModBulkUpdateDialogCommand.Execute(null);

        Assert.True(canceled);
        Assert.False(dialogs.IsModBulkUpdateDialogOpen);
    }

    [Fact]
    public async Task FailedBulkCheckShowsOnlyAcknowledgementAction()
    {
        var dialogs = new GameSettingsDialogsViewModel(null!, null!, null!);
        var request = new ModBulkUpdateConfirmationRequest();
        dialogs.OpenModBulkUpdate(request);
        dialogs.ConfirmModBulkUpdateDialogCommand.Execute(null);
        request.BeginChecking(10, () => { });

        request.ShowFailure();

        Assert.Equal(Strings.Dialog_ModBulkUpdateFailed, dialogs.ModBulkUpdateDialogMessage);
        Assert.True(dialogs.CanConfirmModBulkUpdate);
        Assert.False(dialogs.CanCancelModBulkUpdate);
        Assert.Equal(Strings.Confirm_Button, dialogs.ModBulkUpdateConfirmButtonText);
        Assert.True(dialogs.IsModBulkUpdateDialogOpen);

        dialogs.ConfirmModBulkUpdateDialogCommand.Execute(null);

        Assert.True(await request.SummaryCompletion);
        Assert.False(dialogs.IsModBulkUpdateDialogOpen);
    }
}
