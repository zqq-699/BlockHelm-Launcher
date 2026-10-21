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

using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Launcher.Infrastructure.Accounts.Credentials;
using Launcher.Application.Accounts;
using Launcher.Infrastructure;
using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;
using XboxAuthNet.Game.SessionStorages;
using XboxAuthNet.OAuth;

namespace Launcher.Infrastructure.Accounts;

internal sealed class MicrosoftAuthProvider
{
    private const string MsalHomeAccountIdSessionKey = "BlockHelmMsalHomeAccountId";
    private const string MsalLoginHintSessionKey = "MicrosoftOAuthLoginHint";
    private static readonly TimeSpan MsalRollbackTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultInteractiveAuthenticationTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly HttpClient EntitlementHttpClient = LauncherHttpUserAgent.Apply(new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    });

    private readonly DpapiMicrosoftJsonStorage credentialStorage;
    private readonly MicrosoftClientIdProvider clientIdProvider;
    private readonly IMicrosoftLoginBrowserPageProvider? browserPageProvider;
    private readonly IMinecraftJavaEntitlementVerifier entitlementVerifier;
    private readonly ILogger<MicrosoftAuthProvider> logger;
    private readonly TimeSpan interactiveAuthenticationTimeout;
    private readonly SemaphoreSlim loginHandlerGate = new(1, 1);
    private readonly Lazy<Task<IPublicClientApplication>> msalApplication;
    private JsonXboxGameAccountManager accountManager;
    private JELoginHandler? loginHandler;

    public MicrosoftAuthProvider(LauncherPathProvider pathProvider)
        : this(pathProvider, new MicrosoftClientIdProvider())
    {
    }

    internal MicrosoftAuthProvider(
        LauncherPathProvider pathProvider,
        MicrosoftClientIdProvider clientIdProvider,
        IMicrosoftLoginBrowserPageProvider? browserPageProvider = null,
        IMinecraftJavaEntitlementVerifier? entitlementVerifier = null,
        ILogger<MicrosoftAuthProvider>? logger = null,
        TimeSpan? interactiveAuthenticationTimeout = null)
    {
        credentialStorage = new DpapiMicrosoftJsonStorage(pathProvider);
        this.clientIdProvider = clientIdProvider;
        this.browserPageProvider = browserPageProvider;
        this.entitlementVerifier = entitlementVerifier
            ?? new MinecraftJavaEntitlementVerifier(EntitlementHttpClient);
        this.logger = logger ?? NullLogger<MicrosoftAuthProvider>.Instance;
        this.interactiveAuthenticationTimeout =
            interactiveAuthenticationTimeout ?? DefaultInteractiveAuthenticationTimeout;
        if (this.interactiveAuthenticationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interactiveAuthenticationTimeout));
        accountManager = CreatePersistentAccountManager();
        msalApplication = new Lazy<Task<IPublicClientApplication>>(
            async () =>
            {
                var clientId = clientIdProvider.GetRequiredClientId();
                MicrosoftCredentialSessionMigration.EnsureClientIdentity(
                    credentialStorage,
                    pathProvider,
                    clientId);
                accountManager = CreatePersistentAccountManager();
                loginHandler = null;
                return await MsalClientHelper.BuildApplicationWithCache(clientId).ConfigureAwait(false);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IEnumerable<JEGameAccount> GetSavedAccounts()
    {
        return accountManager.GetAccounts().OfType<JEGameAccount>().ToArray();
    }

    public async Task<MicrosoftLoginResult> LoginInteractivelyAsync(CancellationToken cancellationToken)
    {
        InteractiveAuthentication? authentication = null;
        var committed = false;
        try
        {
            authentication = await AuthenticateInMemoryAsync(cancellationToken);
            var refreshedAccount = JEGameAccount.FromSessionStorage(
                authentication.Account.SessionStorage);
            var profile = refreshedAccount.Profile;
            var accessToken = refreshedAccount.Token?.AccessToken;
            await entitlementVerifier.EnsureOwnedAsync(
                accessToken ?? string.Empty,
                cancellationToken);
            await CaptureMsalAccountIdentityAsync(authentication, cancellationToken);
            CommitSession(authentication.Account.SessionStorage);
            committed = true;
            return new MicrosoftLoginResult(
                profile,
                authentication.Session.Username,
                authentication.Session.UUID,
                accessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MicrosoftAccountAuthenticationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw TranslateAuthenticationException(exception);
        }
        finally
        {
            if (!committed && authentication is not null)
                await TryRemoveNewMsalAccountsAsync(authentication);
        }
    }

    public async Task<MicrosoftLoginResult> ReauthenticateInteractivelyAsync(
        LauncherAccount existingAccount,
        CancellationToken cancellationToken)
    {
        InteractiveAuthentication? authentication = null;
        var committed = false;
        try
        {
            authentication = await AuthenticateInMemoryAsync(cancellationToken);
            var refreshedAccount = JEGameAccount.FromSessionStorage(
                authentication.Account.SessionStorage);
            var accessToken = refreshedAccount.Token?.AccessToken;
            await entitlementVerifier.EnsureOwnedAsync(
                accessToken ?? string.Empty,
                cancellationToken);
            var refreshedUuid = MinecraftAccountHelpers.NormalizeUuid(
                refreshedAccount.Profile?.UUID ?? authentication.Session.UUID);
            if (string.IsNullOrWhiteSpace(existingAccount.Uuid)
                || !string.Equals(refreshedUuid, existingAccount.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.ReauthenticationRequired,
                    "The signed-in Microsoft account does not match the selected launcher account.");
            }

            await CaptureMsalAccountIdentityAsync(authentication, cancellationToken);
            CommitSession(authentication.Account.SessionStorage);
            committed = true;
            return new MicrosoftLoginResult(
                refreshedAccount.Profile,
                authentication.Session.Username,
                authentication.Session.UUID,
                accessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MicrosoftAccountAuthenticationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw TranslateAuthenticationException(exception);
        }
        finally
        {
            if (!committed && authentication is not null)
                await TryRemoveNewMsalAccountsAsync(authentication);
        }
    }

    public async Task<bool> DeleteAccountAsync(LauncherAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.Uuid))
            return false;

        var handler = await GetPersistentLoginHandlerAsync(cancellationToken);
        foreach (var savedAccount in GetSavedAccounts())
        {
            var savedUuid = MinecraftAccountHelpers.NormalizeUuid(savedAccount.Profile?.UUID);
            if (!string.Equals(savedUuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
                continue;

            var homeAccountId = TryGetSessionString(
                savedAccount.SessionStorage,
                MsalHomeAccountIdSessionKey);
            var loginHint = TryGetSessionString(
                savedAccount.SessionStorage,
                MsalLoginHintSessionKey);
            var app = await msalApplication.Value.WaitAsync(cancellationToken);
            await RemoveMatchingMsalAccountsAsync(
                app,
                homeAccountId,
                loginHint,
                cancellationToken);
            await handler.Signout(savedAccount, cancellationToken);
            handler.AccountManager.SaveAccounts();
            return true;
        }

        return false;
    }

    public async Task<string> GetAccessTokenAsync(LauncherAccount account, CancellationToken cancellationToken)
    {
        if (!account.IsMicrosoft || string.IsNullOrWhiteSpace(account.Uuid))
            throw new InvalidOperationException("\u53ea\u6709\u6b63\u7248\u8d26\u6237\u652f\u6301\u6b64\u64cd\u4f5c");

        try
        {
            var handler = await GetPersistentLoginHandlerAsync(cancellationToken);
            var savedAccount = FindSavedAccount(account);
            if (savedAccount is null)
            {
                throw new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.ReauthenticationRequired,
                    "Microsoft account credentials are missing.");
            }

            await handler.AuthenticateSilently(savedAccount, cancellationToken);

            var refreshedAccount = JEGameAccount.FromSessionStorage(savedAccount.SessionStorage);
            var accessToken = refreshedAccount.Token?.AccessToken ?? savedAccount.Token?.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.ReauthenticationRequired,
                    "Microsoft account access token is missing.");
            }

            await entitlementVerifier.EnsureOwnedAsync(accessToken, cancellationToken);
            handler.AccountManager.SaveAccounts();
            return accessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MicrosoftAccountAuthenticationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw TranslateAuthenticationException(exception);
        }
    }

    public void UpdateSavedProfile(LauncherAccount account, string displayName, string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(displayName))
            return;

        var savedAccount = FindSavedAccount(account);
        if (savedAccount?.Profile is null)
            return;

        savedAccount.Profile.Username = displayName;
        savedAccount.Profile.UUID = uuid;
        accountManager.SaveAccounts();
    }

    private JEGameAccount? FindSavedAccount(LauncherAccount account)
    {
        var targetUuid = MinecraftAccountHelpers.NormalizeUuid(account.Uuid);
        return GetSavedAccounts()
            .FirstOrDefault(savedAccount =>
            {
                var savedUuid = MinecraftAccountHelpers.NormalizeUuid(savedAccount.Profile?.UUID);
                if (string.IsNullOrWhiteSpace(savedUuid))
                {
                    savedUuid = MinecraftAccountHelpers.NormalizeUuid(
                        JEGameAccount.FromSessionStorage(savedAccount.SessionStorage).Profile?.UUID);
                }

                return string.Equals(savedUuid, targetUuid, StringComparison.OrdinalIgnoreCase);
            });
    }

    private JsonXboxGameAccountManager CreatePersistentAccountManager()
    {
        return new JsonXboxGameAccountManager(
            credentialStorage,
            JEGameAccount.FromSessionStorage,
            JsonXboxGameAccountManager.DefaultSerializerOption);
    }

    private async Task<JELoginHandler> GetPersistentLoginHandlerAsync(CancellationToken cancellationToken)
    {
        if (loginHandler is not null)
            return loginHandler;

        await loginHandlerGate.WaitAsync(cancellationToken);
        try
        {
            if (loginHandler is not null)
                return loginHandler;

            var app = await msalApplication.Value.WaitAsync(cancellationToken);
            loginHandler = CreateLoginHandler(accountManager, app, browserPageProvider);
            return loginHandler;
        }
        finally
        {
            loginHandlerGate.Release();
        }
    }

    private async Task<InteractiveAuthentication> AuthenticateInMemoryAsync(
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(interactiveAuthenticationTimeout);
        var authenticationToken = timeoutSource.Token;
        IPublicClientApplication? app = null;
        IReadOnlySet<string> initialAccountKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            app = await msalApplication.Value.WaitAsync(authenticationToken);
            initialAccountKeys = (await app.GetAccountsAsync().WaitAsync(authenticationToken))
                .Select(GetMsalAccountKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
            var accountManager = new InMemoryXboxGameAccountManager(JEGameAccount.FromSessionStorage);
            var handler = CreateLoginHandler(accountManager, app, browserPageProvider);
            var account = (JEGameAccount)accountManager.NewAccount();
            var session = await handler.AuthenticateInteractively(account, authenticationToken);
            return new InteractiveAuthentication(account, session, app, initialAccountKeys);
        }
        catch (Exception exception)
        {
            if (app is not null)
                await TryRemoveNewMsalAccountsAsync(app, initialAccountKeys);
            if (exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested
                && timeoutSource.IsCancellationRequested)
            {
                throw new MicrosoftInteractiveAuthenticationTimeoutException(
                    "Interactive Microsoft authentication timed out.",
                    exception);
            }

            throw;
        }
    }

    private async Task CaptureMsalAccountIdentityAsync(
        InteractiveAuthentication authentication,
        CancellationToken cancellationToken)
    {
        var accounts = (await authentication.MsalApplication
                .GetAccountsAsync()
                .WaitAsync(cancellationToken))
            .ToArray();
        var loginHint = TryGetSessionString(
            authentication.Account.SessionStorage,
            MsalLoginHintSessionKey);
        var matchingAccount = accounts.FirstOrDefault(account =>
                !string.IsNullOrWhiteSpace(loginHint)
                && string.Equals(account.Username, loginHint, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault(account =>
                !authentication.InitialMsalAccountKeys.Contains(GetMsalAccountKey(account)));
        var homeAccountId = matchingAccount?.HomeAccountId?.Identifier;
        if (!string.IsNullOrWhiteSpace(homeAccountId))
            authentication.Account.SessionStorage.Set(MsalHomeAccountIdSessionKey, homeAccountId);
    }

    private async Task RemoveMatchingMsalAccountsAsync(
        IPublicClientApplication app,
        string? homeAccountId,
        string? loginHint,
        CancellationToken cancellationToken)
    {
        var accounts = (await app.GetAccountsAsync().WaitAsync(cancellationToken))
            .Where(account => IsMatchingMsalAccount(
                account.HomeAccountId?.Identifier,
                account.Username,
                homeAccountId,
                loginHint))
            .ToArray();

        foreach (var account in accounts)
            await app.RemoveAsync(account).WaitAsync(cancellationToken);

        if (accounts.Length == 0
            && (!string.IsNullOrWhiteSpace(homeAccountId)
                || !string.IsNullOrWhiteSpace(loginHint)))
        {
            logger.LogDebug("No matching MSAL token-cache account remained during account deletion.");
        }
    }

    private Task TryRemoveNewMsalAccountsAsync(InteractiveAuthentication authentication)
    {
        return TryRemoveNewMsalAccountsAsync(
            authentication.MsalApplication,
            authentication.InitialMsalAccountKeys);
    }

    private async Task TryRemoveNewMsalAccountsAsync(
        IPublicClientApplication app,
        IReadOnlySet<string> initialAccountKeys)
    {
        try
        {
            using var rollbackTimeout = new CancellationTokenSource(MsalRollbackTimeout);
            var accounts = await app.GetAccountsAsync().WaitAsync(rollbackTimeout.Token);
            foreach (var account in accounts)
            {
                var key = GetMsalAccountKey(account);
                if (!string.IsNullOrWhiteSpace(key) && !initialAccountKeys.Contains(key))
                    await app.RemoveAsync(account).WaitAsync(rollbackTimeout.Token);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not roll back the MSAL token-cache account created by a failed interactive login.");
        }
    }

    private static string GetMsalAccountKey(IAccount account)
    {
        return account.HomeAccountId?.Identifier
            ?? account.Username
            ?? string.Empty;
    }

    internal static bool IsMatchingMsalAccount(
        string? candidateHomeAccountId,
        string? candidateUsername,
        string? storedHomeAccountId,
        string? storedLoginHint)
    {
        if (!string.IsNullOrWhiteSpace(storedHomeAccountId))
        {
            return string.Equals(
                candidateHomeAccountId,
                storedHomeAccountId,
                StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(storedLoginHint)
            && string.Equals(
                candidateUsername,
                storedLoginHint,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetSessionString(ISessionStorage storage, string key)
    {
        try
        {
            return storage.Get<string>(key);
        }
        catch
        {
            return null;
        }
    }

    private void CommitSession(ISessionStorage source)
    {
        try
        {
            var sessionStorage = JsonSessionStorage.CreateEmpty(
                JsonXboxGameAccountManager.DefaultSerializerOption);
            foreach (var key in source.Keys.ToArray())
            {
                var value = source.Get<object>(key);
                sessionStorage.Set(key, value);
                sessionStorage.SetKeyMode(key, source.GetKeyMode(key));
            }

            var account = JEGameAccount.FromSessionStorage(sessionStorage);
            if (string.IsNullOrWhiteSpace(account.Identifier))
                throw new InvalidDataException("Microsoft account identifier is missing.");

            var root = credentialStorage.ReadAsJsonNode() as JsonObject ?? new JsonObject();
            root[account.Identifier] = sessionStorage.ToJsonObjectForStoring();
            credentialStorage.Write(root, JsonXboxGameAccountManager.DefaultSerializerOption);
            accountManager = CreatePersistentAccountManager();
            loginHandler = null;
        }
        catch (MicrosoftCredentialStorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MicrosoftCredentialStorageException(
                "Microsoft account credentials could not be saved.",
                exception);
        }
    }

    private static bool RequiresInteractiveLogin(MicrosoftOAuthException exception)
    {
        return exception.StatusCode is 0 or (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Unauthorized
            || string.Equals(exception.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.Error, "interaction_required", StringComparison.OrdinalIgnoreCase)
            || string.Equals(exception.Error, "login_required", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Interactive Microsoft authentication is required", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresInteractiveLogin(MsalException exception)
    {
        return exception is MsalUiRequiredException
            || ContainsInteractiveLoginRequirement(exception.ErrorCode)
            || ContainsInteractiveLoginRequirement(exception.Message);
    }

    private static bool ContainsInteractiveLoginRequirement(string? value)
    {
        return value?.Contains("loginHint was empty", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("MsalInteractiveOAuth", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("Interactive Microsoft OAuth", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int ResolveStatusCode(JEAuthException exception)
    {
        if (exception.StatusCode != 0)
            return exception.StatusCode;

        // 响应体不是预期的 JSON 错误结构时，CmlLib 降级为 new JEAuthException($"{statusCode}: {reasonPhrase}")，
        // 该构造函数不会填充 StatusCode，因此真实状态码只能从消息前缀还原。
        var message = exception.Message;
        if (message.Length >= 3
            && (message.Length == 3 || message[3] == ':')
            && int.TryParse(message.AsSpan(0, 3), out var parsed)
            && parsed is >= 100 and <= 599)
            return parsed;

        return 0;
    }

    internal static JELoginHandler CreateLoginHandler(
        IXboxGameAccountManager accountManager,
        IPublicClientApplication msalApplication,
        IMicrosoftLoginBrowserPageProvider? browserPageProvider = null)
    {
        ArgumentNullException.ThrowIfNull(accountManager);
        ArgumentNullException.ThrowIfNull(msalApplication);

        XboxAuthNet.Game.IAuthenticationProvider oauthProvider = browserPageProvider is null
            ? new MsalCodeFlowProvider(msalApplication)
            : new BrowserCompletionMsalCodeFlowProvider(msalApplication, browserPageProvider);

        return new JELoginHandlerBuilder()
            .WithAccountManager(accountManager)
            .WithOAuthProvider(oauthProvider)
            .Build();
    }

    internal static MicrosoftAccountAuthenticationException TranslateAuthenticationException(Exception exception)
    {
        return exception switch
        {
            MicrosoftAuthenticationConfigurationException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.AuthenticationNotConfigured,
                "Microsoft account login is not configured.",
                exception),
            MicrosoftCredentialStorageException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.CredentialStorageFailed,
                "Microsoft account credentials could not be accessed.",
                exception),
            MicrosoftInteractiveAuthenticationTimeoutException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.AuthenticationTimedOut,
                "Interactive Microsoft authentication timed out.",
                exception),
            MsalUiRequiredException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.ReauthenticationRequired,
                "Interactive Microsoft authentication is required.",
                exception),
            MsalException msalException when RequiresInteractiveLogin(msalException)
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.ReauthenticationRequired,
                    "Interactive Microsoft authentication is required.",
                    exception),
            MsalServiceException msalException when msalException.StatusCode >= 500
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.AuthenticationServerUnavailable,
                    "Microsoft authentication services are unavailable.",
                    exception),
            MsalException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "Microsoft authentication failed.",
                exception),
            MicrosoftOAuthException oauthException when RequiresInteractiveLogin(oauthException)
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.ReauthenticationRequired,
                    "Microsoft account credentials have expired.",
                    exception),
            MicrosoftOAuthException oauthException when oauthException.StatusCode >= 500
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.AuthenticationServerUnavailable,
                    "Microsoft authentication services are unavailable.",
                    exception),
            MicrosoftOAuthException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "Microsoft authentication failed.",
                exception),
            JEAuthException jeException when ResolveStatusCode(jeException) == (int)HttpStatusCode.Forbidden
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.AuthenticationApplicationNotAuthorized,
                    "The Microsoft application is not authorized by Minecraft services.",
                    exception),
            JEAuthException jeException when ResolveStatusCode(jeException) == (int)HttpStatusCode.Unauthorized
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.ReauthenticationRequired,
                    "Microsoft account credentials were rejected.",
                    exception),
            JEAuthException jeException when ResolveStatusCode(jeException) >= 500
                => new MicrosoftAccountAuthenticationException(
                    LaunchAccountSessionFailureReason.AuthenticationServerUnavailable,
                    "Minecraft authentication services are unavailable.",
                    exception),
            JEAuthException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "Minecraft authentication failed.",
                exception),
            HttpRequestException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.AuthenticationServerUnavailable,
                "Microsoft authentication services are unavailable.",
                exception),
            JsonException => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "Microsoft authentication returned an invalid response.",
                exception),
            _ => new MicrosoftAccountAuthenticationException(
                LaunchAccountSessionFailureReason.Unknown,
                "Microsoft authentication failed.",
                exception)
        };
    }

    private sealed record InteractiveAuthentication(
        JEGameAccount Account,
        CmlLib.Core.Auth.MSession Session,
        IPublicClientApplication MsalApplication,
        IReadOnlySet<string> InitialMsalAccountKeys);
}
