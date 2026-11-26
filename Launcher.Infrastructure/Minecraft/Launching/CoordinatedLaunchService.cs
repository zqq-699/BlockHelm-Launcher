/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class CoordinatedLaunchService(ILaunchService inner, InstanceActivityCoordinator activity) : ILaunchService
{
    public async Task<GameLaunchSession> LaunchAsync(GameInstance instance, LauncherAccount account, LauncherSettings settings,
        IProgress<LauncherProgress>? progress, LaunchRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var lease = activity.EnterLaunch(instance.InstanceDirectory);
        try
        {
            var session = await inner.LaunchAsync(instance, account, settings, progress, options, cancellationToken).ConfigureAwait(false);
            _ = session.ExitTask.ContinueWith(_ => lease.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return session;
        }
        catch { lease.Dispose(); throw; }
    }
}
