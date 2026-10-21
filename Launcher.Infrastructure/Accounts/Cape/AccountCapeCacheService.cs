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

using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Launcher.Infrastructure.Accounts;

internal sealed class AccountCapeCacheService
{
    private const string CapeCacheVersion = "v1";

    private readonly HttpClient httpClient;
    private readonly string capeDirectory;

    public AccountCapeCacheService(HttpClient httpClient, LauncherPathProvider pathProvider)
        : this(
            httpClient,
            Path.Combine(pathProvider.DefaultAccountDataDirectory, "microsoft", "capes"))
    {
    }

    internal AccountCapeCacheService(HttpClient httpClient, string capeDirectory)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.capeDirectory = capeDirectory;
        Directory.CreateDirectory(this.capeDirectory);
    }

    public async Task<string?> GetOrCreateCapeSourceAsync(
        string accountId,
        string? capeId,
        string? capeUrl,
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool useRemoteFallback = true)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(capeUrl))
            return null;

        if (TryResolveExistingLocalCape(capeUrl) is { } localCapeSource)
            return localCapeSource;

        var cacheKey = string.IsNullOrWhiteSpace(capeId) ? capeUrl : capeId;
        var cachedCapePath = GetLatestCachedCapePath(accountId, cacheKey);
        if (!forceRefresh && cachedCapePath is not null)
            return new Uri(cachedCapePath).AbsoluteUri;

        try
        {
            var capeBytes = await httpClient.GetByteArrayAsync(capeUrl, cancellationToken);
            var hash = ComputeHash(capeBytes);
            var capePath = CreateCapePath(accountId, cacheKey, hash);
            if (!File.Exists(capePath) || forceRefresh)
                await File.WriteAllBytesAsync(capePath, capeBytes, cancellationToken);
            DeleteStaleCapes(accountId, cacheKey, capePath);
            return new Uri(capePath).AbsoluteUri;
        }
        catch
        {
            if (cachedCapePath is not null)
                return new Uri(cachedCapePath).AbsoluteUri;

            return useRemoteFallback ? capeUrl : null;
        }
    }

    private string CreateCapePath(string accountId, string cacheKey, string contentHash)
    {
        var accountCapeDirectory = GetAccountCapeDirectory(accountId);
        Directory.CreateDirectory(accountCapeDirectory);
        var safeKey = SanitizePathSegment(cacheKey);
        if (safeKey.Length > 48)
            safeKey = safeKey[..48];
        var safeHash = contentHash.Length > 24 ? contentHash[..24] : contentHash;
        return Path.Combine(accountCapeDirectory, $"{CapeCacheVersion}-{safeKey}-{safeHash}.png");
    }

    private string? GetLatestCachedCapePath(string accountId, string cacheKey)
    {
        var accountCapeDirectory = GetAccountCapeDirectory(accountId);
        if (!Directory.Exists(accountCapeDirectory))
            return null;

        var safeKey = SanitizePathSegment(cacheKey);
        if (safeKey.Length > 48)
            safeKey = safeKey[..48];

        return Directory.EnumerateFiles(accountCapeDirectory, $"{CapeCacheVersion}-{safeKey}-*.png")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private void DeleteStaleCapes(string accountId, string cacheKey, string currentCapePath)
    {
        var accountCapeDirectory = GetAccountCapeDirectory(accountId);
        if (!Directory.Exists(accountCapeDirectory))
            return;

        var safeKey = SanitizePathSegment(cacheKey);
        if (safeKey.Length > 48)
            safeKey = safeKey[..48];

        foreach (var capePath in Directory.EnumerateFiles(accountCapeDirectory, $"{CapeCacheVersion}-{safeKey}-*.png"))
        {
            if (string.Equals(capePath, currentCapePath, StringComparison.OrdinalIgnoreCase))
                continue;

            TryDeleteFile(capePath);
        }
    }

    private string GetAccountCapeDirectory(string accountId)
    {
        return Path.Combine(capeDirectory, SanitizePathSegment(accountId));
    }

    private static string? TryResolveExistingLocalCape(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.IsFile)
            return null;

        return File.Exists(uri.LocalPath) ? uri.AbsoluteUri : null;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }

    private static string ComputeHash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
