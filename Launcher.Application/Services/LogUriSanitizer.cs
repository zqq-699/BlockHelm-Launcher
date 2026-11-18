/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Application.Services;

/// <summary>Shared URI policy for structured download logs and free-form diagnostic text.</summary>
public static class LogUriSanitizer
{
    private const string InvalidUriPlaceholder = "<redacted-url>";

    public static string Sanitize(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? Sanitize(uri) : InvalidUriPlaceholder;

    public static string Sanitize(Uri? uri)
    {
        try
        {
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
                return InvalidUriPlaceholder;

            // Rebuild the authority so userinfo is never retained. Query and fragment
            // are omitted even on public artifact hosts (CDNs may use signed queries).
            var origin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            var path = uri.AbsolutePath;
            return origin + (HasPublicPath(uri) && !path.Equals("/%3Credacted-path%3E", StringComparison.OrdinalIgnoreCase)
                ? path : "/<redacted-path>");
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException or InvalidOperationException)
        {
            // Logging must remain usable even when a parsed URI cannot be normalized.
            return InvalidUriPlaceholder;
        }
    }

    private static bool HasPublicPath(Uri uri)
    {
        if (!uri.IsDefaultPort) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath;
        // Exact dedicated metadata/artifact hosts only, never a suffix match.
        return host switch
        {
            "piston-meta.mojang.com" or "piston-data.mojang.com"
                or "launchermeta.mojang.com" or "launcher.mojang.com"
                or "libraries.minecraft.net" or "resources.download.minecraft.net"
                or "maven.minecraftforge.net" or "files.minecraftforge.net"
                or "meta.fabricmc.net" or "maven.fabricmc.net"
                or "meta.quiltmc.org" or "maven.quiltmc.org"
                or "cdn.modrinth.com" or "edge.forgecdn.net"
                or "mediafilez.forgecdn.net" or "media.forgecdn.net" => true,
            "maven.neoforged.net" => StartsWithAny(path,
                "/releases/", "/snapshots/", "/api/maven/"),
            "bmclapi2.bangbang93.com" => StartsWithAny(path,
                "/mc/", "/v1/", "/v2/", "/maven/", "/libraries/", "/assets/",
                "/forge/", "/neoforge/", "/fabric-meta/", "/mirrors/authlib-injector/"),
            "api.curseforge.com" => path is "/v1/mods" or "/v1/categories"
                || StartsWithAny(path, "/v1/mods/", "/v1/fingerprints/"),
            "api.modrinth.com" => path is "/v2/search" or "/v2/projects" or "/v2/version_files"
                || StartsWithAny(path, "/v2/project/", "/v2/version/", "/v2/tag/"),
            "mirrors.cernet.edu.cn" or "mirrors.ustc.edu.cn" => StartsWithAny(path, "/bmclapi/"),
            _ => false
        };
    }

    private static bool StartsWithAny(string path, params string[] prefixes) =>
        prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
}
