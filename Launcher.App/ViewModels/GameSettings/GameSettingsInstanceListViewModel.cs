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
using Launcher.App.Resources;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

/// <summary>
/// 维护实例设置页的完整实例缓存、分类投影和稳定选择，并协调激活时刷新。
/// </summary>
public sealed partial class GameSettingsInstanceListViewModel : ObservableObject
{
    public const string LocalImportCategoryId = "local_import";

    // AllInstances 是权威 UI 缓存，Instances 仅是当前分类的稳定可观察投影。
    private readonly ILogger<GameSettingsInstanceListViewModel> logger;
    private bool hasLoadedInstances;
    private bool preserveFilteredSelection;
    private long appliedCatalogRevision = -1;

    [ObservableProperty]
    private GameSettingsInstanceCategory? selectedCategory;

    [ObservableProperty]
    private GameSettingsInstanceItem? selectedInstance;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string loadError = string.Empty;

    [ObservableProperty]
    private string emptyMessage = string.Empty;

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private int entranceAnimationToken;

    public GameSettingsInstanceListViewModel(
        ILogger<GameSettingsInstanceListViewModel>? logger = null)
    {
        this.logger = logger ?? NullLogger<GameSettingsInstanceListViewModel>.Instance;

        Categories.Add(new GameSettingsInstanceCategory("all", Strings.GameSettings_AllCategory, string.Empty, "general/general_all_application"));
        Categories.Add(new GameSettingsInstanceCategory("mod_loader", Strings.GameSettings_ModLoaderCategory, string.Empty, "general/general_extention"));
        Categories.Add(new GameSettingsInstanceCategory("release", Strings.Download_ReleaseCategory, string.Empty, "instance_download_page/release"));
        Categories.Add(new GameSettingsInstanceCategory("snapshot", Strings.Download_SnapshotCategory, string.Empty, "instance_download_page/snapshot"));
        Categories.Add(new GameSettingsInstanceCategory("april_fools", Strings.Download_AprilFoolsCategory, string.Empty, "instance_download_page/winking-face-with-open-eyes"));
        Categories.Add(new GameSettingsInstanceCategory("ancient", Strings.Download_AncientCategory, string.Empty, "instance_download_page/time"));
        Categories.Add(new GameSettingsInstanceCategory(LocalImportCategoryId, Strings.Download_LocalImportCategory, string.Empty, "instance_download_page/localimport"));
        SelectCategory(Categories[0], refreshVisibleInstances: false);
    }

    public event Action? LocalImportRequested;

    public ObservableCollection<GameSettingsInstanceCategory> Categories { get; } = [];

    public List<GameSettingsInstanceItem> AllInstances { get; } = [];

    public ObservableCollection<GameSettingsInstanceItem> VisibleInstances { get; } = [];

    public bool HasVisibleInstances => VisibleInstances.Count > 0;

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public bool HasEmptyMessage => !string.IsNullOrWhiteSpace(EmptyMessage);

    public long AppliedCatalogRevision => appliedCatalogRevision;

    public bool ApplyInstanceCatalog(
        IReadOnlyList<GameInstance> instances,
        long catalogRevision,
        bool playEntranceAnimation)
    {
        if (appliedCatalogRevision == catalogRevision)
            return false;

        var wasLoaded = hasLoadedInstances;
        var selectedId = SelectedInstance?.Instance.InstanceDirectory;
        var selectedSnapshot = SelectedInstance?.Instance;
        IsLoading = false;
        LoadError = string.Empty;
        EmptyMessage = string.Empty;
        var changed = Reconcile(instances);
        RestoreSelection(selectedId);
        if (SelectedInstance is not null && !ReferenceEquals(selectedSnapshot, SelectedInstance.Instance))
            OnPropertyChanged(nameof(SelectedInstance));
        hasLoadedInstances = true;
        appliedCatalogRevision = catalogRevision;
        RefreshVisibleInstances();
        if (!wasLoaded && playEntranceAnimation)
            EntranceAnimationToken++;

        logger.LogDebug(
            "Game settings instance catalog applied. Count={InstanceCount} VisibleCount={VisibleCount} SelectedInstanceDirectory={SelectedInstanceDirectory} CatalogRevision={CatalogRevision}",
            AllInstances.Count,
            VisibleInstances.Count,
            SelectedInstance?.Instance.InstanceDirectory,
            catalogRevision);
        return changed;
    }

    public void SetPreserveFilteredSelection(bool value)
    {
        preserveFilteredSelection = value;
        RefreshVisibleInstances();
    }

    public void SelectCategory(GameSettingsInstanceCategory category, bool refreshVisibleInstances = true)
    {
        if (string.Equals(category.Id, LocalImportCategoryId, StringComparison.Ordinal))
        {
            LocalImportRequested?.Invoke();
            return;
        }

        // 分类对象保持稳定，切换时只重建投影，不重新发现实例。
        var changed = !ReferenceEquals(SelectedCategory, category)
            && !string.Equals(SelectedCategory?.Id, category.Id, StringComparison.OrdinalIgnoreCase);
        SelectedCategory = category;
        foreach (var item in Categories)
            item.IsSelected = ReferenceEquals(item, category);
        if (refreshVisibleInstances)
            RefreshVisibleInstances();
        if (hasLoadedInstances && changed)
            EntranceAnimationToken++;
    }

    public GameSettingsInstanceItem SelectInstance(GameSettingsInstanceItem instance)
    {
        SelectInstanceCore(instance);
        return instance;
    }

    public GameSettingsInstanceItem GetOrAdd(GameInstance instance)
    {
        var item = Find(instance.InstanceDirectory);
        if (item is not null)
            return item;
        item = CreateItem(instance);
        AllInstances.Add(item);
        RefreshVisibleInstances();
        return item;
    }

