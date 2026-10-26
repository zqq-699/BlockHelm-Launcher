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
    public void OpenRenameAccountDialog()
    {
        if (accountList.SelectedAccount is null || accountList.SelectedAccount.IsThirdParty)
            return;

        AccountPendingRename = accountList.SelectedAccount;
        ResetRenameAccountDialogState(accountList.SelectedAccount.DisplayName);
        IsRenameAccountDialogOpen = true;
    }

    public void CancelRenameAccountDialog()
    {
        if (IsRenameAccountDialogBusy)
            return;

        IsRenameAccountDialogOpen = false;
    }

    public void ResetRenameAccountDialog()
    {
        AccountPendingRename = null;
        ResetRenameAccountDialogState(string.Empty);
    }

    public async Task ConfirmRenameAccountDialogAsync()
    {
        if (IsRenameAccountDialogBusy)
            return;

        if (IsRenameAccountResultStep)
        {
            IsRenameAccountDialogOpen = false;
            return;
        }

        var account = AccountPendingRename;
        if (account is null)
            return;
        if (account.IsThirdParty)
        {
            IsRenameAccountDialogOpen = false;
            return;
        }

        var newName = RenameAccountName.Trim();
        if (!AccountNameValidator.IsValid(newName))
        {
            IsRenameAccountNameInvalid = true;
            ReportStatus(AccountNameValidator.ValidationMessage);
            return;
        }

        if (string.Equals(newName, account.DisplayName, StringComparison.Ordinal))
        {
            ShowRenameAccountResult(true, Strings.Status_AccountNameUnchanged);
            return;
        }

        try
        {
            // 状态页在远端操作期间替代输入页，既阻止重复提交，也向用户说明可能较慢的网络步骤。
            IsRenameAccountDialogBusy = true;
            RenameAccountDialogStep = AccountDialogSteps.RenameStatus;
            RenameAccountIcon = DialogBusyIcon;
            RenameAccountMessage = account.IsOffline
                ? Strings.Status_SavingOfflineAccountName
                : Strings.Status_ChangingMicrosoftAccountName;

            LauncherAccount updatedAccount;
            if (account.IsOffline)
            {
                // 离线名称参与 UUID 生成；保留原生成模式，才能兼容不同历史版本创建的账户。
                var uuid = offlineUuidService.CreateUuid(
                    newName,
                    account.OfflineUuidGenerationMode,
                    account.Uuid);
                updatedAccount = AccountMapper.WithDisplayNameAndOfflineUuid(
                    account,
                    newName,
                    account.OfflineUuidGenerationMode,
                    uuid);
            }
            else
            {
                // Microsoft 重命名由远端服务执行，不能只修改本地显示名，否则下次刷新会被服务端覆盖。
                var renamedAccount = await microsoftAccountService.ChangeNameAsync(account, newName);
                updatedAccount = AccountMapper.WithCapeCache(
                    AccountMapper.WithAppearanceFallback(renamedAccount, account),
                    account.CachedCapeOptions);
            }

            accountList.ReplaceSelectedAccount(account, updatedAccount);
            AccountPendingRename = updatedAccount;
            await accountList.PersistAccountOrderAsync();
            var message = string.Format(Strings.Status_AccountRenamedFormat, updatedAccount.DisplayName);
            ReportStatus(message);
            ShowRenameAccountResult(true, string.Format(Strings.Status_AccountRenameResultFormat, updatedAccount.DisplayName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Account rename failed. AccountId={AccountId} IsOffline={IsOffline} ErrorType={ErrorType}",
                account.Id,
                account.IsOffline,
                ex.GetType().FullName);
            var message = GetRenameFailureMessage(ex);
            var errorCodeMessage = AccountErrorCodeMessageFormatter.Format(ex);
            ReportStatus(message);
            ShowRenameAccountResult(false, message, errorCodeMessage);
        }
        finally
        {
            IsRenameAccountDialogBusy = false;
        }
    }

    partial void OnAddAccountDialogStepChanged(string value)
    {
        NotifyAddAccountDialogStepPropertiesChanged();
    }

    partial void OnIsAddAccountDialogBusyChanged(bool value)
    {
        NotifyAddAccountDialogActionPropertiesChanged();
    }

    partial void OnIsMicrosoftLoginSuccessfulChanged(bool value)
    {
        OnPropertyChanged(nameof(AddAccountDialogTitle));
        OnPropertyChanged(nameof(MicrosoftLoginIconKey));
    }

    partial void OnIsMicrosoftAccountAlreadyAddedChanged(bool value)
    {
        OnPropertyChanged(nameof(AddAccountDialogTitle));
    }

    partial void OnAccountPendingMicrosoftReauthenticationChanged(LauncherAccount? value)
    {
        OnPropertyChanged(nameof(IsMicrosoftReauthenticationMode));
        NotifyAddAccountDialogActionPropertiesChanged();
    }

    partial void OnSelectedAccountTypeOptionChanged(AccountTypeOption? value)
    {
        OnPropertyChanged(nameof(IsMicrosoftAccountTypeSelected));
        OnPropertyChanged(nameof(CanConfirmAddAccountDialog));
    }

    partial void OnRenameAccountDialogStepChanged(string value)
    {
        NotifyRenameAccountDialogStepPropertiesChanged();
    }

    partial void OnIsRenameAccountDialogBusyChanged(bool value)
    {
        NotifyRenameAccountDialogActionPropertiesChanged();
    }

    partial void OnAccountPendingRenameChanged(LauncherAccount? value)
    {
        OnPropertyChanged(nameof(IsRenameMicrosoftAccount));
        OnPropertyChanged(nameof(RenameAccountDialogSubtitle));
    }

    partial void OnIsRenameAccountSuccessfulChanged(bool value)
    {
        OnPropertyChanged(nameof(RenameAccountDialogTitle));
        OnPropertyChanged(nameof(RenameAccountIconKey));
    }

    partial void OnRenameAccountNameChanged(string value)
    {
        IsRenameAccountNameInvalid = false;
        OnPropertyChanged(nameof(CanConfirmRenameAccountDialog));
    }

    partial void OnRenameAccountErrorCodeMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasRenameAccountErrorCode));
    }

    partial void OnNewOfflineAccountNameChanged(string value)
    {
        IsNewOfflineAccountNameInvalid = false;
    }

    partial void OnThirdPartyImportCompletedCountChanged(int value) =>
        OnPropertyChanged(nameof(ThirdPartyImportProgressText));

    partial void OnThirdPartyImportTotalCountChanged(int value) =>
        OnPropertyChanged(nameof(ThirdPartyImportProgressText));

    partial void OnThirdPartyImportFailedCountChanged(int value) =>
        OnPropertyChanged(nameof(ThirdPartyImportFailureText));

}
