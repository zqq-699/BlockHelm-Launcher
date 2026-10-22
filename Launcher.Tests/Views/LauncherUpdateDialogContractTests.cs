/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;

namespace Launcher.Tests.Views;

public sealed class LauncherUpdateDialogContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void UpdateReleaseNotesUseThemedScrollableMarkdownView()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "Launcher.App", "Views", "Shell", "MainWindow.xaml"));
        var dialog = Assert.Single(document.Descendants().Where(element =>
            (string?)element.Attribute(Xaml + "Name") == "LauncherUpdateDialogHost"));
        var markdownView = Assert.Single(dialog.Descendants().Where(element =>
            element.Name.LocalName == "MarkdownView"));
        var scrollViewer = markdownView.Ancestors().First(element => element.Name.LocalName == "ScrollViewer");

        Assert.Equal("{Binding UpdateReleaseNotesMarkdown}", (string?)markdownView.Attribute("Markdown"));
        Assert.Equal("{Binding OpenUpdateReleaseNotesLinkCommand}", (string?)markdownView.Attribute("LinkCommand"));
        Assert.Equal("{StaticResource MainContentScrollViewerStyle}", (string?)scrollViewer.Attribute("Style"));
        Assert.Equal("Auto", (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("0,0,12,0", (string?)scrollViewer.Attribute("Padding"));
    }

    [Fact]
    public void DebugPreviewEntryIsBoundToItsGuardedViewModelState()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(), "Launcher.App", "Views", "Settings", "InfoSettingsView.xaml"));
        var button = Assert.Single(document.Descendants().Where(element =>
            (string?)element.Attribute("Command") == "{Binding PreviewUpdateDialogCommand}"));

        Assert.Equal(
            "{Binding IsUpdateDialogPreviewAvailable, Converter={StaticResource BooleanToMenuTextVisibilityConverter}}",
            (string?)button.Attribute("Visibility"));
        Assert.Equal(
            "{x:Static res:Strings.Settings_PreviewUpdateDialogButton}",
            (string?)button.Attribute("Content"));
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
