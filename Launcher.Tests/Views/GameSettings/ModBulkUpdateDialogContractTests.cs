/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;

namespace Launcher.Tests.Views.GameSettings;

public sealed class ModBulkUpdateDialogContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ReadOnlyCheckingProgressUsesOneWayBinding()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "Launcher.App",
            "Views",
            "Shell",
            "MainWindow.xaml"));
        var dialog = Assert.Single(document.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "ModBulkUpdateDialogHost"));
        var progressBar = Assert.Single(dialog.Descendants().Where(element =>
            element.Name.LocalName == "ProgressBar"));

        Assert.Equal(
            "{Binding ModBulkUpdateCheckingProgress, Mode=OneWay}",
            (string?)progressBar.Attribute("Value"));

        var message = Assert.Single(dialog.Descendants().Where(element =>
            (string?)element.Attribute("Text") == "{Binding ModBulkUpdateDialogMessage}"));
        var count = Assert.Single(dialog.Descendants().Where(element =>
            (string?)element.Attribute("Text") == "{Binding ModBulkUpdateCheckingCountText}"));
        Assert.Same(message.Parent, count.Parent);
        Assert.Equal("1", (string?)count.Attribute("Grid.Column"));
    }

    [Fact]
    public void SingleModUpdateFooterKeepsItsOwnGridRow()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "Launcher.App",
            "Views",
            "Shell",
            "MainWindow.xaml"));
        var dialog = Assert.Single(document.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "ModUpdateDialogHost"));
        var rowDefinitions = Assert.Single(dialog.Descendants().Where(element =>
            element.Name.LocalName == "Grid.RowDefinitions"));
        var footer = Assert.Single(dialog.Descendants().Where(element =>
            element.Name.LocalName == "StackPanel"
            && (string?)element.Attribute("Grid.Row") == "3"));

        Assert.Equal(4, rowDefinitions.Elements().Count());
        Assert.NotNull(footer);
    }

    [Fact]
    public void ModManagementReceivesTheApplicationUiDispatcher()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Launcher.App",
            "ViewModels",
            "GameSettings",
            "GameSettingsDetailsViewModel.cs"));

        Assert.Contains("uiDispatcher: uiDispatcher", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Launcher.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
