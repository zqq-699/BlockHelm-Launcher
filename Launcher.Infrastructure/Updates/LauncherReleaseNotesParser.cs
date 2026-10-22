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

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Updates;

internal static class LauncherReleaseNotesParser
{
    private const int MaximumCharacters = 64 * 1024;
    private const int MaximumSections = 16;
    private const int MaximumItems = 200;
    private const int MaximumItemCharacters = 1000;

    public static IReadOnlyList<LauncherUpdateReleaseNotesSection> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var source = markdown.Length <= MaximumCharacters
            ? markdown
            : markdown[..MaximumCharacters];
        var sections = new List<LauncherUpdateReleaseNotesSection>();
        string? currentTitle = null;
        var currentItems = new List<string>();
        var itemCount = 0;

        foreach (var rawLine in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                AddSection(sections, currentTitle, currentItems);
                if (sections.Count >= MaximumSections)
                    return sections;

                currentTitle = LimitLength(line[3..].Trim());
                currentItems = [];
                continue;
            }

            // 一级标题只是发布页标题；弹窗已经单独显示版本号，不重复展示。
            if (line.StartsWith("# ", StringComparison.Ordinal))
                continue;

            if (TryReadListItem(line, out var item))
            {
                currentItems.Add(LimitLength(item));
                itemCount++;
                if (itemCount >= MaximumItems)
                    break;
                continue;
            }

            // 兼容旧日志中的普通段落，避免格式不规范时弹窗完全丢失内容。
            currentItems.Add(LimitLength(line));
            itemCount++;
            if (itemCount >= MaximumItems)
                break;
        }

        AddSection(sections, currentTitle, currentItems);
        return sections;
    }

    private static bool TryReadListItem(string line, out string item)
    {
        item = string.Empty;
        if (line.Length < 3
            || line[1] != ' '
            || line[0] is not ('-' or '*'))
        {
            return false;
        }

        item = line[2..].Trim();
        return item.Length > 0;
    }

    private static void AddSection(
        ICollection<LauncherUpdateReleaseNotesSection> sections,
        string? title,
        IReadOnlyList<string> items)
    {
        if (items.Count == 0 || sections.Count >= MaximumSections)
            return;

        sections.Add(new LauncherUpdateReleaseNotesSection(
            string.IsNullOrWhiteSpace(title) ? null : title,
            items.ToArray()));
    }

    private static string LimitLength(string value) => value.Length <= MaximumItemCharacters
        ? value
        : string.Concat(value.AsSpan(0, MaximumItemCharacters - 1), "…");
}
