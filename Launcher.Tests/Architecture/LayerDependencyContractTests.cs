/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Xml.Linq;

namespace Launcher.Tests.Architecture;

public sealed class LayerDependencyContractTests
{
    private static readonly string[] ProductionProjects =
        ["Launcher.App", "Launcher.Application", "Launcher.Domain", "Launcher.Infrastructure"];

    [Fact]
    public void ProjectReferencesFollowLayerDirection()
    {
        var root = new DirectoryInfo(TestRepository.Root);

        AssertProjectReferences(root, "Launcher.Domain");
        AssertProjectReferences(root, "Launcher.Application", "Launcher.Domain");
        AssertProjectReferences(root, "Launcher.Infrastructure", "Launcher.Application", "Launcher.Domain");
        AssertProjectReferences(root, "Launcher.App", "Launcher.Application", "Launcher.Domain", "Launcher.Infrastructure");
    }

    [Fact]
    public void SourceDoesNotRestoreLauncherCoreOrReverseLayerDependencies()
    {
        var root = new DirectoryInfo(TestRepository.Root);
        var sourceFiles = ProductionProjects
            .SelectMany(project => TestRepository.EnumerateProjectFiles(project, "*.cs"))
            .ToArray();

        Assert.DoesNotContain(sourceFiles, file => File.ReadAllText(file).Contains("using Launcher.Core", StringComparison.Ordinal));
        Assert.DoesNotContain(
            sourceFiles.Where(file => IsUnder(file, root, "Launcher.Infrastructure")),
            file => HasUsing(file, "Launcher.App"));
        Assert.DoesNotContain(
            sourceFiles.Where(file => IsUnder(file, root, "Launcher.Application")),
            file => HasUsing(file, "Launcher.Infrastructure") || HasUsing(file, "Launcher.App"));
        Assert.DoesNotContain(
            sourceFiles.Where(file => IsUnder(file, root, "Launcher.Domain")),
            file => HasUsing(file, "Launcher.Application")
                || HasUsing(file, "Launcher.Infrastructure")
                || HasUsing(file, "Launcher.App"));
        Assert.DoesNotContain(
            sourceFiles.Where(file => IsUnder(file, root, "Launcher.App"))
                .Where(file => !string.Equals(
                    file,
                    Path.Combine(root.FullName, "Launcher.App", "App.xaml.cs"),
                    StringComparison.OrdinalIgnoreCase)),
            file => HasUsing(file, "Launcher.Infrastructure"));
    }

    [Fact]
    public void ShellKeepsStateMonitoringAndBlockingShutdownOutOfUiBoundaries()
    {
        var root = new DirectoryInfo(TestRepository.Root);
        var mainWindowSource = File.ReadAllText(Path.Combine(
            root.FullName,
            "Launcher.App",
            "Views",
            "Shell",
            "MainWindow.xaml.cs"));
        var appSource = File.ReadAllText(Path.Combine(root.FullName, "Launcher.App", "App.xaml.cs"));

        Assert.DoesNotContain("ILauncherStateMonitor", mainWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystemWatcher", mainWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCodeDoesNotSynchronouslyBlockOnTasksOrUseAsyncVoidViewModels()
    {
        var productionFiles = ProductionProjects
            .SelectMany(project => TestRepository.EnumerateProjectFiles(project, "*.cs"))
            .ToArray();
        var blockingFiles = productionFiles
            .Where(file => File.ReadAllText(file).Contains("GetAwaiter().GetResult()", StringComparison.Ordinal))
            .ToArray();
        var asyncVoidViewModels = productionFiles
            .Where(file => file.Contains(
                $"{Path.DirectorySeparatorChar}ViewModels{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("async void", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(blockingFiles);
        Assert.Empty(asyncVoidViewModels);
    }

    [Fact]
    public void OnlineResourcePageDoesNotReabsorbChildRequestState()
    {
        var root = new DirectoryInfo(TestRepository.Root);
        var source = File.ReadAllText(Path.Combine(
            root.FullName,
            "Launcher.App",
            "ViewModels",
            "Resources",
            "ResourcesModPageViewModel.cs"));

        Assert.DoesNotContain("CancellationTokenSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservableCollection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchQuery", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadAvailableVersionsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResourcesProjectListViewModel ProjectList", source, StringComparison.Ordinal);
        Assert.Contains("ResourcesProjectVersionsViewModel Versions", source, StringComparison.Ordinal);
    }

    private static void AssertProjectReferences(DirectoryInfo root, string project, params string[] expectedReferences)
    {
        var projectPath = Path.Combine(root.FullName, project, $"{project}.csproj");
        var document = XDocument.Load(projectPath);
        var references = document.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(value!))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), references);
    }

    private static bool IsUnder(string file, DirectoryInfo root, string project)
    {
        var projectRoot = Path.Combine(root.FullName, project) + Path.DirectorySeparatorChar;
        return file.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUsing(string file, string namespacePrefix)
    {
        return File.ReadLines(file).Any(line =>
            line.StartsWith($"using {namespacePrefix}.", StringComparison.Ordinal)
            || string.Equals(line, $"using {namespacePrefix};", StringComparison.Ordinal));
    }

}
