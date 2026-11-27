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
using Launcher.Infrastructure.Resources;
using Launcher.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Launcher.Tests.Infrastructure.Resources;

public sealed class ResourceProjectInstallationServiceTests
{
    [Fact]
    public async Task ExplicitDownloadDestinationIsPreparedAndForwardedWithExpectedState()
    {
        var catalog = new RecordingCatalogService();
        var service = new ResourceProjectInstallationService(
            catalog,
            new StubModpackImportService(ModpackImportResult.Failure(ModpackImportFailureReason.UnexpectedError)),
            new FakeGameInstanceService(),
            NullLogger<ResourceProjectInstallationService>.Instance);
        var destination = Path.Combine(Path.GetTempPath(), "chosen-resource.jar");
        var request = new ResourceProjectInstallationRequest(
            CreateVersion(ResourceProjectKind.Mod),
            ResourceProjectInstallationTargetKind.LocalDirectory,
            TargetDirectory: Path.GetDirectoryName(destination),
            DestinationPath: destination);

        var preparation = await service.PrepareAsync(request);
        var result = await service.ExecuteAsync(
            request with { ExpectedDestinationState = preparation.DestinationState });

        Assert.Equal(catalog.DestinationState, preparation.DestinationState);
        Assert.Equal(destination, preparation.TargetPath);
        Assert.Equal(destination, result.InstalledPath);
        Assert.Equal(destination, catalog.LastExplicitDestinationPath);
        Assert.Equal(catalog.DestinationState, catalog.LastExpectedDestinationState);
    }

    [Fact]
    public async Task ModpackWorkspaceIsRemovedAfterSuccessfulImport()
    {
        var catalog = new RecordingCatalogService();
        var importedInstance = new GameInstance {  Name = "Pack" };
        var instanceService = new FakeGameInstanceService();
        var service = new ResourceProjectInstallationService(
            catalog,
            new StubModpackImportService(ModpackImportResult.Success(importedInstance)),
            instanceService,
            NullLogger<ResourceProjectInstallationService>.Instance);

        var result = await service.ExecuteAsync(new ResourceProjectInstallationRequest(
            CreateVersion(ResourceProjectKind.Modpack),
            ResourceProjectInstallationTargetKind.NewModpackInstance));

        Assert.Same(importedInstance, result.ModpackImportResult?.ImportedInstance);
        Assert.Equal(0, instanceService.SaveCallCount);
        Assert.NotNull(catalog.LastTargetDirectory);
        Assert.False(Directory.Exists(catalog.LastTargetDirectory));
    }

