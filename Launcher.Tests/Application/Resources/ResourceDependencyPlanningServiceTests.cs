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

using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Application.Resources;

public sealed class ResourceDependencyPlanningServiceTests
{
    [Fact]
    public async Task InstalledDependencyAtOrAboveMinimumIsNotPlannedAgain()
    {
        var dependency = CreateDependency();
        var catalog = new DependencyCatalogService(CreateDependencyVersions());
        var modService = new StubModService(
        [
            new LocalMod { ModId = "dependency", Version = "1.5.0", IsEnabled = true }
        ]);
        var installation = new RecordingInstallationService();
        var service = new ResourceDependencyPlanningService(
            catalog,
            installation,
            modService,
            NullLogger<ResourceDependencyPlanningService>.Instance);

        var plan = await service.CreatePlanAsync(
            new ResourceProjectVersion { RequiredDependencies = [dependency] },
            CreateInstance());

        Assert.Empty(plan.MissingDependencies);
        Assert.Equal(ResourceDependencyRequirementState.Installed, Assert.Single(plan.Requirements).State);
    }

    [Fact]
    public async Task DependencyDownloadPreservesTheParentTaskSpeedMeter()
    {
        var dependency = CreateDependency();
        var installation = new RecordingInstallationService();
        var service = new ResourceDependencyPlanningService(
            new DependencyCatalogService(CreateDependencyVersions()),
            installation,
            new StubModService([]),
            NullLogger<ResourceDependencyPlanningService>.Instance);
        var instance = CreateInstance();
        var plan = await service.CreatePlanAsync(
            new ResourceProjectVersion { RequiredDependencies = [dependency] },
            instance);
        var rootProgress = DownloadSpeedTaskProgress.Create(_ => { }, _ => { }, out var lifetime);
        using (lifetime)
        {
            var dependencyProgress = DownloadSpeedTaskProgress.Carry(
                rootProgress,
                new Progress<ResourceDependencyInstallProgress>());

            await service.InstallRequiredDependenciesAsync(
                plan.MissingDependencies,
                instance,
                dependencyProgress);

            Assert.Same(
                SpeedMeterProgress.TryGet(rootProgress),
                SpeedMeterProgress.TryGet(Assert.Single(installation.Progresses)));
        }
    }

    private static ResourceProjectDependency CreateDependency()
    {
        return new ResourceProjectDependency
        {
            VersionId = "minimum-version",
            Project = new ResourceProject
            {
                Kind = ResourceProjectKind.Mod,
                Source = ResourceProjectSource.Modrinth,
                ProjectId = "dependency-id",
                Slug = "dependency",
                Title = "Dependency"
            }
        };
    }

    private static IReadOnlyList<ResourceProjectVersion> CreateDependencyVersions()
    {
        return
        [
            new ResourceProjectVersion
            {
                VersionId = "minimum-version",
                VersionNumber = "1.2.0",
                VersionType = "beta"
            },
            new ResourceProjectVersion
            {
                VersionId = "release-version",
                VersionNumber = "1.6.0",
                VersionType = "release"
            }
        ];
    }

    private static GameInstance CreateInstance()
    {
        return new GameInstance
        {

            MinecraftVersion = "1.20.1",
            Loader = LoaderKind.Fabric,
            InstanceDirectory = "instance"
        };
    }

    private sealed class DependencyCatalogService(IReadOnlyList<ResourceProjectVersion> versions) : IResourceCatalogService
    {
        public Task<ResourceCatalogSearchResult> SearchModsAsync(ResourceCatalogSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceCatalogSearchResult());

        public Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(ResourceProjectVersionsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceProjectVersionsResult { Versions = versions });

        public Task<string> InstallProjectVersionAsync(ResourceProjectVersion version, GameInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> DownloadProjectVersionAsync(ResourceProjectVersion version, string targetDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<bool> ProjectVersionDownloadExistsAsync(ResourceProjectVersion version, string targetDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> ProjectVersionInstallExistsAsync(ResourceProjectVersion version, GameInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class StubModService(IReadOnlyList<LocalMod> mods) : IModService
    {
        public Task<IReadOnlyList<LocalMod>> GetModsAsync(GameInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(mods);

        public Task<LocalMod> ImportAsync(GameInstance instance, string sourceJarPath, bool overwriteExisting = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetEnabledAsync(LocalMod mod, bool enabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(LocalMod mod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingInstallationService : IResourceProjectInstallationService
    {
        public List<ResourceProjectInstallationRequest> Requests { get; } = [];
        public List<IProgress<LauncherProgress>?> Progresses { get; } = [];

        public Task<string> EnsureInstanceContentDirectoryAsync(
            ResourceProjectKind kind,
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(instance.InstanceDirectory);

        public Task<ResourceProjectInstallationPreparationResult> PrepareAsync(ResourceProjectInstallationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceProjectInstallationPreparationResult(false));

        public Task<ResourceProjectInstallationResult> ExecuteAsync(
            ResourceProjectInstallationRequest request,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Progresses.Add(progress);
            return Task.FromResult(new ResourceProjectInstallationResult());
        }
    }
}
