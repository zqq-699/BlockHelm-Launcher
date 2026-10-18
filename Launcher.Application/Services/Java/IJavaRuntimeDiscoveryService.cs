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

public interface IJavaRuntimeDiscoveryService
{
    Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
        string? minecraftDirectory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JavaRuntimeInfo>> SearchAllAsync(
        string? minecraftDirectory,
        CancellationToken cancellationToken = default);

    Task<JavaRuntimeInfo> DiscoverExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken = default);

    async Task<JavaRuntimeInfo> ImportExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        return await DiscoverExecutableAsync(executablePath, cancellationToken);
    }
}
