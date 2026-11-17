/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Infrastructure.Persistence;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Replays a loader provider in a same-volume sandbox and publishes only the
/// client jar and artifacts named by the loader manifest. Existing instance
/// metadata remains authoritative and is never replaced here.
/// </summary>
internal sealed class LoaderArtifactRepairCoordinator
{
    private readonly IReadOnlyDictionary<LoaderKind, ILoaderProvider> providers;
    private readonly IGameInstallCoordinator installCoordinator;
    private readonly ILogger logger;

    public LoaderArtifactRepairCoordinator(
        IEnumerable<ILoaderProvider> providers,
        IGameInstallCoordinator installCoordinator,
        ILogger? logger = null)
    {
        this.providers = providers
            .Where(provider => provider.IsImplemented)
            .GroupBy(provider => provider.Kind)
            .ToDictionary(group => group.Key, group => group.First());
        this.installCoordinator = installCoordinator;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<bool> RequiresRepairAsync(
        string minecraftDirectory,
        string versionName,
        string versionDirectory,
        GameFileLoaderIdentity identity,
        CancellationToken cancellationToken)
    {
        var versionJsonPath = (InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) ?? Path.Combine(versionDirectory, $"{versionName}.json"));
        MinecraftPathGuard.EnsureSafeFileDestination(
            versionJsonPath,
            versionDirectory,
            "Managed version metadata");
        if (!await IsVersionJsonValidAsync(versionDirectory, versionName, cancellationToken).ConfigureAwait(false))
            return true;

        if (identity.LoaderKind is not (LoaderKind.Forge or LoaderKind.NeoForge))
            return false;

        var manifestPath = LoaderArtifactManifestStore.GetPath(versionDirectory);
        MinecraftPathGuard.EnsureSafeFileDestination(
            manifestPath,
            versionDirectory,
            "Managed loader manifest");
        var readResult = await LoaderArtifactManifestStore.ReadAsync(versionDirectory, identity, cancellationToken)
            .ConfigureAwait(false);
        if (!readResult.IsValid)
            return true;

        foreach (var artifact in readResult.Manifest!.Artifacts)
        {
            var path = LoaderArtifactManifestStore.ResolveManagedPath(minecraftDirectory, artifact.RelativePath);
            MinecraftPathGuard.EnsureSafeFileDestination(path, minecraftDirectory, "Managed loader artifact");
            var usesRecordedHashes = LoaderArtifactVerificationPolicy.UsesRecordedHashes(artifact);
            var status = await MinecraftFileIntegrity.EvaluateAsync(
                    path,
                    usesRecordedHashes ? artifact.Sha1 : null,
                    artifact.Size,
                    usesRecordedHashes
                        ? MinecraftFileVerification.Full
                        : MinecraftFileVerification.SizeOnly,
                    cancellationToken)
                .ConfigureAwait(false);
            if (status != MinecraftFileIntegrityStatus.Valid)
                return true;
        }

        return false;
    }

