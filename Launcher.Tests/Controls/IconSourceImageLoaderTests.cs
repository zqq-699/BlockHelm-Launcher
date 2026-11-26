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
 * along with this program. If not, see https://www.gnu.org/licenses/.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.App.Controls;
using Launcher.Tests.Helpers;

namespace Launcher.Tests.Controls;

public sealed class IconSourceImageLoaderTests : TestTempDirectory
{
    [Theory]
    [InlineData(400, 200, 128, 64)]
    [InlineData(200, 400, 64, 128)]
    [InlineData(32, 32, 32, 32)]
    public void ListIconsDecodeToFillTheTargetWithoutChangingAspectRatioOrUpscaling(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        RunOnStaThread(() =>
        {
            var path = CreateIcon("icon.png", sourceWidth, sourceHeight);

            var icon = Assert.IsAssignableFrom<BitmapSource>(IconSourceImageLoader.TryLoad(path, 64, 64));
            var original = Assert.IsAssignableFrom<BitmapSource>(IconSourceImageLoader.TryLoad(path));

            Assert.Equal((expectedWidth, expectedHeight), (icon.PixelWidth, icon.PixelHeight));
            Assert.Equal((sourceWidth, sourceHeight), (original.PixelWidth, original.PixelHeight));
            Assert.True(icon.IsFrozen);
        });
    }

    [Fact]
    public void FileCacheReusesTheMatchingSizeAndInvalidatesAReplacedFile()
    {
        RunOnStaThread(() =>
        {
            var path = CreateIcon("icon.png", 256, 256);
            var small = Assert.IsAssignableFrom<BitmapSource>(IconSourceImageLoader.TryLoad(path, 32, 32));
            var large = Assert.IsAssignableFrom<BitmapSource>(IconSourceImageLoader.TryLoad(path, 64, 64));

            Assert.Equal((32, 64), (small.PixelWidth, large.PixelWidth));
            Assert.Same(small, IconSourceImageLoader.TryLoad(path, 32, 32));

            CreateIcon("icon.png", 256, 128);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var replacement = Assert.IsAssignableFrom<BitmapSource>(IconSourceImageLoader.TryLoad(path, 32, 32));

            Assert.Equal((64, 32), (replacement.PixelWidth, replacement.PixelHeight));
        });
    }

    [Fact]
    public void FileCacheEvictsTheLeastRecentlyUsedImageWhenOverBudget()
    {
        RunOnStaThread(() =>
        {
            var recentPath = CreateIcon("recent.png", 1280, 1280);
            var olderPath = CreateIcon("older.png", 1280, 1280);
            var nextPath = CreateIcon("next.png", 1280, 1280);
            var recent = IconSourceImageLoader.TryLoad(recentPath);
            var older = IconSourceImageLoader.TryLoad(olderPath);
            Assert.NotNull(recent);
            Assert.NotNull(older);
            Assert.Same(recent, IconSourceImageLoader.TryLoad(recentPath));

            Assert.NotNull(IconSourceImageLoader.TryLoad(nextPath));

            Assert.Same(recent, IconSourceImageLoader.TryLoad(recentPath));
            Assert.NotSame(older, IconSourceImageLoader.TryLoad(olderPath));
        });
    }

    private string CreateIcon(string fileName, int width, int height)
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, fileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