    [Fact]
    public async Task ModpackIconIsPreparedBeforeCommitWithoutOverwritingExistingIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-resource-icon-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "cached.png");
        var instanceDirectory = Path.Combine(root, "instance");
        Directory.CreateDirectory(instanceDirectory);
        Directory.CreateDirectory(Path.Combine(instanceDirectory, "BHL"));
        var previousIcon = Path.Combine(instanceDirectory, "BHL", "resource-project-icon.png");
        await File.WriteAllBytesAsync(previousIcon, [9]);
        await File.WriteAllBytesAsync(sourcePath, [5, 6, 7]);
        var project = CreateProject("project");
        var catalog = new RecordingCatalogService();
        catalog.ThumbnailSources[project.ProjectId] = new Uri(sourcePath).AbsoluteUri;
        var importedInstance = new GameInstance
        {

            Name = "Pack",
            InstanceDirectory = instanceDirectory
        };
        var instanceService = new FakeGameInstanceService
        {
            SaveException = new IOException("save failed")
        };
        var service = new ResourceProjectInstallationService(
            catalog,
            new StubModpackImportService(ModpackImportResult.Success(importedInstance)),
            instanceService,
            NullLogger<ResourceProjectInstallationService>.Instance);

        try
        {
            var result = await service.ExecuteAsync(new ResourceProjectInstallationRequest(
                CreateVersion(ResourceProjectKind.Modpack),
                ResourceProjectInstallationTargetKind.NewModpackInstance,
                Project: project));

            Assert.True(result.ModpackImportResult?.IsSuccess);
            Assert.Equal([5, 6, 7], await ReadInstalledIconAsync(result));
            Assert.Equal([9], await File.ReadAllBytesAsync(previousIcon));
            Assert.Equal(0, instanceService.SaveCallCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ModpackIconUsesFinalDirectoryAfterStagingIsCommitted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-staged-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.png");
            await File.WriteAllBytesAsync(source, [4, 2]);
            var project = CreateProject("staged");
            var catalog = new RecordingCatalogService();
            catalog.ThumbnailSources[project.ProjectId] = new Uri(source).AbsoluteUri;
            var importer = new StagingModpackImportService(root);
            var service = new ResourceProjectInstallationService(catalog, importer,
                new FakeGameInstanceService(), NullLogger<ResourceProjectInstallationService>.Instance);

            var result = await service.ExecuteAsync(new ResourceProjectInstallationRequest(
                CreateVersion(ResourceProjectKind.Modpack),
                ResourceProjectInstallationTargetKind.NewModpackInstance, Project: project));

            Assert.True(importer.PreparedBeforeCommit);
            Assert.Equal([4, 2], await ReadInstalledIconAsync(result));
            Assert.StartsWith(Path.Combine(root, "final"),
                new Uri(result.ModpackImportResult!.ImportedInstance!.IconSource!).LocalPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentModpackInstallsKeepProjectIconsIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-resource-icons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstSource = Path.Combine(root, "first.png");
        var secondSource = Path.Combine(root, "second.png");
        await File.WriteAllBytesAsync(firstSource, [1]);
        await File.WriteAllBytesAsync(secondSource, [2]);
        var firstProject = CreateProject("first");
        var secondProject = CreateProject("second");
        var catalog = new RecordingCatalogService();
        catalog.ThumbnailSources[firstProject.ProjectId] = new Uri(firstSource).AbsoluteUri;
        catalog.ThumbnailSources[secondProject.ProjectId] = new Uri(secondSource).AbsoluteUri;
        var instanceService = new FakeGameInstanceService();
        var service = new ResourceProjectInstallationService(
            catalog,
            new ArchiveMappedModpackImportService(root),
            instanceService,
            NullLogger<ResourceProjectInstallationService>.Instance);

        try
        {
            var first = service.ExecuteAsync(new ResourceProjectInstallationRequest(
                CreateVersion(ResourceProjectKind.Modpack, "first"),
                ResourceProjectInstallationTargetKind.NewModpackInstance,
                Project: firstProject));
            var second = service.ExecuteAsync(new ResourceProjectInstallationRequest(
                CreateVersion(ResourceProjectKind.Modpack, "second"),
                ResourceProjectInstallationTargetKind.NewModpackInstance,
                Project: secondProject));
            var results = await Task.WhenAll(first, second);

            Assert.Equal([1], await ReadInstalledIconAsync(results[0]));
            Assert.Equal([2], await ReadInstalledIconAsync(results[1]));
            Assert.NotEqual(
                results[0].ModpackImportResult?.ImportedInstance?.IconSource,
                results[1].ModpackImportResult?.ImportedInstance?.IconSource);
            Assert.Equal(0, instanceService.SaveCallCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupCleanupDeletesOnlyOwnedInactiveResourceInstallWorkspaces()
    {
        var service = new ResourceProjectInstallationService(
            new RecordingCatalogService(),
            new StubModpackImportService(ModpackImportResult.Failure(ModpackImportFailureReason.UnexpectedError)),
            new FakeGameInstanceService(),
            NullLogger<ResourceProjectInstallationService>.Instance);
        var staleId = Guid.NewGuid().ToString("N");
        var staleServerId = Guid.NewGuid().ToString("N");
        var activeId = Guid.NewGuid().ToString("N");
        var invalidId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.GetTempPath();
        var stale = Path.Combine(tempRoot, $"launcher-modpack-install-{staleId}");
        var staleServer = Path.Combine(tempRoot, $"launcher-modpack-install-server-{staleServerId}");
        var active = Path.Combine(tempRoot, $"launcher-modpack-install-{activeId}");
        var invalid = Path.Combine(tempRoot, $"launcher-modpack-install-{invalidId}");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(staleServer);
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(
            Path.Combine(stale, ".launcher-resource-install.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, transactionId = staleId }));
        await File.WriteAllTextAsync(
            Path.Combine(staleServer, ".launcher-resource-install.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, transactionId = staleServerId }));
        await File.WriteAllTextAsync(
            Path.Combine(active, ".launcher-resource-install.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, transactionId = activeId }));
        await File.WriteAllTextAsync(Path.Combine(invalid, ".launcher-resource-install.json"), "{}");
        await using var activeLock = new FileStream(
            Path.Combine(active, ".launcher-resource-install.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            await service.CleanupStaleWorkspacesAsync();

            Assert.False(Directory.Exists(stale));
            Assert.False(Directory.Exists(staleServer));
            Assert.True(Directory.Exists(active));
            Assert.True(Directory.Exists(invalid));
        }
        finally
        {
            await activeLock.DisposeAsync();
            if (Directory.Exists(active))
                Directory.Delete(active, recursive: true);
            if (Directory.Exists(invalid))
                Directory.Delete(invalid, recursive: true);
            if (Directory.Exists(stale))
                Directory.Delete(stale, recursive: true);
            if (Directory.Exists(staleServer))
                Directory.Delete(staleServer, recursive: true);
        }
    }

    private static ResourceProjectVersion CreateVersion(ResourceProjectKind kind, string versionId = "version")
    {
        return new ResourceProjectVersion
        {
            Kind = kind,
            VersionId = versionId,
            FileName = kind is ResourceProjectKind.Modpack ? "pack.mrpack" : "mod.jar"
        };
    }

    private static ResourceProject CreateProject(string projectId) => new()
    {
        Kind = ResourceProjectKind.Modpack,
        Source = ResourceProjectSource.Modrinth,
        ProjectId = projectId,
        Title = projectId,
        IconUrl = $"https://example.invalid/{projectId}.png"
    };

    private static async Task<byte[]> ReadInstalledIconAsync(ResourceProjectInstallationResult result)
    {
        var source = result.ModpackImportResult?.ImportedInstance?.IconSource;
        Assert.NotNull(source);
        return await File.ReadAllBytesAsync(new Uri(source).LocalPath);
    }

    private sealed class RecordingCatalogService :
        IResourceCatalogService,
        IResourceCatalogProgressReporter,
        IResourceCatalogDestinationWriter,
        IResourceThumbnailService
    {
        public string? LastTargetDirectory { get; private set; }
        public GameInstance? LastInstallExistsInstance { get; private set; }
        public bool InstallExists { get; init; }
        public ResourceProjectDestinationState DestinationState { get; } =
            new(false, 0, 0, string.Empty);
        public string? LastExplicitDestinationPath { get; private set; }
        public ResourceProjectDestinationState? LastExpectedDestinationState { get; private set; }
        public Dictionary<string, string?> ThumbnailSources { get; } = [];

        public Task<string> EnsureInstanceContentDirectoryAsync(
            ResourceProjectKind kind,
            GameInstance instance,
            CancellationToken cancellationToken)
        {
            var directory = Path.Combine(instance.InstanceDirectory, "content");
            Directory.CreateDirectory(directory);
            return Task.FromResult(directory);
        }

        public Task<ResourceCatalogSearchResult> SearchModsAsync(ResourceCatalogSearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceCatalogSearchResult());

        public Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(ResourceProjectVersionsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResourceProjectVersionsResult());

        public Task<string> InstallProjectVersionAsync(ResourceProjectVersion version, GameInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult("installed");

        public Task<string> DownloadProjectVersionAsync(ResourceProjectVersion version, string targetDirectory, CancellationToken cancellationToken = default)
        {
            LastTargetDirectory = targetDirectory;
            Directory.CreateDirectory(targetDirectory);
            var archive = Path.Combine(targetDirectory, version.FileName);
            File.WriteAllText(archive, version.VersionId);
            return Task.FromResult(archive);
        }

        public Task<string> InstallProjectVersionWithProgressAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new LauncherProgress(ModProgressStages.DownloadingFile, version.FileName, 50));
            return Task.FromResult("installed");
        }

        public Task<string> DownloadProjectVersionWithProgressAsync(
            ResourceProjectVersion version,
            string targetDirectory,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new LauncherProgress(ModProgressStages.DownloadingFile, version.FileName, 50));
            return DownloadProjectVersionAsync(version, targetDirectory, cancellationToken);
        }

        public Task<bool> ProjectVersionDownloadExistsAsync(ResourceProjectVersion version, string targetDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> ProjectVersionInstallExistsAsync(ResourceProjectVersion version, GameInstance instance, CancellationToken cancellationToken = default)
        {
            LastInstallExistsInstance = instance;
            return Task.FromResult(InstallExists);
        }

        public Task<ResourceProjectDestinationState> CaptureDownloadDestinationAsync(
            ResourceProjectVersion version,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            LastExplicitDestinationPath = destinationPath;
            return Task.FromResult(DestinationState);
        }

        public Task<ResourceProjectDestinationState> CaptureInstallDestinationAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            LastExplicitDestinationPath = destinationPath;
            return Task.FromResult(DestinationState);
        }

        public Task<string> DownloadProjectVersionToDestinationAsync(
            ResourceProjectVersion version,
            string destinationPath,
            ResourceProjectDestinationState expectedState,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastExplicitDestinationPath = destinationPath;
            LastExpectedDestinationState = expectedState;
            return Task.FromResult(destinationPath);
        }

        public Task<string> InstallProjectVersionToDestinationAsync(
            ResourceProjectVersion version,
            GameInstance instance,
            string destinationPath,
            ResourceProjectDestinationState expectedState,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastExplicitDestinationPath = destinationPath;
            LastExpectedDestinationState = expectedState;
            return Task.FromResult(destinationPath);
        }

        public string? TryGetCachedThumbnailSource(ResourceProject project) =>
            ThumbnailSources.GetValueOrDefault(project.ProjectId);

        public Task<string?> GetOrCreateThumbnailSourceAsync(
            ResourceProject project,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TryGetCachedThumbnailSource(project));

        public Task<string?> GetOrCreateThumbnailFromCachedProjectAsync(
            ResourceProject project, CancellationToken cancellationToken = default) =>
            GetOrCreateThumbnailSourceAsync(project, cancellationToken);
    }

    private sealed class StubModpackImportService : ILocalModpackImportService
    {
        private readonly ModpackImportResult? result;
        private readonly Exception? exception;

        public StubModpackImportService(ModpackImportResult? result = null, Exception? exception = null)
        {
            this.result = result;
            this.exception = exception;
        }

        public Task<ModpackRecognitionResult> RecognizeArchiveAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnsupportedArchive));

        public async Task<ModpackImportResult> ImportFromArchiveAsync(
            string archivePath,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0,
            Func<StagedModpackInstance, Task>? prepareInstanceAsync = null)
        {
            if (exception is not null)
                throw exception;
            if (prepareInstanceAsync is not null && result?.ImportedInstance is { } instance)
                await prepareInstanceAsync(new StagedModpackInstance { Instance = instance, InstanceDirectory = instance.InstanceDirectory });
            return result!;
        }
    }

    private sealed class ArchiveMappedModpackImportService(string root) : ILocalModpackImportService
    {
        public Task<ModpackRecognitionResult> RecognizeArchiveAsync(
            string archivePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnsupportedArchive));

        public async Task<ModpackImportResult> ImportFromArchiveAsync(
            string archivePath,
            IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0,
            Func<StagedModpackInstance, Task>? prepareInstanceAsync = null)
        {
            var id = await File.ReadAllTextAsync(archivePath, cancellationToken);
            var instanceDirectory = Path.Combine(root, $"instance-{id}");
            Directory.CreateDirectory(instanceDirectory);
            var instance = new GameInstance
            {

                Name = id,
                InstanceDirectory = instanceDirectory
            };
            if (prepareInstanceAsync is not null)
                await prepareInstanceAsync(new StagedModpackInstance { Instance = instance, InstanceDirectory = instanceDirectory });
            return ModpackImportResult.Success(instance);
        }
    }

    private sealed class StagingModpackImportService(string root) : ILocalModpackImportService
    {
        public bool PreparedBeforeCommit { get; private set; }
        public Task<ModpackRecognitionResult> RecognizeArchiveAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnsupportedArchive));

        public async Task<ModpackImportResult> ImportFromArchiveAsync(string archivePath, IProgress<LauncherProgress>? progress,
            CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0, Func<StagedModpackInstance, Task>? prepareInstanceAsync = null)
        {
            var pending = Path.Combine(root, "pending");
            var final = Path.Combine(root, "final");
            Directory.CreateDirectory(pending);
            var instance = new GameInstance { Name = "Pack", InstanceDirectory = pending };
            var transaction = new TestInstallTransaction(pending, final);
            if (prepareInstanceAsync is not null)
                await prepareInstanceAsync(new StagedModpackInstance
                    { Instance = instance, InstanceDirectory = pending, InstallTransaction = transaction });
            PreparedBeforeCommit = instance.IconSource is not null && !Directory.Exists(final);
            Directory.Move(pending, final);
            instance.InstanceDirectory = final;
            return ModpackImportResult.Success(instance);
        }
    }

    private sealed class TestInstallTransaction(string pending, string final) : IInstanceInstallTransaction
    {
        public string MinecraftDirectory => Path.GetDirectoryName(pending)!;
        public string LogicalVersionName => "Pack";
        public string PendingDirectory => pending;
        public string FinalDirectory => final;
        public bool IsCommitted => false;
        public Task CommitAsync(GameInstance instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteLogicalCommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AbortAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
