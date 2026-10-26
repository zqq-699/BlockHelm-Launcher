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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Application.Accounts;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Account;

public sealed partial class AccountDialogViewModel
{
    public void OpenDeleteAccountDialog(LauncherAccount account)
    {
        AccountPendingDelete = account;
        IsDeleteAccountDialogOpen = true;
    }

    public void CancelDeleteAccountDialog()
    {
        IsDeleteAccountDialogOpen = false;
        AccountPendingDelete = null;
    }

    public async Task ConfirmDeleteAccountDialogAsync()
    {
        if (AccountPendingDelete is null)
            return;

        var account = AccountPendingDelete;
        var deletedName = account.DisplayName;

        // 先从可见列表移除并关闭对话框，再清理 Microsoft 缓存；缓存清理失败不应把已删除账户重新插回。
        var removeTask = accountList.RemoveAsync(account);
        IsDeleteAccountDialogOpen = false;
        AccountPendingDelete = null;
        ReportStatus(string.Format(Strings.Status_AccountDeletedFormat, deletedName));

        try
        {
            await removeTask;
            if (account.IsMicrosoft)
                await microsoftAccountService.DeleteAccountAsync(account);
            else if (account.IsThirdParty)
                await thirdPartyAccountService.DeleteCredentialsAsync(account.Id);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Account delete cleanup failed. AccountId={AccountId} IsOffline={IsOffline} ErrorType={ErrorType}",
                account.Id, account.IsOffline, exception.GetType().FullName);
            ReportStatus(string.Format(Strings.Status_AccountDeletedCacheCleanupFailedFormat, deletedName));
        }
    }
}
