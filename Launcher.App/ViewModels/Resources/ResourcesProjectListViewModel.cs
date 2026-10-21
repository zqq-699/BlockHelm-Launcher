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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Domain.Models;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

/// <summary>
/// 管理在线资源项目搜索、筛选、分页和分批呈现，并保证只有最新查询可以更新页面。
/// </summary>
public sealed partial class ResourcesProjectListViewModel : ObservableObject, IDisposable
{
    private const int SearchDebounceMilliseconds = 250;
    private const int CatalogPageSize = 20;
    private const int InitialProjectBatchSize = 12;
    private const int AppendProjectBatchSize = 8;

    private readonly ResourcesOnlineProjectPageOptions options;
    private readonly IResourceCatalogService? resourceCatalogService;
    private readonly IResourceThumbnailService? thumbnailService;
    private readonly IGameVersionService? gameVersionService;
    private readonly IUiDispatcher uiDispatcher;
    private readonly ILogger? logger;
    // 版本筛选和项目排序共享同一份正式版本清单任务，锁只保护任务创建而不包围网络等待。
    private readonly object releaseVersionGate = new();
    // 根查询、筛选防抖和分页都绑定到同一请求世代；新根查询会取消旧世代的全部后续工作。
    private CancellationTokenSource? requestCancellation;
    private Task<ReleaseVersionData>? releaseVersionDataTask;
    private bool hasRequestedInitialLoad;
    private bool isApplyingVersionOptions;
    private bool isApplyingInstanceFilters;
    private bool isApplyingPendingFilters;

    internal ResourcesProjectListViewModel(
        ResourcesOnlineProjectPageOptions options,
        IResourceCatalogService? resourceCatalogService,
        IGameVersionService? gameVersionService,
        IUiDispatcher uiDispatcher,
        ILogger? logger)
    {
        this.options = options;
        this.resourceCatalogService = resourceCatalogService;
        thumbnailService = resourceCatalogService as IResourceThumbnailService;
        this.gameVersionService = gameVersionService;
        this.uiDispatcher = uiDispatcher;
        this.logger = logger;

        VersionOptions = [new ResourcesFilterOptionItem { Id = "all", Title = options.AllVersionsText }];
        LoaderOptions = CreateLoaderOptions(options);
        SourceOptions = CreateSourceOptions(options);
        TypeOptions = CreateTypeOptions(options);
        selectedVersionOption = VersionOptions[0];
        selectedLoaderOption = LoaderOptions[0];
        selectedSourceOption = SourceOptions[0];
        selectedTypeOption = TypeOptions[0];
    }

    public event Action<ResourcesModProjectItemViewModel>? ProjectSelected;

    public event Action? NavigationResetRequested;

    public ObservableCollection<ResourcesFilterOptionItem> VersionOptions { get; }

    public ObservableCollection<ResourcesFilterOptionItem> LoaderOptions { get; }

    public ObservableCollection<ResourcesFilterOptionItem> SourceOptions { get; }

    public ObservableCollection<ResourcesFilterOptionItem> TypeOptions { get; }

    public ObservableCollection<ResourcesModProjectItemViewModel> VisibleProjects { get; } = [];

    public ObservableCollection<object> ListItems { get; } = [];

    public bool ShowsLoaderFilters => options.ShowsLoaderFilters;

    public bool ShowsSourceFilters => SourceOptions.Count > 1;

    public int AppliedFilterCount =>
        CountAppliedFilter(SelectedVersionOption, VersionOptions)
        + CountAppliedFilter(SelectedLoaderOption, LoaderOptions)
        + CountAppliedFilter(SelectedSourceOption, SourceOptions)
        + CountAppliedFilter(SelectedTypeOption, TypeOptions);

    public string FilterButtonText => AppliedFilterCount == 0
        ? Strings.Resources_ModFilterButtonLabel
        : string.Format(Strings.Resources_ModFilterButtonAppliedFormat, AppliedFilterCount);

