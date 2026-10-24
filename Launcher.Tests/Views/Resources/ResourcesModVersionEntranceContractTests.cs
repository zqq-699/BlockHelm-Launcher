/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;

namespace Launcher.Tests.Views.Resources;

public sealed class ResourcesModVersionEntranceContractTests
{
    [Fact]
    public void VersionTemplateHidesPendingGroupItemsBeforeTheirEntranceAnimation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Launcher.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var document = XDocument.Load(Path.Combine(
            directory.FullName, "Launcher.App", "Views", "Resources", "ResourcesModPageView.xaml"));
        var versionTemplate = Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains("ResourcesModVersionItemViewModel", StringComparison.Ordinal))));
        var versionButton = Assert.Single(versionTemplate.Descendants().Where(element =>
            element.Name.LocalName == "ListPageItemButton"));
        Assert.DoesNotContain(versionTemplate.Descendants(), element =>
            element.Attributes().Any(attribute => attribute.Value.Contains("RowHeight", StringComparison.Ordinal)
                || attribute.Value.Contains("RowVisualOffsetY", StringComparison.Ordinal)));
        Assert.Equal("{Binding IsGroupEntrancePending, Mode=TwoWay}",
            (string?)versionButton.Attribute("IsGroupEntrancePending"));
        Assert.Equal("{Binding IsFirstInGroup}",
            (string?)versionButton.Attribute("IsFirstInGroup"));
        Assert.Null(versionButton.Attribute("IsGroupEntranceAnimation"));
        var headerTemplate = Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "DataType"
                && attribute.Value.Contains("ResourcesModVersionListHeaderItem", StringComparison.Ordinal))));
        var headerButton = Assert.Single(headerTemplate.Descendants().Where(element =>
            element.Name.LocalName == "Button"));
        Assert.Equal("8,10", (string?)headerButton.Attribute("Padding"));
        var headerDivider = Assert.Single(headerTemplate.Descendants().Where(element =>
            element.Name.LocalName == "Border"
            && (string?)element.Attribute("Height") == "1"
            && ((string?)element.Attribute("Background"))?.Contains("Brush.14FFFFFF", StringComparison.Ordinal) == true));
        Assert.Equal("10,0,20,0", (string?)headerDivider.Attribute("Margin"));
        var versionList = Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "ListBox"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "Name"
                && attribute.Value == "ResourcesModVersionListBox")));
        Assert.Equal("Recycling", versionList.Attributes().Single(attribute =>
            attribute.Name.LocalName.EndsWith("VirtualizationMode", StringComparison.Ordinal)).Value);
    }
}
