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

namespace Launcher.Application.Services;

public interface IGameInstanceService
{
    bool CanReuseSettingsSnapshot(GameInstance previous, GameInstance current, bool allowOlderRead = false) => ReferenceEquals(previous, current);

    Task<IReadOnlyList<GameInstance>> GetStoredInstancesAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default);
    Task<GameInstance?> GetDefaultInstanceAsync(CancellationToken cancellationToken = default);
    Task<GameInstance> CreateInstanceAsync(
        string minecraftVersion,
        LoaderKind loader,
        string? loaderVersion,
        string? name,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        bool installFabricApi = true,
        string? fabricApiVersionId = null,
        string? quiltStandardLibraryVersionId = null);
    Task SaveInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default);
    Task<GameInstance> RenameInstanceAsync(string instanceDirectory, string? newName, string? newIconSource, CancellationToken cancellationToken = default);
    Task<GameInstance> RenameInstanceAsync(GameInstance instance, string? newName, string? newIconSource, CancellationToken cancellationToken = default)
        => RenameInstanceAsync(instance.InstanceDirectory, newName, newIconSource, cancellationToken);
    Task<bool> SetDefaultInstanceAsync(string instanceDirectory, CancellationToken cancellationToken = default);
    Task<bool> DeleteInstanceAsync(string instanceDirectory, CancellationToken cancellationToken = default);
    Task<bool> DeleteInstanceAsync(GameInstance instance, CancellationToken cancellationToken = default)
        => DeleteInstanceAsync(instance.InstanceDirectory, cancellationToken);
}
