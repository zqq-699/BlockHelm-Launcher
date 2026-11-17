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

namespace Launcher.App.ViewModels.Home;

public sealed partial class HomeLaunchGameListViewModel : ObservableObject
{
    private readonly IStatusService statusService;
    private readonly Func<GameInstance, Task<bool>> selectLaunchInstance;
    private readonly Func<bool, Task<bool>> setLaunchMenuPinned;
    private long appliedCatalogRevision = -1;

    [ObservableProperty]
    private GameInstance? selectedInstance;

    [ObservableProperty]
    private bool isLaunchMenuPinned;

    public HomeLaunchGameListViewModel(
        IStatusService statusService,
        Func<GameInstance, Task<bool>> selectLaunchInstance,
        Func<bool, Task<bool>>? setLaunchMenuPinned = null)
    {
        this.statusService = statusService;
        this.selectLaunchInstance = selectLaunchInstance;
        this.setLaunchMenuPinned = setLaunchMenuPinned ?? (_ => Task.FromResult(true));
    }

    public ObservableCollection<HomeLaunchInstanceItem> LaunchInstances { get; } = [];

    public bool HasLaunchInstances => LaunchInstances.Count > 0;

    public bool HasNoLaunchInstances => !HasLaunchInstances;

    public HomeLaunchInstanceItem? SelectedLaunchInstanceItem => LaunchInstances.FirstOrDefault(item => item.IsSelected);

    public bool HasSelectedLaunchInstance => SelectedLaunchInstanceItem is not null;

    public string LaunchMenuPinTooltip => IsLaunchMenuPinned
        ? Strings.Home_UnpinLaunchMenuTooltip
        : Strings.Home_PinLaunchMenuTooltip;

    public void SetLaunchMenuPinned(bool isPinned)
    {
        IsLaunchMenuPinned = isPinned;
    }

    public void SetSelectedInstance(GameInstance? instance)
    {
        SelectedInstance = instance;
    }

    public void SetLaunchInstances(IEnumerable<GameInstance> instances)
    {
        ReconcileLaunchInstances(instances);
    }

    public bool ApplyInstanceCatalog(IEnumerable<GameInstance> instances, long catalogRevision)
    {
        if (appliedCatalogRevision == catalogRevision)
            return false;

        var changed = ReconcileLaunchInstances(instances);
        appliedCatalogRevision = catalogRevision;
        return changed;
    }

    private bool ReconcileLaunchInstances(IEnumerable<GameInstance> instances)
    {
        var selectedInstanceDirectory = SelectedInstance?.InstanceDirectory;
        var existing = LaunchInstances
            .Where(item => !string.IsNullOrWhiteSpace(item.Instance.InstanceDirectory))
            .GroupBy(item => item.Instance.InstanceDirectory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var next = new List<HomeLaunchInstanceItem>();
        var itemChanged = false;
        foreach (var instance in instances.OrderByDescending(instance => instance.CreatedAt))
        {
            if (!string.IsNullOrWhiteSpace(instance.InstanceDirectory)
                && existing.TryGetValue(instance.InstanceDirectory, out var item))
            {
                itemChanged |= item.Update(instance, instance.VersionType);
                next.Add(item);
            }
            else
            {
                next.Add(CreateLaunchInstanceItem(instance));
                itemChanged = true;
            }
        }

        var collectionChanged = ApplyLaunchInstances(next);
        UpdateLaunchInstanceSelection(selectedInstanceDirectory);
        if (itemChanged || collectionChanged)
            NotifyLaunchInstancesChanged();
        return itemChanged || collectionChanged;
    }

    [RelayCommand]
    private async Task ToggleLaunchMenuPinnedAsync()
    {
        var previousValue = IsLaunchMenuPinned;
        var nextValue = !previousValue;
        IsLaunchMenuPinned = nextValue;

        try
        {
            var saved = await setLaunchMenuPinned(nextValue);
            if (saved)
                return;
        }
        catch (Exception)
        {
        }

        IsLaunchMenuPinned = previousValue;
        statusService.Report(Strings.Status_SettingsSaveFailed);
    }

    [RelayCommand]
    private async Task SelectLaunchInstanceAsync(HomeLaunchInstanceItem? item)
    {
        if (item is null)
            return;

        var previousSelectedInstance = SelectedInstance;
        SetSelectedInstance(item.Instance);

        try
        {
            var saved = await selectLaunchInstance(item.Instance);
            if (!saved)
            {
                SetSelectedInstance(previousSelectedInstance);
                statusService.Report(Strings.Status_LaunchInstanceSelectionFailed);
                return;
            }

            SetSelectedInstance(item.Instance);
            statusService.Report(string.Format(Strings.Status_LaunchInstanceSelectedFormat, item.Name));
        }
        catch (Exception)
        {
            SetSelectedInstance(previousSelectedInstance);
            statusService.Report(Strings.Status_LaunchInstanceSelectionFailed);
        }
    }

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        UpdateLaunchInstanceSelection(value?.InstanceDirectory);
    }

    partial void OnIsLaunchMenuPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(LaunchMenuPinTooltip));
    }

    private void NotifyLaunchInstancesChanged()
    {
        OnPropertyChanged(nameof(HasLaunchInstances));
        OnPropertyChanged(nameof(HasNoLaunchInstances));
        OnPropertyChanged(nameof(SelectedLaunchInstanceItem));
        OnPropertyChanged(nameof(HasSelectedLaunchInstance));
    }

    private void UpdateLaunchInstanceSelection(string? selectedInstanceDirectory)
    {
        foreach (var item in LaunchInstances)
        {
            item.IsSelected = !string.IsNullOrWhiteSpace(selectedInstanceDirectory)
                && string.Equals(item.Instance.InstanceDirectory, selectedInstanceDirectory, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(SelectedLaunchInstanceItem));
        OnPropertyChanged(nameof(HasSelectedLaunchInstance));
    }

    private HomeLaunchInstanceItem CreateLaunchInstanceItem(GameInstance instance)
    {
        return new HomeLaunchInstanceItem(instance, instance.VersionType);
    }

    private bool ApplyLaunchInstances(IReadOnlyList<HomeLaunchInstanceItem> instances)
    {
        var changed = false;
        for (var index = 0; index < instances.Count; index++)
        {
            var item = instances[index];
            if (index < LaunchInstances.Count && ReferenceEquals(LaunchInstances[index], item))
                continue;

            var existingIndex = -1;
            for (var candidate = index + 1; candidate < LaunchInstances.Count; candidate++)
            {
                if (!ReferenceEquals(LaunchInstances[candidate], item))
                    continue;
                existingIndex = candidate;
                break;
            }

            if (existingIndex >= 0)
                LaunchInstances.Move(existingIndex, index);
            else
                LaunchInstances.Insert(index, item);
            changed = true;
        }

        while (LaunchInstances.Count > instances.Count)
        {
            LaunchInstances.RemoveAt(LaunchInstances.Count - 1);
            changed = true;
        }

        return changed;
    }

}

