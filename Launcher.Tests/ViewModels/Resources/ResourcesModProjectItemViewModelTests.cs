/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.ViewModels.Resources;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Resources;

public sealed class ResourcesModProjectItemViewModelTests
{
    [Fact]
    public void TitleCombinesLocalizedAndOriginalTitles()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        var viewModel = new ResourcesModProjectItemViewModel(new ResourceProject
        {
            Title = "Sodium",
            LocalizedTitle = "钠"
        });

        Assert.Equal("钠 | Sodium", viewModel.Title);
    }

    [Fact]
    public void TitleUsesOriginalTitleWhenLocalizationIsMissing()
    {
        var viewModel = new ResourcesModProjectItemViewModel(new ResourceProject
        {
            Title = "Sodium"
        });

        Assert.Equal("Sodium", viewModel.Title);
    }

    [Fact]
    public void DependencyRequirementUsesLocalizedTitle()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        var viewModel = new ResourcesModDependencyRequirementItemViewModel(
            new ResourceProjectDependency
            {
                Project = new ResourceProject { Title = "Sodium", LocalizedTitle = "钠" }
            },
            minimumVersion: null,
            installVersion: null,
            state: ResourceDependencyRequirementState.Missing);

        Assert.Equal("钠 | Sodium", viewModel.Title);
    }

    [Fact]
    public void NonChineseLanguageIgnoresCachedLocalizedTitle()
    {
        using var culture = new TestUiCultureScope("en");
        var viewModel = new ResourcesModProjectItemViewModel(new ResourceProject
        {
            Title = "Sodium",
            LocalizedTitle = "钠"
        });

        Assert.Equal("Sodium", viewModel.Title);
    }
}
