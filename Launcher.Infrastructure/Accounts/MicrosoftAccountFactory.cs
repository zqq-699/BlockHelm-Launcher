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

using CmlLib.Core.Auth.Microsoft.Sessions;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts;

internal sealed class MicrosoftAccountFactory
{
    private readonly AccountAvatarService avatarService;
    private readonly AccountSkinCacheService skinCacheService;
    private readonly IAccountSkinLibraryService skinLibraryService;
    private readonly ILogger logger;

    public MicrosoftAccountFactory(
        AccountAvatarService avatarService,
        AccountSkinCacheService skinCacheService,
        IAccountSkinLibraryService skinLibraryService,
        ILogger? logger = null)
    {
        this.avatarService = avatarService;
        this.skinCacheService = skinCacheService;
        this.skinLibraryService = skinLibraryService;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<LauncherAccount> CreateCachedAccountFromProfileAsync(
        JEProfile profile,
        CancellationToken cancellationToken)
    {
        // 启动枚举只用本地缓存建账户：皮肤材质在 textures 服务器上，直连时可能长时间不返回，
        // 首屏账户列表不能等它。远端外观由后台的资料刷新补齐，本地皮肤记录由账户存储合并回来。
        var uuid = MinecraftAccountHelpers.NormalizeUuid(profile.UUID);
        var avatarSource = await avatarService.GetOrCreateAvatarSourceAsync(
            uuid,
            MinecraftAccountHelpers.GetActiveSkinUrl(profile),
            forceRefresh: false,
            cancellationToken);
        return new LauncherAccount
        {
            Id = $"microsoft-{uuid}",
            DisplayName = profile.Username ?? string.Empty,
            Uuid = uuid,
            AvatarSource = avatarSource,
            Kind = LauncherAccountKind.Microsoft
        };
    }

    public async Task<LauncherAccount> CreateAccountFromProfileAsync(
        JEProfile profile,
        bool forceRefreshAvatar,
        CancellationToken cancellationToken,
        IReadOnlyList<LauncherSkinRecord>? existingSkins = null)
    {
        var uuid = MinecraftAccountHelpers.NormalizeUuid(profile.UUID);
        var skinUrl = MinecraftAccountHelpers.GetActiveSkinUrl(profile);
        var skinModel = MinecraftAccountHelpers.GetActiveSkinModel(profile);
        var avatarSource = await avatarService.GetOrCreateAvatarSourceAsync(
            uuid,
            skinUrl,
            forceRefreshAvatar,
            cancellationToken);
        var skin = skinModel is { } confirmedSkinModel
            ? await skinCacheService.GetOrCreateSkinRecordFromUrlAsync(
                uuid,
                skinUrl,
                confirmedSkinModel,
                existingSkins ?? [],
                forceRefreshAvatar,
                useSharedLibrary: true,
                cancellationToken)
            : null;
        var skins = MergeSkinLibrary(existingSkins ?? [], skin);

        var account = new LauncherAccount
        {
            Id = $"microsoft-{uuid}",
            DisplayName = profile.Username ?? string.Empty,
            Uuid = uuid,
            AvatarSource = avatarSource,
            // Variant 缺失时保持未知，账户存储合并会补回上次已确认的当前皮肤状态。
            SkinSource = skin?.Source,
            SkinModel = skin?.SkinModel,
            SkinLibrary = skins,
            ActiveSkinId = skin?.Id,
            Kind = LauncherAccountKind.Microsoft
        };
        return await TrySyncCurrentSkinAsync(account, cancellationToken);
    }

    public async Task<LauncherAccount> CreateAccountFromProfileAsync(
        MinecraftProfileResponse profile,
        bool forceRefreshAvatar,
        CancellationToken cancellationToken,
        IReadOnlyList<LauncherSkinRecord>? existingSkins = null)
    {
        var uuid = MinecraftAccountHelpers.NormalizeUuid(profile.Id);
        var skinUrl = MinecraftAccountHelpers.GetActiveSkinUrl(profile);
        var skinModel = MinecraftAccountHelpers.GetActiveSkinModel(profile);
        var avatarSource = await avatarService.GetOrCreateAvatarSourceAsync(
            uuid,
            skinUrl,
            forceRefreshAvatar,
            cancellationToken);
        var skin = skinModel is { } confirmedSkinModel
            ? await skinCacheService.GetOrCreateSkinRecordFromUrlAsync(
                uuid,
                skinUrl,
                confirmedSkinModel,
                existingSkins ?? [],
                forceRefreshAvatar,
                useSharedLibrary: true,
                cancellationToken)
            : null;
        var skins = MergeSkinLibrary(existingSkins ?? [], skin);
        var skinSource = skin?.Source;

        var account = new LauncherAccount
        {
            Id = $"microsoft-{uuid}",
            DisplayName = profile.Name ?? string.Empty,
            Uuid = uuid,
            AvatarSource = avatarSource,
            SkinSource = skinSource,
            SkinModel = skin?.SkinModel,
            SkinLibrary = skins,
            ActiveSkinId = skin?.Id,
            Kind = LauncherAccountKind.Microsoft,
            HasFreshProfile = true
        };
        return await TrySyncCurrentSkinAsync(account, cancellationToken);
    }

    private async Task<LauncherAccount> TrySyncCurrentSkinAsync(
        LauncherAccount account,
        CancellationToken cancellationToken)
    {
        try
        {
            var synchronized = await skinLibraryService.SyncMicrosoftAccountSkinsAsync(
                [account],
                cancellationToken);
            return synchronized.TryGetValue(account.Id, out var sharedSkin)
                ? AccountMapper.WithSkinLibrary(
                    account,
                    [sharedSkin],
                    sharedSkin.Id,
                    sharedSkin.Source,
                    sharedSkin.SkinModel)
                : account;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Current Microsoft skin could not be synchronized to the shared library. AccountId={AccountId} ErrorType={ErrorType}",
                account.Id,
                exception.GetType().FullName);
            return account;
        }
    }

    private static List<LauncherSkinRecord> MergeSkinLibrary(
        IReadOnlyList<LauncherSkinRecord> existingSkins,
        LauncherSkinRecord? activeSkin)
    {
        var skins = existingSkins.Select(skin => new LauncherSkinRecord
        {
            Id = skin.Id,
            Source = skin.Source,
            SkinModel = skin.SkinModel,
            ContentHash = skin.ContentHash,
            AddedAtUtc = skin.AddedAtUtc
        }).ToList();

        if (activeSkin is null)
            return skins;

        var index = skins.FindIndex(skin =>
            skin.SkinModel == activeSkin.SkinModel
            && string.Equals(skin.ContentHash, activeSkin.ContentHash, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            skins[index] = activeSkin;
        else
            skins.Add(activeSkin);

        return skins;
    }
}
