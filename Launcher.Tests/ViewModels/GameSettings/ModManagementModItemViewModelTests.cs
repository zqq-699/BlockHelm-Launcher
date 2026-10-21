/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.ViewModels.GameSettings;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class ModManagementModItemViewModelTests
{
    [Fact]
    public void TitleCombinesLocalizedAndOriginalNames()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        var viewModel = new ModManagementModItemViewModel(new LocalMod
        {
            Name = "Sodium",
            LocalizedName = "钠",
            FileName = "sodium.jar"
        });

        Assert.Equal("钠 | Sodium", viewModel.Title);
    }

    [Fact]
    public void NonChineseLanguageIgnoresCachedLocalizedName()
    {
        using var culture = new TestUiCultureScope("ja-JP");
        var viewModel = new ModManagementModItemViewModel(new LocalMod
        {
            Name = "Sodium",
            LocalizedName = "钠",
            FileName = "sodium.jar"
        });

        Assert.Equal("Sodium", viewModel.Title);
    }

    [Fact]
    public void TitleUsesOriginalNameWhenLocalizationIsMissing()
    {
        var viewModel = new ModManagementModItemViewModel(new LocalMod
        {
            Name = "Sodium",
            FileName = "sodium.jar"
        });

        Assert.Equal("Sodium", viewModel.Title);
    }
}
