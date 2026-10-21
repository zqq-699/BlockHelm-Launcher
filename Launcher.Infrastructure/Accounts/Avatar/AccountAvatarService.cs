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

using Launcher.Infrastructure;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.Infrastructure.Accounts;

/// <summary>
/// 从 Minecraft 皮肤生成带第二层覆盖的头像，并按账户和内容版本维护本地缓存。
/// </summary>
internal sealed class AccountAvatarService
{
    // 输出尺寸高于皮肤面部原始像素，仍使用最近邻采样保持 Minecraft 像素风格。
    private const int AvatarSize = 576;
    private const int SkinHeadSize = 8;
    private const int AvatarFaceSize = AvatarSize * 8 / 9;
    private const int AvatarFaceOffset = (AvatarSize - AvatarFaceSize) / 2;
    private const int AvatarOverlayShadowOffset = AvatarSize / 48;
    private const int AvatarOverlayShadowBlurRadius = AvatarSize / 24;
    private const byte AvatarOverlayShadowAlpha = 96;
    private const string AvatarCacheVersion = "v6";

    private readonly HttpClient httpClient;
    private readonly string avatarDirectory;

    public AccountAvatarService(HttpClient httpClient, LauncherPathProvider pathProvider)
        : this(
            httpClient,
            Path.Combine(pathProvider.DefaultAccountDataDirectory, "microsoft", "avatars"))
    {
    }

