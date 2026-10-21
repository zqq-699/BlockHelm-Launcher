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
using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;

namespace Launcher.Infrastructure.Accounts;

public sealed class AccountSkinLibraryService : IAccountSkinLibraryService
{
    private static readonly HttpClient HttpClient = LauncherHttpUserAgent.Apply(new HttpClient());
    private readonly AccountSkinCacheService skinCacheService;
    private readonly AccountAvatarService avatarService;
    private readonly object sharedLibraryGate = new();
    private IReadOnlyList<LauncherSkinRecord>? sharedSnapshot;

    public AccountSkinLibraryService(
        LauncherPathProvider? pathProvider = null,
        IUserFileDeletionService? userFileDeletionService = null)
        : this(
            HttpClient,
            pathProvider ?? new LauncherPathProvider(),
            userFileDeletionService ?? new UserFileDeletionService())
    {
    }

    internal AccountSkinLibraryService(
        HttpClient httpClient,
        LauncherPathProvider pathProvider)
        : this(httpClient, pathProvider, new UserFileDeletionService())
    {
    }

    internal AccountSkinLibraryService(
        HttpClient httpClient,
        LauncherPathProvider pathProvider,
        IUserFileDeletionService userFileDeletionService)
    {
        LauncherHttpUserAgent.Apply(httpClient);
        skinCacheService = new AccountSkinCacheService(
            httpClient,
            Path.Combine(pathProvider.DefaultAccountDataDirectory, "microsoft", "skins"),
            userFileDeletionService);
        avatarService = new AccountAvatarService(httpClient, pathProvider);
    }

    public IReadOnlyList<LauncherSkinRecord> GetAvailableSkins(LauncherAccount account)
    {
        return account.IsThirdParty
            ? skinCacheService.GetAvailableSkins(account)
            : GetSharedSkins();
    }

    public IReadOnlyList<LauncherSkinRecord> GetSharedSkins()
    {
        lock (sharedLibraryGate)
        {
            sharedSnapshot ??= skinCacheService.GetSharedLibrarySkins().ToArray();
            return sharedSnapshot!;
        }
    }

    public Task MigrateLegacySkinsAsync(
        IReadOnlyList<LauncherAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        lock (sharedLibraryGate)
        {
            foreach (var account in accounts.Where(account => !account.IsThirdParty))
            {
                cancellationToken.ThrowIfCancellationRequested();
                skinCacheService.MigrateAccountSkinsToLibrary(account, cancellationToken);
            }

            sharedSnapshot = null;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, LauncherSkinRecord>> SyncMicrosoftAccountSkinsAsync(
        IReadOnlyList<LauncherAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var synchronized = new Dictionary<string, LauncherSkinRecord>(
            StringComparer.OrdinalIgnoreCase);
        lock (sharedLibraryGate)
        {
            var sharedLibraryChanged = false;
            foreach (var account in accounts.Where(account => account.IsMicrosoft))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activeSkin = FindActiveSkinReference(account);
                if (activeSkin is not null
                    && skinCacheService.CopyExistingSkinIntoLibrary(account, activeSkin) is { } sharedSkin)
                {
                    synchronized[account.Id] = sharedSkin;
                    sharedLibraryChanged |= sharedSnapshot is not null
                        && !ContainsSkin(sharedSnapshot, sharedSkin);
                }
            }

            if (sharedLibraryChanged)
                sharedSnapshot = null;
        }

        return Task.FromResult<IReadOnlyDictionary<string, LauncherSkinRecord>>(synchronized);
    }

    public async Task<LauncherSkinRecord> ImportSkinAsync(
        LauncherAccount account,
        string skinFilePath,
        MinecraftSkinModel skinModel,
        CancellationToken cancellationToken = default)
    {
        var imported = await skinCacheService.ImportSkinAsync(
            account,
            skinFilePath,
            skinModel,
            cancellationToken);
        if (!account.IsThirdParty)
            InvalidateSharedSnapshot();
        return imported;
    }

    public Task<string?> CreateAvatarSourceAsync(
        LauncherAccount account,
        LauncherSkinRecord skin,
        CancellationToken cancellationToken = default)
    {
        return avatarService.GetOrCreateAvatarSourceAsync(
            account.Uuid ?? account.Id,
            skin.Source,
            forceRefresh: true,
            cancellationToken,
            useRemoteFallback: false);
    }

    public async Task DeleteSkinAsync(
        LauncherAccount account,
        LauncherSkinRecord skin,
        CancellationToken cancellationToken = default)
    {
        await skinCacheService.DeleteSkinAsync(account, skin, cancellationToken);
        if (!account.IsThirdParty)
            InvalidateSharedSnapshot();
    }

    private void InvalidateSharedSnapshot()
    {
        lock (sharedLibraryGate)
            sharedSnapshot = null;
    }

    private static LauncherSkinRecord? FindActiveSkinReference(LauncherAccount account) =>
        account.SkinLibrary.FirstOrDefault(skin =>
                string.Equals(skin.Id, account.ActiveSkinId, StringComparison.Ordinal))
            ?? account.SkinLibrary.FirstOrDefault(skin =>
                account.SkinModel == skin.SkinModel
                && string.Equals(skin.Source, account.SkinSource, StringComparison.Ordinal));

    private static bool ContainsSkin(
        IReadOnlyList<LauncherSkinRecord> skins,
        LauncherSkinRecord candidate) =>
        skins.Any(skin =>
            skin.SkinModel == candidate.SkinModel
            && string.Equals(
                skin.ContentHash,
                candidate.ContentHash,
                StringComparison.OrdinalIgnoreCase));
}
