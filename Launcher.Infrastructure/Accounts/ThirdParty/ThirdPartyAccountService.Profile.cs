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
    public async Task<LauncherAccount> ReauthenticateAsync(
        LauncherAccount account,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!account.IsThirdParty
            || string.IsNullOrWhiteSpace(account.AuthenticationServerUrl)
            || string.IsNullOrWhiteSpace(account.ThirdPartyLoginUsername)
            || string.IsNullOrWhiteSpace(account.Uuid))
        {
            throw new ThirdPartyAccountLoginException(
                ThirdPartyAccountLoginFailureReason.InvalidResponse,
                "The existing third-party account identity is incomplete.");
        }

        LauncherAccount authenticated;
        if (account.ThirdPartyLoginUsername.Contains('@', StringComparison.Ordinal))
        {
            var selection = await BeginEmailLoginAsync(
                account.AuthenticationServerUrl,
                account.ThirdPartyLoginUsername,
                password,
                cancellationToken).ConfigureAwait(false);
            try
            {
                authenticated = await ImportEmailProfileAsync(
                    selection.AttemptId,
                    account.Uuid,
                    password,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await CancelEmailLoginAsync(selection.AttemptId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        else
        {
            authenticated = await LoginWithUsernameAsync(
                account.AuthenticationServerUrl,
                account.ThirdPartyLoginUsername,
                password,
                cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(authenticated.Id, account.Id, StringComparison.Ordinal)
            || !string.Equals(
                NormalizeUuid(authenticated.Uuid),
                NormalizeUuid(account.Uuid),
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await tokenStore.DeleteAsync(authenticated.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Unable to clean credentials returned for a mismatched third-party account. AccountId={AccountId} ErrorType={ErrorType}",
                    authenticated.Id,
                    exception.GetType().FullName);
            }
            throw new ThirdPartyAccountLoginException(
                ThirdPartyAccountLoginFailureReason.AccountMismatch,
                "The authenticated profile does not match the existing account.");
        }

        var activeSkin = authenticated.SkinLibrary.FirstOrDefault(skin =>
            string.Equals(skin.Id, authenticated.ActiveSkinId, StringComparison.Ordinal));
        var activeCape = authenticated.CachedCapeOptions.FirstOrDefault(cape => cape.IsActive && !cape.IsNone);
        return AccountMapper.WithThirdPartyProfile(
            account,
            authenticated.DisplayName,
            authenticated.AvatarSource,
            activeSkin,
            activeCape,
            authenticated.ThirdPartyPlatformName);
    }

    public async Task<LauncherAccount> RefreshAccountProfileAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        if (!account.IsThirdParty
            || appearanceService is null
            || string.IsNullOrWhiteSpace(account.AuthenticationServerUrl)
            || string.IsNullOrWhiteSpace(account.Uuid)
            || !Uri.TryCreate(account.AuthenticationServerUrl, UriKind.Absolute, out var apiRoot))
        {
            return account;
        }

        var (resolvedApiRoot, metadataDocument) = await ResolveApiRootAsync(apiRoot, cancellationToken).ConfigureAwait(false);
        using var metadata = metadataDocument;
        var platformName = GetPlatformName(metadata);
        var profile = await appearanceService.GetProfileAsync(
            resolvedApiRoot,
            account.Uuid,
            account.Id,
            cancellationToken).ConfigureAwait(false);
        if (!profile.IsAvailable)
        {
            throw new ThirdPartyAccountProfileRefreshException(
                "The third-party account profile could not be refreshed.");
        }

        var refreshed = AccountMapper.WithThirdPartyProfile(
            account,
            profile.ProfileName!,
            profile.AvatarSource,
            profile.Skin,
            profile.Cape,
            platformName);
        logger.LogDebug(
            "Third-party account profile refreshed. AccountId={AccountId} HasSkin={HasSkin} HasCape={HasCape}",
            account.Id,
            profile.Skin is not null,
            profile.Cape is not null);
        return refreshed;
    }
}
