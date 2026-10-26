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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Account;

public sealed partial class AccountOfflineUuidViewModel : ObservableObject
{
    private readonly AccountListViewModel accountList;
    private readonly IOfflineAccountUuidService offlineUuidService;
    private readonly IStatusService statusService;
    private readonly IClipboardService clipboardService;
    private readonly ILogger<AccountOfflineUuidViewModel> logger;
    private bool isRefreshingSelection;
    private OfflineUuidModeOption? acceptedOfflineUuidOption;
    private OfflineUuidModeOption? pendingOfflineUuidOption;
    private LauncherAccount? pendingAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeOfflineUuidMode))]
    [NotifyPropertyChangedFor(nameof(CanApplyManualUuid))]
    [NotifyCanExecuteChangedFor(nameof(ApplyManualUuidCommand))]
    private bool isOfflineUuidModeChangeDialogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeOfflineUuidMode))]
    [NotifyPropertyChangedFor(nameof(CanApplyManualUuid))]
    [NotifyCanExecuteChangedFor(nameof(ApplyManualUuidCommand))]
    private bool isSavingUuid;

    [ObservableProperty]
    private OfflineUuidModeOption? selectedOfflineUuidOption;

    [ObservableProperty]
    private string manualUuidText = string.Empty;

    [ObservableProperty]
    private bool isManualUuidInvalid;

    public AccountOfflineUuidViewModel(
        AccountListViewModel accountList,
        IOfflineAccountUuidService offlineUuidService,
        IStatusService statusService,
        IClipboardService clipboardService,
        ILogger<AccountOfflineUuidViewModel>? logger = null)
    {
        this.accountList = accountList;
        this.offlineUuidService = offlineUuidService;
        this.statusService = statusService;
        this.clipboardService = clipboardService;
        this.logger = logger ?? NullLogger<AccountOfflineUuidViewModel>.Instance;

        OfflineUuidOptions = new ObservableCollection<OfflineUuidModeOption>(
        [
            new()
            {
                Mode = OfflineUuidGenerationMode.Standard,
                Title = Strings.Account_OfflineUuidStandardTitle,
                Description = Strings.Account_OfflineUuidStandardDescription
            },
            new()
            {
                Mode = OfflineUuidGenerationMode.Manual,
                Title = Strings.Account_OfflineUuidManualTitle,
                Description = Strings.Account_OfflineUuidManualDescription
            }
        ]);

        accountList.PropertyChanged += AccountList_PropertyChanged;
        RefreshSelection();
    }

    public ObservableCollection<OfflineUuidModeOption> OfflineUuidOptions { get; }

    public bool HasSelectedOfflineAccount => accountList.SelectedAccount?.IsOffline == true;

    public bool HasManualUuidEditor =>
        HasSelectedOfflineAccount && acceptedOfflineUuidOption?.Mode == OfflineUuidGenerationMode.Manual;

    public bool CanChangeOfflineUuidMode => HasSelectedOfflineAccount && !IsOfflineUuidModeChangeDialogOpen && !IsSavingUuid;

    public bool CanApplyManualUuid => CanChangeOfflineUuidMode && HasManualUuidEditor && !string.IsNullOrWhiteSpace(ManualUuidText);

    public string OfflineUuidModeChangeMessage => string.Format(
        Strings.Dialog_OfflineUuidModeChangeMessageFormat, pendingOfflineUuidOption?.Title);

    public string SelectedAccountUuidText
    {
        get
        {
            var uuid = accountList.SelectedAccount?.Uuid;
            return string.IsNullOrWhiteSpace(uuid) ? Strings.Account_NoneValue : uuid;
        }
    }

    partial void OnSelectedOfflineUuidOptionChanged(OfflineUuidModeOption? value)
    {
        if (isRefreshingSelection || value is null)
            return;

        if (!CanChangeOfflineUuidMode)
        {
            RestoreAcceptedOption();
            return;
        }

        if (value.Mode == acceptedOfflineUuidOption?.Mode)
            return;

        pendingAccount = accountList.SelectedAccount;
        pendingOfflineUuidOption = value;
        OnPropertyChanged(nameof(OfflineUuidModeChangeMessage));
        IsOfflineUuidModeChangeDialogOpen = true;
    }

    [RelayCommand]
    private void CancelOfflineUuidModeChange()
    {
        pendingAccount = null;
        pendingOfflineUuidOption = null;
        IsOfflineUuidModeChangeDialogOpen = false;
        RestoreAcceptedOption();
    }

    [RelayCommand]
    private async Task ConfirmOfflineUuidModeChangeAsync()
    {
        var account = pendingAccount;
        var option = pendingOfflineUuidOption;
        if (!IsOfflineUuidModeChangeDialogOpen || IsSavingUuid || account is null || option is null
            || !ReferenceEquals(accountList.SelectedAccount, account))
        {
            CancelOfflineUuidModeChange();
            return;
        }

        pendingAccount = null;
        pendingOfflineUuidOption = null;
        IsOfflineUuidModeChangeDialogOpen = false;
        logger.LogInformation("Offline UUID mode change confirmed. AccountId={AccountId} Mode={Mode}", account.Id, option.Mode);

        if (option.Mode == OfflineUuidGenerationMode.Manual)
        {
            acceptedOfflineUuidOption = option;
            RestoreAcceptedOption();
            IsManualUuidInvalid = false;
            ManualUuidText = account.Uuid ?? string.Empty;
            OnPropertyChanged(nameof(HasManualUuidEditor));
            OnPropertyChanged(nameof(CanApplyManualUuid));
            ApplyManualUuidCommand.NotifyCanExecuteChanged();
            return;
        }

        await SelectOfflineUuidModeAsync(account, option);
    }

    private void RestoreAcceptedOption()
    {
        isRefreshingSelection = true;
        try { SelectedOfflineUuidOption = acceptedOfflineUuidOption; }
        finally { isRefreshingSelection = false; }
    }

    partial void OnManualUuidTextChanged(string value)
    {
        IsManualUuidInvalid = false;
        OnPropertyChanged(nameof(CanApplyManualUuid));
        ApplyManualUuidCommand.NotifyCanExecuteChanged();
    }

    private async Task SelectOfflineUuidModeAsync(LauncherAccount account, OfflineUuidModeOption option)
    {
        IsSavingUuid = true;
        try
        {
            var existingUuid = account.OfflineUuidGenerationMode == option.Mode
                ? account.Uuid
                : null;
            var uuid = offlineUuidService.CreateUuid(account.DisplayName, option.Mode, existingUuid);
            var updatedAccount = AccountMapper.WithOfflineUuid(account, option.Mode, uuid);

            await accountList.ReplaceSelectedAccountAndPersistAsync(account, updatedAccount);
            logger.LogInformation("Offline UUID mode changed. AccountId={AccountId} Mode={Mode}", account.Id, option.Mode);
            statusService.Report(string.Format(Strings.Status_OfflineUuidModeChangedFormat, option.Title));
        }
        catch (Exception exception)
        {
            logger.LogWarning("Offline UUID mode change failed. AccountId={AccountId} Mode={Mode} ErrorType={ErrorType}",
                account.Id, option.Mode, exception.GetType().FullName);
            RefreshSelection();
            statusService.Report(Strings.Status_OfflineUuidModeChangeFailed);
        }
        finally { IsSavingUuid = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApplyManualUuid))]
    private async Task ApplyManualUuidAsync()
    {
        if (!CanApplyManualUuid)
            return;

        var account = accountList.SelectedAccount;
        if (account is null || !account.IsOffline)
            return;

        if (!offlineUuidService.TryNormalizeUuid(ManualUuidText, out var uuid))
        {
            IsManualUuidInvalid = true;
            statusService.Report(Strings.Status_OfflineUuidInvalid);
            return;
        }

        IsSavingUuid = true;
        try
        {
            var updatedAccount = AccountMapper.WithOfflineUuid(
                account,
                OfflineUuidGenerationMode.Manual,
                uuid);

            await accountList.ReplaceSelectedAccountAndPersistAsync(account, updatedAccount);
            if (ReferenceEquals(accountList.SelectedAccount, updatedAccount))
                ManualUuidText = uuid;
            logger.LogInformation("Manual offline UUID applied. AccountId={AccountId}", account.Id);
            statusService.Report(Strings.Status_OfflineUuidApplied);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Manual offline UUID apply failed. AccountId={AccountId} ErrorType={ErrorType}",
                account.Id, exception.GetType().FullName);
            RefreshSelection();
            statusService.Report(Strings.Status_OfflineUuidModeChangeFailed);
        }
        finally { IsSavingUuid = false; }
    }

    [RelayCommand]
    private async Task CopySelectedUuidAsync(CancellationToken cancellationToken)
    {
        var uuid = accountList.SelectedAccount?.Uuid;
        if (!string.IsNullOrWhiteSpace(uuid))
            await clipboardService.CopyTextAsync(uuid, cancellationToken);
    }

    private void RefreshSelection()
    {
        pendingAccount = null;
        pendingOfflineUuidOption = null;
        IsOfflineUuidModeChangeDialogOpen = false;
        isRefreshingSelection = true;
        try
        {
            var account = accountList.SelectedAccount;
            // Previously generated random UUIDs are fixed values; expose them as custom without regenerating them.
            var displayedMode = account?.OfflineUuidGenerationMode == OfflineUuidGenerationMode.Random
                ? OfflineUuidGenerationMode.Manual
                : account?.OfflineUuidGenerationMode;
            acceptedOfflineUuidOption = account is { IsOffline: true }
                ? OfflineUuidOptions.FirstOrDefault(option => option.Mode == displayedMode)
                : null;
            SelectedOfflineUuidOption = acceptedOfflineUuidOption;
            ManualUuidText = account?.Uuid ?? string.Empty;
            IsManualUuidInvalid = false;
        }
        finally
        {
            isRefreshingSelection = false;
        }

        OnPropertyChanged(nameof(HasSelectedOfflineAccount));
        OnPropertyChanged(nameof(CanChangeOfflineUuidMode));
        OnPropertyChanged(nameof(HasManualUuidEditor));
        OnPropertyChanged(nameof(CanApplyManualUuid));
        OnPropertyChanged(nameof(SelectedAccountUuidText));
        ApplyManualUuidCommand.NotifyCanExecuteChanged();
    }

    private void AccountList_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountListViewModel.SelectedAccount))
            RefreshSelection();
    }
}

