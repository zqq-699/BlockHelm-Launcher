/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class MinecraftHttpClientFactoryTests
{
    [Fact]
    public void TransportBoundsConnectionLifetimeAndRetainsProxyAndManualRedirects()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(MinecraftHttpClientFactory.CreateTransportHandler());
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionIdleTimeout);
        Assert.True(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportUsesHttp11AcrossRedirectsForOrdinaryAndRangeRequests(bool useRange)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
            Assert.Equal(useRange ? "bytes=0-99" : null, request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(++calls == 1 ? HttpStatusCode.Found : HttpStatusCode.OK)
            {
                Version = HttpVersion.Version11,
                RequestMessage = request,
                Content = new StringContent("ok")
            };
            if (calls == 1) response.Headers.Location = new Uri("https://cdn.example.test/file");
            return response;
        }));
        var transport = new MinecraftDownloadTransport(client, new DownloadRetryOptions());
        await using var response = await transport.SendAsync(
            "https://example.test/file",
            CancellationToken.None,
            useRange ? request => request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99) : null);
        Assert.Equal("cdn.example.test", response.FinalHost);
        Assert.Equal(HttpVersion.Version11, response.Response.Version);
        Assert.Equal("ok", await response.Response.Content.ReadAsStringAsync());
        Assert.Equal(2, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(response(request));
    }
}
