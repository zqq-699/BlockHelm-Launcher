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

using System.Collections.Concurrent;
using System.IO;
using Launcher.App.Logging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.App.Controls;

internal static class IconSourceImageLoader
{
    private const string ComponentPathPrefix = "/BlockHelm_Launcher_x64;component/";
    private const string ComponentUriPrefix = "pack://application:,,,/BlockHelm_Launcher_x64;component";

    private static readonly ConcurrentDictionary<string, CachedImageSource> CachedImagesByKey = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryLoad(object? source)
    {
        if (source is ImageSource imageSource)
            return imageSource;

        if (source is not string text || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var uri = CreateIconUri(text.Trim());
            if (TryGetCachedImage(uri, out var cachedImage))
                return cachedImage;

            var image = new BitmapImage();
            ImageLoadDiagnostics.Observe(image, text);
            image.BeginInit();
            if (ShouldLoadImmediately(uri))
            {
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            }

            image.UriSource = uri;
            image.EndInit();
            if (image.CanFreeze)
                image.Freeze();
            CacheImage(uri, image);
            return image;
        }
        catch (Exception exception)
        {
            ImageLoadDiagnostics.Failed(text, exception);
            return null;
        }
    }

    private static Uri CreateIconUri(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var absoluteUri))
            return absoluteUri;

        if (source.StartsWith(ComponentPathPrefix, StringComparison.OrdinalIgnoreCase))
            return new Uri($"pack://application:,,,{source}", UriKind.Absolute);

        if (source.StartsWith("/", StringComparison.Ordinal))
            return new Uri($"{ComponentUriPrefix}{source}", UriKind.Absolute);

        if (Uri.TryCreate(source, UriKind.Relative, out var relativeUri))
            return relativeUri;

        return new Uri(source, UriKind.RelativeOrAbsolute);
    }

    private static bool ShouldLoadImmediately(Uri uri)
    {
        return uri.IsFile
            || string.Equals(uri.Scheme, "pack", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCachedImage(Uri uri, out ImageSource? imageSource)
    {
        imageSource = null;
        var cacheKey = TryCreateCacheKey(uri);
        if (string.IsNullOrWhiteSpace(cacheKey))
            return false;

        if (!CachedImagesByKey.TryGetValue(cacheKey, out var cached))
            return false;

        if (uri.IsFile && !HasMatchingFileState(uri, cached))
        {
            CachedImagesByKey.TryRemove(cacheKey, out _);
            return false;
        }

        imageSource = cached.ImageSource;
        return true;
    }

    private static void CacheImage(Uri uri, ImageSource imageSource)
    {
        var cacheKey = TryCreateCacheKey(uri);
        if (string.IsNullOrWhiteSpace(cacheKey))
            return;

        if (uri.IsFile)
        {
            var localPath = uri.LocalPath;
            if (!File.Exists(localPath))
                return;

            var fileInfo = new FileInfo(localPath);
            CachedImagesByKey[cacheKey] = new CachedImageSource(
                imageSource,
                fileInfo.LastWriteTimeUtc.Ticks,
                fileInfo.Length);
            return;
        }

        CachedImagesByKey[cacheKey] = new CachedImageSource(imageSource, null, null);
    }

    private static string? TryCreateCacheKey(Uri uri)
    {
        if (uri.IsFile)
            return uri.LocalPath;

        if (string.Equals(uri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;

        return null;
    }

    private static bool HasMatchingFileState(Uri uri, CachedImageSource cached)
    {
        var localPath = uri.LocalPath;
        if (!File.Exists(localPath))
            return false;

        var fileInfo = new FileInfo(localPath);
        return cached.LastWriteTimeUtcTicks == fileInfo.LastWriteTimeUtc.Ticks
            && cached.FileLength == fileInfo.Length;
    }

    private sealed record CachedImageSource(
        ImageSource ImageSource,
        long? LastWriteTimeUtcTicks,
        long? FileLength);
}