    public async Task RepairAsync(
        string minecraftDirectory,
        string versionName,
        string versionDirectory,
        GameFileLoaderIdentity identity,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken)
    {
        if (!providers.TryGetValue(identity.LoaderKind, out var provider))
            throw new InstanceRepairException($"No repair provider is registered for loader {identity.LoaderKind}.");
        if (string.IsNullOrWhiteSpace(identity.MinecraftVersion))
            throw new InstanceRepairException("Minecraft version is missing from loader identity.");
        if (identity.LoaderKind is LoaderKind.Forge or LoaderKind.NeoForge
            && string.IsNullOrWhiteSpace(identity.LoaderVersion))
            throw new InstanceRepairException("Loader version is missing from Forge-like loader identity.");

        await using var installLease = await installCoordinator
            .AcquireInstallAsync(minecraftDirectory, versionName, progress, cancellationToken)
            .ConfigureAwait(false);

        var workRoot = Path.Combine(
            Path.GetFullPath(minecraftDirectory),
            LauncherApplicationIdentity.StorageDirectoryName,
            "repair-work",
            Guid.NewGuid().ToString("N"));
        var sandboxMinecraftDirectory = Path.Combine(workRoot, ".minecraft");
        var cleanupWorkRoot = true;
        Directory.CreateDirectory(sandboxMinecraftDirectory);
        try
        {
            logger.LogInformation(
                "Loader sandbox repair started. VersionName={VersionName} Loader={Loader} MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion}",
                versionName,
                identity.LoaderKind,
                identity.MinecraftVersion,
                identity.LoaderVersion);
            var sandboxVersionName = await provider.InstallAsync(
                    identity.MinecraftVersion,
                    sandboxMinecraftDirectory,
                    versionName,
                    identity.LoaderVersion,
                    progress,
                    downloadSourcePreference,
                    cancellationToken,
                    downloadSpeedLimitMbPerSecond)
                .ConfigureAwait(false);
            if (!string.Equals(sandboxVersionName, versionName, StringComparison.Ordinal))
            {
                throw new InstanceRepairException(
                    $"Loader repair provider returned an unexpected version name: {sandboxVersionName}.");
            }
            var sandboxVersionDirectory = Path.Combine(
                sandboxMinecraftDirectory,
                "versions",
                sandboxVersionName);
            if (!await IsVersionJsonValidAsync(sandboxVersionDirectory, sandboxVersionName, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InstanceRepairException("Loader sandbox did not produce valid version metadata.");
            }

            LoaderArtifactManifest? loaderManifest = null;
            if (identity.LoaderKind is LoaderKind.Forge or LoaderKind.NeoForge)
            {
                var manifestResult = await LoaderArtifactManifestStore.ReadAsync(
                        sandboxVersionDirectory,
                        identity,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!manifestResult.IsValid)
                {
                    throw new InstanceRepairException(
                        $"Loader sandbox did not produce a valid artifact manifest: {manifestResult.Error}");
                }
                loaderManifest = manifestResult.Manifest;
            }

            (progress as ILoaderRepairPublicationProgress)?.ReportLoaderPublication(completed: false);
            await PublishAsync(
                    minecraftDirectory,
                    versionName,
                    versionDirectory,
                    sandboxMinecraftDirectory,
                    sandboxVersionName,
                    sandboxVersionDirectory,
                    loaderManifest,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            (progress as ILoaderRepairPublicationProgress)?.ReportLoaderPublication(completed: true);
            logger.LogInformation(
                "Loader sandbox repair completed. VersionName={VersionName} Loader={Loader} ArtifactCount={ArtifactCount}",
                versionName,
                identity.LoaderKind,
                loaderManifest?.Artifacts.Count ?? 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LoaderArtifactPublicationRollbackException exception)
        {
            cleanupWorkRoot = false;
            logger.LogError(
                exception,
                "Loader artifact publication rollback was incomplete. RecoveryWorkspace={RecoveryWorkspace}",
                workRoot);
            throw new InstanceRepairException(
                $"Loader artifact publication rollback was incomplete. Recovery workspace: {workRoot}",
                exception);
        }
        catch (InstanceRepairException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InstanceRepairException(
                $"{identity.LoaderKind} {identity.LoaderVersion} sandbox repair failed.",
                exception);
        }
        finally
        {
            if (cleanupWorkRoot)
                LoaderVersionDirectoryTransaction.TryDeleteDirectory(workRoot, logger);
        }
    }

    private async Task PublishAsync(
        string minecraftDirectory,
        string versionName,
        string versionDirectory,
        string sandboxMinecraftDirectory,
        string sandboxVersionName,
        string sandboxVersionDirectory,
        LoaderArtifactManifest? loaderManifest,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var coordinationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetInstallCoordinationPath(minecraftDirectory),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        await using var mutationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetMutationPath(minecraftDirectory),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        var versionRoot = Path.GetDirectoryName(Path.GetFullPath(versionDirectory))
            ?? throw SafeExceptionLog.Describe(new InvalidDataException("Managed version directory has no parent."), "DownloadValidation", ("Reason", "Managed version directory has no parent."));
        MinecraftPathGuard.EnsureSafeDirectory(versionDirectory, versionRoot, "Managed version directory");
        var rollback = new LoaderPublicationRollback(
            Path.Combine(sandboxMinecraftDirectory, ".publish-rollback"), logger);
        try
        {
            var targetJson = (InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) ?? Path.Combine(versionDirectory, $"{versionName}.json"));
            MinecraftPathGuard.EnsureSafeFileDestination(targetJson, versionDirectory, "Managed version metadata");
            if (!await IsJsonFileValidAsync(targetJson, cancellationToken).ConfigureAwait(false))
            {
                throw new InstanceRepairException(
                    $"Version metadata is missing or invalid for {versionName}; automatic loader repair will not replace it.");
            }

            var sourceJar = Path.Combine(sandboxVersionDirectory, $"{sandboxVersionName}.jar");
            var targetJar = InstanceVersionFiles.ResolveJarPath(versionDirectory, versionName);
            MinecraftPathGuard.EnsureSafeFileDestination(targetJar, versionRoot, "Managed version client");
            if (File.Exists(sourceJar))
            {
                var sourceSha1 = AtomicSharedFilePublisher.ComputeSha1(sourceJar);
                var sourceSize = new FileInfo(sourceJar).Length;
                var targetStatus = await MinecraftFileIntegrity.EvaluateAsync(
                        targetJar,
                        sourceSha1,
                        sourceSize,
                        MinecraftFileVerification.Full,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (targetStatus != MinecraftFileIntegrityStatus.Valid)
                {
                    await PublishTrustedReplacementAsync(
                            sourceJar,
                            targetJar,
                            rollback,
                            cancellationToken,
                            versionRoot)
                        .ConfigureAwait(false);
                }
            }

            if (loaderManifest is null)
            {
                rollback.Commit();
                return;
            }

            foreach (var artifact in loaderManifest.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = LoaderArtifactManifestStore.ResolveManagedPath(
                    sandboxMinecraftDirectory,
                    artifact.RelativePath);
                var destination = LoaderArtifactManifestStore.ResolveManagedPath(
                    minecraftDirectory,
                    artifact.RelativePath);
                MinecraftPathGuard.EnsureSafeFileDestination(
                    destination,
                    minecraftDirectory,
                    "Managed loader artifact");
                var sourceStatus = await MinecraftFileIntegrity.InspectAsync(
                        source,
                        artifact.Sha1,
                        artifact.Size,
                        MinecraftFileVerification.Full,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (sourceStatus.Status != MinecraftFileIntegrityStatus.Valid)
                    throw sourceStatus.CreateException(source, "RepairSandboxArtifact");

                var destinationStatus = await MinecraftFileIntegrity.EvaluateAsync(
                        destination,
                        artifact.Sha1,
                        artifact.Size,
                        MinecraftFileVerification.Full,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (destinationStatus == MinecraftFileIntegrityStatus.Valid)
                    continue;
                await PublishTrustedReplacementAsync(
                        source,
                        destination,
                        rollback,
                        cancellationToken,
                        minecraftDirectory)
                    .ConfigureAwait(false);
            }

            var sourceManifest = LoaderArtifactManifestStore.GetPath(sandboxVersionDirectory);
            var targetManifest = LoaderArtifactManifestStore.GetPath(versionDirectory);
            await PublishTrustedReplacementAsync(sourceManifest, targetManifest, rollback, cancellationToken, versionDirectory)
                .ConfigureAwait(false);
            rollback.Commit();
        }
        catch (Exception publicationException)
        {
            try
            {
                rollback.Rollback();
            }
            catch (Exception rollbackException)
            {
                throw new LoaderArtifactPublicationRollbackException(
                    "Loader artifact publication failed and rollback was incomplete.",
                    publicationException,
                    rollbackException);
            }
            throw;
        }
        finally
        {
            rollback.Cleanup();
        }
    }

    private async Task PublishTrustedReplacementAsync(
        string source,
        string destination,
        LoaderPublicationRollback rollback,
        CancellationToken cancellationToken,
        string managedRoot)
    {
        var sha1 = AtomicSharedFilePublisher.ComputeSha1(source);
        rollback.Prepare(destination, managedRoot);
        await AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
                source,
                destination,
                sha1,
                cancellationToken,
                managedRoot, logger)
            .ConfigureAwait(false);
    }

    internal sealed class LoaderPublicationRollback(string backupDirectory, ILogger? logger = null)
    {
        private readonly Dictionary<string, PublicationSnapshot> snapshots = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private bool committed;
        private bool rolledBack;

        public void Prepare(string destinationPath, string? managedRoot = null)
        {
            var destination = Path.GetFullPath(destinationPath);
            if (!string.IsNullOrWhiteSpace(managedRoot))
                MinecraftPathGuard.EnsureSafeFileDestination(destination, managedRoot, "Managed publication destination");
            if (snapshots.ContainsKey(destination))
                return;
            if (!File.Exists(destination))
            {
                snapshots[destination] = new PublicationSnapshot(destination, BackupPath: null, managedRoot);
                return;
            }
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Managed publication destination is a reparse point: {destination}");
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, $"{snapshots.Count:D4}-{Guid.NewGuid():N}.bak");
            File.Copy(destination, backup, overwrite: false);
            snapshots[destination] = new PublicationSnapshot(destination, backup, managedRoot);
        }

        public void Commit() => committed = true;

        public void Rollback()
        {
            if (committed)
                return;
            List<Exception>? failures = null;
            foreach (var snapshot in snapshots.Values.Reverse())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(snapshot.ManagedRoot))
                        MinecraftPathGuard.EnsureSafeFileDestination(
                            snapshot.DestinationPath,
                            snapshot.ManagedRoot,
                            "Managed publication rollback destination");
                    if (snapshot.BackupPath is null)
                    {
                        File.Delete(snapshot.DestinationPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot.DestinationPath)!);
                    File.Copy(snapshot.BackupPath, snapshot.DestinationPath, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    (failures ??= []).Add(exception);
                }
            }
            if (failures is not null)
                throw new AggregateException("Managed publication rollback failed.", failures);
            rolledBack = true;
        }

        public void Cleanup()
        {
            if (!committed && !rolledBack)
                return;
            LoaderVersionDirectoryTransaction.TryDeleteDirectory(backupDirectory, logger);
        }

        private sealed record PublicationSnapshot(
            string DestinationPath,
            string? BackupPath,
            string? ManagedRoot);
    }

    private static Task<bool> IsVersionJsonValidAsync(
        string versionDirectory,
        string versionName,
        CancellationToken cancellationToken) =>
        IsJsonFileValidAsync((InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) ?? Path.Combine(versionDirectory, $"{versionName}.json")), cancellationToken);

    private static async Task<bool> IsJsonFileValidAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

internal sealed class LoaderArtifactPublicationRollbackException(
    string message,
    Exception publicationException,
    Exception rollbackException)
    : AggregateException(message, publicationException, rollbackException);
