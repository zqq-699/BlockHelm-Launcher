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
using System.Reflection;
using Launcher.App.ViewModels.Settings;
using Launcher.Infrastructure;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Updates;

namespace Launcher.Tests.Infrastructure;

public sealed class LauncherHttpUserAgentTests
{
    [Theory]
    [InlineData("0.9.16", "BHL/0.9.16")]
    [InlineData("1.2.3-beta.1+sha.abcdef", "BHL/1.2.3-beta.1+sha.abcdef")]
    [InlineData(" 2.0.0-rc.1 ", "BHL/2.0.0-rc.1")]
    public void BuildValue_UsesValidInformationalVersion(string version, string expected)
    {
        Assert.Equal(expected, LauncherHttpUserAgent.BuildValue(version, new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void BuildValue_UsesAssemblyVersionWhenInformationalVersionIsInvalid()
    {
        Assert.Equal(
            "BHL/2.3.4.5",
            LauncherHttpUserAgent.BuildValue("invalid version", new Version(2, 3, 4, 5)));
    }

    [Fact]
    public void BuildValue_UsesSafeFallbackWhenNoValidVersionExists()
    {
        Assert.Equal("BHL/0.0.0", LauncherHttpUserAgent.BuildValue("invalid version", null));
    }

    [Fact]
    public void AppAssembly_ProducesCurrentReleaseUserAgent()
    {
        Assert.Equal(
            "BHL/0.9.16",
            LauncherHttpUserAgent.BuildValue(typeof(InfoSettingsViewModel).Assembly));
    }

    [Fact]
    public async Task Apply_ReplacesExistingHeaderAndSendsExactlyOneLauncherProduct()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Legacy/1.0 (old)");

        LauncherHttpUserAgent.Apply(client);
        using var response = await client.GetAsync("https://example.test/");

        var expected = LauncherHttpUserAgent.BuildValue(Assembly.GetEntryAssembly());
        Assert.Equal([expected], handler.UserAgents);
    }

    [Fact]
    public void LauncherFactories_ApplyTheUnifiedHeader()
    {
        var expected = LauncherHttpUserAgent.BuildValue(Assembly.GetEntryAssembly());
        using var downloadClient = MinecraftHttpClientFactory.CreateTransportClient();
        using var updateClient = OfficialUpdateHttp.CreateClient(TimeSpan.FromSeconds(1));

        Assert.Equal(expected, downloadClient.DefaultRequestHeaders.UserAgent.Single().ToString());
        Assert.Equal(expected, updateClient.DefaultRequestHeaders.UserAgent.Single().ToString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public IReadOnlyList<string> UserAgents { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UserAgents = request.Headers.UserAgent.Select(value => value.ToString()).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request
            });
        }
    }
}
