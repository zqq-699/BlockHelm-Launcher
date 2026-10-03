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

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

/// <summary>
/// 组合各资源类型页面，维护顶层分区选择并转发标题、返回与整合包结果事件。
/// </summary>
public sealed partial class ResourcesPageViewModel : ObservableObject
{
    // 各在线页面保持长期实例以复用搜索缓存；顶层只切换当前分区而不重建子页面。
    private readonly ILogger<ResourcesPageViewModel>? logger;
    private readonly IStatusService? statusService;
    private readonly IExternalLinkService? externalLinkService;
    private readonly IResourceCatalogService? resourceCatalogService;
    private ResourceProjectReference? pendingProjectDetailsReference;

    public ResourcesPageViewModel(
        IResourceCatalogService? resourceCatalogService = null,
        ILogger<ResourcesPageViewModel>? logger = null,
        IUiDispatcher? uiDispatcher = null,
        IGameVersionService? gameVersionService = null,
        GameManagementViewModel? gameManagement = null,
        IStatusService? statusService = null,
        IFilePickerService? filePickerService = null,
        IFloatingMessageService? floatingMessageService = null,
        DownloadTasksPageViewModel? downloadTasksPage = null,
        IResourceProjectInstallationService? resourceProjectInstallationService = null,
        IResourceDependencyPlanningService? resourceDependencyPlanningService = null,
        IExternalLinkService? externalLinkService = null)
    {
        this.logger = logger;
        this.statusService = statusService;
        this.externalLinkService = externalLinkService;
        this.resourceCatalogService = resourceCatalogService;
        Func<IReadOnlyList<GameInstance>>? getInstanceCatalogSnapshot =
            gameManagement is null ? null : gameManagement.GetInstanceCatalogSnapshot;

        Sections =
        [
            new ResourcesSectionItem { Id = "mods", Title = Strings.Resources_SectionMods, IconKey = "instance_setting_page/mod" },
            new ResourcesSectionItem { Id = "resource_packs", Title = Strings.Resources_SectionResourcePacks, IconKey = "main_menu_library" },
            new ResourcesSectionItem { Id = "shader_packs", Title = Strings.Resources_SectionShaderPacks, IconKey = "instance_setting_page/shader" },
            new ResourcesSectionItem { Id = "worlds", Title = Strings.Resources_SectionWorlds, IconKey = "world" },
            new ResourcesSectionItem { Id = "modpacks", Title = Strings.Resources_SectionModpacks, IconKey = "modpack" }
        ];

        ModPage = new ResourcesModPageViewModel(
            this,
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
            resourceDependencyPlanningService: resourceDependencyPlanningService);
        ModPage.PropertyChanged += ModPage_PropertyChanged;
        SubscribeOnlinePageChildren(ModPage);
        ResourcePacksPage = new ResourcesResourcePacksPageViewModel(
            this,
            resourceCatalogService,
            logger,
            uiDispatcher,
            gameVersionService,
            getInstanceCatalogSnapshot,
            statusService,
            filePickerService,
            floatingMessageService,
            downloadTasksPage,
            resourceProjectInstallationService,
            resourceDependencyPlanningService);
        ResourcePacksPage.PropertyChanged += OnlineProjectPage_PropertyChanged;
        SubscribeOnlinePageChildren(ResourcePacksPage);
        ShaderPacksPage = new ResourcesShaderPacksPageViewModel(
            this,
            resourceCatalogService,
            logger,
            uiDispatcher,
            gameVersionService,
            getInstanceCatalogSnapshot,
            statusService,
            filePickerService,
            floatingMessageService,
            downloadTasksPage,
            resourceProjectInstallationService,
            resourceDependencyPlanningService);
        ShaderPacksPage.PropertyChanged += OnlineProjectPage_PropertyChanged;
        SubscribeOnlinePageChildren(ShaderPacksPage);
        WorldsPage = new ResourcesWorldsPageViewModel(
            this,
            resourceCatalogService,
            logger,
            uiDispatcher,
            gameVersionService,
            getInstanceCatalogSnapshot,
            statusService,
            filePickerService,
            floatingMessageService,
            downloadTasksPage,
            resourceProjectInstallationService,
            resourceDependencyPlanningService);
        WorldsPage.PropertyChanged += OnlineProjectPage_PropertyChanged;
        SubscribeOnlinePageChildren(WorldsPage);
        ModpacksPage = new ResourcesModpacksPageViewModel(
            this,
            resourceCatalogService,
            logger,
            uiDispatcher,
            gameVersionService,
            getInstanceCatalogSnapshot,
            statusService,
            filePickerService,
            floatingMessageService,
            downloadTasksPage,
            resourceProjectInstallationService,
            resourceDependencyPlanningService);
        ModpacksPage.PropertyChanged += OnlineProjectPage_PropertyChanged;
        SubscribeOnlinePageChildren(ModpacksPage);
        ModpacksPage.ModpackImported += (_, instance) => ModpackImported?.Invoke(this, instance);
        ModpacksPage.ModpackManualDownloadsRequested += (_, args) => ModpackManualDownloadsRequested?.Invoke(this, args);

        SelectSection(Sections[0], logSelection: false);
    }

