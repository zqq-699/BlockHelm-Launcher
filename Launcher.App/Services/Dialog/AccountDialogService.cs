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

using System.Windows;
using Launcher.App.Controls;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Launcher.App.Views.Account.Dialogs;

namespace Launcher.App.Services;

public sealed class AccountDialogService : IAccountDialogService
{
    private AccountPageViewModel? accountPage;
    private DialogHost? addAccountHost;
    private AddAccountDialogView? addAccountView;
    private DialogHost? deleteAccountHost;
    private DialogHost? renameAccountHost;
    private DialogHost? skinModelDialogHost;
    private DialogHost? skinManagerDialogHost;
    private TaskCompletionSource<bool>? thirdPartyReauthenticationCompletion;
    private TaskCompletionSource<bool>? microsoftReauthenticationCompletion;

    public void Attach(
        AccountPageViewModel accountPage,
        DialogHost addAccountHost,
        AddAccountDialogView addAccountView,
        DialogHost deleteAccountHost,
        DialogHost renameAccountHost,
        DialogHost skinModelDialogHost,
        DialogHost skinManagerDialogHost)
    {
        this.accountPage = accountPage;
        this.addAccountHost = addAccountHost;
        this.addAccountView = addAccountView;
        this.deleteAccountHost = deleteAccountHost;
        this.renameAccountHost = renameAccountHost;
        this.skinModelDialogHost = skinModelDialogHost;
        this.skinManagerDialogHost = skinManagerDialogHost;
    }

    public void ShowAddAccountDialog()
    {
        if (accountPage is null || addAccountHost is null)
            return;

        accountPage.Dialog.OpenAddAccountDialog();
        addAccountHost.Show();
    }

    public void ShowThirdPartyAddAccountDialog(string authenticationServer)
    {
        if (accountPage is null || addAccountHost is null || addAccountView is null)
            return;

        if (addAccountHost.IsOpen
            && accountPage.Dialog.IsAddAccountDialogOpen
            && (accountPage.Dialog.IsAccountTypeStep || accountPage.Dialog.IsThirdPartyCredentialsStep))
        {
            var shouldAnimate = accountPage.Dialog.IsAccountTypeStep;
            var previousHeight = addAccountHost.SurfaceBorder.ActualHeight;
            if (accountPage.Dialog.ApplyThirdPartyAuthenticationServer(authenticationServer))
                addAccountView.ClearThirdPartyPassword();
            if (shouldAnimate)
                addAccountHost.AnimateSizeChange(previousHeight);
            return;
        }

        addAccountView.ClearThirdPartyPassword();
        accountPage.Dialog.OpenThirdPartyAddAccountDialog(authenticationServer);
        addAccountHost.Show();
    }

    public Task<bool> ShowThirdPartyReauthenticationDialogAsync(LauncherAccount account)
    {
        if (accountPage is null || addAccountHost is null || addAccountView is null || !account.IsThirdParty)
            return Task.FromResult(false);

        thirdPartyReauthenticationCompletion?.TrySetResult(false);
        thirdPartyReauthenticationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        addAccountView.ClearThirdPartyPassword();
        accountPage.Dialog.OpenThirdPartyReauthenticationDialog(account);
        addAccountHost.Show();
        return thirdPartyReauthenticationCompletion.Task;
    }

    public Task<bool> ShowMicrosoftReauthenticationDialogAsync(LauncherAccount account)
    {
        if (accountPage is null || addAccountHost is null || !account.IsMicrosoft)
            return Task.FromResult(false);

        microsoftReauthenticationCompletion?.TrySetResult(false);
        microsoftReauthenticationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        accountPage.Dialog.OpenMicrosoftReauthenticationDialog(account);
        addAccountHost.Show();
        return microsoftReauthenticationCompletion.Task;
    }

    public void ShowDeleteAccountDialog(LauncherAccount account)
    {
        if (accountPage is null || deleteAccountHost is null)
            return;

        accountPage.Dialog.OpenDeleteAccountDialog(account);
        deleteAccountHost.Show();
    }

    public void ShowRenameAccountDialog()
    {
        if (accountPage is null || renameAccountHost is null)
            return;

        accountPage.Dialog.OpenRenameAccountDialog();
        if (accountPage.Dialog.IsRenameAccountDialogOpen)
            renameAccountHost.Show();
    }

    public void ShowSkinModelDialog(string skinFilePath)
    {
        if (accountPage is null || skinModelDialogHost is null)
            return;

        accountPage.Appearance.SkinLibrary.SkinModelDialog.Open(skinFilePath);
        skinModelDialogHost.Show();
    }

