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

using Microsoft.Extensions.Logging;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Infrastructure.Accounts;

internal sealed partial class AccountSkinCacheService
{
    public async Task<LauncherSkinRecord?> GetOrCreateSkinRecordFromUrlAsync(
        string uuid,
        string? skinUrl,
        MinecraftSkinModel skinModel,
        IReadOnlyList<LauncherSkinRecord> existingSkins,
        bool forceRefresh,
        bool useSharedLibrary,
        CancellationToken cancellationToken)
    {
        // URL 可能变化但内容相同，使用像素内容哈希匹配已有记录而不是只比较地址。
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(skinUrl))
            return null;

        try
        {
            var skinBytes = await DownloadHttpDiagnostics.GetBytesAsync(httpClient, logger, skinUrl, cancellationToken);
            var hash = DownloadDiagnostics.RunStage(logger, "SkinImageDecode", () => ComputeSkinContentHash(skinBytes));
            var skinPath = useSharedLibrary
                ? CreateSharedLibrarySkinPath(hash, skinModel)
                : CreateSkinPath(uuid, hash);
            if (!File.Exists(skinPath) || forceRefresh)
                await DownloadDiagnostics.RunStageAsync(logger, "SkinCacheWrite", () => File.WriteAllBytesAsync(skinPath, skinBytes, cancellationToken));

            if (useSharedLibrary)
                return CreateRecord(hash, skinModel, new Uri(skinPath).AbsoluteUri);

            var existing = FindExisting(existingSkins, hash, skinModel);
            return existing is null
                ? CreateRecord(hash, skinModel, new Uri(skinPath).AbsoluteUri)
                : CopyRecordWithSource(existing, new Uri(skinPath).AbsoluteUri, hash);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Remote skin download, decode or cache write failed. CancellationRequested={CancellationRequested}", cancellationToken.IsCancellationRequested);
            return null;
        }
    }

    public async Task<LauncherSkinRecord> ImportSkinAsync(
        LauncherAccount account,
        string skinFilePath,
        MinecraftSkinModel skinModel,
        CancellationToken cancellationToken)
    {
        // 导入先完整读取，再写入共享库；源文件始终由用户保留。
        var skinBytes = await File.ReadAllBytesAsync(skinFilePath, cancellationToken);
        var hash = ComputeSkinContentHash(skinBytes);
        var skinPath = CreateLibrarySkinPath(account, hash, skinModel);
        if (!File.Exists(skinPath))
            await File.WriteAllBytesAsync(skinPath, skinBytes, cancellationToken);

        // 全局素材拥有稳定的内容身份，不复用某个账户的历史 Id；第三方账户仍保留
        // 其独立皮肤记录身份。
        var existing = account.IsThirdParty
            ? FindExisting(account.SkinLibrary, hash, skinModel)
            : null;
        return existing is null
            ? CreateRecord(hash, skinModel, new Uri(skinPath).AbsoluteUri)
            : CopyRecordWithSource(existing, new Uri(skinPath).AbsoluteUri, hash);
    }

    public Task DeleteSkinAsync(
        LauncherAccount account,
        LauncherSkinRecord skin,
        CancellationToken cancellationToken)
    {
        // 仅删除确认位于对应皮肤库目录内的文件，外部来源路径绝不删除。
        var libraryDirectory = Path.GetFullPath(GetLibrarySkinDirectory(account));
        if (!Directory.Exists(libraryDirectory))
            return Task.CompletedTask;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourcePath = ResolveSkinSourcePath(skin.Source);
        if (sourcePath is not null)
        {
            var fullSourcePath = Path.GetFullPath(sourcePath);
            if (IsPathInDirectory(fullSourcePath, libraryDirectory) && File.Exists(fullSourcePath))
                candidates.Add(fullSourcePath);
        }

        if (!string.IsNullOrWhiteSpace(skin.ContentHash))
        {
            foreach (var skinPath in Directory.EnumerateFiles(libraryDirectory, "*.png"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (string.Equals(
                        ComputeSkinContentHash(File.ReadAllBytes(skinPath)),
                        skin.ContentHash,
                        StringComparison.OrdinalIgnoreCase)
                        && (TryParseSkinModel(skinPath) ?? MinecraftSkinModel.Classic) == skin.SkinModel)
                    {
                        candidates.Add(Path.GetFullPath(skinPath));
                    }
                }
                catch
                {
                }
            }
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            userFileDeletionService.DeleteFile(candidate);
        }

        return Task.CompletedTask;
    }
}
