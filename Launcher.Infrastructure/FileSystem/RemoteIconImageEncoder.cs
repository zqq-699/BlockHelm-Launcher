/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.FileSystem;

internal static class RemoteIconImageEncoder
{
    private const int ThumbnailSize = IResourceThumbnailService.ThumbnailSize;

    public static async Task<byte[]> ReadLimitedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();

            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("Remote icon exceeds the maximum allowed size.");

            buffer.Write(chunk, 0, read);
        }
    }

    public static (string ContentHash, byte[] Png) EncodeThumbnail(byte[] imageBytes)
    {
        using var source = new MemoryStream(imageBytes, writable: false);
        var frame = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None)
            .Frames.FirstOrDefault()
            ?? throw new InvalidDataException("Remote icon contains no frames.");
        var thumbnail = ResizeThumbnail(source, frame);
        var pixels = new FormatConvertedBitmap(thumbnail, PixelFormats.Pbgra32, null, 0);
        var stride = pixels.PixelWidth * 4;
        var identity = new byte[8 + stride * pixels.PixelHeight];
        BinaryPrimitives.WriteInt32LittleEndian(identity, pixels.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(identity.AsSpan(4), pixels.PixelHeight);
        pixels.CopyPixels(identity, stride, 8);
        var hash = Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();

        // Use the hashed pixels and fixed DPI for the stored image so the result is independent of source metadata.
        var normalized = BitmapSource.Create(
            pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Pbgra32, null,
            identity[8..], stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(normalized, null, null, null));
        using var output = new MemoryStream();
        encoder.Save(output);
        return (hash, output.ToArray());
    }

    private static BitmapSource ResizeThumbnail(Stream source, BitmapFrame frame)
    {
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (Math.Max(width, height) <= ThumbnailSize)
            return frame;

        var edge = Math.Min(width, height);
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
}