    internal AccountAvatarService(HttpClient httpClient, string avatarDirectory)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.avatarDirectory = avatarDirectory;
        Directory.CreateDirectory(this.avatarDirectory);
    }

    public async Task<string?> GetOrCreateAvatarSourceAsync(
        string uuid,
        string? skinUrl,
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool useRemoteFallback = true)
    {
        // cacheBust 用于明确要求刷新远端皮肤；正常加载优先复用最近缓存降低登录页网络开销。
        if (string.IsNullOrWhiteSpace(uuid))
            return null;

        var cachedAvatarPath = GetLatestCachedAvatarPath(uuid);
        if (!forceRefresh && cachedAvatarPath is not null)
            return new Uri(cachedAvatarPath).AbsoluteUri;

        if (!forceRefresh)
            return useRemoteFallback ? GetFallbackAvatarSource(uuid, cacheBust: false) : null;

        if (string.IsNullOrWhiteSpace(skinUrl))
            return useRemoteFallback ? GetFallbackAvatarSource(uuid, forceRefresh) : null;

        try
        {
            var avatarPath = CreateAvatarPath(uuid);
            var skinBytes = await ReadSkinBytesAsync(skinUrl, cancellationToken);
            var skin = LoadBitmap(skinBytes);
            var avatar = CreateAvatarBitmap(skin);
            SavePng(avatar, avatarPath);
            DeleteStaleAvatars(uuid, avatarPath);
            return new Uri(avatarPath).AbsoluteUri;
        }
        catch
        {
            if (cachedAvatarPath is not null)
                return new Uri(cachedAvatarPath).AbsoluteUri;

            return useRemoteFallback ? GetFallbackAvatarSource(uuid, forceRefresh) : null;
        }
    }

    private async Task<byte[]> ReadSkinBytesAsync(
        string skinSource,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(skinSource, UriKind.Absolute, out var uri) && uri.IsFile)
            return await File.ReadAllBytesAsync(uri.LocalPath, cancellationToken);

        if (File.Exists(skinSource))
            return await File.ReadAllBytesAsync(skinSource, cancellationToken);

        return await httpClient.GetByteArrayAsync(skinSource, cancellationToken);
    }

    public void DeleteAvatar(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid) || !Directory.Exists(avatarDirectory))
            return;

        foreach (var avatarPath in EnumerateAvatarFiles(uuid))
            TryDeleteFile(avatarPath);
    }

    private static BitmapSource LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateAvatarBitmap(BitmapSource skin)
    {
        // 先绘制基础脸部，再叠加帽子层和阴影；透明帽子像素不能覆盖基础皮肤。
        var source = EnsureBgra32(skin);
        var face = ReadPixels(source, 8, 8, SkinHeadSize, SkinHeadSize);
        var overlay = source.PixelWidth >= 48 && source.PixelHeight >= 16
            ? ReadPixels(source, 40, 8, SkinHeadSize, SkinHeadSize)
            : null;
        var hasOverlay = overlay is not null && HasVisiblePixels(overlay);

        var output = new byte[AvatarSize * AvatarSize * 4];
        if (hasOverlay)
        {
            DrawSkinPart(output, face, AvatarFaceOffset, AvatarFaceOffset, AvatarFaceSize, blend: false);
            DrawOverlayShadow(output, overlay!);
            DrawSkinPart(output, overlay!, 0, 0, AvatarSize, blend: true);
        }
        else
        {
            DrawSkinPart(output, face, 0, 0, AvatarSize, blend: false);
        }

        var avatar = BitmapSource.Create(
            AvatarSize,
            AvatarSize,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            output,
            AvatarSize * 4);
        avatar.Freeze();
        return avatar;
    }

    private static void DrawOverlayShadow(byte[] target, byte[] overlay)
    {
        // 阴影由覆盖层 alpha 蒙版模糊得到，只增强层次，不改变皮肤自身颜色数据。
        var mask = new int[AvatarSize * AvatarSize];
        for (var y = 0; y < AvatarSize - AvatarOverlayShadowOffset; y++)
        {
            var sourceY = y * SkinHeadSize / AvatarSize;
            var outputY = y + AvatarOverlayShadowOffset;
            for (var x = 0; x < AvatarSize - AvatarOverlayShadowOffset; x++)
            {
                var sourceX = x * SkinHeadSize / AvatarSize;
                var sourceIndex = GetPixelIndex(sourceX, sourceY);
                var sourceAlpha = overlay[sourceIndex + 3];
                if (sourceAlpha == 0)
                    continue;

                var outputX = x + AvatarOverlayShadowOffset;
                mask[outputY * AvatarSize + outputX] = sourceAlpha * AvatarOverlayShadowAlpha / byte.MaxValue;
            }
        }

        var blurredMask = BlurMask(mask, AvatarOverlayShadowBlurRadius);
        for (var index = 0; index < blurredMask.Length; index++)
        {
            var shadowAlpha = blurredMask[index];
            if (shadowAlpha == 0)
                continue;

            BlendPixel(target, index * 4, 0, 0, 0, shadowAlpha);
        }
    }

    private static int[] BlurMask(int[] mask, int radius)
    {
        // 使用可分离的水平/垂直盒式模糊，固定小图上比逐像素二维卷积更简单稳定。
        var horizontal = new int[mask.Length];
        var blurred = new int[mask.Length];
        var diameter = radius * 2 + 1;

        for (var y = 0; y < AvatarSize; y++)
        {
            var rowStart = y * AvatarSize;
            var sum = 0;
            for (var x = -radius; x <= radius; x++)
                sum += mask[rowStart + ClampToAvatar(x)];

            for (var x = 0; x < AvatarSize; x++)
            {
                horizontal[rowStart + x] = sum / diameter;
                sum -= mask[rowStart + ClampToAvatar(x - radius)];
                sum += mask[rowStart + ClampToAvatar(x + radius + 1)];
            }
        }

        for (var x = 0; x < AvatarSize; x++)
        {
            var sum = 0;
            for (var y = -radius; y <= radius; y++)
                sum += horizontal[ClampToAvatar(y) * AvatarSize + x];

            for (var y = 0; y < AvatarSize; y++)
            {
                blurred[y * AvatarSize + x] = sum / diameter;
                sum -= horizontal[ClampToAvatar(y - radius) * AvatarSize + x];
                sum += horizontal[ClampToAvatar(y + radius + 1) * AvatarSize + x];
            }
        }

        return blurred;
    }

    private static int ClampToAvatar(int value)
    {
        return Math.Clamp(value, 0, AvatarSize - 1);
    }

    private static void DrawSkinPart(
        byte[] target,
        byte[] source,
        int outputOffsetX,
        int outputOffsetY,
        int outputSize,
        bool blend)
    {
        // 源皮肤可能是旧 64x32 格式，所有坐标读取前都经过尺寸边界保护。
        for (var y = 0; y < outputSize; y++)
        {
            var sourceY = y * SkinHeadSize / outputSize;
            var outputY = outputOffsetY + y;
            for (var x = 0; x < outputSize; x++)
            {
                var sourceX = x * SkinHeadSize / outputSize;
                var sourceIndex = GetPixelIndex(sourceX, sourceY);
                var outputX = outputOffsetX + x;
                var outputIndex = (outputY * AvatarSize + outputX) * 4;

                if (blend)
                {
                    BlendPixel(target, outputIndex, source, sourceIndex);
                    continue;
                }

                target[outputIndex] = source[sourceIndex];
                target[outputIndex + 1] = source[sourceIndex + 1];
                target[outputIndex + 2] = source[sourceIndex + 2];
                target[outputIndex + 3] = source[sourceIndex + 3];
            }
        }
    }

    private static bool HasVisiblePixels(byte[] pixels)
    {
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
                return true;
        }

        return false;
    }

    private static int GetPixelIndex(int x, int y)
    {
        return (y * SkinHeadSize + x) * 4;
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static byte[] ReadPixels(BitmapSource source, int x, int y, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        source.CopyPixels(new Int32Rect(x, y, width, height), pixels, width * 4, 0);
        return pixels;
    }

    private static void BlendPixel(byte[] target, int targetIndex, byte[] overlay, int overlayIndex)
    {
        // 使用标准 alpha 合成而非直接替换，保留半透明第二层皮肤的视觉效果。
        var overlayAlpha = overlay[overlayIndex + 3];
        BlendPixel(
            target,
            targetIndex,
            overlay[overlayIndex],
            overlay[overlayIndex + 1],
            overlay[overlayIndex + 2],
            overlayAlpha);
    }

    private static void BlendPixel(byte[] target, int targetIndex, byte blue, byte green, byte red, int alpha)
    {
        if (alpha == 0)
            return;

        if (alpha == byte.MaxValue)
        {
            target[targetIndex] = blue;
            target[targetIndex + 1] = green;
            target[targetIndex + 2] = red;
            target[targetIndex + 3] = byte.MaxValue;
            return;
        }

        var inverseAlpha = byte.MaxValue - alpha;
        var targetAlpha = target[targetIndex + 3];
        var outputAlpha = alpha + targetAlpha * inverseAlpha / byte.MaxValue;
        if (outputAlpha == 0)
            return;

        target[targetIndex] = BlendColor(blue, alpha, target[targetIndex], targetAlpha, inverseAlpha, outputAlpha);
        target[targetIndex + 1] = BlendColor(green, alpha, target[targetIndex + 1], targetAlpha, inverseAlpha, outputAlpha);
        target[targetIndex + 2] = BlendColor(red, alpha, target[targetIndex + 2], targetAlpha, inverseAlpha, outputAlpha);
        target[targetIndex + 3] = (byte)outputAlpha;
    }

    private static byte BlendColor(
        byte overlayColor,
        int overlayAlpha,
        byte targetColor,
        byte targetAlpha,
        int inverseAlpha,
        int outputAlpha)
    {
        return (byte)((overlayColor * overlayAlpha + targetColor * targetAlpha * inverseAlpha / byte.MaxValue) / outputAlpha);
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private string CreateAvatarPath(string uuid)
    {
        return Path.Combine(avatarDirectory, $"{uuid}-{AvatarSize}-{AvatarCacheVersion}-{Guid.NewGuid():N}.png");
    }

    private string? GetLatestCachedAvatarPath(string uuid)
    {
        return EnumerateCurrentAvatarFiles(uuid)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private IEnumerable<string> EnumerateCurrentAvatarFiles(string uuid)
    {
        if (!Directory.Exists(avatarDirectory))
            return [];

        return Directory.EnumerateFiles(avatarDirectory, $"{uuid}-{AvatarSize}-{AvatarCacheVersion}-*.png");
    }

    private IEnumerable<string> EnumerateAvatarFiles(string uuid)
    {
        if (!Directory.Exists(avatarDirectory))
            return [];

        return Directory.EnumerateFiles(avatarDirectory, $"{uuid}*.png");
    }

    private void DeleteStaleAvatars(string uuid, string currentAvatarPath)
    {
        // 同账户只保留当前内容版本，清理范围由文件名前缀约束，避免删除其他账户缓存。
        foreach (var avatarPath in EnumerateAvatarFiles(uuid))
        {
            if (string.Equals(avatarPath, currentAvatarPath, StringComparison.OrdinalIgnoreCase))
                continue;

            TryDeleteFile(avatarPath);
        }
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

    private static string GetFallbackAvatarSource(string uuid, bool cacheBust)
    {
        var uri = $"https://crafatar.com/avatars/{uuid}?size={AvatarSize}&overlay";
        return cacheBust
            ? $"{uri}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : uri;
    }
}
