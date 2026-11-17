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

public enum InstanceDirectoryKind
{
    Mods,
    Saves,
    ResourcePacks,
    ShaderPacks
}

public sealed class InstanceDirectoryChangedEventArgs(
    string changeType,
    string fullPath,
    string? oldFullPath = null) : EventArgs
{
    public string ChangeType { get; } = changeType;
    public string FullPath { get; } = fullPath;
    public string? OldFullPath { get; } = oldFullPath;
}

public interface IInstanceDirectoryWatch : IDisposable
{
    event EventHandler<InstanceDirectoryChangedEventArgs>? Changed;
}

public interface IInstanceDirectoryMonitor
{
    bool IsSameWatchTarget(GameInstance previous, GameInstance current) =>
        string.Equals(previous.InstanceDirectory, current.InstanceDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Observes an existing instance directory without creating or repairing the instance root
    /// or any content subdirectory. A missing instance root produces an inert watch.
    /// </summary>
    IInstanceDirectoryWatch Watch(GameInstance instance, InstanceDirectoryKind directoryKind);
}
