/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Text;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Accounts.ThirdParty;

namespace Launcher.Tests.Infrastructure.Accounts;

public sealed class ThirdPartyLaunchSessionServiceTests
{
    [Fact]
    public async Task CreateAsyncRequestsMetadataWithoutTrailingSlash()
    {
        var requestedUris = new List<Uri>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri!);
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "{\"meta\":{}}")
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var tokenStore = new StubTokenStore(
            new ThirdPartyAccountTokens("access-token", "client-token"));
        var service = new ThirdPartyLaunchSessionService(new HttpClient(handler), tokenStore);
        var account = new LauncherAccount
        {
            Id = "third-party-account",
            DisplayName = "Player",
            Kind = LauncherAccountKind.ThirdParty,
            Uuid = "00112233-4455-6677-8899-aabbccddeeff",
            AuthenticationServerUrl = "https://auth.example.test/api/yggdrasil/"
        };

        await service.CreateAsync(account);

        Assert.Equal(
            [
                new Uri("https://auth.example.test/api/yggdrasil"),
                new Uri("https://auth.example.test/api/yggdrasil/authserver/validate")
            ],
            requestedUris);
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

    private sealed class StubTokenStore(ThirdPartyAccountTokens tokens) : IThirdPartyAccountTokenStore
    {
        public Task<ThirdPartyAccountTokens?> GetAsync(
            string accountId,
            CancellationToken cancellationToken = default) => Task.FromResult<ThirdPartyAccountTokens?>(tokens);

        public Task SaveAsync(
            string accountId,
            ThirdPartyAccountTokens savedTokens,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            string accountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