    public string LoadingMessage => options.ProjectsLoadingText;

    public string EmptyMessage => options.ProjectsEmptyText;

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool isLoadingMore;

    [ObservableProperty]
    private string loadErrorMessage = string.Empty;

    [ObservableProperty]
    private string loadMoreMessage = string.Empty;

    [ObservableProperty]
    private string partialWarningMessage = string.Empty;

    [ObservableProperty]
    private int listEntranceAnimationToken;

    [ObservableProperty]
    private bool hasMore;

    [ObservableProperty]
    private int nextPageOffset;

    [ObservableProperty]
    private ResourcesFilterOptionItem? selectedVersionOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? selectedLoaderOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? selectedSourceOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? selectedTypeOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? pendingVersionOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? pendingLoaderOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? pendingSourceOption;

    [ObservableProperty]
    private ResourcesFilterOptionItem? pendingTypeOption;

    [ObservableProperty]
    private bool isFilterDialogOpen;

    public bool HasVisibleProjects => VisibleProjects.Count > 0;

    public bool HasLoadErrorMessage => !string.IsNullOrWhiteSpace(LoadErrorMessage);

    public bool HasPartialWarningMessage => !string.IsNullOrWhiteSpace(PartialWarningMessage);

    public bool CanShowLoadingState => IsLoading && !HasVisibleProjects;

    public bool CanShowEmptyState => !IsLoading && !HasVisibleProjects && !HasLoadErrorMessage;

    public bool CanShowLoadErrorState => !IsLoading && !HasVisibleProjects && HasLoadErrorMessage;

    public bool CanShowLoadMoreState => HasVisibleProjects
        && (IsLoadingMore || !string.IsNullOrWhiteSpace(LoadMoreMessage));

    public void BeginEnsureLoaded()
    {
        BeginEnsureVersionOptionsLoaded();
        if (hasRequestedInitialLoad || resourceCatalogService is null)
            return;

        Observe(RefreshAsync(), "load initial resource projects");
    }

    public void BeginLoadMore()
    {
        if (resourceCatalogService is null || !HasVisibleProjects || !HasMore || IsLoading || IsLoadingMore)
            return;

        Observe(LoadMoreAsync(), "load more resource projects");
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        if (resourceCatalogService is null)
            return Task.CompletedTask;

        var token = BeginRequest();
        return LoadAsync(CreateSearchRequest(0), append: false, token);
    }

    [RelayCommand]
    public Task LoadMoreAsync()
    {
        if (resourceCatalogService is null || !HasVisibleProjects || !HasMore || IsLoading || IsLoadingMore)
            return Task.CompletedTask;

        IsLoadingMore = true;
        LoadMoreMessage = options.ProjectsLoadingMoreText;
        UpdateFooter();
        return LoadAsync(CreateSearchRequest(NextPageOffset), append: true, requestCancellation?.Token ?? CancellationToken.None);
    }

    [RelayCommand]
    private void SelectProject(ResourcesModProjectItemViewModel? project)
    {
        if (project is not null)
            ProjectSelected?.Invoke(project);
    }

    [RelayCommand]
    private void OpenFilterDialog()
    {
        PendingVersionOption = SelectedVersionOption;
        PendingLoaderOption = SelectedLoaderOption;
        PendingSourceOption = SelectedSourceOption;
        PendingTypeOption = SelectedTypeOption;
        IsFilterDialogOpen = true;
    }

    [RelayCommand]
    private void CancelFilterDialog()
    {
        IsFilterDialogOpen = false;
    }

