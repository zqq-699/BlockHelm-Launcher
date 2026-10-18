/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;

namespace Launcher.Tests.Views;

public sealed class JavaSearchButtonsContractTests
{
    [Theory]
    [InlineData(
        "Launcher.App/Views/Settings/JavaSettingsView.xaml",
        "DeepSearchJavaRuntimesCommand",
        "RefreshJavaRuntimesCommand")]
    [InlineData(
        "Launcher.App/Views/GameSettings/InstanceJavaSettingsView.xaml",
        "DeepSearchInstanceJavaRuntimesCommand",
        "RefreshInstanceJavaRuntimesCommand")]
    public void DeepSearchButtonAppearsBeforeAutomaticSearchButton(
        string relativePath,
        string deepSearchCommand,
        string automaticSearchCommand)
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot().FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var buttons = document.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToList();
        var deepSearchButton = FindButton(buttons, deepSearchCommand);
        var automaticSearchButton = FindButton(buttons, automaticSearchCommand);

        Assert.Equal("{x:Static res:Strings.Settings_JavaDeepSearchButton}",
            deepSearchButton.Attribute("Content")?.Value);
        Assert.True(
            int.Parse(deepSearchButton.Attribute("Grid.Column")!.Value)
            < int.Parse(automaticSearchButton.Attribute("Grid.Column")!.Value));
    }

    private static XElement FindButton(IEnumerable<XElement> buttons, string commandName) =>
        buttons.Single(element =>
            string.Equals(
                element.Attribute("Command")?.Value,
                $"{{Binding {commandName}}}",
                StringComparison.Ordinal));

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.GetFiles("Launcher.sln").Length == 0)
            root = root.Parent ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        return root;
    }
}
