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
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Resources;

internal sealed partial class ResourcesAvailableVersionListBuilder
{
    private static readonly IComparer<string> MinecraftGroupVersionComparer =
        Comparer<string>.Create(CompareMinecraftGroupVersions);

    public AvailableVersionListBuildResult Build(
        IReadOnlyList<ResourceProjectVersion> versions,
        string title,
        ResourcesModProjectItemViewModel? selectedProject,
        string fallbackIconKey,
        string? selectedVersionId,
        string? selectedLoaderId,
        string searchQuery,
        IReadOnlyDictionary<string, bool> groupExpansionStates,
        Action<string, bool> expansionChanged)
    {
        // UI 集合同时包含标题和版本项；先完成筛选，再生成不会出现空组的扁平投影。
        var items = new List<object>();
        var filteredVersions = versions
            .Where(version => MatchesFilters(version, selectedVersionId, selectedLoaderId, searchQuery))
            .ToList();
        var (visibleCount, groupVersions) = AddGroupedItems(
            items,
            filteredVersions,
            title,
            selectedProject,
            fallbackIconKey,
            selectedVersionId,
            selectedLoaderId,
            groupExpansionStates,
            expansionChanged);
        return new AvailableVersionListBuildResult(items, visibleCount, groupVersions);
    }

    public int Append(
        IList<object> items,
        IReadOnlyList<ResourceProjectVersion> versions,
        string title,
        ResourcesModProjectItemViewModel? selectedProject,
        string fallbackIconKey,
        string? selectedVersionId,
        string? selectedLoaderId,
        string searchQuery,
        int currentVisibleCount)
    {
        // 追加分页保留已有对象，避免虚拟化容器和当前滚动位置因全量重建而失效。
        RemoveEmptyPlaceholderHeader(items, title, currentVisibleCount);

        var appendedCount = 0;
        foreach (var version in versions.Where(version => MatchesFilters(version, selectedVersionId, selectedLoaderId, searchQuery)))
        {
            foreach (var groupTitle in CreateFilteredCompatibilityGroupTitles(version, selectedVersionId, selectedLoaderId))
            {
                var insertIndex = FindGroupInsertIndex(items, groupTitle);
                items.Insert(insertIndex, new ResourcesModVersionItemViewModel(version, selectedProject, fallbackIconKey));
                appendedCount++;
            }
        }

        return appendedCount;
    }

    public string GetLoaderTitle(string loaderId)
    {
        return loaderId switch
        {
            "fabric" => Strings.Download_FabricLoaderTitle,
            "forge" => Strings.Download_ForgeLoaderTitle,
            "neoforge" => Strings.Download_NeoForgeLoaderTitle,
            "quilt" => Strings.Download_QuiltLoaderTitle,
            _ => loaderId
        };
    }

    internal bool MatchesFilters(
        ResourceProjectVersion version,
        string? selectedVersionId,
        string? selectedLoaderId,
        string searchQuery)
    {
        // 搜索、Minecraft 版本与 Loader 是交集关系；“全部”值由各自匹配函数解释。
        return MatchesSearch(version, searchQuery)
            && MatchesVersionFilter(version, selectedVersionId)
            && MatchesLoaderFilter(version, selectedLoaderId);
    }

    private (int VisibleCount, IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectVersion>> GroupVersions) AddGroupedItems(
        ICollection<object> items,
        IReadOnlyList<ResourceProjectVersion> versions,
        string title,
        ResourcesModProjectItemViewModel? selectedProject,
        string fallbackIconKey,
        string? selectedVersionId,
        string? selectedLoaderId,
        IReadOnlyDictionary<string, bool> groupExpansionStates,
        Action<string, bool> expansionChanged)
    {
        // 标题只在至少存在一个可见子项时加入，避免留下无法解释的空分组。
        var groups = new List<AvailableVersionCompatibilityGroup>();
        var groupsByTitle = new Dictionary<string, AvailableVersionCompatibilityGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            foreach (var groupTitle in CreateFilteredCompatibilityGroupTitles(version, selectedVersionId, selectedLoaderId))
            {
                if (!groupsByTitle.TryGetValue(groupTitle, out var group))
                {
                    group = new AvailableVersionCompatibilityGroup(groupTitle);
                    groupsByTitle.Add(groupTitle, group);
                    groups.Add(group);
                }

                group.Versions.Add(version);
            }
        }

        if (groups.Count == 0)
        {
            items.Add(new ResourcesModVersionListHeaderItem(title));
            return (0, new Dictionary<string, IReadOnlyList<ResourceProjectVersion>>(StringComparer.OrdinalIgnoreCase));
        }