    [RelayCommand]
    private void ConfirmFilterDialog()
    {
        var changed = !ReferenceEquals(PendingVersionOption, SelectedVersionOption)
            || !ReferenceEquals(PendingLoaderOption, SelectedLoaderOption)
            || !ReferenceEquals(PendingSourceOption, SelectedSourceOption)
            || !ReferenceEquals(PendingTypeOption, SelectedTypeOption);
        try
        {
            isApplyingPendingFilters = true;
            ApplyPendingOption(PendingVersionOption, SelectedVersionOption, value => SelectedVersionOption = value);
            ApplyPendingOption(PendingLoaderOption, SelectedLoaderOption, value => SelectedLoaderOption = value);
            ApplyPendingOption(PendingSourceOption, SelectedSourceOption, value => SelectedSourceOption = value);
            ApplyPendingOption(PendingTypeOption, SelectedTypeOption, value => SelectedTypeOption = value);
        }
        finally
        {
            isApplyingPendingFilters = false;
        }
        IsFilterDialogOpen = false;
        if (changed)
        {
            NavigationResetRequested?.Invoke();
            logger?.LogDebug("Resource project filters confirmed. Kind={Kind}", options.Kind);
            ScheduleRefresh(debounce: false);
        }
    }

    public async Task ApplyInstanceFiltersAsync(GameInstance instance)
    {
        await EnsureVersionOptionsLoadedAsync().ConfigureAwait(true);

        try
        {
            isApplyingInstanceFilters = true;
            SelectedVersionOption = ResolveVersionOption(instance);
            SelectedLoaderOption = ResolveLoaderOption(instance);
        }
        finally
        {
            isApplyingInstanceFilters = false;
        }

        NavigationResetRequested?.Invoke();
        logger?.LogDebug(
            "Applied resource project filters from instance. Kind={Kind} InstanceId={InstanceId} VersionFilter={VersionFilter} LoaderFilter={LoaderFilter}",
            options.Kind,
            instance.Id,
            SelectedVersionOption?.Id,
            SelectedLoaderOption?.Id);
        await RefreshAsync().ConfigureAwait(true);
    }

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref requestCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    partial void OnSearchQueryChanged(string value) => ScheduleRefresh(debounce: true);

    partial void OnSelectedVersionOptionChanged(ResourcesFilterOptionItem? value)
    {
        RaiseAppliedFilterStateChanged();
        if (!isApplyingVersionOptions && !isApplyingInstanceFilters)
            FilterChanged("version", value);
    }

    partial void OnSelectedLoaderOptionChanged(ResourcesFilterOptionItem? value)
    {
        RaiseAppliedFilterStateChanged();
        if (!isApplyingInstanceFilters)
            FilterChanged("loader", value);
    }

    partial void OnSelectedSourceOptionChanged(ResourcesFilterOptionItem? value)
    {
        RaiseAppliedFilterStateChanged();
        FilterChanged("source", value);
    }

    partial void OnSelectedTypeOptionChanged(ResourcesFilterOptionItem? value)
    {
        RaiseAppliedFilterStateChanged();
        FilterChanged("type", value);
    }

    partial void OnIsFilterDialogOpenChanged(bool value)
    {
        if (!value)
        {
            PendingVersionOption = null;
            PendingLoaderOption = null;
            PendingSourceOption = null;
            PendingTypeOption = null;
        }
    }

    private void FilterChanged(string filter, ResourcesFilterOptionItem? value)
    {
        if (isApplyingPendingFilters)
            return;

        NavigationResetRequested?.Invoke();
        logger?.LogDebug(
            "Resource project filter selected. Kind={Kind} FilterId={FilterId} OptionId={OptionId}",
            options.Kind,
            filter,
            value?.Id);
        ScheduleRefresh(debounce: false);
    }

    private void ScheduleRefresh(bool debounce)
    {
        if (resourceCatalogService is null)
            return;

        var token = BeginRequest();
        Observe(ScheduleRefreshAsync(token, debounce), "refresh resource projects after query change");
    }

