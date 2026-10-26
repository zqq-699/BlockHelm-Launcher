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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Accounts;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Account;

public sealed partial class AccountProfileViewModel : ObservableObject
{
    private readonly AccountListViewModel accountList;
    private readonly IMicrosoftAccountService microsoftAccountService;
    private readonly IThirdPartyAccountService thirdPartyAccountService;
    private readonly AccountAppearanceOperationCoordinator operations;
    private readonly MicrosoftAccountOperationRetryHandler microsoftOperationRetryHandler;
    private readonly IFloatingMessageService? floatingMessageService;
    private readonly ILogger logger;

    internal AccountProfileViewModel(
        AccountListViewModel accountList,
        IMicrosoftAccountService microsoftAccountService,
        IThirdPartyAccountService thirdPartyAccountService,
        AccountAppearanceOperationCoordinator operations,
        MicrosoftAccountOperationRetryHandler microsoftOperationRetryHandler,
        IFloatingMessageService? floatingMessageService,
        ILogger logger)
    {
        this.accountList = accountList;
        this.microsoftAccountService = microsoftAccountService;
        this.thirdPartyAccountService = thirdPartyAccountService;
        this.operations = operations;
        this.microsoftOperationRetryHandler = microsoftOperationRetryHandler;
        this.floatingMessageService = floatingMessageService;
        this.logger = logger;
        operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountAppearanceOperationCoordinator.IsBusy))
                OnPropertyChanged(nameof(IsBusy));
        };
    }

    [ObservableProperty]
    private string message = string.Empty;

    [ObservableProperty]
    private string errorCodeMessage = string.Empty;

    public bool IsBusy => operations.IsBusy;

    public bool HasErrorCode => !string.IsNullOrWhiteSpace(ErrorCodeMessage);

    public bool CanRefresh => accountList.SelectedAccount is { IsOffline: false } && !IsBusy;

    public void SetAccount(LauncherAccount? account)
    {
        operations.SetAccount(account);
        ErrorCodeMessage = string.Empty;
        Message = account is null
            ? string.Empty
            : account.IsOffline
                ? Strings.Account_ProfileOfflineUnsupported
                : account.CachedCapeOptions.Count > 0
                    ? Strings.Account_ProfileCacheLoaded
                    : Strings.Account_ProfileRefreshHint;
        RefreshInfoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshInfoAsync()
    {
        var account = accountList.SelectedAccount;
        if (account is null)
            return;
        if (account.IsOffline)
        {
            Message = Strings.Status_AccountProfileRefreshOfflineUnsupported;
            return;
        }

        var operation = operations.Begin(account);
        SetMessage(Strings.Status_RefreshingAccountProfile);
        try
        {
            var result = account.IsMicrosoft
                ? await microsoftOperationRetryHandler.ExecuteAsync(
                    account,
                    current => microsoftAccountService.RefreshAccountProfileAsync(current, operation.Token))
                : new MicrosoftAccountOperationResult<LauncherAccount>(
                    account,
                    await thirdPartyAccountService.RefreshAccountProfileAsync(account, operation.Token));
            if (!operations.IsCurrent(result.Account, operation))
                return;
            var updated = account.IsMicrosoft
                ? AccountMapper.WithCapeCache(result.Value, result.Account.CachedCapeOptions)
                : result.Value;
            accountList.ReplaceSelectedAccount(result.Account, updated);
            await accountList.PersistAccountOrderAsync();
            SetMessage(Strings.Status_AccountProfileRefreshed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Account profile refresh failed. AccountId={AccountId} AccountKind={AccountKind} ErrorType={ErrorType}",
                account.Id,
                account.Kind,
                exception.GetType().FullName);
            SetError(exception, GetRefreshFailureMessage(exception, Strings.Status_AccountProfileRefreshFailed), showFloating: true);
        }
        finally
        {
            operations.Complete(account, operation);
            RefreshInfoCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task RefreshAccountsSilentlyAsync()
    {
        var accounts = accountList.Accounts
            .Where(item => item.Account.IsMicrosoft || item.Account.IsThirdParty)
            .Select(item => item.Account)
            .ToList();
        var changed = false;
        foreach (var account in accounts)
        {
            LauncherAccount refreshed;
            try
            {
                refreshed = account.IsMicrosoft
                    ? await microsoftAccountService.RefreshAccountProfileAsync(account)
                    : await thirdPartyAccountService.RefreshAccountProfileAsync(account);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogDebug(
                    "Silent account refresh failed. AccountId={AccountId} AccountKind={AccountKind} ErrorType={ErrorType}",
                    account.Id,
                    account.Kind,
                    exception.GetType().FullName);
                continue;
            }

            var current = accountList.FindAccount(account.Id);
            if (current is null || IsBusy && string.Equals(current.Id, accountList.SelectedAccount?.Id, StringComparison.Ordinal))
                continue;
            var updated = account.IsMicrosoft
                ? AccountMapper.WithCapeCache(refreshed, current.CachedCapeOptions)
                : refreshed;
            changed |= accountList.TryReplaceAccount(current.Id, updated);
        }

        if (!changed)
            return;
        try
        {
            await accountList.PersistAccountOrderAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning("Persisting silently refreshed accounts failed. ErrorType={ErrorType}",
                exception.GetType().FullName);
        }
    }

    public void SetMessage(string message, bool showFloating = false)
    {
        Message = message;
        ErrorCodeMessage = string.Empty;
        if (showFloating)
            floatingMessageService?.Show(message);
    }

    public void SetError(Exception exception, string message, bool showFloating = false)
    {
        Message = message;
        ErrorCodeMessage = AccountErrorCodeMessageFormatter.Format(exception);
        if (showFloating)
            floatingMessageService?.Show(message);
    }

    internal AccountAppearanceOperation BeginOperation(LauncherAccount account, string message)
    {
        var operation = operations.Begin(account);
        SetMessage(message);
        return operation;
    }

    internal bool IsCurrent(LauncherAccount account, AccountAppearanceOperation operation) =>
        operations.IsCurrent(account, operation);

    internal void Complete(LauncherAccount account, AccountAppearanceOperation operation) =>
        operations.Complete(account, operation);

    internal static string GetRefreshFailureMessage(Exception exception, string fallback)
    {
        var error = exception is MicrosoftAccountProfileRefreshException { ErrorCode: { Length: > 0 } code }
            ? code
            : exception.Message;
        return !string.IsNullOrWhiteSpace(error)
            && error.Contains("429", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("too many request", StringComparison.OrdinalIgnoreCase)
                || error.Contains("too_many_request", StringComparison.OrdinalIgnoreCase))
            ? Strings.Status_AccountProfileRefreshTooFrequent
            : fallback;
    }
}
