/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Behaviors;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

/// <summary>版本分组的投影、展开状态和异步动画收尾。</summary>
public sealed partial class ResourcesProjectVersionsViewModel
{
    private const int GroupExpansionStagingThreshold = 8;
    private const int FallbackVisibleGroupRowCount = 20;
    private const double GroupRowHeight = 52;
    private static readonly TimeSpan GroupExpansionStagingDuration = TimeSpan.FromMilliseconds(420);

    // 只记录用户主动切换过的分组；未操作分组仍按当前分组数量应用默认状态。
    private readonly Dictionary<string, bool> groupExpansionStates = new(StringComparer.OrdinalIgnoreCase);
    // 每次切换同时使上一次收起回调和分批展开回调失效。
    private readonly Dictionary<string, int> groupTransitionGenerations = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectVersion>> groupVersions =
        new Dictionary<string, IReadOnlyList<ResourceProjectVersion>>(StringComparer.OrdinalIgnoreCase);
    private bool isGroupExpansionStagingEnabled;
    private double groupExpansionViewportHeight;
    private int listRevision;

    /// <summary>由版本列表视图挂载时开启；无视图时保持一次性插入。</summary>
    public bool IsGroupExpansionStagingEnabled
    {
        get => isGroupExpansionStagingEnabled;
        set => SetProperty(ref isGroupExpansionStagingEnabled, value);
    }

    /// <summary>用于只插入足以覆盖当前视口的首批子项；其余子项在位移动画后补齐。</summary>
    public double GroupExpansionViewportHeight
    {
        get => groupExpansionViewportHeight;
        set => groupExpansionViewportHeight = Math.Max(0, value);
    }

    public bool CanAutoLoadMoreVersions
    {
        get
        {
            var groups = ListItems.OfType<ResourcesModVersionListHeaderItem>()
                .Where(header => header.ToggleExpandedCommand.CanExecute(null))
                .ToList();
            return groups.Count == 0 || groups.Any(header => header.IsExpanded);
        }
    }

    public event Action<IReadOnlyList<ResourcesModVersionItemViewModel>>? GroupEntranceRequested;

    public event Action<IReadOnlyList<ResourcesModVersionItemViewModel>, Action>? GroupCollapseRequested;

    private void SetGroupExpanded(string title, bool isExpanded)
    {
        groupExpansionStates[title] = isExpanded;
        var generation = groupTransitionGenerations.GetValueOrDefault(title) + 1;
        groupTransitionGenerations[title] = generation;

        var revision = listRevision;
        var children = GetGroupChildren(title);
        if (!isExpanded
            && children.Count > 0
            && GroupCollapseRequested is not null)
        {
            // 真实行控件先播放倒放入场动画，动画结束后再移除行并补偿下方布局。
            GroupCollapseRequested.Invoke(children, () =>
            {
                if (revision == listRevision
                    && groupTransitionGenerations.GetValueOrDefault(title) == generation
                    && groupExpansionStates.TryGetValue(title, out var expanded)
                    && !expanded)
                {
                    ApplyGroupExpansion(title);
                }
            });
            return;
        }

        ApplyGroupExpansion(title);
    }

    private void ApplyGroupExpansion(string title)
    {
        var groupIndex = -1;
        for (var index = 0; index < ListItems.Count; index++)
        {
            if (ListItems[index] is ResourcesModVersionListHeaderItem header
                && string.Equals(header.Title, title, StringComparison.OrdinalIgnoreCase))
            {
                groupIndex = index;
                break;
            }
        }

        if (groupIndex < 0)
            return;

        if (!groupExpansionStates[title])
        {
            var children = GetGroupChildren(title);
            if (children.Count > 0)
            {
                // 行控件已经淡出；用一段可见高度占位把下方内容平滑收回。
                var visibleRows = Math.Min(children.Count, GetVisibleGroupRowCount());
                var spacer = new ResourcesGroupCollapseSpacerItem(
                    visibleRows * GroupRowHeight,
                    ListEntranceAnimationDefaults.LayoutShiftDuration);
                RemoveGroupChildren(title);
                ListItems.Insert(groupIndex + 1, spacer);
                RaiseStateChanged();

                _ = RemoveGroupCollapseSpacerAsync(title, spacer, listRevision);
            }
            else
            {
                RaiseStateChanged();
            }

            return;
        }

        // 分组数据在重建列表时已筛选并索引；切换单组不再重建整页版本项。
        if (!groupVersions.TryGetValue(title, out var versions))
            return;

        var newChildren = new List<ResourcesModVersionItemViewModel>(versions.Count);
        for (var index = 0; index < versions.Count; index++)
            newChildren.Add(new ResourcesModVersionItemViewModel(
                versions[index], currentProject, options.FallbackIconKey)
            {
                IsFirstInGroup = index == 0
            });

        var stageLargeGroup = IsGroupExpansionStagingEnabled
            && newChildren.Count > GroupExpansionStagingThreshold;
        var stagedRowCount = GetVisibleGroupRowCount();
        var entranceChildren = stageLargeGroup
            ? newChildren.Take(stagedRowCount).ToList()
            : newChildren;

        if (newChildren.Count > 0 && GroupEntranceRequested is not null)
        {
            foreach (var child in entranceChildren)
                child.IsGroupEntrancePending = true;
            GroupEntranceRequested.Invoke(entranceChildren);
        }

        var firstChildIndex = groupIndex + 1;
        var endChildIndex = firstChildIndex;
        while (endChildIndex < ListItems.Count
            && ListItems[endChildIndex] is not ResourcesModVersionListHeaderItem
            && ListItems[endChildIndex] is not ResourcesListFooterStatusItem)
            endChildIndex++;
        // 从尾部删除，避免每次都移动尚未删除的所有子项。
        for (var index = endChildIndex - 1; index >= firstChildIndex; index--)
            ListItems.RemoveAt(index);

        if (stageLargeGroup)
        {
            foreach (var child in entranceChildren)
                ListItems.Insert(firstChildIndex++, child);

            StartGroupExpansionCompletion(
                title,
                newChildren.Skip(stagedRowCount).ToList());
        }
        else
        {
            foreach (var child in newChildren)
                ListItems.Insert(firstChildIndex++, child);
        }

        RaiseStateChanged();
    }