    public void ShowSkinModelDialog(MinecraftSkinModel skinModel)
    {
        if (accountPage is null || skinModelDialogHost is null)
            return;

        accountPage.Appearance.SkinLibrary.SkinModelDialog.OpenForExistingSkin(skinModel);
        skinModelDialogHost.Show();
    }

    public void ShowSkinFormatErrorDialog()
    {
        if (accountPage is null || skinModelDialogHost is null)
            return;

        accountPage.Appearance.SkinLibrary.SkinModelDialog.OpenFormatError();
        skinModelDialogHost.Show();
    }

    public void ShowSkinManagerDialog()
    {
        if (accountPage is null || skinManagerDialogHost is null)
            return;

        accountPage.Appearance.SkinLibrary.OpenManagerDialog();
        if (accountPage.Appearance.SkinLibrary.IsManagerDialogOpen)
            skinManagerDialogHost.Show();
    }

    public void CancelAddAccountDialog()
    {
        if (accountPage is null || addAccountHost is null)
            return;

        var wasReauthentication = accountPage.Dialog.IsThirdPartyReauthenticationStep;
        var wasMicrosoftReauthentication = accountPage.Dialog.IsMicrosoftReauthenticationMode;
        accountPage.Dialog.CancelAddAccountDialog();
        if (!accountPage.Dialog.IsAddAccountDialogOpen)
        {
            addAccountView?.ClearThirdPartyPassword();
            addAccountHost.Hide(accountPage.Dialog.ResetAddAccountDialog);
            if (wasReauthentication)
            {
                thirdPartyReauthenticationCompletion?.TrySetResult(false);
                thirdPartyReauthenticationCompletion = null;
            }
            if (wasMicrosoftReauthentication)
            {
                microsoftReauthenticationCompletion?.TrySetResult(false);
                microsoftReauthenticationCompletion = null;
            }
        }
    }

    public void BackAddAccountDialog()
    {
        if (accountPage is null || addAccountHost is null)
            return;

        var previousHeight = addAccountHost.SurfaceBorder.ActualHeight;

        accountPage.Dialog.BackToAddAccountTypeStep();
        addAccountHost.AnimateSizeChange(previousHeight);
    }

    public async Task ConfirmAddAccountDialogAsync()
    {
        if (accountPage is null || addAccountHost is null)
            return;

        var previousHeight = addAccountHost.SurfaceBorder.ActualHeight;

        if (accountPage.Dialog.IsMicrosoftReauthenticationResultStep)
        {
            await CompleteMicrosoftReauthenticationAsync();
            return;
        }

        if (accountPage.Dialog.IsMicrosoftReauthenticationPromptStep)
        {
            await CompleteMicrosoftReauthenticationAsync();
            return;
        }

        if (accountPage.Dialog.IsAccountTypeStep
            && accountPage.Dialog.SelectedAccountTypeOption?.Kind is AccountTypeKinds.Microsoft)
        {
            accountPage.Dialog.BeginMicrosoftAccountLogin();
            addAccountHost.AnimateSizeChange(previousHeight);

            var loginHeight = addAccountHost.SurfaceBorder.ActualHeight;
            await accountPage.Dialog.CompleteMicrosoftAccountLoginAsync();
            addAccountHost.AnimateSizeChange(loginHeight);
            return;
        }

        var wasReauthentication = accountPage.Dialog.IsThirdPartyReauthenticationStep;
        var wasProfileSelection = accountPage.Dialog.IsThirdPartyProfileSelectionStep;
        var confirmTask = accountPage.Dialog.ConfirmAddAccountDialogAsync(addAccountView?.ThirdPartyPassword);
        if (wasProfileSelection)
            addAccountHost.AnimateSizeChange(previousHeight);
        await confirmTask;
        if (accountPage.Dialog.IsAddAccountDialogOpen)
            addAccountHost.AnimateSizeChange(previousHeight);
        else
        {
            addAccountView?.ClearThirdPartyPassword();
            addAccountHost.Hide(accountPage.Dialog.ResetAddAccountDialog);
            if (wasReauthentication)
            {
                thirdPartyReauthenticationCompletion?.TrySetResult(true);
                thirdPartyReauthenticationCompletion = null;
            }
        }
    }

