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

public interface IMicrosoftAccountReauthenticationDialogService
{
    Task<bool> ShowMicrosoftReauthenticationDialogAsync(LauncherAccount account);
}

public interface IAccountDialogService : IMicrosoftAccountReauthenticationDialogService
{
    void Attach(
        AccountPageViewModel accountPage,
        DialogHost addAccountHost,
        AddAccountDialogView addAccountView,
        DialogHost deleteAccountHost,
        DialogHost renameAccountHost,
        DialogHost skinModelDialogHost,
        DialogHost skinManagerDialogHost);

    void ShowAddAccountDialog();

    void ShowThirdPartyAddAccountDialog(string authenticationServer);

    Task<bool> ShowThirdPartyReauthenticationDialogAsync(LauncherAccount account);

    void ShowDeleteAccountDialog(LauncherAccount account);

    void ShowRenameAccountDialog();

    void ShowSkinModelDialog(string skinFilePath);

    void ShowSkinModelDialog(MinecraftSkinModel skinModel);

    void ShowSkinFormatErrorDialog();

    void ShowSkinManagerDialog();

    void CancelAddAccountDialog();

    void BackAddAccountDialog();

    Task ConfirmAddAccountDialogAsync();

    void SelectAllThirdPartyProfiles();

    Task RetryThirdPartyProfileImportAsync();

    void CancelDeleteAccountDialog();

    Task ConfirmDeleteAccountDialogAsync();

    void CancelRenameAccountDialog();

    Task ConfirmRenameAccountDialogAsync();

    void CancelSkinModelDialog();

    Task ConfirmSkinModelDialogAsync();

    void CancelSkinManagerDialog();
}

