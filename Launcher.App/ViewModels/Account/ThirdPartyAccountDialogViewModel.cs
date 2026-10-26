/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Application.Accounts;
using Launcher.App.Resources;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Launcher.App.ViewModels.Account;

public sealed partial class ThirdPartyAccountDialogViewModel : ObservableObject
{
    private readonly AccountListViewModel accountList;
    private readonly IThirdPartyAccountService accountService;
    private readonly ILogger logger;

    [ObservableProperty] private string authenticationServer = string.Empty;
    [ObservableProperty] private string usernameOrEmail = string.Empty;
    [ObservableProperty] private bool hasPassword;
    [ObservableProperty] private string authenticationServerError = string.Empty;
    [ObservableProperty] private string usernameError = string.Empty;
    [ObservableProperty] private string passwordError = string.Empty;

    public ThirdPartyAccountDialogViewModel(
        AccountListViewModel accountList,
        IThirdPartyAccountService accountService,
        ILogger logger)
    {
        this.accountList = accountList;
        this.accountService = accountService;
        this.logger = logger;
    }

    public bool CanConfirm => !string.IsNullOrWhiteSpace(AuthenticationServer)
        && !string.IsNullOrWhiteSpace(UsernameOrEmail)
        && HasPassword;

    public bool HasAuthenticationServerError => !string.IsNullOrWhiteSpace(AuthenticationServerError);
    public bool HasUsernameError => !string.IsNullOrWhiteSpace(UsernameError);
    public bool HasPasswordError => !string.IsNullOrWhiteSpace(PasswordError);
    public ObservableCollection<ThirdPartyProfileOptionViewModel> Profiles { get; } = [];
    public string? EmailAttemptId { get; private set; }
    public bool IsEmailIdentifier => UsernameOrEmail.Contains('@', StringComparison.Ordinal);
    public bool HasSelectedProfiles => Profiles.Any(profile => profile.IsSelected);
    public bool CanSelectAllProfiles => Profiles.Count > 0 && Profiles.Any(profile => !profile.IsSelected);

    public void Reset()
    {
        AuthenticationServer = string.Empty;
        ResetCredentials();
    }

    /// <summary>清空身份与密码状态，但保留当前认证服务器地址。</summary>
    public void ResetCredentials()
    {
        UsernameOrEmail = string.Empty;
        HasPassword = false;
        ClearErrors();
        Profiles.Clear();
        EmailAttemptId = null;
    }

    public void PrepareReauthentication(LauncherAccount account)
    {
        AuthenticationServer = account.AuthenticationServerUrl ?? string.Empty;
        UsernameOrEmail = account.ThirdPartyLoginUsername ?? string.Empty;
        HasPassword = false;
        ClearErrors();
    }

    public void UpdatePasswordState(bool hasPassword) => HasPassword = hasPassword;

