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

using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

public sealed class ResourcesDataPacksPageViewModel : ResourcesModPageViewModel
{
    public ResourcesDataPacksPageViewModel(
        ResourcesPageViewModel parent,
        IResourceCatalogService? resourceCatalogService = null,
        ILogger? logger = null,
        IUiDispatcher? uiDispatcher = null,
        IGameVersionService? gameVersionService = null,
        Func<IReadOnlyList<GameInstance>>? getInstanceCatalogSnapshot = null,
        IStatusService? statusService = null,
        IFilePickerService? filePickerService = null,
        IFloatingMessageService? floatingMessageService = null,
        DownloadTasksPageViewModel? downloadTasksPage = null,
        IResourceProjectInstallationService? resourceProjectInstallationService = null,
        IResourceDependencyPlanningService? resourceDependencyPlanningService = null)
        : base(
            parent,
            CreateDataPackOptions(),
            resourceCatalogService,
            logger,
            uiDispatcher,
            gameVersionService,
            getInstanceCatalogSnapshot,
            statusService,
            filePickerService,
            floatingMessageService,
            downloadTasksPage,
            resourceProjectInstallationService: resourceProjectInstallationService,
            resourceDependencyPlanningService: resourceDependencyPlanningService)
    {
    }

    private static ResourcesOnlineProjectPageOptions CreateDataPackOptions()
    {
        return new ResourcesOnlineProjectPageOptions(
            ResourceProjectKind.DataPack,
            Strings.Resources_SectionDataPacks,
            "datapack",
            ShowsLoaderFilters: false,
            Strings.Resources_ModFilterAllVersions,
            Strings.Resources_DataPackFilterAllLoaders,
            Strings.Resources_DataPackProjectsLoading,
            Strings.Resources_DataPackProjectsEmpty,
            Strings.Resources_DataPackProjectsLoadError,
            Strings.Resources_DataPackProjectsLoadingMore,
            Strings.Resources_DataPackProjectsNoMore,
            Strings.Resources_DataPackProjectsLoadMoreError,
            Strings.Resources_DataPackCurseForgeMissingApiKey,
            Strings.Resources_DataPackDetailsInfoSection,
            Strings.Resources_DataPackInstallTargetSection,
            Strings.Resources_DataPackInstallTargetLocal,
            Strings.Resources_DataPackInstallTargetsLoading,
            Strings.Resources_DataPackInstallTargetsLoadError,
            Strings.Resources_DataPackVersionsLoading,
            Strings.Resources_DataPackVersionsEmpty,
            Strings.Resources_DataPackVersionsEmptyLocal,
            Strings.Resources_DataPackVersionsFilterEmpty,
            Strings.Resources_DataPackVersionsLoadError,
            Strings.Resources_DataPackVersionsLoadingMore,
            Strings.Resources_DataPackVersionsNoMore,
            Strings.Resources_DataPackVersionsLoadMoreError,
            Strings.Resources_DataPackVersionsAllTitle,
            Strings.FilePicker_DataPackDownloadDirectoryTitle,
            Strings.Status_DataPackDownloading,
            Strings.Status_DataPackDownloadingFormat,
            Strings.Status_DataPackDownloadedFormat,
            Strings.Status_DataPackDownloadFailed,
            Strings.Status_DataPackDownloadedFormat,
            Strings.Status_DataPackDownloadFailed,
            Strings.Resources_DataPackDownloadFileExistsMessageFormat,
            [
                new("optimization", Strings.Resources_ModFilterTypeOptimization, ResourceProjectCategory.Optimization),
                new("utility", Strings.Resources_ModFilterTypeUtility, ResourceProjectCategory.Utility),
                new("adventure", Strings.Resources_ModFilterTypeAdventure, ResourceProjectCategory.Adventure),
                new("decoration", Strings.Resources_ModFilterTypeDecoration, ResourceProjectCategory.Decoration),
                new("equipment", Strings.Resources_ModFilterTypeEquipment, ResourceProjectCategory.Equipment),
                new("technology", Strings.Resources_ModFilterTypeTechnology, ResourceProjectCategory.Technology),
                new("magic", Strings.Resources_ModFilterTypeMagic, ResourceProjectCategory.Magic),
                new("mobs", Strings.Resources_ModFilterTypeMobs, ResourceProjectCategory.Mobs),
                new("worldgen", Strings.Resources_ModFilterTypeWorldGeneration, ResourceProjectCategory.WorldGeneration),
                new("storage", Strings.Resources_ModFilterTypeStorage, ResourceProjectCategory.Storage),
                new("library", Strings.Resources_ModFilterTypeLibrary, ResourceProjectCategory.Library)
            ]);
    }
}
