/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Security.Cryptography;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Tests.Infrastructure.FileSystem;

public sealed class LocalFileFingerprintServiceTests : TestTempDirectory
{
    [Fact]
    public async Task EnabledAndDisabledAliasesDoNotShareFingerprintWhenBothExist()
    {
        Directory.CreateDirectory(TempRoot);
        var enabledPath = Path.Combine(TempRoot, "example.jar");
        var disabledPath = enabledPath + ".disabled";
        var enabledContent = new byte[] { 1, 2, 3, 4 };
        var disabledContent = new byte[] { 5, 6, 7, 8 };
        await File.WriteAllBytesAsync(enabledPath, enabledContent);
        await File.WriteAllBytesAsync(disabledPath, disabledContent);
        var sharedWriteTime = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(enabledPath, sharedWriteTime);
        File.SetLastWriteTimeUtc(disabledPath, sharedWriteTime);
        var service = new LocalFileFingerprintService();

        var enabled = await service.GetFingerprintAsync(enabledPath);
        var disabled = await service.GetFingerprintAsync(disabledPath);

        Assert.Equal(
            Convert.ToHexString(SHA1.HashData(enabledContent)).ToLowerInvariant(),
            enabled.Sha1);
        Assert.Equal(
            Convert.ToHexString(SHA1.HashData(disabledContent)).ToLowerInvariant(),
            disabled.Sha1);
        Assert.NotEqual(enabled.Sha1, disabled.Sha1);
    }
}
