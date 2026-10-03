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
using Launcher.Application.Repositories;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Accounts;
using Launcher.Infrastructure.Accounts.ThirdParty;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Modrinth;
using Launcher.Infrastructure.Multiplayer;
using Launcher.Infrastructure.Platform;
using Launcher.Infrastructure.Persistence;
using Launcher.Infrastructure.Resources;
using Launcher.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLauncherInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<LauncherPathProvider>();
        services.AddSingleton<ILauncherBackgroundImageCatalog, LauncherBackgroundImageCatalog>();
        services.AddSingleton<IDownloadSpeedLimitState, DownloadSpeedLimitState>();
        services.AddSingleton<IImportConcurrencyLimiter>(_ => ImportConcurrencyLimiter.Shared);
        services.AddSingleton<IDownloadConcurrencyLimitState>(_ => ImportConcurrencyLimiter.Shared);
        services.AddSingleton<ICustomFileDownloadService, CustomFileDownloadService>();
        services.AddSingleton<ICurseForgeApiKeyResolver, CurseForgeApiKeyResolver>();
        services.AddSingleton<IMcresBhlApiKeyResolver, McresBhlApiKeyResolver>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IMinecraftDirectoryFileSystem, MinecraftDirectoryFileSystem>();
        services.AddSingleton<IMinecraftDirectoryDiscoveryService, MinecraftDirectoryDiscoveryService>();
        services.AddSingleton<IAccountStateService, JsonAccountStateService>();
        services.AddSingleton<IGameInstanceRepository, JsonGameInstanceRepository>();
        services.AddSingleton<IInstanceInstallNameAvailabilityService, InstanceInstallNameAvailabilityService>();
        services.AddSingleton<IInstanceInstallTransactionService, InstanceInstallTransactionService>();
        services.AddSingleton<IInstanceInstallCleanupService, InstanceInstallCleanupService>();
        services.AddSingleton<IGameVersionService, GameVersionService>();
        services.AddSingleton<ILoaderInstallerJavaRequirementResolver, LoaderInstallerJavaRequirementResolver>();
        services.AddSingleton<LoaderInstallerJavaRuntimeResolver>();
        services.AddSingleton<ILoaderProvider, VanillaLoaderProvider>();
        services.AddSingleton<ILoaderProvider, FabricLoaderProvider>();
        services.AddSingleton<ILoaderProvider>(serviceProvider => new ForgeLoaderProvider(
            httpClient: null,
            installerRunner: null,
            finalVersionInstaller: null,
            tempRootDirectory: null,
            serviceProvider.GetService<IDownloadSpeedLimitState>(),
            serviceProvider.GetService<ILogger<ForgeLoaderProvider>>(),
            serviceProvider.GetRequiredService<LoaderInstallerJavaRuntimeResolver>()));
        services.AddSingleton<ILoaderProvider>(serviceProvider => new NeoForgeLoaderProvider(
            httpClient: null,
            installerRunner: null,
            finalVersionInstaller: null,
            tempRootDirectory: null,
            serviceProvider.GetService<IDownloadSpeedLimitState>(),
            serviceProvider.GetService<ILogger<NeoForgeLoaderProvider>>(),
            serviceProvider.GetRequiredService<LoaderInstallerJavaRuntimeResolver>()));
        services.AddSingleton<ILoaderProvider, QuiltLoaderProvider>();
        services.AddSingleton<ILoaderFileManifestContributor>(
            new ResolvedVersionLoaderFileManifestContributor(LoaderKind.Vanilla));
        services.AddSingleton<ILoaderFileManifestContributor>(
            new ResolvedVersionLoaderFileManifestContributor(LoaderKind.Fabric));
        services.AddSingleton<ILoaderFileManifestContributor>(
            new InstallerProfileLoaderFileManifestContributor(LoaderKind.Forge));
        services.AddSingleton<ILoaderFileManifestContributor>(
            new InstallerProfileLoaderFileManifestContributor(LoaderKind.NeoForge));
        services.AddSingleton<ILoaderFileManifestContributor>(
            new ResolvedVersionLoaderFileManifestContributor(LoaderKind.Quilt));
        services.AddSingleton<IGameFileIntegrityService>(serviceProvider => new GameFileIntegrityService(
            serviceProvider.GetServices<ILoaderProvider>(),
            serviceProvider.GetRequiredService<IGameInstallCoordinator>(),
            serviceProvider.GetService<IDownloadSpeedLimitState>(),
            serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<GameFileIntegrityService>>(),
            serviceProvider.GetServices<ILoaderFileManifestContributor>()));
        services.AddSingleton<ILaunchService, LaunchService>();
        services.AddSingleton<ILaunchDiagnosticExportService, LaunchDiagnosticExportService>();
        services.AddSingleton<IGameLanguageService, GameLanguageService>();
        services.AddSingleton<IJavaRuntimeDiscoveryService, JavaRuntimeDiscoveryService>();
        services.AddSingleton<IJavaRuntimeSelectionService, JavaRuntimeSelectionService>();
        services.AddSingleton<CmlLibJavaRuntimeProvisioningService>();
        services.AddSingleton<IJavaRuntimeProvisioningService>(serviceProvider =>
            serviceProvider.GetRequiredService<CmlLibJavaRuntimeProvisioningService>());
        services.AddSingleton<ILoaderInstallerJavaRuntimeProvisioner>(serviceProvider =>
            serviceProvider.GetRequiredService<CmlLibJavaRuntimeProvisioningService>());
        services.AddSingleton<ISystemMemoryService, WindowsSystemMemoryService>();
        services.AddSingleton<IUserFileDeletionService, UserFileDeletionService>();
        services.AddSingleton<IModService, ModService>();
        services.AddSingleton<LocalFileFingerprintService>();
        services.AddSingleton<IModUpdateProvider, ModrinthModUpdateProvider>();
        services.AddSingleton<IModUpdateProvider, CurseForgeModUpdateProvider>();
        services.AddSingleton<IModUpdateFileTransaction, ModUpdateFileTransaction>();
        services.AddSingleton<ILocalModIconEnrichmentService, LocalModIconEnrichmentService>();
        services.AddSingleton<ILocalResourceCategoryEnrichmentService, LocalResourceCategoryEnrichmentService>();
        services.AddSingleton<IInstanceBackupService, InstanceBackupService>();
        services.AddSingleton<ILocalSaveService, LocalSaveService>();
        services.AddSingleton<ModpackSandboxCleanupService>();
        services.AddSingleton<IModpackSandboxCleanupService>(serviceProvider =>
            serviceProvider.GetRequiredService<ModpackSandboxCleanupService>());
        services.AddSingleton<IModpackGameInstaller, ModpackGameInstaller>();
        services.AddSingleton<IModpackInstanceStagingService, ModpackInstanceStagingService>();
        services.AddSingleton<CurseForgeApiClient>();
        services.AddSingleton<ModrinthApiClient>();
        services.AddSingleton<IModpackPackageService, LocalModpackPackageService>();
        services.AddSingleton<IServerDeploymentTransactionService, ServerDeploymentTransactionService>();
        services.AddSingleton<IServerPackExtractor, CurseForgeServerPackExtractor>();
        services.AddSingleton<IServerRuntimeInstaller, ServerRuntimeInstaller>();
        services.AddSingleton<IModpackExportService, ModpackExportService>();
        services.AddSingleton<IModpackWorkspaceCleanupService, ModpackWorkspaceCleanupService>();
        services.AddSingleton<ILocalResourcePackService, LocalResourcePackService>();
        services.AddSingleton<ILocalShaderPackService, LocalShaderPackService>();
        services.AddSingleton<IModrinthService>(serviceProvider => new ModrinthService(
            MinecraftHttpClientFactory.CreateTransportClient(),
            serviceProvider.GetService<ILogger<ModrinthService>>(),
            serviceProvider.GetService<ISettingsService>(),
            serviceProvider.GetService<IDownloadSpeedLimitState>(),
            serviceProvider.GetService<IImportConcurrencyLimiter>()));
        services.AddSingleton<ResourceCatalogService>();
        services.AddSingleton<IResourceCatalogService>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceCatalogService>());
        services.AddSingleton<IResourceThumbnailService>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceCatalogService>());
        services.AddSingleton<IResourceProjectInstallationService, ResourceProjectInstallationService>();
        services.AddSingleton<ILauncherUpdateService, RemoteManifestLauncherUpdateService>();
        services.AddSingleton<LauncherUpdateCacheCleaner>();
        services.AddSingleton<ILauncherSelfUpdateService, LauncherSelfUpdateService>();
        services.AddSingleton<ILauncherStateMonitor, LauncherStateMonitor>();
        services.AddSingleton<IInstanceDirectoryMonitor, InstanceDirectoryMonitor>();
        services.AddSingleton<IInstanceContentImportPathValidator, InstanceContentImportPathValidator>();
        services.AddSingleton<IExistingFilePathValidator, ExistingFilePathValidator>();
        services.AddSingleton<IMicrosoftAccountService>(serviceProvider => new MicrosoftAccountService(
            serviceProvider.GetRequiredService<MicrosoftAuthProvider>(),
            serviceProvider.GetRequiredService<LauncherPathProvider>(),
            serviceProvider.GetRequiredService<IAccountSkinLibraryService>(),
            serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<MicrosoftAccountService>>()));
        services.AddSingleton<IThirdPartyAccountTokenStore, DpapiThirdPartyAccountTokenStore>();
        services.AddSingleton<IThirdPartyAccountService, ThirdPartyAccountService>();
        services.AddSingleton<IThirdPartyLaunchSessionService, ThirdPartyLaunchSessionService>();
        services.AddSingleton<IAuthlibInjectorProvisioningService, AuthlibInjectorProvisioningService>();
        services.AddSingleton<IOfflineSkinLaunchService, OfflineSkinYggdrasilService>();
        services.AddSingleton<ITerracottaProvisioningService, TerracottaProvisioningService>();
        services.AddSingleton<IMultiplayerLobbyService, MultiplayerLobbyService>();
        services.AddSingleton<IAccountSkinLibraryService, AccountSkinLibraryService>();
        services.AddSingleton<IMinecraftSkinFileValidator, MinecraftSkinFileValidator>();
        services.AddSingleton<IOfflineAccountUuidService, OfflineAccountUuidService>();
        services.AddSingleton(serviceProvider => new MicrosoftClientIdProvider(
            serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<MicrosoftClientIdProvider>>()));
        services.AddSingleton(serviceProvider => new MicrosoftAuthProvider(
            serviceProvider.GetRequiredService<LauncherPathProvider>(),
            serviceProvider.GetRequiredService<MicrosoftClientIdProvider>(),
            serviceProvider.GetService<IMicrosoftLoginBrowserPageProvider>(),
            logger: serviceProvider.GetService<
                Microsoft.Extensions.Logging.ILogger<MicrosoftAuthProvider>>()));
        services.AddSingleton<ILaunchAccountSessionService, LaunchAccountSessionService>();
        return services;
    }
}
