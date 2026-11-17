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

using System.IO;
using System.Text.Json;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Resources;

public sealed class ResourceProjectInstallationService : IResourceProjectInstallationService
{
    private const string WorkspacePrefix = "launcher-modpack-install-";
    private const string MarkerFileName = ".launcher-resource-install.json";
    private const string ActiveLockFileName = ".launcher-resource-install.lock";
    private const string ResourceProjectIconFileName = "resource-project-icon.png";
    private static readonly JsonSerializerOptions MarkerJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IResourceCatalogService resourceCatalogService;
    private readonly ILocalModpackImportService localModpackImportService;
    private readonly IServerModpackDeploymentService serverModpackDeploymentService;
    private readonly IGameInstanceService gameInstanceService;
    private readonly ILogger<ResourceProjectInstallationService> logger;

    public ResourceProjectInstallationService(
        IResourceCatalogService resourceCatalogService,
        ILocalModpackImportService localModpackImportService,
        IGameInstanceService gameInstanceService,
        ILogger<ResourceProjectInstallationService> logger)
        : this(
            resourceCatalogService,
            localModpackImportService,
            new UnsupportedServerModpackDeploymentService(),
            gameInstanceService,
            logger)
    {
    }

    public ResourceProjectInstallationService(
        IResourceCatalogService resourceCatalogService,
        ILocalModpackImportService localModpackImportService,
        IServerModpackDeploymentService serverModpackDeploymentService,
        IGameInstanceService gameInstanceService,
        ILogger<ResourceProjectInstallationService> logger)
    {
        this.resourceCatalogService = resourceCatalogService;
        this.localModpackImportService = localModpackImportService;
        this.serverModpackDeploymentService = serverModpackDeploymentService;
        this.gameInstanceService = gameInstanceService;
        this.logger = logger;
    }

    public Task<string> EnsureInstanceContentDirectoryAsync(
        ResourceProjectKind kind,
        GameInstance instance,
        CancellationToken cancellationToken = default) =>
        RequireDestinationWriter().EnsureInstanceContentDirectoryAsync(
            kind,
            instance,
            cancellationToken);