    /// <summary>
    /// 搜索输入完成防抖后、筛选变化时立即从第一页重新查询。
    /// </summary>
    private async Task ScheduleRefreshAsync(CancellationToken cancellationToken, bool debounce)
    {
        try
        {
            // 快速连续输入只保留最后一次；明确提交的筛选不增加人为等待。
            if (debounce)
                await Task.Delay(SearchDebounceMilliseconds, cancellationToken).ConfigureAwait(false);
            await LoadAsync(CreateSearchRequest(0), append: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 开启新的根查询，取消旧查询并同步重置分页与加载状态。
    /// </summary>
    private CancellationToken BeginRequest()
    {
        // 新筛选条件拥有页面状态；取消旧请求并立即重置分页，避免不同查询的结果混入同一列表。
        hasRequestedInitialLoad = true;
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref requestCancellation, replacement);
        previous?.Cancel();
        previous?.Dispose();

        // 根查询开始时分页立即失效，用户不能在旧查询仍显示时继续加载下一页。
        IsLoading = true;
        IsLoadingMore = false;
        HasMore = false;
        NextPageOffset = 0;
        LoadErrorMessage = string.Empty;
        LoadMoreMessage = string.Empty;
        PartialWarningMessage = string.Empty;
        UpdateFooter();
        RaiseStateChanged();
        return replacement.Token;
    }

    /// <summary>
    /// 并行取得项目结果与版本排序信息，再将组合后的列表发布到 UI 线程。
    /// </summary>
    private async Task LoadAsync(ResourceCatalogSearchRequest request, bool append, CancellationToken cancellationToken)
    {
        try
        {
            // 排序信息与项目搜索互不依赖，并行等待可缩短首屏时间。
            var releaseOrderTask = GetReleaseVersionOrderAsync(cancellationToken);
            var result = await resourceCatalogService!.SearchProjectsAsync(request, cancellationToken).ConfigureAwait(false);
            var releaseOrder = await releaseOrderTask.ConfigureAwait(false);
            var items = result.Projects
                .Select(project => new ResourcesModProjectItemViewModel(
                    project,
                    releaseOrder,
                    options.FallbackIconKey,
                    options.TypeOptions))
                .ToList();
            ApplyCachedThumbnailSources(items);

            // 进入 UI 线程前最后检查一次，避免排队发布已经被新筛选条件替代的结果。
            cancellationToken.ThrowIfCancellationRequested();
            // 列表物化是过渡期间最贵的一件事（实测一次 258ms）。从工作线程调 Invoke 是
            // Send 优先级，比渲染还高，会直接抢占动画帧，因此显式让位给过渡动画。
            // ApplyResult 内部已有取消检查，推迟期间请求作废时会自行放弃。
            //
            // 用可等待的版本，保持 RefreshAsync/LoadMoreAsync 的契约：任务完成即代表列表已更新。
            // 只排队不等待会让 await 提前返回，此时 VisibleProjects 和 IsLoading 都还是旧的。
            await uiDispatcher
                .PostAfterTransitionAsync(
                    () => ApplyResult(result, items, request.Offset, append, cancellationToken))
                .ConfigureAwait(false);
            if (thumbnailService is not null)
                Observe(RefreshThumbnailSourcesAsync(items, cancellationToken), "refresh resource project thumbnails");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            uiDispatcher.Invoke(() => ApplyFailure(exception, append, cancellationToken));
        }
    }

    private void ApplyCachedThumbnailSources(IEnumerable<ResourcesModProjectItemViewModel> items)
    {
        if (thumbnailService is null)
            return;

        foreach (var item in items)
        {
            try
            {
                item.SetManagedIconSource(thumbnailService.TryGetCachedThumbnailSource(item.Project));
            }
            catch (Exception exception)
            {
                item.SetManagedIconSource(null);
                logger?.LogDebug(
                    exception,
                    "Failed to resolve cached resource project thumbnail. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                    item.Project.Kind,
                    item.Project.Source,
                    item.Project.ProjectId);
            }
        }
    }

    private async Task RefreshThumbnailSourcesAsync(
        IReadOnlyList<ResourcesModProjectItemViewModel> items,
        CancellationToken cancellationToken)
    {
        if (thumbnailService is null)
            return;

        var tasks = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Project.IconUrl))
            .Select(async item =>
            {
                var source = await thumbnailService
                    .GetOrCreateThumbnailSourceAsync(item.Project, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(source) || cancellationToken.IsCancellationRequested)
                    return;

                // 每张缩略图一次回填，几十张就是几十次调度。从工作线程调 Invoke 是
                // Send 优先级，比渲染还高，密集插进动画就会连续抢占出帧。
                // 缩略图晚几十毫秒出现无所谓，因此让位给正在播的过渡动画。
                //
                // 只按取消状态判断，不再要求项目已经在 VisibleProjects 里。结果落地和
                // 缩略图回填都是排队执行的，谁先轮到并不确定；而且大结果集是分批加入集合的，
                // 后面几批在缩略图返回时本来就还没进去。要求"已在集合中"会让这些图标被静默丢弃，
                // 直到下次重新加载才出现。图标先写进项目、项目随后入列同样能正常显示。
                uiDispatcher.PostAfterTransition(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        item.SetManagedIconSource(source);
                });
            });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 应用首页或追加页结果，并把大结果集拆成多个 UI 批次。
    /// </summary>
    private void ApplyResult(
        ResourceCatalogSearchResult result,
        IReadOnlyList<ResourcesModProjectItemViewModel> items,
        int offset,
        bool append,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        if (!append)
        {
            // 新查询替换整个投影并启动入场动画；分页追加必须保留已有对象和滚动位置。
            VisibleProjects.Clear();
            ListItems.Clear();
        }

        // 游标按服务页大小推进，而不是按过滤后的数量推进，避免重复请求同一远端页。
        NextPageOffset = offset + CatalogPageSize;
        HasMore = result.HasMore && items.Count > 0;
        LoadMoreMessage = HasMore || items.Count == 0 ? string.Empty : options.ProjectsNoMoreText;
        // 某个来源不可用仍可展示其他来源结果，因此使用非阻断警告而不是整体错误页。
        PartialWarningMessage = result.IsCurseForgeApiKeyMissing ? options.CurseForgeMissingApiKeyText : string.Empty;

        var batchSize = append ? AppendProjectBatchSize : InitialProjectBatchSize;
        // 首批立即呈现，其余项分帧追加，避免一次性创建大量 WPF 项导致界面卡顿。
        AddBatch(items, 0, batchSize);
        if (!append)
            ListEntranceAnimationToken++;
        if (items.Count > batchSize)
            Observe(AppendRemainingBatchesAsync(items, batchSize, cancellationToken), "append resource project batches");

        IsLoading = false;
        IsLoadingMore = false;
        UpdateFooter();
        RaiseStateChanged();
        logger?.LogDebug(
            append ? "Resource projects appended. Kind={Kind} ResultCount={ResultCount}" : "Resource projects loaded. Kind={Kind} ResultCount={ResultCount}",
            options.Kind,
            items.Count);
    }