    public async Task<bool> BeginEmailLoginAsync(string password)
    {
        ClearErrors();
        if (!Validate(password))
            return false;
        try
        {
            var session = await accountService.BeginEmailLoginAsync(
                AuthenticationServer.Trim(),
                UsernameOrEmail.Trim(),
                password);
            EmailAttemptId = session.AttemptId;
            Profiles.Clear();
            foreach (var profile in session.Profiles)
            {
                var item = new ThirdPartyProfileOptionViewModel(profile);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ThirdPartyProfileOptionViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(HasSelectedProfiles));
                        OnPropertyChanged(nameof(CanSelectAllProfiles));
                    }
                };
                Profiles.Add(item);
            }
            OnPropertyChanged(nameof(HasSelectedProfiles));
            OnPropertyChanged(nameof(CanSelectAllProfiles));
            return true;
        }
        catch (ThirdPartyAccountLoginException exception)
        {
            ApplyLoginError(exception.Reason);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Third-party email login failed. ErrorType={ErrorType}", exception.GetType().FullName);
            AuthenticationServerError = Strings.Account_ThirdPartyServerInvalidResponse;
            return false;
        }
    }

    public void SelectAllProfiles()
    {
        foreach (var profile in Profiles)
            profile.IsSelected = true;
    }

    public async Task<LauncherAccount?> ImportEmailProfileAsync(
        ThirdPartyProfileOptionViewModel profile,
        string password,
        CancellationToken cancellationToken)
    {
        if (EmailAttemptId is null)
            return null;
        LauncherAccount? importedAccount = null;
        var isNewAccount = false;
        try
        {
            importedAccount = await accountService.ImportEmailProfileAsync(
                EmailAttemptId,
                profile.Uuid,
                password,
                cancellationToken);
            var existing = accountList.Accounts.FirstOrDefault(item =>
                string.Equals(item.Id, importedAccount.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                isNewAccount = true;
                await accountList.AddAndSelectAsync(importedAccount);
            }
            else
            {
                await accountList.ReplaceSelectedAccountAndPersistAsync(existing.Account, importedAccount);
            }
            return importedAccount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Third-party email profile import failed. ProfileUuid={ProfileUuid} ErrorType={ErrorType}",
                profile.Uuid, exception.GetType().FullName);
            if (isNewAccount && importedAccount is not null)
                await TryRollbackCredentialsAsync(importedAccount.Id);
            return null;
        }
    }

    public async Task CancelEmailLoginAsync()
    {
        var attemptId = EmailAttemptId;
        EmailAttemptId = null;
        if (attemptId is null)
            return;
        try
        {
            await accountService.CancelEmailLoginAsync(attemptId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug("Unable to clean up third-party email login attempt. ErrorType={ErrorType}",
                exception.GetType().FullName);
        }
    }

    public async Task<bool> LoginAsync(string password)
    {
        ClearErrors();
        if (!Validate(password))
            return false;

        LauncherAccount? authenticatedAccount = null;
        var isNewAccount = false;
        try
        {
            authenticatedAccount = await accountService.LoginWithUsernameAsync(
                AuthenticationServer.Trim(),
                UsernameOrEmail.Trim(),
                password);
            var existing = accountList.Accounts.FirstOrDefault(item =>
                string.Equals(item.Id, authenticatedAccount.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                isNewAccount = true;
                await accountList.AddAndSelectAsync(authenticatedAccount);
            }
            else
            {
                accountList.SelectItem(existing, persistSelection: false);
                await accountList.ReplaceSelectedAccountAndPersistAsync(existing.Account, authenticatedAccount);
            }
            return true;
        }
        catch (ThirdPartyAccountLoginException exception)
        {
            ApplyLoginError(exception.Reason);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Third-party account login or persistence failed. ErrorType={ErrorType}",
                exception.GetType().FullName);
            PasswordError = Strings.Account_ThirdPartyLoginFailed;
            if (isNewAccount && authenticatedAccount is not null)
                await TryRollbackCredentialsAsync(authenticatedAccount.Id);
            return false;
        }
    }

    public async Task<LauncherAccount?> ReauthenticateAsync(LauncherAccount account, string password)
    {
        ClearErrors();
        if (string.IsNullOrEmpty(password))
        {
            PasswordError = Strings.Account_ThirdPartyPasswordRequired;
            return null;
        }

        try
        {
            return await accountService.ReauthenticateAsync(account, password);
        }
        catch (ThirdPartyAccountLoginException exception)
        {
            ApplyLoginError(exception.Reason);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Third-party account reauthentication failed. AccountId={AccountId} ErrorType={ErrorType}",
                account.Id, exception.GetType().FullName);
            PasswordError = Strings.Account_ThirdPartyLoginFailed;
            return null;
        }
    }

    partial void OnAuthenticationServerChanged(string value)
    {
        AuthenticationServerError = string.Empty;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnUsernameOrEmailChanged(string value)
    {
        UsernameError = string.Empty;
        PasswordError = string.Empty;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnHasPasswordChanged(bool value)
    {
        PasswordError = string.Empty;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnAuthenticationServerErrorChanged(string value) =>
        OnPropertyChanged(nameof(HasAuthenticationServerError));

    partial void OnUsernameErrorChanged(string value) =>
        OnPropertyChanged(nameof(HasUsernameError));

    partial void OnPasswordErrorChanged(string value) =>
        OnPropertyChanged(nameof(HasPasswordError));

    private bool Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(AuthenticationServer))
            AuthenticationServerError = Strings.Account_ThirdPartyServerRequired;
        if (string.IsNullOrWhiteSpace(UsernameOrEmail))
            UsernameError = Strings.Account_ThirdPartyUsernameRequired;
        if (string.IsNullOrEmpty(password))
            PasswordError = Strings.Account_ThirdPartyPasswordRequired;
        return string.IsNullOrEmpty(AuthenticationServerError)
            && string.IsNullOrEmpty(UsernameError)
            && string.IsNullOrEmpty(PasswordError);
    }

    private void ApplyLoginError(ThirdPartyAccountLoginFailureReason reason)
    {
        switch (reason)
        {
            case ThirdPartyAccountLoginFailureReason.InvalidServerAddress:
                AuthenticationServerError = Strings.Account_ThirdPartyServerInvalid;
                break;
            case ThirdPartyAccountLoginFailureReason.InsecureServerAddress:
                AuthenticationServerError = Strings.Account_ThirdPartyServerHttpsRequired;
                break;
            case ThirdPartyAccountLoginFailureReason.ServerUnavailable:
                AuthenticationServerError = Strings.Account_ThirdPartyServerUnavailable;
                break;
            case ThirdPartyAccountLoginFailureReason.UsernameLoginUnsupported:
                UsernameError = Strings.Account_ThirdPartyUsernameUnsupported;
                break;
            case ThirdPartyAccountLoginFailureReason.ProfileMissing:
            case ThirdPartyAccountLoginFailureReason.AccountMismatch:
                UsernameError = Strings.Account_ThirdPartyProfileMissing;
                break;
            case ThirdPartyAccountLoginFailureReason.InvalidCredentials:
                PasswordError = Strings.Account_ThirdPartyInvalidCredentials;
                break;
            case ThirdPartyAccountLoginFailureReason.CredentialStorageFailed:
                PasswordError = Strings.Account_ThirdPartyCredentialStorageFailed;
                break;
            default:
                AuthenticationServerError = Strings.Account_ThirdPartyServerInvalidResponse;
                break;
        }
    }

    private void ClearErrors()
    {
        AuthenticationServerError = string.Empty;
        UsernameError = string.Empty;
        PasswordError = string.Empty;
    }

    private async Task TryRollbackCredentialsAsync(string accountId)
    {
        try
        {
            await accountService.DeleteCredentialsAsync(accountId);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Third-party credential rollback failed. AccountId={AccountId} ErrorType={ErrorType}",
                accountId, exception.GetType().FullName);
        }
    }
}
