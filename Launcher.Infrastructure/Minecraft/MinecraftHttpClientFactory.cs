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

using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal static class MinecraftHttpClientFactory
{
    public static HttpClient CreateTransportClient(LauncherPathProvider? pathProvider = null, ILogger? logger = null, string? connectionPurpose = null)
    {
        var client = LauncherHttpUserAgent.Apply(new HttpClient(CreateTransportHandler(logger, connectionPurpose))
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        });
        VanillaVersionMetadataCache.ForLauncher(client, pathProvider ?? new LauncherPathProvider());
        return client;
    }

    public static HttpMessageHandler CreateTransportHandler(ILogger? logger = null, string? connectionPurpose = null)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            ConnectCallback = logger is null ? null : (context, token) =>
                DownloadConnectionDiagnostics.ConnectAsync(context, logger, connectionPurpose, token),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
    }
}
