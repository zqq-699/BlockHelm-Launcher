/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Sockets;
using System.Text;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class MinecraftHttpClientFactoryTests
{
    [Fact]
    public async Task ConnectionDiagnosticsRedactProxyEndpointAndPreserveRequests()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var logger = new ConnectionLogger();
            using var handler = Assert.IsType<SocketsHttpHandler>(MinecraftHttpClientFactory.CreateTransportHandler(logger, "MinecraftContent"));
            handler.Proxy = new WebProxy($"http://private-user:private-password@127.0.0.1:{port}");
            using var client = new HttpClient(handler);
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"));
            });

            Assert.Equal("OK", await client.GetStringAsync("http://maven.neoforged.net/example"));
            await server;
            var line = Assert.Single(logger.Messages, message => message.Contains("Download connection established."));
            Assert.Contains("MinecraftContent", line);
            Assert.Contains("ProxyOrUnknown", line);
            Assert.Contains("<redacted>", line);
            Assert.DoesNotContain("127.0.0.1", line);
            Assert.DoesNotContain("private-user", line);
            Assert.DoesNotContain("private-password", line);
            Assert.DoesNotContain(port.ToString(), line);
        }
        finally
        {
            listener.Stop();
        }
    }

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

    private sealed class ConnectionLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
