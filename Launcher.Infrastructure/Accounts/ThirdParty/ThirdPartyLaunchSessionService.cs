/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Launcher.Application.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts.ThirdParty;

internal sealed class ThirdPartyLaunchSessionService : IThirdPartyLaunchSessionService
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private readonly HttpClient httpClient;
    private readonly IThirdPartyAccountTokenStore tokenStore;
    private readonly ILogger<ThirdPartyLaunchSessionService> logger;

    public ThirdPartyLaunchSessionService(
        IThirdPartyAccountTokenStore tokenStore,
        ILogger<ThirdPartyLaunchSessionService>? logger = null)
        : this(
            LauncherHttpUserAgent.Apply(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }),
            tokenStore,
            logger)
    {
    }

    internal ThirdPartyLaunchSessionService(
        HttpClient httpClient,
        IThirdPartyAccountTokenStore tokenStore,
        ILogger<ThirdPartyLaunchSessionService>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.tokenStore = tokenStore;
        this.logger = logger ?? NullLogger<ThirdPartyLaunchSessionService>.Instance;
    }

    public async Task<ThirdPartyLaunchSession> CreateAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        if (!account.IsThirdParty
            || !TryNormalizeHttpsApiRoot(account.AuthenticationServerUrl, out var apiRoot)
            || !TryNormalizeUuid(account.Uuid, out var compactUuid))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "The third-party account identity is incomplete.");
        }

        ThirdPartyAccountTokens? tokens;
        try
        {
            tokens = await tokenStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.ReauthenticationRequired,
                "The third-party account credentials are unavailable.",
                exception);
        }

        if (tokens is null
            || string.IsNullOrWhiteSpace(tokens.AccessToken)
            || string.IsNullOrWhiteSpace(tokens.ClientToken))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.ReauthenticationRequired,
                "The third-party account must be authenticated again.");
        }

        try
        {
            var metadata = await GetPrefetchedMetadataAsync(apiRoot, cancellationToken).ConfigureAwait(false);
            var validated = await ValidateAsync(apiRoot, tokens, cancellationToken).ConfigureAwait(false);
            var username = account.DisplayName;
            if (!validated)
            {
                var refreshed = await RefreshAsync(apiRoot, tokens, compactUuid, cancellationToken).ConfigureAwait(false);
                tokens = refreshed.Tokens;
                username = refreshed.ProfileName;
                try
                {
                    await tokenStore.SaveAsync(account.Id, tokens, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new LaunchAccountSessionException(
                        LaunchAccountSessionFailureReason.CredentialStorageFailed,
                        "The refreshed third-party account credentials could not be saved.",
                        exception);
                }
            }

            logger.LogInformation(
                "Third-party launch session prepared. AccountId={AccountId} AuthenticationServerHost={AuthenticationServerHost} TokenRefreshed={TokenRefreshed}",
                account.Id,
                apiRoot.Host,
                !validated);
            return new ThirdPartyLaunchSession(
                username,
                tokens.AccessToken,
                compactUuid,
                apiRoot.AbsoluteUri,
                metadata);
        }
        catch (LaunchAccountSessionException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.AuthenticationServerUnavailable,
                "The third-party authentication server request timed out.",
                exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.AuthenticationServerUnavailable,
                "The third-party authentication server is unavailable.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "The third-party authentication server returned invalid JSON.",
                exception);
        }
    }

    private async Task<string> GetPrefetchedMetadataAsync(Uri apiRoot, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(apiRoot, cancellationToken).ConfigureAwait(false);
        EnsureHttpsResponse(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                $"Authentication metadata failed with HTTP status {(int)response.StatusCode}.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaximumMetadataBytes)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "Authentication metadata is empty or too large.");
        }

        using var document = JsonDocument.Parse(bytes);
        var compact = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        return Convert.ToBase64String(compact);
    }

    private async Task<bool> ValidateAsync(
        Uri apiRoot,
        ThirdPartyAccountTokens tokens,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(apiRoot, "authserver/validate"),
            new { accessToken = tokens.AccessToken, clientToken = tokens.ClientToken },
            cancellationToken).ConfigureAwait(false);
        EnsureHttpsResponse(response);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return true;
        if (response.StatusCode == HttpStatusCode.Forbidden)
            return false;

        throw new LaunchAccountSessionException(
            LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
            $"Token validation failed with HTTP status {(int)response.StatusCode}.");
    }

    private async Task<RefreshResult> RefreshAsync(
        Uri apiRoot,
        ThirdPartyAccountTokens currentTokens,
        string expectedUuid,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(apiRoot, "authserver/refresh"),
            new
            {
                accessToken = currentTokens.AccessToken,
                clientToken = currentTokens.ClientToken,
                requestUser = true
            },
            cancellationToken).ConfigureAwait(false);
        EnsureHttpsResponse(response);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.ReauthenticationRequired,
                "The third-party account login has expired.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                $"Token refresh failed with HTTP status {(int)response.StatusCode}.");
        }

        using var payload = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = payload.RootElement;
        var accessToken = GetRequiredString(root, "accessToken");
        var clientToken = GetRequiredString(root, "clientToken");
        if (!string.Equals(clientToken, currentTokens.ClientToken, StringComparison.Ordinal))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "The refreshed client token does not match the stored token.");
        }
        if (!root.TryGetProperty("selectedProfile", out var profile)
            || profile.ValueKind != JsonValueKind.Object
            || !TryNormalizeUuid(GetRequiredString(profile, "id"), out var profileUuid)
            || !string.Equals(profileUuid, expectedUuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "The refreshed profile does not match the selected account.");
        }

        return new RefreshResult(
            new ThirdPartyAccountTokens(accessToken, clientToken),
            GetRequiredString(profile, "name"));
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                $"The authentication response is missing {propertyName}.");
        }
        return property.GetString()!;
    }

    private static bool TryNormalizeHttpsApiRoot(string? value, out Uri apiRoot)
    {
        apiRoot = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        apiRoot = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed
            : new Uri(parsed.AbsoluteUri + "/", UriKind.Absolute);
        return true;
    }

    private static bool TryNormalizeUuid(string? value, out string compactUuid)
    {
        compactUuid = string.Empty;
        if (!Guid.TryParse(value, out var uuid))
            return false;
        compactUuid = uuid.ToString("N");
        return true;
    }

    private static void EnsureHttpsResponse(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is { } finalUri
            && !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchAccountSessionException(
                LaunchAccountSessionFailureReason.InvalidAuthenticationResponse,
                "The authentication server redirected to an insecure address.");
        }
    }

    private sealed record RefreshResult(ThirdPartyAccountTokens Tokens, string ProfileName);
}
