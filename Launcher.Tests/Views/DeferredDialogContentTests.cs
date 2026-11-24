/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Launcher.Tests.Helpers;

namespace Launcher.Tests.Views;

public sealed class DeferredDialogContentTests
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void SelectedDialogsBindTheirViewModelThroughAContentTemplate()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = PresentationNamespace;
        var app = TestRepository.ProjectDirectory("Launcher.App");
        var window = XDocument.Load(Path.Combine(app, "Views", "Shell", "MainWindow.xaml"));
        var dialogHost = XDocument.Load(Path.Combine(app, "Controls", "Dialogs", "DialogHost.xaml"));
        var presenter = Assert.Single(dialogHost.Descendants(presentation + "ContentPresenter"));
        Assert.Equal("{Binding DialogContent, ElementName=Host}", (string?)presenter.Attribute("Content"));
        Assert.Equal("{Binding DialogContentTemplate, ElementName=Host}", (string?)presenter.Attribute("ContentTemplate"));

        string[] deferredDialogs =
        [
            "DownloadModpackManualDownloadsDialogHost",
            "ModBulkUpdateDialogHost",
            "SettingsFeedbackDialogHost",
            "LauncherUpdateDialogHost"
        ];
        foreach (var name in deferredDialogs)
        {
            var dialog = Assert.Single(window.Descendants()
                .Where(element => (string?)element.Attribute(xaml + "Name") == name));
            Assert.Equal("{Binding}", (string?)dialog.Attribute("DialogContent"));
            Assert.NotNull(dialog.Attribute("DataContext"));
            var templateProperty = Assert.Single(dialog.Elements(dialog.Name.Namespace + "DialogHost.DialogContentTemplate"));
            Assert.Single(templateProperty.Elements(presentation + "DataTemplate"));
        }
    }

    [Fact]
    public async Task CollapsedOverlayCreatesTemplatedContentOnFirstLayoutAfterOpening()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var template = (DataTemplate)XamlReader.Parse(
                    "<DataTemplate xmlns=\"" + PresentationNamespace + "\"><TextBlock Text=\"{Binding}\" /></DataTemplate>");
                var presenter = new ContentPresenter { Content = "dialog content", ContentTemplate = template };
                var overlay = new Grid { Visibility = Visibility.Collapsed, Children = { presenter } };
                var size = new Size(320, 200);

                overlay.Measure(size);
                overlay.Arrange(new Rect(size));
                Assert.Equal(0, VisualTreeHelper.GetChildrenCount(presenter));

                overlay.Visibility = Visibility.Visible;
                overlay.Measure(size);
                overlay.Arrange(new Rect(size));
                Assert.Equal("dialog content", Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(presenter, 0)).Text);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
    }
}