    /// <summary>
    /// 在后续调度周期分批追加剩余项目，避免长时间占用 UI 线程。
    /// </summary>
    private async Task AppendRemainingBatchesAsync(
        IReadOnlyList<ResourcesModProjectItemViewModel> items,
        int startIndex,
        CancellationToken cancellationToken)
    {
        for (var index = startIndex; index < items.Count; index += AppendProjectBatchSize)
        {
            // 主动让出调度权，使滚动、输入和动画能穿插在批量集合更新之间。
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var batchStart = index;
            uiDispatcher.PostAfterTransition(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    AddBatch(items, batchStart, AppendProjectBatchSize);
            });
        }
    }

    private void AddBatch(IReadOnlyList<ResourcesModProjectItemViewModel> items, int startIndex, int count)
    {
        RemoveFooter();
        foreach (var item in items.Skip(startIndex).Take(count))
        {
            VisibleProjects.Add(item);
            ListItems.Add(item);
        }
        UpdateFooter();
        RaiseStateChanged();
    }

    private void ApplyFailure(Exception exception, bool append, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        if (append)
        {
            // 加载更多失败保留已有内容，只把页脚变为错误提示，用户可以继续查看当前结果。
            IsLoadingMore = false;
            LoadMoreMessage = options.ProjectsLoadMoreErrorText;
        }
        else
        {
            // 根查询失败更新主错误状态，但不伪造一个成功的空结果。
            hasRequestedInitialLoad = false;
            IsLoading = false;
            LoadErrorMessage = options.ProjectsLoadErrorText;
        }

        UpdateFooter();
        RaiseStateChanged();
        logger?.LogError(exception, "Failed to load resource projects. Kind={Kind} Append={Append}", options.Kind, append);
    }

    private ResourceCatalogSearchRequest CreateSearchRequest(int offset)
    {
        // “全部小版本”筛选通过 MinecraftVersions 传递；单版本同时填充旧字段保持提供方兼容。
        var versions = ResolveMinecraftVersions(SelectedVersionOption);
        return new ResourceCatalogSearchRequest
        {
            Kind = options.Kind,
            Query = SearchQuery,
            MinecraftVersion = versions.Count == 1 ? versions[0] : string.Empty,
            MinecraftVersions = versions,
            Loader = !options.ShowsLoaderFilters ? LoaderKind.Vanilla : SelectedLoaderOption?.Id switch
            {
                "fabric" => LoaderKind.Fabric,
                "forge" => LoaderKind.Forge,
                "neoforge" => LoaderKind.NeoForge,
                "quilt" => LoaderKind.Quilt,
                _ => LoaderKind.Vanilla
            },
            Source = SelectedSourceOption?.Id switch
            {
                "modrinth" => ResourceProjectSource.Modrinth,
                "curseforge" => ResourceProjectSource.CurseForge,
                _ => null
            },
            Category = ResolveCategory(SelectedTypeOption),
            Offset = offset,
            PageSize = CatalogPageSize
        };
    }

    private async Task<IReadOnlyList<string>?> GetReleaseVersionOrderAsync(CancellationToken cancellationToken)
    {
        return (await GetReleaseVersionDataAsync(cancellationToken).ConfigureAwait(false)).ReleaseVersionOrder;
    }

    private void BeginEnsureVersionOptionsLoaded()
    {
        if (gameVersionService is not null)
            Observe(GetReleaseVersionDataAsync(CancellationToken.None), "load resource version filters");
    }

    private async Task EnsureVersionOptionsLoadedAsync()
    {
        var data = await GetReleaseVersionDataAsync(CancellationToken.None).ConfigureAwait(false);
        if (data.VersionOptions.Count > 0)
            uiDispatcher.Invoke(() => ApplyVersionOptions(data.VersionOptions));
    }

    /// <summary>
    /// 共享并缓存 Minecraft 正式版本清单；每个调用方可独立取消等待。
    /// </summary>
    private async Task<ReleaseVersionData> GetReleaseVersionDataAsync(CancellationToken cancellationToken)
    {
        if (gameVersionService is null)
            return new ReleaseVersionData([], []);

        Task<ReleaseVersionData> task;
        lock (releaseVersionGate)
        {
            // 排序和筛选共享同一次版本清单请求；调用方取消等待不应取消这份可复用缓存。
            releaseVersionDataTask ??= LoadReleaseVersionDataAsync();
            task = releaseVersionDataTask;
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReleaseVersionData> LoadReleaseVersionDataAsync()
    {
        try
        {
            // 缓存任务自身不接收页面取消令牌；某个页面离开不会浪费已经开始的共享请求。
            var versions = await gameVersionService!.GetVersionsAsync().ConfigureAwait(false);
            var order = versions
                .Where(version => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase))
                .Select(version => version.Name)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .ToList();
            var filterOptions = CreateVersionFilterOptions(order);
            uiDispatcher.Post(() => ApplyVersionOptions(filterOptions));
            return new ReleaseVersionData(order, filterOptions);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Failed to load Minecraft release versions for resource filters. Kind={Kind}", options.Kind);
            return new ReleaseVersionData([], []);
        }
    }

    private void ApplyVersionOptions(IReadOnlyList<ResourcesFilterOptionItem> values)
    {
        if (values.Count == 0)
            return;

        var selectedId = SelectedVersionOption?.Id ?? "all";
        VersionOptions.Clear();
        VersionOptions.Add(new ResourcesFilterOptionItem { Id = "all", Title = options.AllVersionsText });
        foreach (var option in values)
            VersionOptions.Add(option);

        try
        {
            // 重建选项会触发 SelectedVersionOption 回调，标志用于阻止无意义的项目刷新。
            isApplyingVersionOptions = true;
            SelectedVersionOption = VersionOptions.FirstOrDefault(option =>
                string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? VersionOptions[0];
        }
        finally
        {
            isApplyingVersionOptions = false;
        }
    }

    private ResourcesFilterOptionItem ResolveVersionOption(GameInstance instance)
    {
        var all = VersionOptions.FirstOrDefault(option => option.Id == "all") ?? VersionOptions[0];
        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion))
            return all;

        return VersionOptions.FirstOrDefault(option => option.Id != "all"
            && (string.Equals(option.Id, instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
                || option.MinecraftVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))) ?? all;
    }

    private ResourcesFilterOptionItem ResolveLoaderOption(GameInstance instance)
    {
        var id = instance.Loader switch
        {
            LoaderKind.Fabric => "fabric",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            LoaderKind.Quilt => "quilt",
            _ => "all"
        };
        return LoaderOptions.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? LoaderOptions[0];
    }

    private void UpdateFooter()
    {
        RemoveFooter();
        if (CanShowLoadMoreState && !string.IsNullOrWhiteSpace(LoadMoreMessage))
            ListItems.Add(new ResourcesListFooterStatusItem(LoadMoreMessage));
    }

    private void RemoveFooter()
    {
        for (var index = ListItems.Count - 1; index >= 0; index--)
        {
            if (ListItems[index] is ResourcesListFooterStatusItem)
                ListItems.RemoveAt(index);
        }
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasVisibleProjects));
        OnPropertyChanged(nameof(HasLoadErrorMessage));
        OnPropertyChanged(nameof(HasPartialWarningMessage));
        OnPropertyChanged(nameof(CanShowLoadingState));
        OnPropertyChanged(nameof(CanShowEmptyState));
        OnPropertyChanged(nameof(CanShowLoadErrorState));
        OnPropertyChanged(nameof(CanShowLoadMoreState));
    }

    private void Observe(Task task, string operation)
    {
        _ = ObserveAsync(task, operation);
    }

    private async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Unhandled resource list operation failure. Operation={Operation} Kind={Kind}", operation, options.Kind);
        }
    }

    private static void ApplyPendingOption(
        ResourcesFilterOptionItem? pending,
        ResourcesFilterOptionItem? current,
        Action<ResourcesFilterOptionItem?> apply)
    {
        if (!ReferenceEquals(pending, current))
            apply(pending);
    }

    private static int CountAppliedFilter(
        ResourcesFilterOptionItem? selected,
        IReadOnlyList<ResourcesFilterOptionItem> options)
    {
        if (selected is null || options.Count == 0)
            return 0;

        return string.Equals(selected.Id, options[0].Id, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private void RaiseAppliedFilterStateChanged()
    {
        OnPropertyChanged(nameof(AppliedFilterCount));
        OnPropertyChanged(nameof(FilterButtonText));
    }

    private static IReadOnlyList<string> ResolveMinecraftVersions(ResourcesFilterOptionItem? option)
    {
        if (option is null || option.Id == "all")
            return [];
        return option.MinecraftVersions.Count > 0 ? option.MinecraftVersions : [option.Id];
    }

    private ResourceProjectCategory? ResolveCategory(ResourcesFilterOptionItem? option)
    {
        if (option is null || option.Id == "all")
            return null;
        return options.TypeOptions.FirstOrDefault(value =>
            string.Equals(value.Id, option.Id, StringComparison.OrdinalIgnoreCase))?.Category;
    }

    private static IReadOnlyList<ResourcesFilterOptionItem> CreateVersionFilterOptions(IReadOnlyList<string> releases)
    {
        return releases
            .Select(version => new { Original = version.Trim(), Major = TryNormalizeMajorMinecraftVersion(version) })
            .Where(version => !string.IsNullOrWhiteSpace(version.Original) && version.Major is not null)
            .GroupBy(version => version.Major!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResourcesFilterOptionItem
            {
                Id = group.Key,
                Title = group.Key,
                MinecraftVersions = group.Select(value => value.Original).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();
    }

    private static string? TryNormalizeMajorMinecraftVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var parts = version.Trim().Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out var single))
            return single.ToString();
        if (parts.Length < 2 || !TryParseLeadingNumber(parts[0], out var major) || !TryParseLeadingNumber(parts[1], out var minor))
            return null;
        return major == 1 ? $"{major}.{minor}" : major.ToString();
    }

    private static bool TryParseLeadingNumber(string value, out int number)
    {
        var length = value.TakeWhile(char.IsDigit).Count();
        return int.TryParse(value[..length], out number);
    }

    private static ObservableCollection<ResourcesFilterOptionItem> CreateLoaderOptions(ResourcesOnlineProjectPageOptions options)
    {
        if (!options.ShowsLoaderFilters)
            return [new ResourcesFilterOptionItem { Id = "all", Title = options.AllLoadersText }];
        return
        [
            new ResourcesFilterOptionItem { Id = "all", Title = options.AllLoadersText },
            new ResourcesFilterOptionItem { Id = "fabric", Title = Strings.Download_FabricLoaderTitle },
            new ResourcesFilterOptionItem { Id = "forge", Title = Strings.Download_ForgeLoaderTitle },
            new ResourcesFilterOptionItem { Id = "neoforge", Title = Strings.Download_NeoForgeLoaderTitle },
            new ResourcesFilterOptionItem { Id = "quilt", Title = Strings.Download_QuiltLoaderTitle }
        ];
    }

    private static ObservableCollection<ResourcesFilterOptionItem> CreateSourceOptions(ResourcesOnlineProjectPageOptions options)
    {
        if (options.SourceOptions is { Count: > 0 } configured)
            return [.. configured];
        return
        [
            new ResourcesFilterOptionItem { Id = "all", Title = Strings.Resources_ModFilterAllSources },
            new ResourcesFilterOptionItem { Id = "modrinth", Title = Strings.Resources_ModSourceModrinth },
            new ResourcesFilterOptionItem { Id = "curseforge", Title = Strings.Resources_ModSourceCurseForge }
        ];
    }

    private static ObservableCollection<ResourcesFilterOptionItem> CreateTypeOptions(ResourcesOnlineProjectPageOptions options)
    {
        var values = new ObservableCollection<ResourcesFilterOptionItem>
        {
            new() { Id = "all", Title = Strings.Resources_ModFilterAllTypes }
        };
        foreach (var option in options.TypeOptions)
            values.Add(new ResourcesFilterOptionItem { Id = option.Id, Title = option.Title });
        return values;
    }

    private sealed record ReleaseVersionData(
        IReadOnlyList<string> ReleaseVersionOrder,
        IReadOnlyList<ResourcesFilterOptionItem> VersionOptions);
}
