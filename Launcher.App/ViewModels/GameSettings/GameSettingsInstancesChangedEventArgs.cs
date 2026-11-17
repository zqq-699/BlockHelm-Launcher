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

namespace Launcher.App.ViewModels.GameSettings;

public sealed class GameSettingsInstancesChangedEventArgs : EventArgs
{
    private GameSettingsInstancesChangedEventArgs(
        GameSettingsInstancesChangedKind kind,
        GameInstance? updatedInstance,
        string? deletedInstanceDirectory)
    {
        Kind = kind;
        UpdatedInstance = updatedInstance;
        DeletedInstanceDirectory = deletedInstanceDirectory;
    }

    public GameSettingsInstancesChangedKind Kind { get; }

    public string? PreviousInstanceDirectory { get; private init; }

    public GameInstance? UpdatedInstance { get; }

    public string? DeletedInstanceDirectory { get; }

    public static GameSettingsInstancesChangedEventArgs Updated(GameInstance instance, string? previousInstanceDirectory = null)
    {
        return new GameSettingsInstancesChangedEventArgs(
            GameSettingsInstancesChangedKind.Updated,
            instance,
            null) { PreviousInstanceDirectory = previousInstanceDirectory };
    }

    public static GameSettingsInstancesChangedEventArgs Deleted(string instanceDirectory)
    {
        return new GameSettingsInstancesChangedEventArgs(
            GameSettingsInstancesChangedKind.Deleted,
            null,
            instanceDirectory);
    }

    public static GameSettingsInstancesChangedEventArgs ReloadRequired()
    {
        return new GameSettingsInstancesChangedEventArgs(
            GameSettingsInstancesChangedKind.ReloadRequired,
            null,
            null);
    }
}
