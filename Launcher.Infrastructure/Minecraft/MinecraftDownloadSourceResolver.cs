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

using System.Text.RegularExpressions;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Minecraft;

internal static class MinecraftDownloadSourceResolver
{
    private const string BmclApiHost = "bmclapi2.bangbang93.com";
    private const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclManifestUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    private static readonly Regex ForgeIndexPathRegex = new(
        @"^/net/minecraftforge/forge/index_(?<version>[^/]+)\.html$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BmclForgeIndexPathRegex = new(
        @"^/forge/minecraft/(?<version>[^/]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NeoForgeInstallerPathRegex = new(
        @"^/releases/net/neoforged/neoforge/(?<version>[^/]+)/neoforge-\k<version>-installer\.jar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BmclNeoForgeInstallerPathRegex = new(
        @"^/neoforge/version/(?<version>[^/]+)/download/installer\.jar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IEnumerable<ResolvedDownloadRequest> EnumerateRequests(
        string originalUrl,
        DownloadSourcePreference preference,
        string? categoryHint = null)
    {
        if (preference is DownloadSourcePreference.Auto)
            throw new ArgumentException("Automatic preference must be resolved before URL conversion.", nameof(preference));
        var useBmclApiFirst = preference is DownloadSourcePreference.BmclApi;
        var primary = ResolveRequest(originalUrl, preference, useBmclApi: useBmclApiFirst, categoryHint);
        yield return primary;

        var fallback = ResolveRequest(originalUrl, preference, useBmclApi: !useBmclApiFirst, categoryHint);
        if (string.Equals(primary.ActualUrl, fallback.ActualUrl, StringComparison.OrdinalIgnoreCase))
            yield break;

        yield return fallback;
    }

