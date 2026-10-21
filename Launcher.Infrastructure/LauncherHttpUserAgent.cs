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

using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;

namespace Launcher.Infrastructure;

internal static class LauncherHttpUserAgent
{
    private const string ProductName = "BHL";
    private const string FallbackVersion = "0.0.0";
    private static readonly string CurrentValue = BuildValue(Assembly.GetEntryAssembly());

    public static HttpClient Apply(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.DefaultRequestHeaders.UserAgent.Count == 1
            && string.Equals(
                client.DefaultRequestHeaders.UserAgent.First().ToString(),
                CurrentValue,
                StringComparison.Ordinal))
        {
            return client;
        }

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CurrentValue);
        return client;
    }

    internal static string BuildValue(Assembly? entryAssembly)
    {
        var informationalVersion = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return BuildValue(informationalVersion, entryAssembly?.GetName().Version);
    }

    internal static string BuildValue(string? informationalVersion, Version? assemblyVersion)
    {
        if (TryBuildValue(informationalVersion, out var value))
            return value;
        if (TryBuildValue(assemblyVersion?.ToString(), out value))
            return value;
        return $"{ProductName}/{FallbackVersion}";
    }

    private static bool TryBuildValue(string? version, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var candidate = $"{ProductName}/{version.Trim()}";
        if (!ProductInfoHeaderValue.TryParse(candidate, out var parsed)
            || parsed.Product is null
            || parsed.Comment is not null)
        {
            return false;
        }

        value = parsed.ToString();
        return true;
    }
}
