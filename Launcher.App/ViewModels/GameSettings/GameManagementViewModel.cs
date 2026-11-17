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
using Launcher.App.Models;
using Launcher.App.Services;
using Launcher.App.Utilities;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 为 Shell 聚合实例、创建、在线资源搜索和设置子 ViewModel，并转发常用命令与进度状态。
/// </summary>
public sealed partial class GameManagementViewModel : ObservableObject
{
    // 实例内容由当前详情分区独占；Shell 门面不持有任何本地内容 watcher。
    private readonly IStatusService statusService;

    [ObservableProperty]
    private double progressPercent;

    public GameManagementViewModel(
        InstanceManagementViewModel instances,
        LoaderSelectionViewModel loaderSelection,
        ModrinthSearchViewModel modrinthSearch,
        IStatusService statusService)
    {
        InstancesViewModel = instances;
        LoaderSelection = loaderSelection;
        ModrinthSearch = modrinthSearch;
        this.statusService = statusService;

        InstancesViewModel.PropertyChanged += InstancesViewModel_PropertyChanged;
        LoaderSelection.PropertyChanged += ForwardChildPropertyChanged;
        ModrinthSearch.PropertyChanged += ForwardChildPropertyChanged;
    }

    public InstanceManagementViewModel InstancesViewModel { get; }

    public LoaderSelectionViewModel LoaderSelection { get; }

    public ModrinthSearchViewModel ModrinthSearch { get; }

    public ObservableCollection<GameInstance> Instances => InstancesViewModel.Instances;
    public long InstanceCatalogRevision => InstancesViewModel.CatalogRevision;
    public ObservableCollection<MinecraftVersionInfo> MinecraftVersions => LoaderSelection.MinecraftVersions;
    public ObservableCollection<NavigationItem> LoaderItems => LoaderSelection.LoaderItems;
    public ObservableCollection<LoaderVersionInfo> LoaderVersions => LoaderSelection.LoaderVersions;
    public ObservableCollection<ModrinthProject> ModrinthProjects => ModrinthSearch.ModrinthProjects;

    public GameInstance? SelectedInstance
    {
        get => InstancesViewModel.SelectedInstance;
        set => InstancesViewModel.SelectedInstance = value;
    }

    public IReadOnlyList<GameInstance> GetInstanceCatalogSnapshot()
    {
        return Instances.ToArray();
    }

    public MinecraftVersionInfo? SelectedMinecraftVersion
    {
        get => LoaderSelection.SelectedMinecraftVersion;
        set => LoaderSelection.SelectedMinecraftVersion = value;
    }

    public LoaderKind SelectedLoader
    {
        get => LoaderSelection.SelectedLoader;
        set => LoaderSelection.SelectedLoader = value;
    }

    public LoaderVersionInfo? SelectedLoaderVersion
    {
        get => LoaderSelection.SelectedLoaderVersion;
        set => LoaderSelection.SelectedLoaderVersion = value;
    }

    public string NewInstanceName
    {
        get => InstancesViewModel.NewInstanceName;
        set => InstancesViewModel.NewInstanceName = value;
    }

    public string ModSearchQuery
    {
        get => ModrinthSearch.ModSearchQuery;
        set => ModrinthSearch.ModSearchQuery = value;
    }

    public ModrinthProject? SelectedModrinthProject
    {
        get => ModrinthSearch.SelectedModrinthProject;
        set => ModrinthSearch.SelectedModrinthProject = value;
    }

    public async Task InitializeAsync(LauncherSettings launcherSettings)
    {
        // 先 Prime 设置与磁盘快照，再执行需要等待的版本目录同步。
        LoaderSelection.PrimeFromSettings(launcherSettings);
        await InstancesViewModel.InitializeAsync(launcherSettings);
    }

    public Task PrimeInstancesAsync(LauncherSettings launcherSettings)
    {
        return InstancesViewModel.PrimeInstancesAsync(launcherSettings);
    }

    public void ApplyDownloadSourcePreference(DownloadSourcePreference preference)
    {
        LoaderSelection.ApplyDownloadSourcePreference(preference);
    }

    public void ApplyDownloadSpeedLimit(int downloadSpeedLimitMbPerSecond)
    {
        LoaderSelection.ApplyDownloadSpeedLimit(downloadSpeedLimitMbPerSecond);
    }

    public async Task EnsureInstancesLoadedAsync()
    {
        if (InstancesViewModel.HasLoadedInstances)
            return;

        await InstancesViewModel.EnsureInstancesLoadedAsync();
    }

    public void SelectLoader(LoaderKind loader)
    {
        LoaderSelection.SelectLoader(loader);
    }

    public Task<bool> SelectLaunchInstanceAsync(GameInstance instance)
    {
        return InstancesViewModel.SelectLaunchInstanceAsync(instance);
    }

    public Task ApplyUpdatedInstanceAsync(GameInstance instance, string? previousInstanceDirectory = null)
    {
        // 保存产生的新实例对象按目录增量合并，避免全量刷新打断当前页面选择。
        return InstancesViewModel.ApplyUpdatedInstanceAsync(instance, previousInstanceDirectory);
    }

    public Task<bool> RemoveInstanceAsync(string instanceDirectory)
    {
        return InstancesViewModel.RemoveInstanceAsync(instanceDirectory);
    }

    [RelayCommand]
    private Task LoadMinecraftVersionsAsync()
    {
        return LoaderSelection.LoadMinecraftVersionsAsync();
    }

    [RelayCommand]
    public Task RefreshInstancesAsync()
    {
        return InstancesViewModel.RefreshInstancesAsync();
    }

    [RelayCommand]
    private Task LoadLoaderVersionsAsync()
    {
        return LoaderSelection.LoadLoaderVersionsAsync();
    }

    [RelayCommand]
    private Task CreateInstanceAsync()
    {
        return InstancesViewModel.CreateInstanceAsync(
            SelectedMinecraftVersion,
            SelectedLoader,
            SelectedLoaderVersion,
            CreateProgress());
    }

    [RelayCommand]
    private Task SearchModsAsync()
    {
        return ModrinthSearch.SearchModsAsync(SelectedInstance);
    }

    [RelayCommand]
    private async Task InstallSelectedModAsync()
    {
        await ModrinthSearch.InstallSelectedModAsync(SelectedInstance, CreateProgress());
    }

    [RelayCommand]
    private Task SaveSettingsAsync()
    {
        return InstancesViewModel.SaveSettingsAsync();
    }

    [RelayCommand]
    private Task SaveInstanceAsync()
    {
        return InstancesViewModel.SaveInstanceAsync();
    }

    [RelayCommand]
    private Task SetDefaultInstanceAsync()
    {
        return InstancesViewModel.SetDefaultInstanceAsync();
    }

    private IProgress<LauncherProgress> CreateProgress()
    {
        return new Progress<LauncherProgress>(progress =>
        {
            ReportStatus(LauncherProgressTextFormatter.Format(progress));
            ProgressPercent = progress.Percent ?? 0;
        });
    }

    private void InstancesViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ForwardChildPropertyChanged(sender, e);
    }

    private void ForwardChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }

    private void ReportStatus(string message)
    {
        statusService.Report(message);
    }
}

