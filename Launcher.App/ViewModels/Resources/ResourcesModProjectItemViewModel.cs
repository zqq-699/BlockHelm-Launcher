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
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Launcher.App.ViewModels.Resources;

public sealed partial class ResourcesModProjectItemViewModel : ObservableObject
{
    private static readonly string[] LoaderDisplayOrder =
    [
        "fabric",
        "forge",
        "neoforge",
        "quilt"
    ];

    private readonly IReadOnlyList<string>? minecraftReleaseVersionOrder;
    private readonly string fallbackIconKey;

    public ResourcesModProjectItemViewModel(
        ResourceProject project,
        IReadOnlyList<string>? minecraftReleaseVersionOrder = null,
        string fallbackIconKey = "instance_setting_page/mod",
        IReadOnlyList<ResourcesOnlineProjectTypeOption>? typeOptions = null)
    {
        Project = project;
        this.minecraftReleaseVersionOrder = minecraftReleaseVersionOrder;
        this.fallbackIconKey = fallbackIconKey;
        iconSource = project.IconUrl;
        TitleTags = CreateTitleTags(project.Categories, typeOptions);
    }

    public ResourceProject Project { get; }

    public string Title => FormatTitle(Project);

    internal static string FormatTitle(ResourceProject project) => !LauncherLanguages.IsChineseCulture(CultureInfo.CurrentUICulture)
        || string.IsNullOrWhiteSpace(project.LocalizedTitle)
        || string.Equals(project.LocalizedTitle.Trim(), project.Title.Trim(), StringComparison.OrdinalIgnoreCase)
            ? project.Title
            : string.Format(Strings.Resources_ModLocalizedTitleFormat, project.LocalizedTitle.Trim(), project.Title);

    public string Description => Project.Description;

    public IReadOnlyList<string> TitleTags { get; }

    public bool HasTitleTags => TitleTags.Count > 0;

    public string TitleTagsText => string.Join(", ", TitleTags);

    public string Subtitle => Project.Kind is ResourceProjectKind.Mod
        ? string.Join("  ", SupportedMinecraftVersionsText, SupportedLoadersText, SourceText)
        : string.Join("  ", SupportedMinecraftVersionsText, SourceText);

    public string TrailingText => string.Format(Strings.Resources_ModDownloadsFormat, DownloadsText);

    public string SupportedMinecraftVersionsText => ResourceMinecraftVersionSupportFormatter.Format(
        Project.SupportedMinecraftVersions,
        minecraftReleaseVersionOrder);

    public string SupportedLoadersText => FormatLoaders(Project.SupportedLoaders);

    public string SourceText => Project.Source switch
    {
        ResourceProjectSource.Modrinth => Strings.Resources_ModSourceModrinth,
        ResourceProjectSource.CurseForge => Strings.Resources_ModSourceCurseForge,
        _ => string.Empty
    };

    public string DownloadsText => FormatDownloads(Project.Downloads);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey))]
    private string? iconSource;

    public bool ShowsLoaders => Project.Kind is ResourceProjectKind.Mod or ResourceProjectKind.Modpack;

    public string IconKey => string.IsNullOrWhiteSpace(IconSource)
        ? fallbackIconKey
        : string.Empty;

    internal void SetManagedIconSource(string? source)
    {
        IconSource = source;
    }

    private static string FormatLoaders(IReadOnlyList<string> loaders)
    {
        var normalizedLoaders = loaders
            .Where(loader => !string.IsNullOrWhiteSpace(loader))
            .Select(loader => loader.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(loader =>
            {
                var index = Array.IndexOf(LoaderDisplayOrder, loader);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(loader => loader, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalizedLoaders.Count == 0
            ? Strings.Resources_ModLoadersUnknown
            : string.Join("/", normalizedLoaders);
    }

    private static string FormatDownloads(long downloads)
    {
        if (downloads >= 100_000_000)
            return string.Format(Strings.Resources_ModDownloadsHundredMillionFormat, downloads / 100_000_000d);

        if (downloads >= 10_000)
            return string.Format(Strings.Resources_ModDownloadsTenThousandFormat, downloads / 10_000d);

        return downloads.ToString("N0");
    }

    private static IReadOnlyList<string> CreateTitleTags(
        IReadOnlyList<ResourceProjectCategory> categories,
        IReadOnlyList<ResourcesOnlineProjectTypeOption>? typeOptions)
    {
        if (categories.Count == 0 || typeOptions is null || typeOptions.Count == 0)
            return [];

        var titlesByCategory = typeOptions
            .GroupBy(option => option.Category)
            .ToDictionary(group => group.Key, group => group.First().Title);
        var titles = categories
            .Distinct()
            .Where(titlesByCategory.ContainsKey)
            .Select(category => titlesByCategory[category])
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToList();
        return titles;
    }
}
