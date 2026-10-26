/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Collections.Concurrent;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts.ThirdParty;

internal sealed partial class ThirdPartyAccountService
{
    public async Task<ThirdPartyEmailLoginSession> BeginEmailLoginAsync(
        string authenticationServer,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        RemoveExpiredEmailLogins();
        var initialUri = ParseServerUri(authenticationServer);
        try
        {
            var (apiRoot, metadataDocument) = await ResolveApiRootAsync(initialUri, cancellationToken).ConfigureAwait(false);
            using var metadata = metadataDocument;
            var platformName = GetPlatformName(metadata);
            var authentication = await AuthenticateEmailAsync(apiRoot, email, password, cancellationToken).ConfigureAwait(false);
            if (authentication.Profiles.Count == 0)
                throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.ProfileMissing, "The account has no profiles.");

            var normalizedApiRoot = EnsureTrailingSlash(apiRoot).AbsoluteUri;
            var snapshots = new ConcurrentDictionary<string, ThirdPartyAccountProfileSnapshot>(StringComparer.OrdinalIgnoreCase);
            using var concurrency = new SemaphoreSlim(4, 4);
            var options = await Task.WhenAll(authentication.Profiles.Select(async profile =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var accountId = CreateAccountId(normalizedApiRoot, profile.Uuid);
                    var snapshot = ThirdPartyAccountProfileSnapshot.Unavailable;
                    if (appearanceService is not null)
                    {
                        try
                        {
                            snapshot = await appearanceService
                                .GetProfileAsync(apiRoot, profile.Uuid, accountId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            logger.LogDebug(
                                "Could not load a third-party profile avatar during email login. AuthenticationServerHost={AuthenticationServerHost} ErrorType={ErrorType}",
                                apiRoot.Host,
                                exception.GetType().FullName);
                        }
                    }
                    snapshots[profile.Uuid] = snapshot;
                    return new ThirdPartyProfileOption(
                        profile.Uuid,
                        snapshot.IsAvailable ? snapshot.ProfileName! : profile.Name,
                        snapshot.AvatarSource ?? LauncherAccount.DefaultSteveAvatarUrl);
                }
                finally
                {
                    concurrency.Release();
                }
            })).ConfigureAwait(false);

            var attemptId = Guid.NewGuid().ToString("N");
            pendingEmailLogins[attemptId] = new PendingEmailLogin(
                attemptId,
                apiRoot,
                normalizedApiRoot,
                platformName,
                email.Trim(),
                authentication,
                options,
                snapshots,
                DateTimeOffset.UtcNow.Add(EmailLoginLifetime));
            logger.LogInformation(
                "Third-party email login prepared profile selection. AuthenticationServerHost={AuthenticationServerHost} ProfileCount={ProfileCount}",
                apiRoot.Host,
                options.Length);
            return new ThirdPartyEmailLoginSession(attemptId, options);
        }
        catch (ThirdPartyAccountLoginException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.ServerUnavailable, "The authentication server request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.ServerUnavailable, "The authentication server is unavailable.", exception);
        }
        catch (JsonException exception)
        {
            throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.InvalidResponse, "The authentication server returned invalid JSON.", exception);
        }
    }

    public async Task<LauncherAccount> ImportEmailProfileAsync(
        string attemptId,
        string profileUuid,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!pendingEmailLogins.TryGetValue(attemptId, out var pending)
            || pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            pendingEmailLogins.TryRemove(attemptId, out _);
            throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.InvalidResponse, "The email login session has expired.");
        }
        var normalizedUuid = NormalizeUuid(profileUuid);
        var profile = pending.Profiles.FirstOrDefault(item =>
            string.Equals(NormalizeUuid(item.Uuid), normalizedUuid, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.ProfileMissing, "The selected profile is unavailable.");

        await pending.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var authentication = pending.InitialConsumed
                ? await AuthenticateEmailAsync(pending.ApiRoot, pending.Email, password, cancellationToken).ConfigureAwait(false)
                : pending.InitialAuthentication;
            pending.InitialConsumed = true;
            if (authentication.Profiles.All(item => !string.Equals(
                    NormalizeUuid(item.Uuid), normalizedUuid, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.ProfileMissing, "The selected profile no longer belongs to the account.");
            }

            var tokens = await BindEmailProfileAsync(
                pending.ApiRoot,
                authentication,
                profile,
                cancellationToken).ConfigureAwait(false);
            var accountId = CreateAccountId(pending.NormalizedApiRoot, profile.Uuid);
            try
            {
                await tokenStore.SaveAsync(accountId, tokens, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ThirdPartyAccountLoginException(ThirdPartyAccountLoginFailureReason.CredentialStorageFailed, "Third-party account credentials could not be saved.", exception);
            }

            pending.Snapshots.TryGetValue(profile.Uuid, out var snapshot);
            var account = new LauncherAccount
            {
                Id = accountId,
                DisplayName = profile.Name,
                Kind = LauncherAccountKind.ThirdParty,
                Uuid = profile.Uuid,
                AuthenticationServerUrl = pending.NormalizedApiRoot,
                ThirdPartyPlatformName = pending.PlatformName,
                ThirdPartyLoginUsername = pending.Email
            };
            return snapshot is { IsAvailable: true }
                ? AccountMapper.WithThirdPartyProfile(account, snapshot.ProfileName!, snapshot.AvatarSource, snapshot.Skin, snapshot.Cape)
                : AccountMapper.WithThirdPartyProfile(account, profile.Name, null, null, null);
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    public async Task CancelEmailLoginAsync(string attemptId, CancellationToken cancellationToken = default)
    {
        if (!pendingEmailLogins.TryRemove(attemptId, out var pending) || pending.InitialConsumed)
            return;
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                new Uri(EnsureTrailingSlash(pending.ApiRoot), "authserver/invalidate"),
                new
                {
                    accessToken = pending.InitialAuthentication.AccessToken,
                    clientToken = pending.InitialAuthentication.ClientToken
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug("Unable to invalidate an unused third-party email login token. ErrorType={ErrorType}",
                exception.GetType().FullName);
        }
    }
}