    public async Task<ResourceProjectInstallationPreparationResult> PrepareAsync(
        ResourceProjectInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            var destinationWriter = RequireDestinationWriter();
            var destinationState = request.TargetKind switch
            {
                ResourceProjectInstallationTargetKind.LocalDirectory =>
                    await destinationWriter.CaptureDownloadDestinationAsync(
                        request.Version,
                        request.DestinationPath,
                        cancellationToken).ConfigureAwait(false),
                ResourceProjectInstallationTargetKind.ExistingInstance =>
                    await destinationWriter.CaptureInstallDestinationAsync(
                        request.Version,
                        RequireInstance(request),
                        request.DestinationPath,
                        cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException(
                    "An explicit destination is not supported for this installation target.",
                    nameof(request))
            };
            return new ResourceProjectInstallationPreparationResult(
                destinationState.Exists,
                Path.GetFullPath(request.DestinationPath),
                destinationState);
        }

        if (request.TargetKind is ResourceProjectInstallationTargetKind.NewServerDirectory)
        {
            var targetPath = serverModpackDeploymentService.ResolveTargetDirectory(
                RequireTargetDirectory(request),
                request.Version.FileName,
                request.Version.VersionId);
            return new ResourceProjectInstallationPreparationResult(
                Directory.Exists(targetPath) || File.Exists(targetPath),
                targetPath);
        }

        var targetExists = request.TargetKind switch
        {
            ResourceProjectInstallationTargetKind.LocalDirectory =>
                await resourceCatalogService.ProjectVersionDownloadExistsAsync(
                    request.Version,
                    RequireTargetDirectory(request),
                    cancellationToken).ConfigureAwait(false),
            ResourceProjectInstallationTargetKind.ExistingInstance =>
                await resourceCatalogService.ProjectVersionInstallExistsAsync(
                    request.Version,
                    RequireInstance(request),
                    cancellationToken).ConfigureAwait(false),
            ResourceProjectInstallationTargetKind.NewModpackInstance => false,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        return new ResourceProjectInstallationPreparationResult(targetExists);
    }

    public Task CleanupStaleWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CleanupStaleWorkspaces(cancellationToken), cancellationToken);
    }

    public async Task<ResourceProjectInstallationResult> ExecuteAsync(
        ResourceProjectInstallationRequest request,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        switch (request.TargetKind)
        {
            case ResourceProjectInstallationTargetKind.LocalDirectory:
            {
                var path = !string.IsNullOrWhiteSpace(request.DestinationPath)
                    ? await RequireDestinationWriter().DownloadProjectVersionToDestinationAsync(
                        request.Version,
                        request.DestinationPath,
                        RequireExpectedDestinationState(request),
                        progress,
                        cancellationToken).ConfigureAwait(false)
                    : await DownloadProjectVersionAsync(
                        request.Version,
                        RequireTargetDirectory(request),
                        progress,
                        cancellationToken).ConfigureAwait(false);
                progress?.Report(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty, 99));
                return new ResourceProjectInstallationResult(InstalledPath: path);
            }
            case ResourceProjectInstallationTargetKind.ExistingInstance:
            {
                var path = !string.IsNullOrWhiteSpace(request.DestinationPath)
                    ? await RequireDestinationWriter().InstallProjectVersionToDestinationAsync(
                        request.Version,
                        RequireInstance(request),
                        request.DestinationPath,
                        RequireExpectedDestinationState(request),
                        progress,
                        cancellationToken).ConfigureAwait(false)
                    : await InstallProjectVersionAsync(
                        request.Version,
                        RequireInstance(request),
                        progress,
                        cancellationToken).ConfigureAwait(false);
                progress?.Report(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty, 99));
                return new ResourceProjectInstallationResult(InstalledPath: path);
            }
            case ResourceProjectInstallationTargetKind.NewModpackInstance:
                return await ImportModpackAsNewInstanceAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false);
            case ResourceProjectInstallationTargetKind.NewServerDirectory:
                return await DeployModpackServerAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private Task<string> DownloadProjectVersionAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken) =>
        resourceCatalogService is IResourceCatalogProgressReporter progressReporter
            ? progressReporter.DownloadProjectVersionWithProgressAsync(version, targetDirectory, progress, cancellationToken)
            : resourceCatalogService.DownloadProjectVersionAsync(version, targetDirectory, cancellationToken);

    private Task<string> InstallProjectVersionAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken) =>
        resourceCatalogService is IResourceCatalogProgressReporter progressReporter
            ? progressReporter.InstallProjectVersionWithProgressAsync(version, instance, progress, cancellationToken)
            : resourceCatalogService.InstallProjectVersionAsync(version, instance, cancellationToken);

    private IResourceCatalogDestinationWriter RequireDestinationWriter() =>
        resourceCatalogService as IResourceCatalogDestinationWriter
        ?? throw SafeExceptionLog.Describe(new InvalidOperationException("The configured resource catalog does not support explicit download destinations."), "DownloadValidation", ("Reason", "The configured resource catalog does not support explicit download destinations."));

    private static ResourceProjectDestinationState RequireExpectedDestinationState(
        ResourceProjectInstallationRequest request) =>
        request.ExpectedDestinationState
        ?? throw new ArgumentException(
            "The explicit destination must be prepared before execution.",
            nameof(request));

    private async Task<ResourceProjectInstallationResult> ImportModpackAsNewInstanceAsync(
        ResourceProjectInstallationRequest request,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"{WorkspacePrefix}{transactionId}");
        Directory.CreateDirectory(tempDirectory);
        await using var activeLock = new FileStream(
            Path.Combine(tempDirectory, ActiveLockFileName),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        await AtomicJsonFileWriter.WriteAsync(
                Path.Combine(tempDirectory, MarkerFileName),
                new ResourceInstallWorkspaceMarker(1, transactionId),
                MarkerJsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var iconPreparationTask = PrepareResourceProjectIconAsync(
                request.Project,
                tempDirectory,
                cancellationToken);
            var archiveDownloadTask = DownloadProjectVersionAsync(
                request.Version,
                tempDirectory,
                progress,
                cancellationToken);
            await Task.WhenAll((Task)iconPreparationTask, archiveDownloadTask).ConfigureAwait(false);
            var archivePath = await archiveDownloadTask.ConfigureAwait(false);
            var preparedIconPath = await iconPreparationTask.ConfigureAwait(false);
            var result = await localModpackImportService.ImportFromArchiveAsync(
                archivePath,
                progress,
                cancellationToken,
                prepareInstanceAsync: preparedIconPath is null ? null : staged =>
                {
                    TryApplyResourceProjectIcon(preparedIconPath, staged);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            return new ResourceProjectInstallationResult(archivePath, result);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    await activeLock.DisposeAsync().ConfigureAwait(false);
                    DeleteOwnedWorkspace(tempDirectory, transactionId);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to clean resource project installation workspace. Workspace={Workspace}",
                    tempDirectory);
            }
        }
    }

    private async Task<ResourceProjectInstallationResult> DeployModpackServerAsync(
        ResourceProjectInstallationRequest request,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var project = request.Project
            ?? throw new ArgumentException("A source project is required for server deployment.", nameof(request));
        var transactionId = Guid.NewGuid().ToString("N");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"{WorkspacePrefix}server-{transactionId}");
        Directory.CreateDirectory(tempDirectory);
        await using var activeLock = new FileStream(
            Path.Combine(tempDirectory, ActiveLockFileName),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        await AtomicJsonFileWriter.WriteAsync(
                Path.Combine(tempDirectory, MarkerFileName),
                new ResourceInstallWorkspaceMarker(1, transactionId),
                MarkerJsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var archivePath = await DownloadProjectVersionAsync(
                request.Version,
                tempDirectory,
                progress,
                cancellationToken).ConfigureAwait(false);
            var result = await serverModpackDeploymentService.DeployAsync(
                new ServerModpackDeploymentRequest(
                    archivePath,
                    RequireTargetDirectory(request),
                    request.Version.FileName,
                    request.Version.VersionId,
                    project.Source),
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty, 99));
            return new ResourceProjectInstallationResult(InstalledPath: result.FinalDirectory);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    await activeLock.DisposeAsync().ConfigureAwait(false);
                    DeleteOwnedWorkspace(tempDirectory, transactionId);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to clean server modpack download workspace. TransactionId={TransactionId}",
                    transactionId);
            }
        }
    }

    private async Task<string?> PrepareResourceProjectIconAsync(
        ResourceProject? project,
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        if (project is null
            || string.IsNullOrWhiteSpace(project.IconUrl)
            || resourceCatalogService is not IResourceThumbnailService thumbnailService)
        {
            return null;
        }

        try
        {
            var source = await thumbnailService
                .GetOrCreateThumbnailSourceAsync(project, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(source)
                || !Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
                || !sourceUri.IsFile
                || !File.Exists(sourceUri.LocalPath))
            {
                logger.LogWarning(
                    "Resource project thumbnail did not resolve to a local file. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                    project.Kind,
                    project.Source,
                    project.ProjectId);
                return null;
            }

            var preparedPath = Path.Combine(workspaceDirectory, ResourceProjectIconFileName);
            File.Copy(sourceUri.LocalPath, preparedPath, overwrite: true);
            return preparedPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to prepare resource project icon. Kind={Kind} Source={Source} ProjectId={ProjectId}",
                project.Kind,
                project.Source,
                project.ProjectId);
            return null;
        }
    }

    private void TryApplyResourceProjectIcon(
        string preparedIconPath,
        StagedModpackInstance staged)
    {
        var instance = staged.Instance;
        var originalIconSource = instance.IconSource;
        var iconDirectory = Path.Combine(
            instance.InstanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName);
        var iconFileName = $"resource-project-icon-{Guid.NewGuid():N}.png";
        var destinationPath = Path.Combine(iconDirectory, iconFileName);
        var finalDirectory = staged.InstallTransaction?.FinalDirectory ?? staged.InstanceDirectory;
        try
        {
            Directory.CreateDirectory(iconDirectory);
            File.Copy(preparedIconPath, destinationPath, overwrite: false);
            instance.IconSource = new Uri(Path.GetFullPath(Path.Combine(
                finalDirectory, LauncherApplicationIdentity.StorageDirectoryName, iconFileName))).AbsoluteUri;
            logger.LogInformation(
                "Resource project icon applied to imported instance. InstanceDirectory={InstanceDirectory}",
                instance.InstanceDirectory);
        }
        catch (Exception exception)
        {
            instance.IconSource = originalIconSource;
            TryDelete(destinationPath);
            logger.LogWarning(
                exception,
                "Failed to apply resource project icon to imported instance. InstanceDirectory={InstanceDirectory}",
                instance.InstanceDirectory);
        }
    }

    private void CleanupStaleWorkspaces(CancellationToken cancellationToken)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        foreach (var directory in Directory.EnumerateDirectories(tempRoot, $"{WorkspacePrefix}*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            var workspaceSuffix = name.StartsWith(WorkspacePrefix, StringComparison.OrdinalIgnoreCase)
                ? name[WorkspacePrefix.Length..]
                : string.Empty;
            var transactionId = workspaceSuffix.StartsWith("server-", StringComparison.OrdinalIgnoreCase)
                ? workspaceSuffix["server-".Length..]
                : workspaceSuffix;
            if (!Guid.TryParseExact(transactionId, "N", out _)
                || !TryReadValidMarker(directory, transactionId))
            {
                continue;
            }

            FileStream? cleanupLock = null;
            try
            {
                cleanupLock = new FileStream(
                    Path.Combine(directory, ActiveLockFileName),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                if (TryReadValidMarker(directory, transactionId))
                    DeleteOwnedWorkspace(directory, transactionId, cleanupLock);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                logger.LogDebug("Resource install workspace is active in another process. Workspace={Workspace}", directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning(exception, "Failed to clean stale resource install workspace. Workspace={Workspace}", directory);
            }
            finally
            {
                cleanupLock?.Dispose();
            }
        }
    }

    private static bool TryReadValidMarker(string directory, string transactionId)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return false;
            var marker = JsonSerializer.Deserialize<ResourceInstallWorkspaceMarker>(
                File.ReadAllText(Path.Combine(directory, MarkerFileName)),
                MarkerJsonOptions);
            return marker is { SchemaVersion: 1 }
                && string.Equals(marker.TransactionId, transactionId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void DeleteOwnedWorkspace(
        string directory,
        string transactionId,
        FileStream? cleanupLock = null)
    {
        if (!TryReadValidMarker(directory, transactionId))
            return;
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            DeleteTreeWithoutFollowingReparsePoints(childDirectory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (cleanupLock is not null
                && string.Equals(file, cleanupLock.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            File.Delete(file);
        }
        cleanupLock?.Dispose();
        var lockPath = Path.Combine(directory, ActiveLockFileName);
        if (File.Exists(lockPath))
            File.Delete(lockPath);
        Directory.Delete(directory, recursive: false);
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        foreach (var child in Directory.EnumerateDirectories(path))
            DeleteTreeWithoutFollowingReparsePoints(child);
        foreach (var file in Directory.EnumerateFiles(path))
            File.Delete(file);
        Directory.Delete(path, recursive: false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private sealed record ResourceInstallWorkspaceMarker(int SchemaVersion, string TransactionId);

    private sealed class UnsupportedServerModpackDeploymentService : IServerModpackDeploymentService
    {
        public string ResolveTargetDirectory(string parentDirectory, string archiveFileName, string versionId) =>
            Path.Combine(
                Path.GetFullPath(parentDirectory),
                Path.GetFileNameWithoutExtension(archiveFileName));

        public Task<ServerModpackDeploymentResult> DeployAsync(
            ServerModpackDeploymentRequest request,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Server modpack deployment is not configured.");
    }

    private static string RequireTargetDirectory(ResourceProjectInstallationRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.TargetDirectory)
            ? request.TargetDirectory
            : throw new ArgumentException("A target directory is required.", nameof(request));
    }

    private static GameInstance RequireInstance(ResourceProjectInstallationRequest request)
    {
        return request.Instance ?? throw new ArgumentException("An instance is required.", nameof(request));
    }
}
