/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows.Data;
using System.Xml.Linq;
using Launcher.App.Controls;

namespace Launcher.Tests.App;

public sealed class JavaRuntimeGroupingTests
{
    [Theory]
    [InlineData("JavaSettingsView.xaml")]
    [InlineData("InfoSettingsView.xaml")]
    public void SettingsSectionUsesGroupedVirtualizationWithoutAnOuterScrollViewer(string viewFile)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Launcher.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "Launcher.App", "Views", "Settings", viewFile));
        var list = Assert.Single(document.Root!.Elements().Where(element => element.Name.LocalName == "ListBox"));
        Assert.Equal("True", list.Attribute("VirtualizingPanel.IsVirtualizingWhenGrouping")?.Value);
        Assert.Contains(list.Descendants(), element => element.Name.LocalName == "VirtualizingStackPanel");
        Assert.DoesNotContain(list.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
    }

    [Fact]
    public void InstanceJavaUsesGroupedVirtualizationInFullViewportHost()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Launcher.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "Launcher.App", "Views", "GameSettings", "InstanceJavaSettingsView.xaml"));
        var list = Assert.Single(document.Root!.Elements().Where(element => element.Name.LocalName == "ListBox"));
        Assert.Equal("True", list.Attribute("VirtualizingPanel.IsVirtualizingWhenGrouping")?.Value);
        Assert.Contains(list.Descendants(), element => element.Name.LocalName == "VirtualizingStackPanel");
        Assert.DoesNotContain(list.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
    }

    [Fact]
    public void SettingsScrollHostUsesOneStateForBothVirtualizedSections()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Launcher.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory.FullName, "Launcher.App", "Views", "Settings", "SettingsPageView.xaml"));
        var hostTriggers = document.Descendants()
            .Where(element => element.Name.LocalName == "DataTrigger"
                && element.Elements().Any(setter => setter.Attribute("Property")?.Value == "UseFrameScrollViewer"
                    && setter.Attribute("Value")?.Value == "False"));
        var trigger = Assert.Single(hostTriggers);
        Assert.Equal("{Binding UsesVirtualizedScrollHost}", trigger.Attribute("Binding")?.Value);
    }

    [Fact]
    public void GroupRemainsAvailableWhenRuntimeListIsEmptyOrChanges()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var items = new ObservableCollection<object>();
                var view = new ListCollectionView(items);
                view.GroupDescriptions.Add(new SingleSettingsSectionGroupDescription());
                Assert.Single(view.Groups!);
                Assert.Empty(Assert.IsAssignableFrom<CollectionViewGroup>(view.Groups![0]).Items);

                var runtime = new object();
                items.Add(runtime);
                Assert.Same(runtime, Assert.Single(Assert.IsAssignableFrom<CollectionViewGroup>(view.Groups![0]).Items));
                items.Clear();
                Assert.Single(view.Groups!);
                Assert.Empty(Assert.IsAssignableFrom<CollectionViewGroup>(view.Groups![0]).Items);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
