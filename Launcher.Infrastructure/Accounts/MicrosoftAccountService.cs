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

using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Accounts.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http;

namespace Launcher.Infrastructure.Accounts;

/// <summary>
/// 封装 Microsoft 登录缓存与 Minecraft Profile API，并在边界处映射为启动器账户模型。
/// </summary>
public sealed class MicrosoftAccountService : IMicrosoftAccountService
{
    // 认证 token 只交给底层客户端和缓存，不写入普通日志或暴露给 ViewModel。
    // 头像、皮肤、披风和资料请求都很小，超时必须有界；默认的 100 秒会让直连不通的网络长时间挂起。
    private static readonly HttpClient HttpClient = LauncherHttpUserAgent.Apply(new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    });

    private readonly MicrosoftAuthProvider authProvider;
    private readonly AccountAvatarService avatarService;
    private readonly AccountSkinCacheService skinCacheService;
    private readonly MinecraftProfileClient profileClient;
    private readonly MicrosoftAccountFactory accountFactory;
    private readonly MinecraftSkinService skinService;
    private readonly MinecraftCapeService capeService;
    private readonly AccountCapeCacheService capeCacheService;
    private readonly ILogger<MicrosoftAccountService> logger;

    public MicrosoftAccountService(ILogger<MicrosoftAccountService>? logger = null)
        : this(
            new MicrosoftAuthProvider(new LauncherPathProvider()),
            new LauncherPathProvider(),
            new AccountSkinLibraryService(),
            logger)
    {
    }

    internal MicrosoftAccountService(
        MicrosoftAuthProvider authProvider,
        LauncherPathProvider pathProvider,
        IAccountSkinLibraryService skinLibraryService,
        ILogger<MicrosoftAccountService>? logger = null)
    {
        this.logger = logger ?? NullLogger<MicrosoftAccountService>.Instance;
        this.authProvider = authProvider;
        avatarService = new AccountAvatarService(HttpClient, pathProvider);
        skinCacheService = new AccountSkinCacheService(HttpClient, pathProvider);
        capeCacheService = new AccountCapeCacheService(HttpClient, pathProvider);
        profileClient = new MinecraftProfileClient(HttpClient);
        accountFactory = new MicrosoftAccountFactory(
            avatarService,
            skinCacheService,
            skinLibraryService,
            this.logger);
        skinService = new MinecraftSkinService(authProvider, profileClient, accountFactory, skinCacheService);
        capeService = new MinecraftCapeService(authProvider, profileClient, capeCacheService);
    }

    public async Task<IReadOnlyList<LauncherAccount>> GetSavedAccountsAsync(CancellationToken cancellationToken = default)
    {
        // 缓存枚举全程只读本地：凭据、资料和头像都来自磁盘，单个失效账户不会阻止其余可用账户进入列表。
        var accounts = new List<LauncherAccount>();
        IReadOnlyList<CmlLib.Core.Auth.Microsoft.Sessions.JEGameAccount> savedAccounts;
        try
        {
            savedAccounts = authProvider.GetSavedAccounts().ToArray();
        }
        catch (MicrosoftCredentialStorageException exception)
        {
            logger.LogError(exception, "Encrypted Microsoft account credentials could not be loaded.");
            return accounts;
        }

        foreach (var savedAccount in savedAccounts)
        {
            var profile = savedAccount.Profile;
            if (profile is null
                || string.IsNullOrWhiteSpace(profile.Username)
                || string.IsNullOrWhiteSpace(profile.UUID))
            {
                continue;
            }

            var cachedAccount = await accountFactory.CreateCachedAccountFromProfileAsync(
                profile,
                cancellationToken);
            accounts.Add(cachedAccount);
        }

        logger.LogDebug("Saved Microsoft accounts loaded. Count={AccountCount}", accounts.Count);
        return accounts;
    }

    public async Task<LauncherAccount> LoginInteractivelyAsync(CancellationToken cancellationToken = default)
    {
        // 浏览器登录成功后仍需获取 Minecraft Profile；没有游戏资料的身份不能作为可启动账户。
        try
        {
            logger.LogInformation("Interactive Microsoft account login started.");
            var login = await authProvider.LoginInteractivelyAsync(cancellationToken);
            var currentProfile = await TryGetCurrentProfileAsync(login, cancellationToken);
            if (currentProfile is not null)
            {
                var account = await accountFactory.CreateAccountFromProfileAsync(
                    currentProfile,
                    forceRefreshAvatar: true,
                    cancellationToken);
                authProvider.UpdateSavedProfile(
                    account,
                    account.DisplayName,
                    account.Uuid ?? string.Empty);
                logger.LogInformation("Interactive Microsoft account login completed with current profile. AccountId={AccountId}", account.Id);
                return account;
            }

            if (login.Profile is not null
                && !string.IsNullOrWhiteSpace(login.Profile.Username)
                && !string.IsNullOrWhiteSpace(login.Profile.UUID))
            {
                var account = await accountFactory.CreateAccountFromProfileAsync(
                    login.Profile,
                    forceRefreshAvatar: true,
                    cancellationToken);
                logger.LogInformation("Interactive Microsoft account login completed with saved profile. AccountId={AccountId}", account.Id);
                return account;
            }

            var uuid = MinecraftAccountHelpers.NormalizeUuid(login.Uuid);
            var fallbackAccount = new LauncherAccount
            {
                Id = $"microsoft-{uuid}",
                DisplayName = login.Username ?? string.Empty,
                Uuid = uuid,
                Kind = LauncherAccountKind.Microsoft
            };
            logger.LogInformation("Interactive Microsoft account login completed with fallback profile. AccountId={AccountId}", fallbackAccount.Id);
            return fallbackAccount;
        }
        catch (MicrosoftAccountAuthenticationException exception)
        {
            var reason = exception.Reason switch
            {
                LaunchAccountSessionFailureReason.AuthenticationNotConfigured
                    => MicrosoftAccountLoginFailureReason.NotConfigured,
                LaunchAccountSessionFailureReason.AuthenticationApplicationNotAuthorized
                    => MicrosoftAccountLoginFailureReason.ApplicationNotAuthorized,
                LaunchAccountSessionFailureReason.AuthenticationTimedOut
                    => MicrosoftAccountLoginFailureReason.TimedOut,
                LaunchAccountSessionFailureReason.GameOwnershipRequired
                    => MicrosoftAccountLoginFailureReason.GameOwnershipRequired,
                LaunchAccountSessionFailureReason.AuthenticationServerUnavailable
                    => MicrosoftAccountLoginFailureReason.AuthenticationServerUnavailable,
                LaunchAccountSessionFailureReason.CredentialStorageFailed
                    => MicrosoftAccountLoginFailureReason.CredentialStorageFailed,
                _ => MicrosoftAccountLoginFailureReason.Unknown
            };
            logger.LogWarning(
                "Interactive Microsoft account login failed. Reason={Reason} ErrorType={ErrorType}",
                reason,
                exception.InnerException?.GetType().FullName ?? exception.GetType().FullName);
            throw new MicrosoftAccountLoginException(
                reason,
                "Microsoft account login failed.",
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Interactive Microsoft account login failed. ErrorType={ErrorType}",
                exception.GetType().FullName);
            throw;
        }
    }

    public async Task<LauncherAccount> ReauthenticateInteractivelyAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        if (!account.IsMicrosoft || string.IsNullOrWhiteSpace(account.Uuid))
        {
            throw new MicrosoftAccountReauthenticationException(
                MicrosoftAccountReauthenticationFailureReason.Unknown,
                "Only an existing Microsoft account can be reauthenticated.");
        }

        try
        {
            logger.LogInformation("Interactive Microsoft account reauthentication started. AccountId={AccountId}", account.Id);
            var login = await authProvider.ReauthenticateInteractivelyAsync(account, cancellationToken);
            var currentProfile = await TryGetCurrentProfileAsync(login, cancellationToken);
            LauncherAccount refreshed;
            if (currentProfile is not null)
            {
                refreshed = await accountFactory.CreateAccountFromProfileAsync(
                    currentProfile,
                    forceRefreshAvatar: true,
                    cancellationToken,
                    account.SkinLibrary);
            }
            else if (login.Profile is not null
                && !string.IsNullOrWhiteSpace(login.Profile.Username)
                && !string.IsNullOrWhiteSpace(login.Profile.UUID))
            {
                refreshed = await accountFactory.CreateAccountFromProfileAsync(
                    login.Profile,
                    forceRefreshAvatar: true,
                    cancellationToken,
                    account.SkinLibrary);
            }
            else
            {
                throw new MicrosoftAccountReauthenticationException(
                    MicrosoftAccountReauthenticationFailureReason.Unknown,
                    "Microsoft reauthentication did not return a Minecraft profile.");
            }

            if (!string.Equals(refreshed.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftAccountReauthenticationException(
                    MicrosoftAccountReauthenticationFailureReason.AccountMismatch,
                    "The signed-in Microsoft account does not match the selected launcher account.");
            }

            authProvider.UpdateSavedProfile(
                refreshed,
                refreshed.DisplayName,
                refreshed.Uuid ?? string.Empty);
            logger.LogInformation("Interactive Microsoft account reauthentication completed. AccountId={AccountId}", account.Id);
            return refreshed;
        }
        catch (MicrosoftAccountAuthenticationException exception)
        {
            var reason = exception.Reason switch
            {
                LaunchAccountSessionFailureReason.AuthenticationNotConfigured
                    => MicrosoftAccountReauthenticationFailureReason.NotConfigured,
                LaunchAccountSessionFailureReason.AuthenticationApplicationNotAuthorized
                    => MicrosoftAccountReauthenticationFailureReason.ApplicationNotAuthorized,
                LaunchAccountSessionFailureReason.AuthenticationTimedOut
                    => MicrosoftAccountReauthenticationFailureReason.TimedOut,
                LaunchAccountSessionFailureReason.GameOwnershipRequired
                    => MicrosoftAccountReauthenticationFailureReason.GameOwnershipRequired,
                LaunchAccountSessionFailureReason.AuthenticationServerUnavailable
                    => MicrosoftAccountReauthenticationFailureReason.AuthenticationServerUnavailable,
                LaunchAccountSessionFailureReason.CredentialStorageFailed
                    => MicrosoftAccountReauthenticationFailureReason.CredentialStorageFailed,
                LaunchAccountSessionFailureReason.ReauthenticationRequired
                    => MicrosoftAccountReauthenticationFailureReason.AccountMismatch,
                _ => MicrosoftAccountReauthenticationFailureReason.Unknown
            };
            logger.LogWarning(
                "Interactive Microsoft account reauthentication failed. AccountId={AccountId} Reason={Reason} ErrorType={ErrorType}",
                account.Id,
                reason,
                exception.InnerException?.GetType().FullName ?? exception.GetType().FullName);
            throw new MicrosoftAccountReauthenticationException(reason, "Microsoft account reauthentication failed.", exception);
        }
        catch (MicrosoftAccountReauthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Interactive Microsoft account reauthentication failed. AccountId={AccountId} ErrorType={ErrorType}",
                account.Id,
                exception.GetType().FullName);
            throw new MicrosoftAccountReauthenticationException(
                MicrosoftAccountReauthenticationFailureReason.Unknown,
                "Microsoft account reauthentication failed.",
                exception);
        }
    }

    public async Task DeleteAccountAsync(LauncherAccount account, CancellationToken cancellationToken = default)
    {
        // 删除只清理对应认证缓存和外观缓存，不影响同 Microsoft 身份之外的离线账户。
        var deleted = await authProvider.DeleteAccountAsync(account, cancellationToken);
        if (deleted && !string.IsNullOrWhiteSpace(account.Uuid))
            avatarService.DeleteAvatar(account.Uuid);
        logger.LogInformation("Microsoft account delete requested. AccountId={AccountId} Deleted={Deleted}", account.Id, deleted);
    }

    public async Task<IReadOnlyList<AccountCapeOption>> GetCapesAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capes = await capeService.GetCapesAsync(account, cancellationToken);
            logger.LogDebug("Microsoft account capes loaded. AccountId={AccountId} CapeCount={CapeCount}", account.Id, capes.Count);
            return capes;
        }
        catch (MicrosoftAccountAuthenticationException ex) when (
            ex.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex)
        {
            logger.LogWarning(ex, "Microsoft account capes load failed. AccountId={AccountId} ErrorCode={ErrorCode}", account.Id, ex.ErrorCode);
            throw new MicrosoftAccountProfileRefreshException(ex.ErrorCode, ex);
        }
    }

    public async Task<LauncherAccount> RefreshAccountProfileAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        // 远端刷新与本地皮肤/披风缓存合并，服务端缺失字段不应抹掉仍可用的本地外观。
        try
        {
            var refreshed = await RefreshSavedAccountAsync(account, cancellationToken);
            logger.LogDebug("Microsoft account profile refreshed. AccountId={AccountId}", refreshed.Id);
            return refreshed;
        }
        catch (MicrosoftAccountAuthenticationException ex) when (
            ex.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex)
        {
            logger.LogWarning(ex, "Microsoft account profile refresh failed. AccountId={AccountId} ErrorCode={ErrorCode}", account.Id, ex.ErrorCode);
            throw new MicrosoftAccountProfileRefreshException(ex.ErrorCode, ex);
        }
    }

    public async Task<LauncherAccount> UploadSkinAsync(
        LauncherAccount account,
        string skinFilePath,
        MinecraftSkinModel skinModel,
        CancellationToken cancellationToken = default)
    {
        // 上传成功后以服务端资料为准，再把实际内容写入本地缓存供离线展示。
        try
        {
            var updated = await skinService.UploadSkinAsync(account, skinFilePath, skinModel, cancellationToken);
            logger.LogInformation("Microsoft account skin uploaded. AccountId={AccountId} SkinModel={SkinModel}", updated.Id, skinModel);
            return updated;
        }
        catch (MicrosoftAccountAuthenticationException ex) when (
            ex.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex)
        {
            logger.LogWarning(ex, "Microsoft account skin upload failed. AccountId={AccountId} SkinModel={SkinModel} ErrorCode={ErrorCode}", account.Id, skinModel, ex.ErrorCode);
            throw new MicrosoftAccountSkinUpdateException(ex.ErrorCode, ex);
        }
    }

    public async Task SetActiveCapeAsync(
        LauncherAccount account,
        string? capeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogDebug("Microsoft account active cape update requested. AccountId={AccountId} HasCape={HasCape}", account.Id, !string.IsNullOrWhiteSpace(capeId));
            await capeService.SetActiveCapeAsync(account, capeId, cancellationToken);
            logger.LogInformation("Microsoft account active cape updated. AccountId={AccountId} HasCape={HasCape}", account.Id, !string.IsNullOrWhiteSpace(capeId));
        }
        catch (MicrosoftAccountAuthenticationException ex) when (
            ex.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex)
        {
            logger.LogWarning(ex, "Microsoft account active cape update failed. AccountId={AccountId} HasCape={HasCape} ErrorCode={ErrorCode}", account.Id, !string.IsNullOrWhiteSpace(capeId), ex.ErrorCode);
            throw new MicrosoftAccountProfileRefreshException(ex.ErrorCode, ex);
        }
    }

    public async Task<LauncherAccount> ChangeNameAsync(
        LauncherAccount account,
        string newName,
        CancellationToken cancellationToken = default)
    {
        // Profile API 错误映射为稳定业务原因，UI 不直接依赖第三方异常文本。
        try
        {
            var accessToken = await authProvider.GetAccessTokenAsync(account, cancellationToken);
            var profile = await profileClient.ChangeNameAsync(accessToken, newName, cancellationToken);
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? newName.Trim() : profile.Name;
            var uuid = MinecraftAccountHelpers.NormalizeUuid(profile.Id);
            authProvider.UpdateSavedProfile(
                account,
                profile.Name,
                uuid);

            var updatedAccount = await accountFactory.CreateAccountFromProfileAsync(
                profile,
                forceRefreshAvatar: true,
                cancellationToken,
                account.SkinLibrary);
            logger.LogInformation("Microsoft account name changed. AccountId={AccountId}", updatedAccount.Id);
            return updatedAccount;
        }
        catch (MicrosoftAccountAuthenticationException ex) when (
            ex.Reason == LaunchAccountSessionFailureReason.ReauthenticationRequired)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CreateSessionExpiredException(ex);
        }
        catch (MinecraftProfileRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Microsoft account name change failed. AccountId={AccountId} Reason={Reason} ErrorCode={ErrorCode}",
                account.Id,
                MapNameChangeFailure(ex.ErrorKind),
                ex.ErrorCode);
            throw new MicrosoftAccountNameChangeException(
                MapNameChangeFailure(ex.ErrorKind),
                ex.ErrorCode,
                ex);
        }
    }

    private static MicrosoftAccountNameChangeFailureReason MapNameChangeFailure(MinecraftProfileErrorKind errorKind)
    {
        return errorKind switch
        {
            MinecraftProfileErrorKind.Duplicate => MicrosoftAccountNameChangeFailureReason.DuplicateName,
            MinecraftProfileErrorKind.NotAllowed => MicrosoftAccountNameChangeFailureReason.NotAllowed,
            MinecraftProfileErrorKind.ConstraintViolation => MicrosoftAccountNameChangeFailureReason.InvalidName,
            _ => MicrosoftAccountNameChangeFailureReason.Unknown
        };
    }

    private static MicrosoftAccountSessionExpiredException CreateSessionExpiredException(Exception exception)
    {
        return new MicrosoftAccountSessionExpiredException(
            "The Microsoft account session has expired and requires interactive reauthentication.",
            exception);
    }

    private async Task<MinecraftProfileResponse?> TryGetCurrentProfileAsync(
        MicrosoftLoginResult login,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login.AccessToken))
            return null;

        try
        {
            return await profileClient.GetProfileAsync(login.AccessToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<LauncherAccount> RefreshSavedAccountAsync(
        LauncherAccount account,
        CancellationToken cancellationToken)
    {
        var accessToken = await authProvider.GetAccessTokenAsync(account, cancellationToken);
        var profile = await profileClient.GetProfileAsync(accessToken, cancellationToken);
        var refreshedAccount = await accountFactory.CreateAccountFromProfileAsync(
            profile,
            forceRefreshAvatar: true,
            cancellationToken,
            account.SkinLibrary);
        authProvider.UpdateSavedProfile(
            refreshedAccount,
            refreshedAccount.DisplayName,
            refreshedAccount.Uuid ?? string.Empty);
        return refreshedAccount;
    }
}
