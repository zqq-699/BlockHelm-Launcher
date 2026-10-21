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
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Infrastructure.Accounts;

/// <summary>
/// 以内容哈希持久化账户皮肤，兼容历史缓存路径，并维护账户皮肤记录与实际文件的一致性。
/// </summary>
internal sealed partial class AccountSkinCacheService
{
    // 微软与离线账户共用全局皮肤库；仅第三方账户继续使用独立目录。
    private const string SkinCacheVersion = "v1";
    private const string SharedLibraryDirectoryName = "_shared-library";

    private readonly HttpClient httpClient;
    private readonly string skinDirectory;
    private readonly IUserFileDeletionService userFileDeletionService;

    public AccountSkinCacheService(HttpClient httpClient, LauncherPathProvider pathProvider)
        : this(
            httpClient,
            Path.Combine(pathProvider.DefaultAccountDataDirectory, "microsoft", "skins"),
            new UserFileDeletionService())
    {
    }

    internal AccountSkinCacheService(HttpClient httpClient, string skinDirectory)
        : this(httpClient, skinDirectory, new UserFileDeletionService())
    {
    }

    internal AccountSkinCacheService(
        HttpClient httpClient,
        string skinDirectory,
        IUserFileDeletionService userFileDeletionService)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.skinDirectory = skinDirectory;
        this.userFileDeletionService = userFileDeletionService;
        Directory.CreateDirectory(this.skinDirectory);
    }

    public async Task<LauncherSkinRecord?> StoreUploadedSkinAsync(
        string skinFilePath,
        MinecraftSkinModel skinModel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(skinFilePath) || !File.Exists(skinFilePath))
            return null;

        var skinBytes = await File.ReadAllBytesAsync(skinFilePath, cancellationToken);
        var hash = ComputeSkinContentHash(skinBytes);
        var skinPath = CreateSharedLibrarySkinPath(hash, skinModel);
        if (!File.Exists(skinPath))
            await File.WriteAllBytesAsync(skinPath, skinBytes, cancellationToken);
        return CreateRecord(hash, skinModel, new Uri(skinPath).AbsoluteUri);
    }
}