    private void StartGroupExpansionCompletion(
        string title,
        IReadOnlyList<ResourcesModVersionItemViewModel> remainingChildren)
    {
        _ = CompleteGroupExpansionAsync(
            title,
            remainingChildren,
            listRevision,
            groupTransitionGenerations.GetValueOrDefault(title));
    }

    private async Task CompleteGroupExpansionAsync(
        string title,
        IReadOnlyList<ResourcesModVersionItemViewModel> remainingChildren,
        int revision,
        int generation)
    {
        try
        {
            await Task.Delay(GroupExpansionStagingDuration).ConfigureAwait(false);
            uiDispatcher.Invoke(() =>
            {
                if (revision != listRevision
                    || !groupExpansionStates.TryGetValue(title, out var expanded)
                    || !expanded
                    || groupTransitionGenerations.GetValueOrDefault(title) != generation)
                    return;

                if (remainingChildren.Count > 0)
                {
                    var insertIndex = GetGroupChildInsertIndex(title);
                    if (insertIndex >= 0)
                        foreach (var child in remainingChildren)
                            ListItems.Insert(insertIndex++, child);
                }

                RaiseStateChanged();
            });
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Failed to complete resource version group expansion. Title={Title}",
                title);
        }
    }

    private List<ResourcesModVersionItemViewModel> GetGroupChildren(string title)
    {
        var children = new List<ResourcesModVersionItemViewModel>();
        var headerIndex = FindGroupHeaderIndex(title);
        for (var index = headerIndex + 1; headerIndex >= 0 && index < ListItems.Count
            && ListItems[index] is ResourcesModVersionItemViewModel child; index++)
            children.Add(child);

        return children;
    }

    private int FindGroupHeaderIndex(string title)
    {
        for (var index = 0; index < ListItems.Count; index++)
            if (ListItems[index] is ResourcesModVersionListHeaderItem header
                && string.Equals(header.Title, title, StringComparison.OrdinalIgnoreCase))
                return index;

        return -1;
    }

    private int GetGroupChildInsertIndex(string title)
    {
        var headerIndex = FindGroupHeaderIndex(title);
        if (headerIndex < 0)
            return -1;

        var index = headerIndex + 1;
        while (index < ListItems.Count && ListItems[index] is ResourcesModVersionItemViewModel)
            index++;
        return index;
    }

    private int GetVisibleGroupRowCount()
    {
        return GroupExpansionViewportHeight > 0
            ? Math.Max(1, (int)Math.Ceiling(GroupExpansionViewportHeight / GroupRowHeight) + 2)
            : FallbackVisibleGroupRowCount;
    }

    private async Task RemoveGroupCollapseSpacerAsync(
        string title,
        ResourcesGroupCollapseSpacerItem spacer,
        int revision)
    {
        try
        {
            await Task.Delay(spacer.Duration + TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            uiDispatcher.Invoke(() =>
            {
                if (revision != listRevision
                    || !groupExpansionStates.TryGetValue(title, out var expanded)
                    || expanded)
                {
                    return;
                }

                if (ListItems.Remove(spacer))
                    RaiseStateChanged();
            });
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Failed to remove resource version group collapse spacer. Title={Title}",
                title);
        }
    }

    private void RemoveGroupChildren(string title)
    {
        var headerIndex = FindGroupHeaderIndex(title);
        if (headerIndex < 0)
            return;

        var childIndex = headerIndex + 1;
        var endIndex = childIndex;
        while (endIndex < ListItems.Count
            && ListItems[endIndex] is ResourcesModVersionItemViewModel)
            endIndex++;
        // 保留增量通知和标题容器，同时避免正向删除产生的子项二次方搬移。
        for (var index = endIndex - 1; index >= childIndex; index--)
            ListItems.RemoveAt(index);
    }
}
