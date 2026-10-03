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

using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLauncherApplication(this IServiceCollection services)
    {
        services.AddSingleton<IAccountStore, AccountStore>();
        services.AddSingleton<IGameInstallCoordinator, GameInstallCoordinator>();
        services.AddSingleton<IGameInstanceService, GameInstanceService>();
        services.AddSingleton<MinecraftDirectoryManagementService>();
        services.AddSingleton<MinecraftDirectoryStartupInitializationService>();
        services.AddSingleton<MinecraftDirectoryStartupRecoveryService>();
        services.AddSingleton<IInstanceDeletionCleanupService, InstanceDeletionCleanupService>();
        services.AddSingleton<IInstanceRenameRecoveryService, InstanceRenameRecoveryService>();
        services.AddSingleton<ILocalModpackImportService, LocalModpackImportService>();
        services.AddSingleton<IServerModpackDeploymentService, ServerModpackDeploymentService>();
        services.AddSingleton<IResourceDependencyPlanningService, ResourceDependencyPlanningService>();
        services.AddSingleton<IModUpdateService, ModUpdateService>();
        return services;
    }
}
