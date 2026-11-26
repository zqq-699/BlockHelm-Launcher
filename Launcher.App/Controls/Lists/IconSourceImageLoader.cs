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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.App.Logging;

namespace Launcher.App.Controls;

internal static class IconSourceImageLoader
{
    private const string ComponentPathPrefix = "/BlockHelm_Launcher_x64;component/";
    private const string ComponentUriPrefix = "pack://application:,,,/BlockHelm_Launcher_x64;component";
    private const long MaximumFileCacheBytes = 16L * 1024 * 1024;
    private const int MaximumFileCacheEntries = 1024;

    private static readonly ConcurrentDictionary<string, ImageSource> PackImages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileCacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedFileIcon>> FileImages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CachedFileIcon> FileImageRecency = new();
    private static long cachedFileBytes;

    public static ImageSource? TryLoad(object? source, int pixelWidth = 0, int pixelHeight = 0)
    {
        if (source is ImageSource imageSource)
            return imageSource;

        if (source is not string text || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var uri = CreateIconUri(text.Trim());
            if (uri.IsAbsoluteUri && uri.IsFile)
                return LoadFileIcon(uri.LocalPath, text, pixelWidth, pixelHeight);

            var isPack = uri.IsAbsoluteUri && string.Equals(uri.Scheme, "pack", StringComparison.OrdinalIgnoreCase);
            if (isPack && PackImages.TryGetValue(uri.AbsoluteUri, out var cached))
                return cached;

            var image = new BitmapImage();
            ImageLoadDiagnostics.Observe(image, text);
            image.BeginInit();
            if (isPack)
            {
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            }

            image.UriSource = uri;
            image.EndInit();
            if (image.CanFreeze)
                image.Freeze();
            if (isPack && image.IsFrozen)
                PackImages.TryAdd(uri.AbsoluteUri, image);
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

    private static BitmapImage LoadFileIcon(string path, string source, int pixelWidth, int pixelHeight)
    {
        var file = new FileInfo(path);
        var state = new FileState(file.LastWriteTimeUtc, file.Length);
        var useDisplaySize = pixelWidth > 0 && pixelHeight > 0;
        var key = useDisplaySize ? $"{pixelWidth}x{pixelHeight}:{file.FullName}" : file.FullName;

        lock (FileCacheLock)
        {
            if (FileImages.TryGetValue(key, out var cached))
            {
                if (cached.Value.State == state)
                {
                    FileImageRecency.Remove(cached);
                    FileImageRecency.AddFirst(cached);
                    return cached.Value.Image;
                }

                RemoveCachedFile(cached);
            }
        }

        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        ImageLoadDiagnostics.Observe(image, source);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (useDisplaySize)
            ApplyDecodeSize(image, stream, pixelWidth, pixelHeight);
        image.EndInit();
        image.Freeze();

        CacheFileIcon(key, state, image);
        return image;
    }

    private static void ApplyDecodeSize(BitmapImage image, Stream stream, int pixelWidth, int pixelHeight)
    {
        // Read the frame dimensions without retaining a full-size pixel buffer.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var sourceWidth = frame.PixelWidth;
        var sourceHeight = frame.PixelHeight;
        stream.Position = 0;

        // Match the list template's UniformToFill scaling and preserve the source aspect ratio.
        if (sourceWidth <= pixelWidth || sourceHeight <= pixelHeight)
            return;

        if ((long)pixelWidth * sourceHeight >= (long)pixelHeight * sourceWidth)
            image.DecodePixelWidth = pixelWidth;
        else
            image.DecodePixelHeight = pixelHeight;
    }

    private static void CacheFileIcon(string key, FileState state, BitmapImage image)
    {
        // WPF can use a 32-bit render surface even when the decoded format has fewer bits per pixel.
        var bytes = ((long)image.PixelWidth * Math.Max(32, image.Format.BitsPerPixel) + 7) / 8 * image.PixelHeight;
        if (bytes > MaximumFileCacheBytes)
            return;

        lock (FileCacheLock)
        {
            if (FileImages.TryGetValue(key, out var existing))
                RemoveCachedFile(existing);

            FileImages.Add(key, FileImageRecency.AddFirst(new CachedFileIcon(key, state, image, bytes)));
            cachedFileBytes += bytes;

            while (cachedFileBytes > MaximumFileCacheBytes || FileImages.Count > MaximumFileCacheEntries)
                RemoveCachedFile(FileImageRecency.Last!);
        }
    }

    private static void RemoveCachedFile(LinkedListNode<CachedFileIcon> node)
    {
        FileImageRecency.Remove(node);
        FileImages.Remove(node.Value.Key);
        cachedFileBytes -= node.Value.Bytes;
    }

    private readonly record struct FileState(DateTime LastWriteTimeUtc, long Length);

    private sealed record CachedFileIcon(string Key, FileState State, BitmapImage Image, long Bytes);
}
