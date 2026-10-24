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
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

/// <summary>
/// 为选定在线项目管理安装目标、兼容版本分页、筛选投影和安装命令状态。
/// </summary>
public sealed partial class ResourcesProjectVersionsViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 10000;

    // 分页来源可能返回重叠结果，稳定 ID 集合确保同一版本只进入页面一次。
    private readonly HashSet<string> loadedVersionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ResourcesOnlineProjectPageOptions options;
    private readonly IResourceCatalogService? resourceCatalogService;
    private readonly Func<IReadOnlyList<GameInstance>>? getInstanceCatalogSnapshot;
    private readonly IUiDispatcher uiDispatcher;
    private readonly ILogger? logger;
    // 目标列表与版本列表具有独立生命周期，切换目标时只需使版本请求失效。
    private CancellationTokenSource? targetsCancellation;
    private CancellationTokenSource? versionsCancellation;
    private bool isApplyingFilters;
    private ResourcesModProjectItemViewModel? currentProject;

    internal ResourcesProjectVersionsViewModel(
        ResourcesOnlineProjectPageOptions options,
        IResourceCatalogService? resourceCatalogService,
        Func<IReadOnlyList<GameInstance>>? getInstanceCatalogSnapshot,
        IUiDispatcher uiDispatcher,
        ILogger? logger)
    {
        this.options = options;
        this.resourceCatalogService = resourceCatalogService;
        this.getInstanceCatalogSnapshot = getInstanceCatalogSnapshot;
        this.uiDispatcher = uiDispatcher;
        this.logger = logger;
        Builder = new ResourcesAvailableVersionListBuilder(options);
        ResetFilterOptions();
    }

    public event Action<ResourcesModInstallTargetItemViewModel>? TargetSelected;

    public event Action<ResourcesModVersionItemViewModel>? InstallRequested;

    internal ResourcesAvailableVersionListBuilder Builder { get; }

    public ObservableCollection<ResourcesModInstallTargetItemViewModel> InstallTargets { get; } = [];

    public ObservableCollection<object> ListItems { get; } = [];

    public ObservableCollection<ResourcesFilterOptionItem> VersionFilterOptions { get; } = [];

    public ObservableCollection<ResourcesFilterOptionItem> LoaderFilterOptions { get; } = [];

    public IReadOnlyList<ResourceProjectVersion> SourceVersions { get; private set; } = [];

    public int NextPageOffset { get; private set; }

    public string InstallTargetSectionText => options.InstallTargetSectionText;

    public string InstallTargetsLoadingMessage => options.InstallTargetsLoadingText;

    public string LoadingMessage => options.VersionsLoadingText;

    public string Title => Builder.FormatTitle(SelectedTarget);

    [ObservableProperty]
    private ResourcesModInstallTargetItemViewModel? selectedTarget;

    [ObservableProperty]
    private bool isLoadingTargets;

    [ObservableProperty]
    private string targetsLoadErrorMessage = string.Empty;

    [ObservableProperty]
    private int installTargetsEntranceAnimationToken;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string loadErrorMessage = string.Empty;

    [ObservableProperty]
    private bool isLoadingMore;

    [ObservableProperty]
    private string loadMoreMessage = string.Empty;

    [ObservableProperty]
    private bool hasMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleVersions))]
    private int visibleVersionCount;

    [ObservableProperty]
    private int listEntranceAnimationToken;

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private ResourcesFilterOptionItem? selectedVersionFilter;

    [ObservableProperty]
    private ResourcesFilterOptionItem? selectedLoaderFilter;

    [ObservableProperty]
    private bool isUnknownInstanceVersionDialogOpen;

    public bool HasTargetsLoadErrorMessage => !string.IsNullOrWhiteSpace(TargetsLoadErrorMessage);

    public bool CanShowTargetsLoadingState => IsLoadingTargets && InstallTargets.Count == 0;

    public bool CanShowTargetsLoadErrorState => !IsLoadingTargets && HasTargetsLoadErrorMessage;

    public bool HasInstallTargets => InstallTargets.Count > 0;

    public bool HasLoadErrorMessage => !string.IsNullOrWhiteSpace(LoadErrorMessage);

    public bool HasVisibleVersions => VisibleVersionCount > 0;

    public bool HasFilters => !string.IsNullOrWhiteSpace(SearchQuery)
        || SelectedVersionFilter?.Id is { } versionId && !string.Equals(versionId, "all", StringComparison.OrdinalIgnoreCase)
        || options.ShowsLoaderFilters && SelectedLoaderFilter?.Id is { } loaderId && !string.Equals(loaderId, "all", StringComparison.OrdinalIgnoreCase);

    public string EmptyMessage => HasFilters
        ? options.VersionsFilterEmptyText
        : SelectedTarget?.IsLocalDownload == true || ResourcesAvailableVersionListBuilder.IsUnknownInstanceVersionTarget(SelectedTarget)
            ? options.VersionsEmptyLocalText
            : options.VersionsEmptyText;

    public bool CanShowLoadingState => IsLoading && VisibleVersionCount == 0;

    public bool CanShowEmptyState => !IsLoading && VisibleVersionCount == 0 && !HasMore && !HasLoadErrorMessage;

    public bool CanShowLoadErrorState => !IsLoading && HasLoadErrorMessage;

    public bool CanShowLoadMoreState => VisibleVersionCount > 0
        && (IsLoadingMore || !string.IsNullOrWhiteSpace(LoadMoreMessage));

    public void SetProject(ResourcesModProjectItemViewModel project)
    {
        currentProject = project;
        SelectedTarget = null;
        CancelVersionsLoad();
        ResetVersions(resetFilters: true);
        _ = LoadTargetsAsync();
    }

    public void Reset()
    {
        currentProject = null;
        SelectedTarget = null;
        CancelTargetsLoad();
        CancelVersionsLoad();
        InstallTargets.Clear();
        TargetsLoadErrorMessage = string.Empty;
        IsLoadingTargets = false;
        IsUnknownInstanceVersionDialogOpen = false;
        ResetVersions(resetFilters: true);
        RaiseTargetStateChanged();
    }

    [RelayCommand]
    private void SelectTarget(ResourcesModInstallTargetItemViewModel? target)
    {
        if (target is null || currentProject is null || resourceCatalogService is null)
            return;

        SelectedTarget = target;
        IsUnknownInstanceVersionDialogOpen = ResourcesAvailableVersionListBuilder.IsUnknownInstanceVersionTarget(target);
        TargetSelected?.Invoke(target);
        _ = RefreshAsync();
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        var target = SelectedTarget;
        if (resourceCatalogService is null || currentProject is null || target is null)
            return Task.CompletedTask;

        return LoadVersionsAsync(target);
    }

    [RelayCommand]
    private void InstallVersion(ResourcesModVersionItemViewModel? item)
    {
        if (item is not null)
            InstallRequested?.Invoke(item);
    }

    [RelayCommand]
    private void CloseUnknownInstanceVersionDialog()
    {
        IsUnknownInstanceVersionDialogOpen = false;
    }

    public void BeginLoadMore()
    {
        if (resourceCatalogService is null || currentProject is null || SelectedTarget is null
            || !HasMore || IsLoading || IsLoadingMore)
        {
            return;
        }

        _ = LoadMoreAsync();
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        var project = currentProject;
        if (resourceCatalogService is null || project is null || SelectedTarget is null
            || !HasMore || IsLoading || IsLoadingMore)
        {
            return;
        }

        var cancellationToken = versionsCancellation?.Token ?? CancellationToken.None;
        IsLoadingMore = true;
        LoadMoreMessage = options.VersionsLoadingMoreText;
        UpdateFooter();
        RaiseStateChanged();
        try
        {
            var page = await LoadNextPageAsync(project.Project, SelectedTarget, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            uiDispatcher.Invoke(() => ApplyMore(page, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            uiDispatcher.Invoke(() =>
            {
                IsLoadingMore = false;
                LoadMoreMessage = options.VersionsLoadMoreErrorText;
                UpdateFooter();
                RaiseStateChanged();
            });
            logger?.LogError(exception, "Failed to load more resource versions. Kind={Kind}", options.Kind);
        }
    }

    public void Dispose()
    {
        CancelTargetsLoad();
        CancelVersionsLoad();
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (!isApplyingFilters)
            RebuildList();
    }

    partial void OnSelectedVersionFilterChanged(ResourcesFilterOptionItem? value)
    {
        if (!isApplyingFilters)
            RebuildList();
    }

    partial void OnSelectedLoaderFilterChanged(ResourcesFilterOptionItem? value)
    {
        if (!isApplyingFilters)
            RebuildList();
    }

    partial void OnSelectedTargetChanged(ResourcesModInstallTargetItemViewModel? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// 按当前安装模式加载可选实例，并加入新实例或本地下载等虚拟目标。
    /// </summary>
    private Task LoadTargetsAsync()
    {
        // 新加载先取消旧目标请求，随后立即清空列表，避免用户点击已不属于当前项目的目标。
        // 项目或安装模式变化后，只有最新一次目标加载可以更新选择列表。
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref targetsCancellation, replacement);
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = replacement.Token;

        uiDispatcher.Invoke(() =>
        {
            InstallTargets.Clear();
            TargetsLoadErrorMessage = string.Empty;
            IsLoadingTargets = true;
            RaiseTargetStateChanged();
        });

        try
        {
            // 新实例安装模式不需要读取本地实例，减少一次仓库和磁盘发现操作。
            IReadOnlyList<GameInstance> instances = [];
            if (getInstanceCatalogSnapshot is not null
                && options.InstallTargetMode is ResourcesOnlineProjectInstallTargetMode.ExistingInstance)
            {
                uiDispatcher.Invoke(() => instances = getInstanceCatalogSnapshot());
            }

            cancellationToken.ThrowIfCancellationRequested();
            uiDispatcher.Invoke(() => ApplyTargets(instances, string.Empty, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.CompletedTask;
            uiDispatcher.Invoke(() => ApplyTargets([], options.InstallTargetsLoadErrorText, cancellationToken));
            logger?.LogError(exception, "Failed to load resource install targets. Kind={Kind}", options.Kind);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 过滤不兼容目标并重建带首尾位置状态的安装目标列表。
    /// </summary>
    private void ApplyTargets(IReadOnlyList<GameInstance> instances, string error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        // Vanilla 实例不能直接安装 Mod 项目；本地下载目标始终可用且不受实例兼容限制。
        var targets = instances
            .Where(instance => options.Kind is not ResourceProjectKind.Mod
                || instance.Loader is not LoaderKind.Vanilla)
            .Select(ResourcesModInstallTargetItemViewModel.FromInstance)
            .ToList();
        if (options.InstallTargetMode is ResourcesOnlineProjectInstallTargetMode.NewInstance)
        {
            targets =
            [
                ResourcesModInstallTargetItemViewModel.CreateNewInstanceInstall(
                    options.InstallTargetNewInstanceText ?? options.InstallTargetSectionText),
                ResourcesModInstallTargetItemViewModel.CreateServerInstall(
                    options.InstallTargetServerText ?? Strings.Resources_ModpackInstallTargetServer),
                ResourcesModInstallTargetItemViewModel.CreateLocalDownload(options.InstallTargetLocalText)
            ];
        }
        else
        {
            targets.Add(ResourcesModInstallTargetItemViewModel.CreateLocalDownload(options.InstallTargetLocalText));
        }

        for (var index = 0; index < targets.Count; index++)
            targets[index].SetVisiblePosition(index == 0, index == targets.Count - 1);

        InstallTargets.Clear();
        foreach (var target in targets)
            InstallTargets.Add(target);
        TargetsLoadErrorMessage = error;
        IsLoadingTargets = false;
        RaiseTargetStateChanged();
        if (InstallTargets.Count > 0)
            InstallTargetsEntranceAnimationToken++;
    }

    /// <summary>
    /// 为选定目标重新加载项目版本，并重置旧目标对应的分页和筛选状态。
    /// </summary>
    private async Task LoadVersionsAsync(ResourcesModInstallTargetItemViewModel target)
    {
        // 目标切换会改变版本兼容条件，因此重置分页、筛选和旧请求必须作为同一状态转换完成。
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref versionsCancellation, replacement);
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = replacement.Token;

        // 在 UI 线程原子重置标题、分页和筛选，避免模板观察到新旧状态混合。
        uiDispatcher.Invoke(() =>
        {
            ResetVersions(resetFilters: true);
            ListItems.Add(new ResourcesModVersionListHeaderItem(Title));
            IsLoading = true;
            RaiseStateChanged();
        });

        try
        {
            // 等待期间项目或目标可能变化，引用身份检查可拒绝旧选择发起的请求。
            var project = currentProject;
            if (resourceCatalogService is null || project is null
                || !ReferenceEquals(target, SelectedTarget)
                || !target.IsLocalDownload && !target.IsNewInstanceInstall && !target.IsServerInstall && target.Instance is null)
            {
                uiDispatcher.Invoke(() => ApplyInitial(new AvailableVersionPage(new ResourceProjectVersionsResult(), []), options.VersionsLoadErrorText, cancellationToken));
                return;
            }

            var page = await LoadNextPageAsync(project.Project, target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var error = page.Result.IsCurseForgeUnavailable ? options.VersionsLoadErrorText : string.Empty;
            uiDispatcher.Invoke(() => ApplyInitial(page, error, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            uiDispatcher.Invoke(() => ApplyInitial(new AvailableVersionPage(new ResourceProjectVersionsResult(), []), options.VersionsLoadErrorText, cancellationToken));
            logger?.LogError(exception, "Failed to load resource project versions. Kind={Kind}", options.Kind);
        }
    }

    /// <summary>
    /// 请求下一页全部候选版本，并在进入页面状态前去除跨页重复项。
    /// </summary>
    private async Task<AvailableVersionPage> LoadNextPageAsync(
        ResourceProject project,
        ResourcesModInstallTargetItemViewModel target,
        CancellationToken cancellationToken)
    {
        // 先请求全部版本再在本地按目标筛选，用户切换筛选项无需重复访问网络。
        var request = new ResourceProjectVersionsRequest
        {
            Kind = options.Kind,
            Source = project.Source,
            ProjectId = project.ProjectId,
            Slug = project.Slug,
            MinecraftVersion = string.Empty,
            Loader = LoaderKind.Vanilla,
            IncludeAllVersions = true,
            ForServerInstallation = target.IsServerInstall,
            Offset = NextPageOffset,
            PageSize = PageSize
        };
        var result = await resourceCatalogService!.GetProjectVersionsAsync(request, cancellationToken).ConfigureAwait(false);
        var accepted = AcceptPage(result.Versions, request.Offset, request.PageSize);
        return new AvailableVersionPage(result, accepted);
    }

    /// <summary>
    /// 接收一页从未见过的版本，并无论去重结果如何都推进远端分页游标。
    /// </summary>
    private IReadOnlyList<ResourceProjectVersion> AcceptPage(
        IReadOnlyList<ResourceProjectVersion> versions,
        int requestOffset,
        int pageSize)
    {
        // 聚合来源可能在相邻分页返回重复版本；优先用稳定 ID，旧数据缺少 ID 时退化为复合键。
        var accepted = new List<ResourceProjectVersion>(versions.Count);
        foreach (var version in versions)
        {
            // 旧来源缺少 VersionId 时使用文件名、版本号和发布时间组合成稳定兜底键。
            var key = string.IsNullOrWhiteSpace(version.VersionId)
                ? $"{version.FileName}|{version.VersionNumber}|{version.PublishedAt:O}"
                : version.VersionId;
            if (!string.IsNullOrWhiteSpace(key) && loadedVersionIds.Add(key))
                accepted.Add(version);
        }
        NextPageOffset = requestOffset + pageSize;
        return accepted;
    }

    private void ApplyInitial(AvailableVersionPage page, string error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        SourceVersions = page.Versions.ToList();
        ApplyDefaultFilters(SourceVersions, SelectedTarget);
        RebuildList();
        LoadErrorMessage = error;
        HasMore = !page.Result.IsCurseForgeUnavailable && page.Result.HasMore;
        LoadMoreMessage = HasMore ? string.Empty : options.VersionsNoMoreText;
        IsLoading = false;
        IsLoadingMore = false;
        UpdateFooter();
        RaiseStateChanged();
    }

    /// <summary>
    /// 合并追加页并保留现有筛选选择；追加时不重复播放整列表入场动画。
    /// </summary>
    private void ApplyMore(AvailableVersionPage page, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        // 先扩充完整源集合，再根据所有页重算可选筛选值，保留当前仍有效的选择。
        SourceVersions = SourceVersions.Concat(page.Versions).ToList();
        UpdateFiltersPreservingSelection(SourceVersions);
        RebuildList(playEntranceAnimation: false);
        HasMore = !page.Result.IsCurseForgeUnavailable && page.Result.HasMore && page.Versions.Count > 0;
        LoadMoreMessage = HasMore ? string.Empty : options.VersionsNoMoreText;
        IsLoadingMore = false;
        UpdateFooter();
        RaiseStateChanged();
    }

    private void ResetVersions(bool resetFilters)
    {
        listRevision++;
        groupTransitionGenerations.Clear();
        SourceVersions = [];
        NextPageOffset = 0;
        loadedVersionIds.Clear();
        groupExpansionStates.Clear();
        groupVersions = new Dictionary<string, IReadOnlyList<ResourceProjectVersion>>(StringComparer.OrdinalIgnoreCase);
        ListItems.Clear();
        VisibleVersionCount = 0;
        HasMore = false;
        IsLoading = false;
        IsLoadingMore = false;
        LoadErrorMessage = string.Empty;
        LoadMoreMessage = string.Empty;
        if (resetFilters)
        {
            try
            {
                isApplyingFilters = true;
                SearchQuery = string.Empty;
                ResetFilterOptions();
            }
            finally
            {
                isApplyingFilters = false;
            }
        }
        RaiseStateChanged();
    }

    private void ResetFilterOptions()
    {
        VersionFilterOptions.Clear();
        VersionFilterOptions.Add(Builder.CreateAllVersionFilterOption());
        LoaderFilterOptions.Clear();
        foreach (var option in Builder.CreateDefaultLoaderFilterOptions())
            LoaderFilterOptions.Add(option);
        SelectedVersionFilter = VersionFilterOptions[0];
        SelectedLoaderFilter = LoaderFilterOptions[0];
    }

    private void ApplyDefaultFilters(
        IReadOnlyList<ResourceProjectVersion> versions,
        ResourcesModInstallTargetItemViewModel? target)
    {
        ApplyFilters(versions, Builder.ResolveDefaultVersionFilterId(target), Builder.ResolveDefaultLoaderFilterId(target));
    }

    private void UpdateFiltersPreservingSelection(IReadOnlyList<ResourceProjectVersion> versions)
    {
        ApplyFilters(versions, SelectedVersionFilter?.Id ?? "all", SelectedLoaderFilter?.Id ?? "all");
    }

    private void ApplyFilters(IReadOnlyList<ResourceProjectVersion> versions, string versionId, string loaderId)
    {
        try
        {
            isApplyingFilters = true;
            VersionFilterOptions.Clear();
            VersionFilterOptions.Add(Builder.CreateAllVersionFilterOption());
            foreach (var option in Builder.CreateVersionFilterOptions(versions))
                VersionFilterOptions.Add(option);
            LoaderFilterOptions.Clear();
            foreach (var option in Builder.CreateLoaderFilterOptions(versions))
                LoaderFilterOptions.Add(option);
            EnsureFilter(VersionFilterOptions, versionId, id => id);
            EnsureFilter(LoaderFilterOptions, loaderId, Builder.GetLoaderTitle);
            SelectedVersionFilter = VersionFilterOptions.First(option => string.Equals(option.Id, versionId, StringComparison.OrdinalIgnoreCase));
            SelectedLoaderFilter = LoaderFilterOptions.First(option => string.Equals(option.Id, loaderId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            isApplyingFilters = false;
        }
    }

    private static void EnsureFilter(
        ICollection<ResourcesFilterOptionItem> values,
        string id,
        Func<string, string> titleFactory)
    {
        if (string.IsNullOrWhiteSpace(id) || values.Any(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase)))
            return;
        values.Add(new ResourcesFilterOptionItem { Id = id, Title = titleFactory(id) });
    }

    /// <summary>
    /// 根据目标、搜索词与兼容筛选重建当前可见版本投影。
    /// </summary>
    private void RebuildList(bool playEntranceAnimation = true)
    {
        listRevision++;
        groupTransitionGenerations.Clear();
        // Builder 集中处理 Minecraft/Loader 兼容、搜索和异构标题项，本类只发布构建结果。
        ListItems.Clear();
        var result = Builder.Build(
            SourceVersions,
            Title,
            currentProject,
            options.FallbackIconKey,
            SelectedVersionFilter?.Id,
            SelectedLoaderFilter?.Id,
            SearchQuery,
            groupExpansionStates,
            SetGroupExpanded);
        groupVersions = result.GroupVersions;
        foreach (var item in result.Items)
            ListItems.Add(item);
        VisibleVersionCount = result.VisibleVersionCount;
        // 初次加载或目标切换播放动画；分页追加显式关闭以保持滚动连续性。
        if (playEntranceAnimation)
            ListEntranceAnimationToken++;
        UpdateFooter();
        RaiseStateChanged();
    }

    private void UpdateFooter()
    {
        for (var index = ListItems.Count - 1; index >= 0; index--)
        {
            if (ListItems[index] is ResourcesListFooterStatusItem)
                ListItems.RemoveAt(index);
        }
        if (CanShowLoadMoreState && !string.IsNullOrWhiteSpace(LoadMoreMessage))
            ListItems.Add(new ResourcesListFooterStatusItem(LoadMoreMessage));
    }

    private void RaiseTargetStateChanged()
    {
        OnPropertyChanged(nameof(HasInstallTargets));
        OnPropertyChanged(nameof(HasTargetsLoadErrorMessage));
        OnPropertyChanged(nameof(CanShowTargetsLoadingState));
        OnPropertyChanged(nameof(CanShowTargetsLoadErrorState));
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasLoadErrorMessage));
        OnPropertyChanged(nameof(HasFilters));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(CanShowLoadingState));
        OnPropertyChanged(nameof(CanShowEmptyState));
        OnPropertyChanged(nameof(CanShowLoadErrorState));
        OnPropertyChanged(nameof(CanShowLoadMoreState));
        OnPropertyChanged(nameof(CanAutoLoadMoreVersions));
    }

    private void CancelTargetsLoad()
    {
        var cancellation = Interlocked.Exchange(ref targetsCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void CancelVersionsLoad()
    {
        var cancellation = Interlocked.Exchange(ref versionsCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private sealed record AvailableVersionPage(
        ResourceProjectVersionsResult Result,
        IReadOnlyList<ResourceProjectVersion> Versions);
}