    private async Task CompleteMicrosoftReauthenticationAsync()
    {
        if (accountPage is null || addAccountHost is null)
            return;

        var previousHeight = addAccountHost.SurfaceBorder.ActualHeight;
        var succeeded = await accountPage.Dialog.CompleteMicrosoftAccountReauthenticationAsync();
        if (succeeded)
        {
            addAccountHost.Hide(accountPage.Dialog.ResetAddAccountDialog);
            microsoftReauthenticationCompletion?.TrySetResult(true);
            microsoftReauthenticationCompletion = null;
        }
        else if (accountPage.Dialog.IsAddAccountDialogOpen)
        {
            addAccountHost.AnimateSizeChange(previousHeight);
        }
    }

    public void SelectAllThirdPartyProfiles()
    {
        accountPage?.Dialog.SelectAllThirdPartyProfiles();
    }

    public async Task RetryThirdPartyProfileImportAsync()
    {
        if (accountPage is null || addAccountHost is null)
            return;
        var previousHeight = addAccountHost.SurfaceBorder.ActualHeight;
        var retryTask = accountPage.Dialog.RetryThirdPartyProfileImportAsync(addAccountView?.ThirdPartyPassword ?? string.Empty);
        addAccountHost.AnimateSizeChange(previousHeight);
        await retryTask;
        if (accountPage.Dialog.IsAddAccountDialogOpen)
            addAccountHost.AnimateSizeChange(previousHeight);
        else
        {
            addAccountView?.ClearThirdPartyPassword();
            addAccountHost.Hide(accountPage.Dialog.ResetAddAccountDialog);
        }
    }

    public void CancelDeleteAccountDialog()
    {
        if (accountPage is null || deleteAccountHost is null)
            return;

        accountPage.Dialog.CancelDeleteAccountDialog();
        deleteAccountHost.Hide();
    }

    public async Task ConfirmDeleteAccountDialogAsync()
    {
        if (accountPage is null || deleteAccountHost is null)
            return;

        var deleteTask = accountPage.Dialog.ConfirmDeleteAccountDialogAsync();
        if (!accountPage.Dialog.IsDeleteAccountDialogOpen)
        {
            deleteAccountHost.Hide();
            await deleteTask;
            return;
        }

        await deleteTask;
        if (!accountPage.Dialog.IsDeleteAccountDialogOpen)
            deleteAccountHost.Hide();
    }

    public void CancelRenameAccountDialog()
    {
        if (accountPage is null || renameAccountHost is null)
            return;

        accountPage.Dialog.CancelRenameAccountDialog();
        if (!accountPage.Dialog.IsRenameAccountDialogOpen)
            renameAccountHost.Hide(accountPage.Dialog.ResetRenameAccountDialog);
    }

    public async Task ConfirmRenameAccountDialogAsync()
    {
        if (accountPage is null || renameAccountHost is null)
            return;

        var previousHeight = renameAccountHost.SurfaceBorder.ActualHeight;
        await accountPage.Dialog.ConfirmRenameAccountDialogAsync();

        if (accountPage.Dialog.IsRenameAccountDialogOpen)
            renameAccountHost.AnimateSizeChange(previousHeight);
        else
            renameAccountHost.Hide(accountPage.Dialog.ResetRenameAccountDialog);
    }

    public void CancelSkinModelDialog()
    {
        if (accountPage is null || skinModelDialogHost is null)
            return;

        accountPage.Appearance.SkinLibrary.SkinModelDialog.Cancel();
        skinModelDialogHost.Hide(accountPage.Appearance.SkinLibrary.SkinModelDialog.Reset);
    }

    public async Task ConfirmSkinModelDialogAsync()
    {
        if (accountPage is null || skinModelDialogHost is null)
            return;

        var confirmTask = accountPage.Appearance.SkinLibrary.ConfirmSkinModelDialogAsync();
        if (!accountPage.Appearance.SkinLibrary.SkinModelDialog.IsSkinModelDialogOpen)
        {
            skinModelDialogHost.Hide(accountPage.Appearance.SkinLibrary.SkinModelDialog.Reset);
            await confirmTask;
            return;
        }

        await confirmTask;
        if (!accountPage.Appearance.SkinLibrary.SkinModelDialog.IsSkinModelDialogOpen)
            skinModelDialogHost.Hide(accountPage.Appearance.SkinLibrary.SkinModelDialog.Reset);
    }

    public void CancelSkinManagerDialog()
    {
        if (accountPage is null || skinManagerDialogHost is null)
            return;

        accountPage.Appearance.SkinLibrary.CloseManagerDialog();
        skinManagerDialogHost.Hide();
    }
}
