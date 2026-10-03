/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;

namespace Launcher.Tests.App;

public sealed class MainWindowMaximizeContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CaptionAreaExposesMinimizeMaximizeAndCloseInOrder()
    {
        var document = LoadMainWindow();
        var buttonNames = document.Descendants(Presentation + "Button")
            .Select(button => (string?)button.Attribute(Xaml + "Name"))
            .Where(name => name is "MinimizeWindowButton" or "MaximizeRestoreButton" or "CloseWindowButton")
            .ToArray();

        Assert.Equal(
            ["MinimizeWindowButton", "MaximizeRestoreButton", "CloseWindowButton"],
            buttonNames);

        var maximizeButton = FindNamedElement(document, "Button", "MaximizeRestoreButton");
        Assert.Equal("MaximizeRestoreButton_OnClick", (string?)maximizeButton.Attribute("Click"));
        Assert.Contains(
            maximizeButton.Descendants(Presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding WindowState, ElementName=RootWindow}"
                       && (string?)trigger.Attribute("Value") == "Maximized");
        Assert.Contains(
            maximizeButton.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "ToolTip"
                      && (string?)setter.Attribute("Value") == "{x:Static res:Strings.Window_MaximizeTooltip}");
        Assert.Contains(
            maximizeButton.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "ToolTip"
                      && (string?)setter.Attribute("Value") == "{x:Static res:Strings.Window_RestoreTooltip}");
        Assert.Contains(
            maximizeButton.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Text"
                      && (string?)setter.Attribute("Value") == "\uE922");
        Assert.Contains(
            maximizeButton.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Text"
                      && (string?)setter.Attribute("Value") == "\uE923");
    }

    [Fact]
    public void MaximizedWindowRemovesBothContentAndChromeCornerRadii()
    {
        var document = LoadMainWindow();
        var surface = FindNamedElement(document, "Border", "WindowSurface");
        Assert.Contains(
            surface.Descendants(Presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding WindowState, ElementName=RootWindow}"
                       && (string?)trigger.Attribute("Value") == "Maximized"
                       && trigger.Elements(Presentation + "Setter").Any(
                           setter => (string?)setter.Attribute("Property") == "CornerRadius"
                                     && (string?)setter.Attribute("Value") == "0"));

        var code = File.ReadAllText(Path.Combine(
            FindRepositoryRoot().FullName,
            "Launcher.App",
            "Views",
            "Shell",
            "MainWindow.xaml.cs"));
        Assert.Contains("StateChanged += MainWindow_StateChanged;", code, StringComparison.Ordinal);
        Assert.Contains("MaximizedWindowWorkArea.Attach(this);", code, StringComparison.Ordinal);
        Assert.Contains("WindowChrome.GetWindowChrome(this)", code, StringComparison.Ordinal);
        Assert.Contains("WindowState == WindowState.Maximized", code, StringComparison.Ordinal);
    }

    private static XDocument LoadMainWindow() => XDocument.Load(Path.Combine(
        FindRepositoryRoot().FullName,
        "Launcher.App",
        "Views",
        "Shell",
        "MainWindow.xaml"));

    private static XElement FindNamedElement(XDocument document, string elementName, string name) =>
        Assert.Single(document.Descendants(Presentation + elementName)
            .Where(element => (string?)element.Attribute(Xaml + "Name") == name));

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.GetFiles("Launcher.sln").Length == 0)
            root = root.Parent ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        return root;
    }
}