    public GameSettingsInstanceItem? Find(string? instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return null;
        return AllInstances.FirstOrDefault(item =>
            string.Equals(item.Instance.InstanceDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase));
    }

    public void AddOrUpdate(GameInstance instance)
    {
        if (!hasLoadedInstances)
            return;
        var item = Find(instance.InstanceDirectory);
        if (item is null)
        {
            AllInstances.Add(CreateItem(instance));
        }
        else
        {
            var wasSelected = ReferenceEquals(SelectedInstance, item);
            var previousInstance = item.Instance;
            item.Update(instance, ResolveVersionType(instance));
            if (wasSelected && !ReferenceEquals(previousInstance, item.Instance))
                SelectInstanceCore(item, forceNotification: true);
        }
        RefreshVisibleInstances();
    }

    public bool Remove(string instanceDirectory)
    {
        var removed = AllInstances.RemoveAll(item =>
            string.Equals(item.Instance.InstanceDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return false;
        if (string.Equals(SelectedInstance?.Instance.InstanceDirectory, instanceDirectory, StringComparison.OrdinalIgnoreCase))
            SelectInstanceCore(null);
        RefreshVisibleInstances();
        return true;
    }

    partial void OnSearchQueryChanged(string value) => RefreshVisibleInstances();

    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));

    partial void OnEmptyMessageChanged(string value) => OnPropertyChanged(nameof(HasEmptyMessage));

    private void RefreshVisibleInstances()
    {
        var result = GameSettingsInstanceFilter.Apply(
            AllInstances,
            SelectedCategory,
            SearchQuery,
            SelectedInstance,
            hasLoadedInstances,
            IsLoading,
            HasLoadError);
        EmptyMessage = result.EmptyMessage;
        if (result.ShouldClearSelectedInstance
            && (!preserveFilteredSelection || SelectedInstance is null || !ContainsSelectedInstance()))
        {
            SelectInstanceCore(null);
        }
        ApplyVisibleInstances(result.Instances);
    }

    private bool ContainsSelectedInstance() => SelectedInstance is not null && AllInstances.Any(item =>
        ReferenceEquals(item, SelectedInstance)
        || (!string.IsNullOrWhiteSpace(item.Instance.InstanceDirectory)
            && string.Equals(item.Instance.InstanceDirectory, SelectedInstance.Instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase)));

    private bool Reconcile(IReadOnlyList<GameInstance> instances)
    {
        // 原地更新已有条目可保留选择、动画和外部 Binding；仅新增/删除才改变集合结构。
        var existing = AllInstances
            .Where(item => !string.IsNullOrWhiteSpace(item.Instance.InstanceDirectory))
            .GroupBy(item => item.Instance.InstanceDirectory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var next = new List<GameSettingsInstanceItem>(instances.Count);
        var changed = AllInstances.Count != instances.Count;
        foreach (var instance in instances)
        {
            if (!string.IsNullOrWhiteSpace(instance.InstanceDirectory) && existing.TryGetValue(instance.InstanceDirectory, out var item))
            {
                changed |= item.Update(instance, ResolveVersionType(instance));
                next.Add(item);
            }
            else
            {
                next.Add(CreateItem(instance));
                changed = true;
            }
        }
        if (!changed)
        {
            for (var index = 0; index < next.Count; index++)
            {
                if (ReferenceEquals(AllInstances[index], next[index]))
                    continue;
                changed = true;
                break;
            }
        }
        AllInstances.Clear();
        AllInstances.AddRange(next);
        return changed;
    }

    private void RestoreSelection(string? instanceDirectory) =>
        SelectInstanceCore(string.IsNullOrWhiteSpace(instanceDirectory) ? null : Find(instanceDirectory));

    private void SelectInstanceCore(GameSettingsInstanceItem? instance, bool forceNotification = false)
    {
        var previous = SelectedInstance;
        SelectedInstance = instance;
        if (forceNotification && ReferenceEquals(previous, instance))
            OnPropertyChanged(nameof(SelectedInstance));
        foreach (var item in AllInstances)
            item.IsSelected = ReferenceEquals(item, instance);
    }

    private void ApplyVisibleInstances(IReadOnlyList<GameSettingsInstanceItem> instances)
    {
        // 批量替换期间抑制中间通知，最后一次性刷新空状态和选择派生属性。
        var changed = false;
        for (var index = VisibleInstances.Count - 1; index >= 0; index--)
        {
            if (instances.Any(item => ReferenceEquals(item, VisibleInstances[index])))
                continue;
            VisibleInstances.RemoveAt(index);
            changed = true;
        }
        for (var index = 0; index < instances.Count; index++)
        {
            var instance = instances[index];
            if (index < VisibleInstances.Count && ReferenceEquals(VisibleInstances[index], instance))
                continue;
            var existingIndex = -1;
            for (var candidate = index + 1; candidate < VisibleInstances.Count; candidate++)
            {
                if (!ReferenceEquals(VisibleInstances[candidate], instance))
                    continue;
                existingIndex = candidate;
                break;
            }
            if (existingIndex >= 0)
                VisibleInstances.Move(existingIndex, index);
            else
                VisibleInstances.Insert(index, instance);
            changed = true;
        }
        if (changed)
            NotifyVisibleInstancesChanged();
    }

    private void NotifyVisibleInstancesChanged()
    {
        OnPropertyChanged(nameof(VisibleInstances));
        OnPropertyChanged(nameof(HasVisibleInstances));
        OnPropertyChanged(nameof(HasEmptyMessage));
    }

    private GameSettingsInstanceItem CreateItem(GameInstance instance) =>
        new(instance, ResolveVersionType(instance));

    private static string ResolveVersionType(GameInstance instance) => instance.VersionType;
}
