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
using System.Windows;
using System.Windows.Media.Imaging;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

internal static class RemoteIconImageEncoder
{
    private const int ThumbnailSize = IResourceThumbnailService.ThumbnailSize;
    private const string LegacyOriginalSuffix = ".original.png";
    private const string UrlHashQuery = "/tEXt/{str=BlockHelmIconUrlHash}";

    public static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();

            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("Remote local mod icon exceeds the maximum allowed size.");

            buffer.Write(chunk, 0, read);
        }
    }

    public static void SaveAsPng(ReadOnlyMemory<byte> imageBytes, string path, string? urlHash = null)
    {
        using var source = new MemoryStream(imageBytes.ToArray(), writable: false);
        SaveAsPng(source, path, urlHash);
    }

    public static void SaveAsPng(Stream source, string path, string? urlHash) =>
        WritePng(ResizeThumbnail(source, ReadFrame(source)), path, urlHash);

    public static bool HasUrlHash(string path, string? urlHash = null)
    {
        try
        {
            using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadUrlHash(ReadFrame(source)) is { Length: 64 } storedHash
                && (urlHash is null || string.Equals(storedHash, urlHash, StringComparison.Ordinal));
        }
        catch (Exception exception) when (IsCacheError(exception))
        {
            return false;
        }
    }

    public static async Task CompactCacheAsync(
        string cacheDirectory,
        SemaphoreSlim writeLock,
        ILogger logger,
        Action<long>? onBytesSaved = null)
    {
        var compacted = 0;
        var completed = true;
        long bytesSaved = 0;
        try
        {
            var directory = new DirectoryInfo(cacheDirectory);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return;
            var markerPath = Path.Combine(cacheDirectory, $"thumbnails-{ThumbnailSize}.migrated");
            if (File.Exists(markerPath))
                return;

            foreach (var file in directory.EnumerateFiles("*.png"))
            {
                // Only migrate files named with the cache's SHA-256 key.
                var isLegacyOriginal = file.Name.EndsWith(LegacyOriginalSuffix, StringComparison.OrdinalIgnoreCase);
                var key = isLegacyOriginal ? file.Name[..^LegacyOriginalSuffix.Length] : Path.GetFileNameWithoutExtension(file.Name);
                if (key.Length != 64 || !key.All(Uri.IsHexDigit))
                    continue;

                await writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (isLegacyOriginal)
                    {
                        var thumbnail = new FileInfo(Path.Combine(cacheDirectory, $"{key}.png"));
                        if (MergeLegacyOriginal(file, thumbnail, AddSavings))
                            compacted++;
                    }
                    else if (CompactThumbnail(file) is { } saved)
                    {
                        compacted++;
                        AddSavings(saved);
                    }
                }
                catch (Exception exception) when (IsCacheError(exception))
                {
                    completed = false;
                    logger.LogDebug(exception, "Failed to compact a cached remote icon.");
                }
                finally
                {
                    writeLock.Release();
                }
            }

            if (completed)
                File.WriteAllText(markerPath, string.Empty);
        }
        catch (Exception exception) when (IsCacheError(exception))
        {
            logger.LogWarning(exception, "Failed to scan the remote icon cache for oversized thumbnails.");
        }

        if (compacted > 0)
            logger.LogDebug("Cached remote thumbnails compacted. Count={Count} BytesSaved={BytesSaved}", compacted, bytesSaved);

        void AddSavings(long saved)
        {
            bytesSaved += saved;
            onBytesSaved?.Invoke(saved);
        }
    }

    private static bool MergeLegacyOriginal(FileInfo original, FileInfo thumbnail, Action<long> addSavings)
    {
        original.Refresh();
        thumbnail.Refresh();
        if (!original.Exists || (original.Attributes & FileAttributes.ReparsePoint) != 0
            || (thumbnail.Exists && (thumbnail.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            return false;
        }

        var originalLength = original.Length;
        // Keep a newer shared-cache image if the two copies have different dates.
        if (!thumbnail.Exists || thumbnail.LastWriteTimeUtc <= original.LastWriteTimeUtc)
        {
            if (CompactThumbnail(original, thumbnail) is not { } saved)
                return false;

            addSavings(saved);
        }

        original.Delete();
        addSavings(originalLength);
        return true;
    }

    private static long? CompactThumbnail(FileInfo file, FileInfo? destination = null)
    {
        file.Refresh();
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return null;

        var originalLength = file.Length;
        var lastWriteTime = file.LastWriteTimeUtc;
        string? tempPath = null;
        try
        {
            using (var source = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var frame = ReadFrame(source);
                var thumbnail = ResizeThumbnail(source, frame);
                if (destination is null && ReferenceEquals(thumbnail, frame))
                    return null;

                tempPath = $"{file.FullName}.{Guid.NewGuid():N}.tmp";
                WritePng(thumbnail, tempPath, ReadUrlHash(frame));
            }

            File.SetLastWriteTimeUtc(tempPath, lastWriteTime);
            file.Refresh();
            if (!file.Exists || file.LastWriteTimeUtc != lastWriteTime || file.Length != originalLength)
                return null;

            var replacedLength = destination is null ? originalLength : destination.Exists ? destination.Length : 0;
            var bytesSaved = replacedLength - new FileInfo(tempPath).Length;
            File.Move(tempPath, destination?.FullName ?? file.FullName, overwrite: true);
            return bytesSaved;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static BitmapFrame ReadFrame(Stream source) =>
        BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None)
            .Frames.FirstOrDefault()
        ?? throw new InvalidDataException("Remote icon contains no frames.");

    private static string? ReadUrlHash(BitmapFrame frame) =>
        (frame.Metadata as BitmapMetadata)?.GetQuery(UrlHashQuery) as string;

    private static BitmapSource ResizeThumbnail(Stream source, BitmapFrame frame)
    {
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (Math.Max(width, height) <= ThumbnailSize)
            return frame;

        var edge = Math.Min(width, height);
        // Match the centered square crop used by list icons before reducing the pixel count.
        source.Position = 0;
        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        thumbnail.SourceRect = new Int32Rect((width - edge) / 2, (height - edge) / 2, edge, edge);
        thumbnail.DecodePixelWidth = Math.Min(edge, ThumbnailSize);
        thumbnail.StreamSource = source;
        thumbnail.EndInit();
        return thumbnail;
    }

    private static void WritePng(BitmapSource image, string path, string? urlHash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        BitmapMetadata? metadata = null;
        if (urlHash is not null)
        {
            // Publish URL freshness atomically with the PNG without adding a sidecar file.
            metadata = new BitmapMetadata("png");
            metadata.SetQuery(UrlHashQuery, urlHash);
        }

        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static bool IsCacheError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidDataException or FormatException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException;
}
