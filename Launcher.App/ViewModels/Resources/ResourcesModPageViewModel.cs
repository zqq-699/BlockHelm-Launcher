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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

/// <summary>
/// 连接资源项目列表、项目版本详情和安装流程，并维护二级页面导航状态。
/// </summary>
public partial class ResourcesModPageViewModel : ResourcesSectionViewModelBase, IDisposable
{
    // 列表、版本和安装分别拥有请求生命周期，本类只保存当前页面步骤和所选项目。
    private readonly ResourcesOnlineProjectPageOptions options;
    private readonly DownloadTasksPageViewModel? downloadTasksPage;
    private readonly ILogger? logger;

    public ResourcesModPageViewModel(
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
        : this(
            parent,
            CreateModOptions(),
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
            resourceDependencyPlanningService)
    {
    }

    protected ResourcesModPageViewModel(
        ResourcesPageViewModel parent,
        ResourcesOnlineProjectPageOptions options,
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
        : base(parent, options.Title)
    {
        this.options = options;
        this.downloadTasksPage = downloadTasksPage;
        this.logger = logger;
        var dispatcher = uiDispatcher ?? ImmediateUiDispatcher.Instance;
        Action<string> reportStatus = message =>
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            if (dispatcher.HasAccess)
                statusService?.Report(message);
            else
                dispatcher.Invoke(() => statusService?.Report(message));
        };

        ProjectList = new ResourcesProjectListViewModel(
            options,
            resourceCatalogService,
            gameVersionService,
            dispatcher,
            logger);
        Details = new ResourcesProjectDetailsViewModel(options, resourceCatalogService, dispatcher, logger);
        Versions = new ResourcesProjectVersionsViewModel(
            options,
            resourceCatalogService,
            getInstanceCatalogSnapshot,
            dispatcher,
            logger);
        Install = new ResourcesProjectInstallViewModel(
            options,
            resourceProjectInstallationService,
            new ResourcesRequiredDependencyPlanner(
                resourceDependencyPlanningService,
                options,
                logger,
                reportStatus),
            filePickerService,
            floatingMessageService,
            downloadTasksPage,
            dispatcher,
            logger,
            reportStatus);

        ProjectList.ProjectSelected += Details.SelectRoot;
        ProjectList.NavigationResetRequested += ResetToProjectList;
        Details.ProjectChanged += OpenProjectDetails;
        Versions.TargetSelected += _ => CurrentStep = ResourcesModPageStep.ProjectVersions;
        Versions.InstallRequested += item => ObserveInstall(item);
        Install.ModpackImported += (_, instance) => ModpackImported?.Invoke(this, instance);
        Install.ModpackManualDownloadsRequested += (_, args) => ModpackManualDownloadsRequested?.Invoke(this, args);
    }

    public event EventHandler<GameInstance>? ModpackImported;

    public event EventHandler<ResourcesModpackManualDownloadsRequestedEventArgs>? ModpackManualDownloadsRequested;

    public ResourcesProjectListViewModel ProjectList { get; }

    public ResourcesProjectDetailsViewModel Details { get; }

    public ResourcesProjectVersionsViewModel Versions { get; }

    public ResourcesProjectInstallViewModel Install { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectListStep))]
    [NotifyPropertyChangedFor(nameof(IsProjectDetailsStep))]
    [NotifyPropertyChangedFor(nameof(IsProjectVersionsStep))]
    [NotifyPropertyChangedFor(nameof(IsProjectContentStep))]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageTitleIconSource))]
    private ResourcesModPageStep currentStep = ResourcesModPageStep.ProjectList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(PageTitleIconSource))]
    private bool isProjectDetailsLoading;

    public bool IsProjectListStep => CurrentStep is ResourcesModPageStep.ProjectList;

    public bool IsProjectDetailsStep => CurrentStep is ResourcesModPageStep.ProjectDetails;

    public bool IsProjectVersionsStep => CurrentStep is ResourcesModPageStep.ProjectVersions;

    public bool IsProjectContentStep => CurrentStep is not ResourcesModPageStep.ProjectList;

    public string ProjectDetailsLoadingMessage => options.ProjectsLoadingText;

    public string PageTitle => IsProjectVersionsStep && Versions.SelectedTarget?.IsLocalDownload == false
        ? Versions.SelectedTarget.Title
        : IsProjectContentStep
            ? IsProjectDetailsLoading ? Title : Details.CurrentProject?.Title ?? Title
            : Title;

    public string? PageTitleIconSource => IsProjectVersionsStep && Versions.SelectedTarget?.IsLocalDownload == false
        ? Versions.SelectedTarget.IconSource
        : IsProjectContentStep
            ? IsProjectDetailsLoading ? null : Details.CurrentProject?.IconSource
            : null;

    [RelayCommand]
    public void BackToProjectList()
    {
        // 返回时取消详情分页并保留项目列表缓存，让用户回到原搜索上下文。
        if (CurrentStep is ResourcesModPageStep.ProjectVersions)
        {
            CurrentStep = ResourcesModPageStep.ProjectDetails;
            return;
        }

        if (CurrentStep is ResourcesModPageStep.ProjectDetails && Details.TryGoBack(out _))
            return;

        ResetToProjectList();
    }

    public void ResetToProjectList()
    {
        // 顶层分区重置需要彻底清空详情选择，但不必重新创建子 ViewModel。
        IsProjectDetailsLoading = false;
        Details.Reset();
        Versions.Reset();
        CurrentStep = ResourcesModPageStep.ProjectList;
        RaisePageTitleChanged();
    }

    public void BeginEnsureProjectsLoaded() => ProjectList.BeginEnsureLoaded();

    public Task ApplyInstanceFiltersAsync(GameInstance instance) => ProjectList.ApplyInstanceFiltersAsync(instance);

    public void BeginLoadMoreProjects() => ProjectList.BeginLoadMore();

    public void BeginLoadMoreAvailableVersions() => Versions.BeginLoadMore();

    public void BeginProjectDetailsLoading()
    {
        Details.Reset();
        Versions.Reset();
        IsProjectDetailsLoading = true;
        CurrentStep = ResourcesModPageStep.ProjectDetails;
        RaisePageTitleChanged();
    }

    public void CancelProjectDetailsLoading()
    {
        if (!IsProjectDetailsLoading)
            return;

        ResetToProjectList();
    }

    public void ShowProjectDetails(ResourceProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        IsProjectDetailsLoading = false;
        Details.SelectRoot(new ResourcesModProjectItemViewModel(
            project,
            fallbackIconKey: options.FallbackIconKey,
            typeOptions: options.TypeOptions));
    }

    public void Dispose()
    {
        ProjectList.ProjectSelected -= Details.SelectRoot;
        ProjectList.NavigationResetRequested -= ResetToProjectList;
        Details.ProjectChanged -= OpenProjectDetails;
        ProjectList.Dispose();
        Details.Dispose();
        Versions.Dispose();
    }

    private void OpenProjectDetails(ResourcesModProjectItemViewModel project)
    {
        // 先冻结项目身份再触发版本加载，快速选择不同项目时由 Versions 的请求代次丢弃旧结果。
        CurrentStep = ResourcesModPageStep.ProjectDetails;
        Versions.SetProject(project);
        RaisePageTitleChanged();
        logger?.LogDebug(
            "Resource project selected. Kind={Kind} Source={Source} ProjectId={ProjectId}",
            options.Kind,
            project.Project.Source,
            project.Project.ProjectId);
    }

    private void ObserveInstall(ResourcesModVersionItemViewModel item)
    {
        var operation = Install.InstallAsync(item, Versions.SelectedTarget, Details.CurrentProject);
        downloadTasksPage?.TrackBackgroundTask(operation);
        _ = ObserveInstallAsync(operation);
    }

    private async Task ObserveInstallAsync(Task operation)
    {
        // 安装异常由安装 ViewModel 映射为用户反馈，此处只保证异步命令被观察。
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Unhandled resource installation command failure. Kind={Kind}", options.Kind);
        }
    }

    private void RaisePageTitleChanged()
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageTitleIconSource));
    }

    protected static ResourcesOnlineProjectPageOptions CreateModOptions()
    {
        return new ResourcesOnlineProjectPageOptions(
            ResourceProjectKind.Mod,
            Strings.Resources_SectionMods,
            "instance_setting_page/mod",
            ShowsLoaderFilters: true,
            Strings.Resources_ModFilterAllVersions,
            Strings.Resources_ModFilterAllLoaders,
            Strings.Resources_ModProjectsLoading,
            Strings.Resources_ModProjectsEmpty,
            Strings.Resources_ModProjectsLoadError,
            Strings.Resources_ModProjectsLoadingMore,
            Strings.Resources_ModProjectsNoMore,
            Strings.Resources_ModProjectsLoadMoreError,
            Strings.Resources_ModCurseForgeMissingApiKey,
            Strings.Resources_ModDetailsInfoSection,
            Strings.Resources_ModInstallTargetSection,
            Strings.Resources_ModInstallTargetLocal,
            Strings.Resources_ModInstallTargetsLoading,
            Strings.Resources_ModInstallTargetsLoadError,
            Strings.Resources_ModVersionsLoading,
            Strings.Resources_ModVersionsEmpty,
            Strings.Resources_ModVersionsEmptyLocal,
            Strings.Resources_ModVersionsFilterEmpty,
            Strings.Resources_ModVersionsLoadError,
            Strings.Resources_ModVersionsLoadingMore,
            Strings.Resources_ModVersionsNoMore,
            Strings.Resources_ModVersionsLoadMoreError,
            Strings.Resources_ModVersionsAllTitle,
            Strings.FilePicker_ModDownloadDirectoryTitle,
            Strings.Status_ModDownloading,
            Strings.Status_ModDownloadingFormat,
            Strings.Status_ModDownloadedFormat,
            Strings.Status_ModDownloadFailed,
            Strings.Status_ModInstalledFormat,
            Strings.Status_ModInstallFailed,
            Strings.Resources_ModDownloadFileExistsMessageFormat,
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