        var visibleCount = 0;
        var defaultExpanded = groups.Count == 1;
        // 分组首次出现的顺序取决于模组文件的发布时间，不能用它表示 Minecraft 版本顺序。
        // 稳定排序保留同一 Minecraft 版本下 Loader 原有的相对顺序。
        foreach (var group in groups.OrderByDescending(group => group.Title, MinecraftGroupVersionComparer))
        {
            var isExpanded = groupExpansionStates.TryGetValue(group.Title, out var explicitState)
                ? explicitState
                : defaultExpanded;
            items.Add(new ResourcesModVersionListHeaderItem(group.Title, isExpanded, expansionChanged));
            visibleCount += group.Versions.Count;
            if (!isExpanded)
                continue;

            var isFirstVersionInGroup = true;
            foreach (var version in group.Versions)
            {
                items.Add(new ResourcesModVersionItemViewModel(version, selectedProject, fallbackIconKey)
                {
                    IsFirstInGroup = isFirstVersionInGroup
                });
                isFirstVersionInGroup = false;
            }
        }

        return (visibleCount, groupsByTitle.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ResourceProjectVersion>)pair.Value.Versions,
            StringComparer.OrdinalIgnoreCase));
    }

    private IEnumerable<string> CreateFilteredCompatibilityGroupTitles(
        ResourceProjectVersion version,
        string? selectedVersionId,
        string? selectedLoaderId)
    {
        var gameVersions = NormalizeGameVersionCompatibilityValues(version.GameVersions);
        var loaders = options.ShowsLoaderFilters
            ? ResolveCompatibilityLoaders(version)
            : [string.Empty];

        if (!string.IsNullOrWhiteSpace(selectedVersionId)
            && !string.Equals(selectedVersionId, "all", StringComparison.OrdinalIgnoreCase))
        {
            gameVersions = gameVersions
                .Where(gameVersion => string.Equals(gameVersion, selectedVersionId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (options.ShowsLoaderFilters
            && !string.IsNullOrWhiteSpace(selectedLoaderId)
            && !string.Equals(selectedLoaderId, "all", StringComparison.OrdinalIgnoreCase))
        {
            loaders = loaders
                .Where(loader => string.Equals(loader, selectedLoaderId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var gameVersion in gameVersions)
        {
            foreach (var loader in loaders)
            {
                yield return string.IsNullOrWhiteSpace(loader)
                    ? gameVersion
                    : $"{gameVersion}-{loader}";
            }
        }
    }

    private bool MatchesSearch(ResourceProjectVersion version, string searchQuery)
    {
        var query = searchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return ContainsSearchText(version.Name, query)
            || ContainsSearchText(version.VersionNumber, query)
            || ContainsSearchText(version.FileName, query)
            || ContainsSearchText(version.VersionType, query);
    }

    private static bool MatchesVersionFilter(ResourceProjectVersion version, string? selectedVersion)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion)
            || string.Equals(selectedVersion, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NormalizeGameVersionCompatibilityValues(version.GameVersions)
            .Any(versionId => string.Equals(versionId, selectedVersion, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesLoaderFilter(ResourceProjectVersion version, string? selectedLoader)
    {
        if (string.IsNullOrWhiteSpace(selectedLoader)
            || string.Equals(selectedLoader, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!options.ShowsLoaderFilters)
            return true;

        return ResolveCompatibilityLoaders(version)
            .Any(loader => string.Equals(loader, selectedLoader, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSearchText(string value, string query)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveEmptyPlaceholderHeader(IList<object> items, string title, int currentVisibleCount)
    {
        if (currentVisibleCount == 0
            && items.Count == 1
            && items[0] is ResourcesModVersionListHeaderItem header
            && string.Equals(header.Title, title, StringComparison.OrdinalIgnoreCase))
        {
            items.Clear();
        }
    }

    private static int FindGroupInsertIndex(IList<object> items, string title)
    {
        // 新页结果插入已有同名标题末尾，而不是简单追加到列表尾部破坏分组连续性。
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not ResourcesModVersionListHeaderItem header
                || !string.Equals(header.Title, title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var insertIndex = index + 1;
            while (insertIndex < items.Count
                && items[insertIndex] is not ResourcesModVersionListHeaderItem)
            {
                insertIndex++;
            }

            return insertIndex;
        }

        items.Add(new ResourcesModVersionListHeaderItem(title));
        return items.Count;
    }

    private static int CompareMinecraftGroupVersions(string left, string right)
    {
        var leftParts = ParseMinecraftVersionPrefix(left);
        var rightParts = ParseMinecraftVersionPrefix(right);
        if (leftParts is null)
            return rightParts is null ? 0 : -1;
        if (rightParts is null)
            return 1;

        for (var index = 0; index < Math.Max(leftParts.Count, rightParts.Count); index++)
        {
            var leftPart = index < leftParts.Count ? leftParts[index] : 0;
            var rightPart = index < rightParts.Count ? rightParts[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static IReadOnlyList<int>? ParseMinecraftVersionPrefix(string title)
    {
        var parts = new List<int>(3);
        var text = title.AsSpan();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
                index++;
            if (start == index || !int.TryParse(text[start..index], out var part))
                return null;

            parts.Add(part);
            if (index == text.Length || text[index] is '-' or '_')
                break;
            if (text[index] != '.')
                return null;
            index++;
        }

        return parts.Count > 0 ? parts : null;
    }
}
