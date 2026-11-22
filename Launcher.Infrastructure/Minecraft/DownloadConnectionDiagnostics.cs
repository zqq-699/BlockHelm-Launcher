/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal static class DownloadConnectionDiagnostics
{
    private static readonly TimeSpan DnsDiagnosticTimeout = TimeSpan.FromMilliseconds(500);

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, ILogger logger, string? purpose, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var requestHost = context.InitialRequestMessage.RequestUri?.IdnHost;
        var direct = requestHost is not null && string.Equals(requestHost, endpoint.Host, StringComparison.OrdinalIgnoreCase);
        var relevant = string.Equals(endpoint.Host, "chinanet.mirrors.ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(endpoint.Host, "maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
            || requestHost is not null && (requestHost.Equals("chinanet.mirrors.ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
                || requestHost.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase));
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (relevant && logger.IsEnabled(LogLevel.Debug))
            {
                var remote = socket.RemoteEndPoint as IPEndPoint;
                logger.LogDebug("Download connection established. ConnectionPurpose={ConnectionPurpose} TargetHost={TargetHost} Route={Route} RemoteAddress={RemoteAddress} RemotePort={RemotePort} AddressFamily={AddressFamily}",
                    purpose, requestHost, direct ? "Direct" : "ProxyOrUnknown",
                    direct ? remote?.Address.ToString() : "<redacted>", direct ? remote?.Port : null,
                    direct ? remote?.AddressFamily.ToString() : null);
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception exception)
        {
            socket.Dispose();
            if (relevant && logger.IsEnabled(LogLevel.Debug) && exception is SocketException or OperationCanceledException)
            {
                var candidates = direct && !cancellationToken.IsCancellationRequested
                    ? await ResolveCandidatesAsync(endpoint.Host).ConfigureAwait(false)
                    : [];
                logger.LogDebug("Download connection failed. ConnectionPurpose={ConnectionPurpose} TargetHost={TargetHost} Route={Route} TargetPort={TargetPort} DnsCandidatesAfterFailure={DnsCandidatesAfterFailure} ActualFailedAddress={ActualFailedAddress} SocketError={SocketError}",
                    purpose, requestHost, direct ? "Direct" : "ProxyOrUnknown", direct ? endpoint.Port : null,
                    candidates, "Unknown", (exception as SocketException)?.SocketErrorCode.ToString());
            }
            throw;
        }
    }

    private static async Task<string[]> ResolveCandidatesAsync(string host)
    {
        try
        {
            using var timeout = new CancellationTokenSource(DnsDiagnosticTimeout);
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            return addresses.Select(address => $"{address} ({address.AddressFamily})").ToArray();
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            return [];
        }
    }
}