    public static ResolvedDownloadRequest ResolveRequest(
        string originalUrl,
        DownloadSourcePreference preference,
        bool useBmclApi,
        string? categoryHint = null)
    {
        if (preference is DownloadSourcePreference.Auto)
            throw new ArgumentException("Automatic preference must be resolved before URL conversion.", nameof(preference));
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var originalUri))
            throw new InvalidOperationException($"Invalid download URL: {originalUrl}");

        var resourceCategory = ResolveResourceCategory(originalUri, categoryHint);
        var actualUrl = resourceCategory switch
        {
            MinecraftDownloadResourceCategory.Mojang => useBmclApi
                ? BuildBmclMojangUrl(originalUri)
                : BuildOfficialMojangUrl(originalUri),
            MinecraftDownloadResourceCategory.Forge => useBmclApi
                ? BuildBmclForgeUrl(originalUri)
                : BuildOfficialForgeUrl(originalUri),
            MinecraftDownloadResourceCategory.Fabric => useBmclApi
                ? BuildBmclFabricUrl(originalUri)
                : BuildOfficialFabricUrl(originalUri),
            MinecraftDownloadResourceCategory.NeoForge => useBmclApi
                ? BuildBmclNeoForgeUrl(originalUri)
                : BuildOfficialNeoForgeUrl(originalUri),
            _ => originalUrl
        };

        return new ResolvedDownloadRequest(
            originalUrl,
            actualUrl,
            preference,
            GetResolvedSourceKind(resourceCategory, useBmclApi),
            resourceCategory.ToString());
    }

    private static MinecraftDownloadResourceCategory ResolveResourceCategory(Uri uri, string? categoryHint)
    {
        if (Enum.TryParse<MinecraftDownloadResourceCategory>(categoryHint, ignoreCase: true, out var hintedCategory))
            return hintedCategory;

        var host = uri.Host;
        var path = uri.AbsolutePath;

        if (host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("maven.fabricmc.net", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/fabric-meta/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/maven/net/fabricmc/", StringComparison.OrdinalIgnoreCase))
        {
            return MinecraftDownloadResourceCategory.Fabric;
        }

        if (host.Equals("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/forge/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/maven/net/minecraftforge/", StringComparison.OrdinalIgnoreCase))
        {
            return MinecraftDownloadResourceCategory.Forge;
        }

        if (host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/maven/net/neoforged/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/neoforge/", StringComparison.OrdinalIgnoreCase))
        {
            return MinecraftDownloadResourceCategory.NeoForge;
        }

        if (host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase)
            || host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase)
            || (host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase)
                && IsBmclMojangPath(path)))
        {
            return MinecraftDownloadResourceCategory.Mojang;
        }

        return MinecraftDownloadResourceCategory.ThirdParty;
    }

    private static string GetResolvedSourceKind(MinecraftDownloadResourceCategory resourceCategory, bool useBmclApi)
    {
        return (resourceCategory, useBmclApi) switch
        {
            (MinecraftDownloadResourceCategory.Mojang, false) => "MojangOfficial",
            (MinecraftDownloadResourceCategory.Mojang, true) => "BmclApiMojang",
            (MinecraftDownloadResourceCategory.Forge, false) => "ForgeOfficial",
            (MinecraftDownloadResourceCategory.Forge, true) => "BmclApiForge",
            (MinecraftDownloadResourceCategory.Fabric, false) => "FabricOfficial",
            (MinecraftDownloadResourceCategory.Fabric, true) => "BmclApiFabric",
            (MinecraftDownloadResourceCategory.NeoForge, false) => "NeoForgeOfficial",
            (MinecraftDownloadResourceCategory.NeoForge, true) => "BmclApiNeoForge",
            (MinecraftDownloadResourceCategory.ThirdParty, _) => "ThirdParty",
            _ => "UnknownSource"
        };
    }

    private static string BuildOfficialMojangUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Equals("/mc/game/version_manifest_v2.json", StringComparison.OrdinalIgnoreCase))
                return OfficialManifestUrl;

            if (uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
                return $"https://libraries.minecraft.net{uri.AbsolutePath["/maven".Length..]}{uri.Query}";

            if (uri.AbsolutePath.StartsWith("/libraries/", StringComparison.OrdinalIgnoreCase))
                return $"https://libraries.minecraft.net{uri.AbsolutePath["/libraries".Length..]}{uri.Query}";

            if (uri.AbsolutePath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                return $"https://resources.download.minecraft.net{uri.AbsolutePath["/assets".Length..]}{uri.Query}";

            if (uri.AbsolutePath.StartsWith("/v1/objects/", StringComparison.OrdinalIgnoreCase))
                return $"https://piston-data.mojang.com{uri.PathAndQuery}";

            return $"https://piston-meta.mojang.com{uri.PathAndQuery}";
        }

        return uri.AbsoluteUri;
    }

    private static string BuildBmclMojangUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        if (uri.AbsolutePath.Contains("version_manifest_v2.json", StringComparison.OrdinalIgnoreCase))
            return BmclManifestUrl;

        if (uri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
            return $"https://{BmclApiHost}/maven{uri.AbsolutePath}{uri.Query}";

        if (uri.Host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
            return $"https://{BmclApiHost}/assets{uri.AbsolutePath}{uri.Query}";

        return $"https://{BmclApiHost}{uri.PathAndQuery}";
    }

    private static string BuildOfficialForgeUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
        {
            if (TryExtractBmclForgeMinecraftVersion(uri.AbsolutePath, out var bmclMinecraftVersion))
                return $"https://files.minecraftforge.net/net/minecraftforge/forge/index_{bmclMinecraftVersion}.html{uri.Query}";

            if (TryExtractForgeMinecraftVersion(uri.AbsolutePath, out var minecraftVersion))
                return $"https://files.minecraftforge.net/net/minecraftforge/forge/index_{minecraftVersion}.html";

            if (uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
                return $"https://maven.minecraftforge.net{uri.AbsolutePath["/maven".Length..]}{uri.Query}";
        }

        return uri.AbsoluteUri;
    }

    private static string BuildBmclForgeUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        if (uri.Host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
            && TryExtractForgeMinecraftVersion(uri.AbsolutePath, out var minecraftVersion))
        {
            return $"https://{BmclApiHost}/forge/minecraft/{minecraftVersion}{uri.Query}";
        }

        if (uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
            return $"https://{BmclApiHost}{uri.PathAndQuery}";

        return $"https://{BmclApiHost}/maven{uri.AbsolutePath}{uri.Query}";
    }

    private static string BuildOfficialFabricUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.StartsWith("/fabric-meta/", StringComparison.OrdinalIgnoreCase))
                return $"https://meta.fabricmc.net{uri.AbsolutePath["/fabric-meta".Length..]}{uri.Query}";

            if (uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
                return $"https://maven.fabricmc.net{uri.AbsolutePath["/maven".Length..]}{uri.Query}";
        }

        return uri.AbsoluteUri;
    }

    private static string BuildBmclFabricUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        if (uri.Host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
            return $"https://{BmclApiHost}/fabric-meta{uri.AbsolutePath}{uri.Query}";

        return $"https://{BmclApiHost}/maven{uri.AbsolutePath}{uri.Query}";
    }

    private static string BuildOfficialNeoForgeUrl(Uri uri)
    {
        if (!uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        var installer = BmclNeoForgeInstallerPathRegex.Match(uri.AbsolutePath);
        if (installer.Success)
        {
            var version = installer.Groups["version"].Value;
            return $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar{uri.Query}";
        }

        if (uri.AbsolutePath.StartsWith("/maven/releases/", StringComparison.OrdinalIgnoreCase))
            return $"https://maven.neoforged.net{uri.AbsolutePath["/maven".Length..]}{uri.Query}";

        if (uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://maven.neoforged.net/releases{uri.AbsolutePath["/maven".Length..]}{uri.Query}";
        }

        const string bmclMetaPrefix = "/neoforge/meta";
        if (uri.AbsolutePath.StartsWith($"{bmclMetaPrefix}/", StringComparison.OrdinalIgnoreCase))
            return $"https://maven.neoforged.net{uri.AbsolutePath[bmclMetaPrefix.Length..]}{uri.Query}";

        return uri.AbsoluteUri;
    }

    private static string BuildBmclNeoForgeUrl(Uri uri)
    {
        if (uri.Host.Equals(BmclApiHost, StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        if (uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
        {
            var installer = NeoForgeInstallerPathRegex.Match(uri.AbsolutePath);
            if (installer.Success)
                return $"https://{BmclApiHost}/neoforge/version/{installer.Groups["version"].Value}/download/installer.jar{uri.Query}";
        }

        if (uri.AbsolutePath.StartsWith("/releases/", StringComparison.OrdinalIgnoreCase))
            return $"https://{BmclApiHost}/maven{uri.AbsolutePath["/releases".Length..]}{uri.Query}";

        const string versionsPrefix = "/api/maven/versions/";
        if (uri.AbsolutePath.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{BmclApiHost}/neoforge/meta/api/maven/details/{uri.AbsolutePath[versionsPrefix.Length..]}{uri.Query}";
        }

        const string detailsPrefix = "/api/maven/details/";
        if (uri.AbsolutePath.StartsWith(detailsPrefix, StringComparison.OrdinalIgnoreCase))
            return $"https://{BmclApiHost}/neoforge/meta{uri.PathAndQuery}";

        return uri.AbsoluteUri;
    }

    private static bool TryExtractForgeMinecraftVersion(string absolutePath, out string minecraftVersion)
    {
        var match = ForgeIndexPathRegex.Match(absolutePath);
        if (match.Success)
        {
            minecraftVersion = match.Groups["version"].Value;
            return true;
        }

        minecraftVersion = string.Empty;
        return false;
    }

    private static bool TryExtractBmclForgeMinecraftVersion(string absolutePath, out string minecraftVersion)
    {
        var match = BmclForgeIndexPathRegex.Match(absolutePath);
        if (match.Success)
        {
            minecraftVersion = match.Groups["version"].Value;
            return true;
        }

        minecraftVersion = string.Empty;
        return false;
    }

    private static bool IsBmclMojangPath(string absolutePath)
    {
        return absolutePath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            || absolutePath.StartsWith("/libraries/", StringComparison.OrdinalIgnoreCase)
            || absolutePath.StartsWith("/mc/game/", StringComparison.OrdinalIgnoreCase)
            || absolutePath.StartsWith("/v1/objects/", StringComparison.OrdinalIgnoreCase)
            || absolutePath.StartsWith("/v1/packages/", StringComparison.OrdinalIgnoreCase)
            || absolutePath.StartsWith("/v1/products/", StringComparison.OrdinalIgnoreCase);
    }

    private enum MinecraftDownloadResourceCategory
    {
        Mojang,
        Forge,
        Fabric,
        NeoForge,
        ThirdParty
    }
}
