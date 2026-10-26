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
    public async Task<LauncherAccount> LoginWithUsernameAsync(
        string authenticationServer,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var initialUri = ParseServerUri(authenticationServer);
        try
        {
            var (apiRoot, metadataDocument) = await ResolveApiRootAsync(initialUri, cancellationToken).ConfigureAwait(false);
            using var metadata = metadataDocument;
            var platformName = GetPlatformName(metadata);
            if (!SupportsUsernameLogin(metadata))
            {
                throw new ThirdPartyAccountLoginException(
                    ThirdPartyAccountLoginFailureReason.UsernameLoginUnsupported,
                    "The authentication server does not support username login.");
            }

            var clientToken = Guid.NewGuid().ToString("N");
            var authenticateUri = new Uri(EnsureTrailingSlash(apiRoot), "authserver/authenticate");
            using var request = new HttpRequestMessage(HttpMethod.Post, authenticateUri)
            {
                Content = JsonContent.Create(new
                {
                    username,
                    password,
                    clientToken,
                    requestUser = true,
                    agent = new { name = "Minecraft", version = 1 }
                })
            };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ThirdPartyAccountLoginException(
                    ThirdPartyAccountLoginFailureReason.InvalidCredentials,
                    "Invalid username or password.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ThirdPartyAccountLoginException(
                    ThirdPartyAccountLoginFailureReason.InvalidResponse,
                    $"Authentication failed with HTTP status {(int)response.StatusCode}.");
            }

            using var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var result = ParseAuthenticationResult(payload, username);
            var normalizedApiRoot = EnsureTrailingSlash(apiRoot).AbsoluteUri;
            var accountId = CreateAccountId(normalizedApiRoot, result.ProfileId);
            var profile = appearanceService is null
                ? ThirdPartyAccountProfileSnapshot.Unavailable
                : await appearanceService.GetProfileAsync(
                    apiRoot,
                    result.ProfileId,
                    accountId,
                    cancellationToken).ConfigureAwait(false);
            try
            {
                await tokenStore.SaveAsync(
                    accountId,
                    new ThirdPartyAccountTokens(result.AccessToken, result.ClientToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ThirdPartyAccountLoginException(
                    ThirdPartyAccountLoginFailureReason.CredentialStorageFailed,
                    "Third-party account credentials could not be saved.",
                    exception);
            }

            logger.LogInformation(
                "Third-party username login completed. AccountId={AccountId} AuthenticationServerHost={AuthenticationServerHost}",
                accountId,
                apiRoot.Host);
            var account = new LauncherAccount
            {
                Id = accountId,
                DisplayName = result.ProfileName,
                Kind = LauncherAccountKind.ThirdParty,
                Uuid = result.ProfileId,
                AuthenticationServerUrl = normalizedApiRoot,
                ThirdPartyPlatformName = platformName,
                ThirdPartyLoginUsername = username.Trim()
            };
            return AccountMapper.WithThirdPartyProfile(
                account,
                profile.IsAvailable ? profile.ProfileName! : result.ProfileName,
                profile.AvatarSource,
                profile.Skin,
                profile.Cape);
        }
        catch (ThirdPartyAccountLoginException exception)
        {
            logger.LogWarning(
                "Third-party username login failed. AuthenticationServerHost={AuthenticationServerHost} Reason={Reason}",
                initialUri.Host,
                exception.Reason);
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ThirdPartyAccountLoginException(
                ThirdPartyAccountLoginFailureReason.ServerUnavailable,
                "The authentication server request timed out.",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ThirdPartyAccountLoginException(
                ThirdPartyAccountLoginFailureReason.ServerUnavailable,
                "The authentication server is unavailable.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new ThirdPartyAccountLoginException(
                ThirdPartyAccountLoginFailureReason.InvalidResponse,
                "The authentication server returned invalid JSON.",
                exception);
        }
    }

    public Task DeleteCredentialsAsync(string accountId, CancellationToken cancellationToken = default) =>
        tokenStore.DeleteAsync(accountId, cancellationToken);
}
