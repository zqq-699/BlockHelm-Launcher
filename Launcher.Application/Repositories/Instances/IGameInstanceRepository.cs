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

using Launcher.Domain.Models;

namespace Launcher.Application.Repositories;

public interface IGameInstanceRepository
{
    bool CanReuseSettingsSnapshot(GameInstance previous, GameInstance current, bool allowOlderRead = false) => ReferenceEquals(previous, current);

    Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameInstance>> GetAllAsync(string minecraftDirectory, CancellationToken cancellationToken = default);

    Task<InstanceCatalogSnapshot> ReadCatalogAsync(string minecraftDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates settings for the supplied instances without deleting settings for omitted instances.
    /// Instance removal must use the explicit deletion transaction.
    /// </summary>
    Task SaveAllAsync(IReadOnlyCollection<GameInstance> instances, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates settings under the explicitly captured Minecraft root. Callers that perform a
    /// multi-stage scan must use this overload so a concurrent settings-root change cannot redirect writes.
    /// </summary>
    Task SaveAllAsync(
        string minecraftDirectory,
        IReadOnlyCollection<GameInstance> instances,
        CancellationToken cancellationToken = default)
        => SaveAllAsync(instances, cancellationToken);

    Task UpdateInstanceAsync(
        string minecraftDirectory,
        GameInstance instance,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstalledGameVersion>> DiscoverInstalledVersionsAsync(
        string minecraftDirectory,
        CancellationToken cancellationToken = default);


    string GetVersionDirectory(string minecraftDirectory, string versionName);

    bool IsInstanceInstalled(GameInstance instance, string minecraftDirectory);

    Task<bool> IsInstalledInstanceDirectoryAsync(string minecraftDirectory, string instanceDirectory,
        CancellationToken cancellationToken = default);

    void CreateInstanceDirectories(string directory);


    Task<string> StageVersionForDeletionAsync(
        string minecraftDirectory,
        string versionName,
        string expectedInstanceDirectory,
        CancellationToken cancellationToken = default);

    Task<string> StageVersionForDeletionAsync(string minecraftDirectory, GameInstance instance, CancellationToken cancellationToken = default)
        => StageVersionForDeletionAsync(minecraftDirectory, instance.VersionName, instance.InstanceDirectory, cancellationToken);

    Task<bool> TryDeleteStagedVersionDirectoryAsync(
        string minecraftDirectory,
        string stagedDirectory,
        CancellationToken cancellationToken = default);

    Task CleanupStagedVersionDirectoriesAsync(
        string minecraftDirectory,
        CancellationToken cancellationToken = default);

    Task RenameVersionAsync(
        string minecraftDirectory,
        GameInstance instance,
        string newVersionName,
        string? newIconSource,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task RecoverPendingVersionRenamesAsync(
        string minecraftDirectory,
        CancellationToken cancellationToken = default);
}
