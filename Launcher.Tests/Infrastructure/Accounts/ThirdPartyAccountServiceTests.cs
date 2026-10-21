/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Reflection;
using System.Text;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Launcher.Infrastructure.Accounts.ThirdParty;

namespace Launcher.Tests.Infrastructure.Accounts;

public sealed class ThirdPartyAccountServiceTests
{
    [Fact]
    public async Task EmailLoginReturnsAllProfilesWithoutUsernameFeature()
    {
        var sentUserAgents = new List<IReadOnlyList<string>>();
        var handler = new StubHttpMessageHandler(request =>
        {
            sentUserAgents.Add(request.Headers.UserAgent.Select(value => value.ToString()).ToArray());
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"meta\":{}}")
                : Json(HttpStatusCode.OK, """
                    {"accessToken":"access","clientToken":"client","availableProfiles":[
                      {"id":"00112233445566778899aabbccddeeff","name":"First"},
                      {"id":"11112222333344445555666677778888","name":"Second"}]}
                    """));
        });
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Legacy/1.0");
        var service = new ThirdPartyAccountService(httpClient, new RecordingTokenStore());

        var session = await service.BeginEmailLoginAsync("https://example.test/api/yggdrasil", "user@example.test", "password");

        Assert.Equal(["First", "Second"], session.Profiles.Select(profile => profile.Name));
        Assert.All(session.Profiles, profile => Assert.Equal(LauncherAccount.DefaultSteveAvatarUrl, profile.AvatarSource));
        var expected = LauncherHttpUserAgent.BuildValue(Assembly.GetEntryAssembly());
        Assert.NotEmpty(sentUserAgents);
        Assert.All(sentUserAgents, values => Assert.Equal([expected], values));
    }

    [Fact]
    public async Task LoginWithUsernameMapsForbiddenToInvalidCredentials()
    {
        var handler = new StubHttpMessageHandler(request => Task.FromResult(
            request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"meta\":{\"feature.non_email_login\":true}}")
                : Json(HttpStatusCode.Forbidden, "{\"error\":\"ForbiddenOperationException\"}")));
        var service = new ThirdPartyAccountService(new HttpClient(handler), new RecordingTokenStore());

        var exception = await Assert.ThrowsAsync<ThirdPartyAccountLoginException>(() =>
            service.LoginWithUsernameAsync("https://example.test/yggdrasil", "Player", "bad"));

        Assert.Equal(ThirdPartyAccountLoginFailureReason.InvalidCredentials, exception.Reason);
    }

    [Fact]
    public async Task ReauthenticationRejectsDifferentProfileAndRemovesReturnedCredentials()
    {
        var tokenStore = new RecordingTokenStore();
        var handler = new StubHttpMessageHandler(request => Task.FromResult(
            request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"meta\":{\"feature.non_email_login\":true}}")
                : Json(HttpStatusCode.OK, """
                    {"accessToken":"new-access","clientToken":"new-client","selectedProfile":{"id":"11112222333344445555666677778888","name":"Player"}}
                    """)));
        var service = new ThirdPartyAccountService(new HttpClient(handler), tokenStore);
        var existing = new LauncherAccount
        {
            Id = "third-party-existing",
            DisplayName = "Player",
            Kind = LauncherAccountKind.ThirdParty,
            Uuid = "00112233-4455-6677-8899-aabbccddeeff",
            AuthenticationServerUrl = "https://example.test/api/yggdrasil/",
            ThirdPartyLoginUsername = "Player"
        };

        var exception = await Assert.ThrowsAsync<ThirdPartyAccountLoginException>(() =>
            service.ReauthenticateAsync(existing, "password"));

        Assert.Equal(ThirdPartyAccountLoginFailureReason.AccountMismatch, exception.Reason);
        Assert.Null(tokenStore.SavedTokens);
    }

    [Fact]
    public async Task RefreshProfileFailurePreservesAllStoredProfileFields()
    {
        var appearanceService = new RecordingAppearanceService(ThirdPartyAccountProfileSnapshot.Unavailable);
        var service = new ThirdPartyAccountService(
            new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"meta\":{}}")))),
            new RecordingTokenStore(),
            appearanceService);
        var account = new LauncherAccount
        {
            Id = "third-party-account",
            DisplayName = "StoredPlayer",
            Kind = LauncherAccountKind.ThirdParty,
            Uuid = "00112233-4455-6677-8899-aabbccddeeff",
            AuthenticationServerUrl = "https://auth.example.test/api/yggdrasil/",
            ThirdPartyLoginUsername = "login-name",
            AvatarSource = "file:///stored-avatar.png",
            SkinSource = "file:///stored-skin.png",
            CachedCapeOptions =
            [
                new AccountCapeOption
                {
                    Id = "stored-cape",
                    DisplayName = string.Empty,
                    ImageUrl = "file:///stored-cape.png",
                    IsActive = true
                }
            ]
        };

        await Assert.ThrowsAsync<ThirdPartyAccountProfileRefreshException>(() =>
            service.RefreshAccountProfileAsync(account));
        Assert.Equal("StoredPlayer", account.DisplayName);
        Assert.Equal("file:///stored-avatar.png", account.AvatarSource);
        Assert.Equal("file:///stored-skin.png", account.SkinSource);
        Assert.Equal("file:///stored-cape.png", Assert.Single(account.CachedCapeOptions).ImageUrl);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class RecordingTokenStore : IThirdPartyAccountTokenStore
    {
        public ThirdPartyAccountTokens? SavedTokens { get; private set; }
        public List<(string AccountId, ThirdPartyAccountTokens Tokens)> SavedEntries { get; } = [];

        public Task<ThirdPartyAccountTokens?> GetAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SavedTokens);

        public Task SaveAsync(
            string accountId,
            ThirdPartyAccountTokens tokens,
            CancellationToken cancellationToken = default)
        {
            SavedTokens = tokens;
            SavedEntries.Add((accountId, tokens));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
        {
            SavedTokens = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAppearanceService(ThirdPartyAccountProfileSnapshot profile)
        : IThirdPartyAccountAppearanceService
    {
        public Uri? ApiRoot { get; private set; }
        public string? ProfileId { get; private set; }
        public string? AccountId { get; private set; }

        public Task<ThirdPartyAccountProfileSnapshot> GetProfileAsync(
            Uri apiRoot,
            string profileId,
            string accountId,
            CancellationToken cancellationToken)
        {
            ApiRoot = apiRoot;
            ProfileId = profileId;
            AccountId = accountId;
            return Task.FromResult(profile);
        }
    }
}
