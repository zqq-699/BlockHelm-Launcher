/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal static class DownloadUriLogSanitizer
{
    public static string Sanitize(string value) => LogUriSanitizer.Sanitize(value);

    public static string Sanitize(Uri uri) => LogUriSanitizer.Sanitize(uri);
}
