/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Infrastructure.Minecraft;

internal static class DownloadUriLogSanitizer
{
    private const string InvalidUriPlaceholder = "<invalid-uri>";

    public static string Sanitize(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? Sanitize(uri)
            : InvalidUriPlaceholder;
    }

    public static string Sanitize(Uri uri)
    {
        try
        {
            if (uri.Scheme is not ("http" or "https"))
                return InvalidUriPlaceholder;
            return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}/<redacted-path>";
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return InvalidUriPlaceholder;
        }
    }
}