    [RelayCommand(CanExecute = nameof(CanOpenProjectPage))]
    private void OpenProjectPage(ResourcesModProjectItemViewModel? project)
    {
        if (project is null || externalLinkService is null)
            return;

        try
        {
            if (externalLinkService.TryOpen(project.Project.ProjectUrl))
                return;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Failed to open resource project page. Source={Source} ProjectId={ProjectId}",
                project.Project.Source,
                project.Project.ProjectId);
        }

        statusService?.Report(Strings.Status_OpenReferenceProjectFailed);
    }

    private bool CanOpenProjectPage(ResourcesModProjectItemViewModel? project)
    {
        if (externalLinkService is null
            || !Uri.TryCreate(project?.Project.ProjectUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanOpenRelatedWebsite))]
    private void OpenRelatedWebsite(ResourceProjectRelatedWebsite? website)
    {
        if (website is null || externalLinkService is null)
            return;

        try
        {
            if (externalLinkService.TryOpen(website.Url))
                return;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Failed to open resource project related website. Source={Source} ProjectId={ProjectId}",
                website.Source,
                website.ProjectId);
        }

        statusService?.Report(Strings.Status_OpenRelatedWebsiteFailed);
    }

    private bool CanOpenRelatedWebsite(ResourceProjectRelatedWebsite? website)
    {
        if (externalLinkService is null
            || !Uri.TryCreate(website?.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("www.mcresource.cn", StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<ResourcesSectionItem> Sections { get; }

    public ResourcesModPageViewModel ModPage { get; }

    public ResourcesResourcePacksPageViewModel ResourcePacksPage { get; }

    public ResourcesShaderPacksPageViewModel ShaderPacksPage { get; }

    public ResourcesWorldsPageViewModel WorldsPage { get; }

    public ResourcesModpacksPageViewModel ModpacksPage { get; }

    public event EventHandler<GameInstance>? ModpackImported;

    public event EventHandler<ResourcesModpackManualDownloadsRequestedEventArgs>? ModpackManualDownloadsRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(IsModsSection))]
    [NotifyPropertyChangedFor(nameof(IsModSearchVisible))]
    [NotifyPropertyChangedFor(nameof(IsModProjectDetailsStep))]
    [NotifyPropertyChangedFor(nameof(CurrentOnlineProjectPage))]
    [NotifyPropertyChangedFor(nameof(PageTitleIconSource))]
    private ResourcesSectionItem? selectedSection;

    [ObservableProperty]
    private ResourcesSectionViewModelBase? currentSectionViewModel;

    public string PageTitle => CurrentOnlineProjectPage is { } onlineProjectPage
        ? onlineProjectPage.PageTitle
        : SelectedSection?.Title ?? Strings.Page_Resources;

    public bool IsModsSection => SelectedSection?.Id == "mods";

    public bool IsModSearchVisible => CurrentOnlineProjectPage is { } onlineProjectPage
        && (onlineProjectPage.IsProjectListStep || onlineProjectPage.IsProjectVersionsStep);

    public bool IsModProjectDetailsStep => CurrentOnlineProjectPage?.IsProjectContentStep == true;

    public string? PageTitleIconSource => CurrentOnlineProjectPage?.PageTitleIconSource;

    public ResourcesModPageViewModel? CurrentOnlineProjectPage => CurrentSectionViewModel as ResourcesModPageViewModel;

    public string ActiveModSearchQuery
    {
        get
        {
            var onlineProjectPage = CurrentOnlineProjectPage;
            if (onlineProjectPage is null)
                return string.Empty;

            return onlineProjectPage.IsProjectVersionsStep
                ? onlineProjectPage.Versions.SearchQuery
                : onlineProjectPage.ProjectList.SearchQuery;
        }
        set
        {
            var onlineProjectPage = CurrentOnlineProjectPage;
            if (onlineProjectPage is null)
                return;

            if (onlineProjectPage.IsProjectVersionsStep)
                onlineProjectPage.Versions.SearchQuery = value;
            else
                onlineProjectPage.ProjectList.SearchQuery = value;

            OnPropertyChanged();
        }
    }

    public void BeginEnsureCurrentSectionLoaded()
    {
        // 页面激活事件不能等待网络请求，子页面自行观察并管理加载状态。
        if (CurrentOnlineProjectPage?.IsProjectListStep is true)
            CurrentOnlineProjectPage.BeginEnsureProjectsLoaded();
    }

    internal async Task<ResourceProject?> LoadProjectDetailsAsync(ResourceProjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (resourceCatalogService is null)
            return null;

        try
        {
            var project = await resourceCatalogService.GetProjectAsync(reference);
            if (project is null)
            {
                statusService?.Report(Strings.Status_OpenResourceDetailsFailed);
                return null;
            }

            return ResolveProjectSection(reference.Kind) is null ? null : project;
        }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "Failed to open recognized local resource project details. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                reference.Kind,
                reference.Source,
                reference.ProjectId);
            statusService?.Report(Strings.Status_OpenResourceDetailsFailed);
            return null;
        }
    }

    internal bool BeginLoadProjectDetails(ResourceProjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var section = ResolveProjectSection(reference.Kind);
        if (section is null)
            return false;

        pendingProjectDetailsReference = reference;
        SelectSection(section, logSelection: false);
        CurrentOnlineProjectPage?.BeginProjectDetailsLoading();
        return true;
    }

    internal bool CancelLoadProjectDetails(ResourceProjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (pendingProjectDetailsReference != reference)
            return false;

        pendingProjectDetailsReference = null;
        CurrentOnlineProjectPage?.CancelProjectDetailsLoading();
        return true;
    }

    internal bool ShowProjectDetails(ResourceProjectReference reference, ResourceProject project)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(project);

        if (pendingProjectDetailsReference != reference)
            return false;

        var section = ResolveProjectSection(reference.Kind);
        if (section is null)
            return false;

        pendingProjectDetailsReference = null;
        SelectSection(section, logSelection: false);
        CurrentOnlineProjectPage?.ShowProjectDetails(project);
        logger?.LogInformation(
            "Opened recognized local resource project details. Kind={Kind} Source={Source} ProjectId={ProjectId}",
            reference.Kind,
            reference.Source,
            reference.ProjectId);
        return true;
    }

    private ResourcesSectionItem? ResolveProjectSection(ResourceProjectKind kind)
    {
        var sectionId = kind switch
        {
            ResourceProjectKind.Mod => "mods",
            ResourceProjectKind.ResourcePack => "resource_packs",
            ResourceProjectKind.ShaderPack => "shader_packs",
            _ => string.Empty
        };
        return Sections.FirstOrDefault(item => item.Id == sectionId);
    }

    public async Task OpenModsForInstanceAsync(GameInstance instance)
    {
        // 从实例设置跳转时先选 Mod 分区，再应用实例版本与 Loader 筛选，避免短暂展示不兼容结果。
        var modsSection = Sections.FirstOrDefault(section => section.Id == "mods") ?? Sections[0];
        SelectSection(modsSection, logSelection: false);
        logger?.LogDebug(
            "Opening resources mod section from instance. InstanceId={InstanceId}, MinecraftVersion={MinecraftVersion}, Loader={Loader}",
            instance.Id,
            instance.MinecraftVersion,
            instance.Loader);
        await ModPage.ApplyInstanceFiltersAsync(instance);
    }

    [RelayCommand]
    private void SelectSection(ResourcesSectionItem? section)
    {
        SelectSection(section, logSelection: true);
    }

    private void SelectSection(ResourcesSectionItem? section, bool logSelection)
    {
        // 同一分区重复选择只确保加载，不重置子页面搜索、详情或滚动状态。
        if (section is null || ReferenceEquals(SelectedSection, section))
            return;

        if (logSelection)
            pendingProjectDetailsReference = null;

        foreach (var item in Sections)
            item.IsSelected = ReferenceEquals(item, section);

        CurrentSectionViewModel = section.Id switch
        {
            "mods" => ModPage,
            "resource_packs" => ResourcePacksPage,
            "shader_packs" => ShaderPacksPage,
            "worlds" => WorldsPage,
            "modpacks" => ModpacksPage,
            _ => ModPage
        };
        SelectedSection = section;

        if (section.Id != "mods")
            ModPage.ResetToProjectList();
        if (section.Id != "resource_packs")
            ResourcePacksPage.ResetToProjectList();
        if (section.Id != "shader_packs")
            ShaderPacksPage.ResetToProjectList();
        if (section.Id != "worlds")
            WorldsPage.ResetToProjectList();
        if (section.Id != "modpacks")
            ModpacksPage.ResetToProjectList();

        if (logSelection)
            logger?.LogDebug("Resources section selected. SectionId={SectionId}", section.Id);

        if (logSelection)
            CurrentOnlineProjectPage?.BeginEnsureProjectsLoaded();
    }

    private void ModPage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnlineProjectPage_PropertyChanged(sender, e);
    }

    private void OnlineProjectPage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ResourcesModPageViewModel.CurrentStep)
            or nameof(ResourcesModPageViewModel.PageTitle)
            or nameof(ResourcesModPageViewModel.PageTitleIconSource))
        {
            if (!ReferenceEquals(sender, CurrentOnlineProjectPage))
                return;

            // 深链接进入详情时不加载背后的项目列表；用户真正返回列表时再确保首页就绪。
            if (e.PropertyName is nameof(ResourcesModPageViewModel.CurrentStep)
                && CurrentOnlineProjectPage?.IsProjectListStep is true)
            {
                pendingProjectDetailsReference = null;
                CurrentOnlineProjectPage.BeginEnsureProjectsLoaded();
            }

            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageTitleIconSource));
            OnPropertyChanged(nameof(IsModSearchVisible));
            OnPropertyChanged(nameof(IsModProjectDetailsStep));
            OnPropertyChanged(nameof(ActiveModSearchQuery));
        }

    }

    private void SubscribeOnlinePageChildren(ResourcesModPageViewModel page)
    {
        page.ProjectList.PropertyChanged += OnlineProjectChild_PropertyChanged;
        page.Versions.PropertyChanged += OnlineProjectChild_PropertyChanged;
    }

    private void OnlineProjectChild_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 页面标题和返回能力来自当前子页面，需要转发到顶层 Shell Binding。
        var currentPage = CurrentOnlineProjectPage;
        if (currentPage is null
            || !ReferenceEquals(sender, currentPage.ProjectList) && !ReferenceEquals(sender, currentPage.Versions))
        {
            return;
        }

        if (e.PropertyName is nameof(ResourcesProjectListViewModel.SearchQuery)
            or nameof(ResourcesProjectVersionsViewModel.SearchQuery))
        {
            OnPropertyChanged(nameof(ActiveModSearchQuery));
        }
    }
}
