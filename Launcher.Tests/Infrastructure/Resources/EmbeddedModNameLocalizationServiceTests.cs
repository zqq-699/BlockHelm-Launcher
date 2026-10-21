/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Infrastructure.Resources.ResourceLocalization;

namespace Launcher.Tests.Infrastructure.Resources;

public sealed class EmbeddedModNameLocalizationServiceTests
{
    [Fact]
    public void OptionalCatalogFallsBackToNoLocalizationWhenNotEmbedded()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        const string resourceName = "Launcher.Infrastructure.Resources.ResourceLocalization.mods.v1.jsonl";
        using var stream = typeof(EmbeddedModNameLocalizationService).Assembly
            .GetManifestResourceStream(resourceName);
        var localization = new EmbeddedModNameLocalizationService();

        if (stream is null)
        {
            Assert.Null(localization.FindByProjectSlug(ResourceProjectSource.Modrinth, "sodium", "Sodium"));
            Assert.Null(localization.FindMcmodIdByProjectSlug(ResourceProjectSource.Modrinth, "sodium"));
            Assert.Null(localization.FindSearchTerm(ResourceProjectSource.Modrinth, "钠"));
            return;
        }

        Assert.Equal("钠", localization.FindByProjectSlug(ResourceProjectSource.Modrinth, "sodium", "Sodium"));
        Assert.Equal(2785, localization.FindMcmodIdByProjectSlug(ResourceProjectSource.Modrinth, "sodium"));
        Assert.Equal("Sodium", localization.FindSearchTerm(ResourceProjectSource.Modrinth, "钠"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja-JP")]
    public void NonChineseLanguageDoesNotProvideChineseNamesOrSearch(string language)
    {
        using var culture = new TestUiCultureScope(language);
        var localization = new EmbeddedModNameLocalizationService();

        Assert.Null(localization.FindByProjectSlug(ResourceProjectSource.Modrinth, "sodium", "Sodium"));
        Assert.Null(localization.FindSearchTerm(ResourceProjectSource.Modrinth, "钠"));
    }

    [Fact]
    public void TraditionalChineseLanguageKeepsChineseLocalizationEnabled()
    {
        using var culture = new TestUiCultureScope("zh-Hant");
        Assert.True(LauncherLanguages.IsChineseCulture(System.Globalization.CultureInfo.CurrentUICulture));
    }
}
