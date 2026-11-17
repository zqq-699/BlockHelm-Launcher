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

namespace Launcher.Application.Services;

public sealed class GameLaunchSession
{
    private int exitHandled;

    public GameLaunchSession(
        string instanceDirectory,
        string instanceName,
        Task<LaunchExitResult> exitTask,
        IReadOnlyList<LaunchWarningKind>? warnings = null)
    {
        InstanceDirectory = instanceDirectory;
        InstanceName = instanceName;
        ExitTask = exitTask;
        Warnings = warnings ?? [];
    }

    public string InstanceDirectory { get; }

    public string InstanceName { get; }

    public Task<LaunchExitResult> ExitTask { get; }

    public IReadOnlyList<LaunchWarningKind> Warnings { get; }

    public bool TryMarkExitHandled()
    {
        return Interlocked.Exchange(ref exitHandled, 1) == 0;
    }
}
