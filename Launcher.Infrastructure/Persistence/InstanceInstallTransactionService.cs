/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Persistence;

public sealed class InstanceInstallTransactionService : IInstanceInstallTransactionService
{
    private const int MaxPendingNameAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly GameInstanceSettingsStore settingsStore;
    private readonly VersionDirectoryManager directoryManager;
    private readonly ILogger logger;
    private readonly Func<Guid> guidFactory;

    public InstanceInstallTransactionService(
        ILogger<InstanceInstallTransactionService>? logger = null)
        : this(logger, null)
    {
    }

    internal InstanceInstallTransactionService(
        ILogger? logger,
        Func<Guid>? guidFactory)
    {
        this.logger = logger ?? NullLogger.Instance;
        this.guidFactory = guidFactory ?? Guid.NewGuid;
        settingsStore = new GameInstanceSettingsStore(this.logger);
        directoryManager = new VersionDirectoryManager(this.logger);
    }

    public async Task<IInstanceInstallTransaction> BeginAsync(
        string minecraftDirectory,
        string logicalVersionName,
        string installKind,
        bool initializeDefaultIfEmpty,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var finalDirectory = directoryManager.GetVersionDirectory(minecraftDirectory, logicalVersionName);
        var versionsDirectory = Path.GetDirectoryName(finalDirectory)!;
        Directory.CreateDirectory(versionsDirectory);
        await using var coordinationLock = await CrossProcessVersionLock.AcquireAsync(
            CrossProcessVersionLock.GetInstallCoordinationPath(minecraftDirectory),
            progress,
            cancellationToken).ConfigureAwait(false);
        await using var mutationLock = await CrossProcessVersionLock.AcquireAsync(
            CrossProcessVersionLock.GetMutationPath(minecraftDirectory),
            progress: null,
            cancellationToken).ConfigureAwait(false);
        if (InstanceInstallNameOccupancy.IsOccupied(
                versionsDirectory,
                finalDirectory,
                logicalVersionName))
        {
            throw new InstanceInstallNameConflictException(logicalVersionName);
        }

        for (var attempt = 1; attempt <= MaxPendingNameAttempts; attempt++)
        {
            var transactionId = guidFactory().ToString("N");
            var pendingDirectory = Path.Combine(
                versionsDirectory,
                $"{PendingInstanceInstallDirectory.Prefix}{logicalVersionName}-{transactionId[..8].ToLowerInvariant()}");
            var preparationRoot = PendingInstanceInstallDirectory.GetPreparationRoot(minecraftDirectory);
            var preparationDirectory = Path.Combine(preparationRoot, transactionId);
            if (Directory.Exists(pendingDirectory)
                || File.Exists(pendingDirectory)
                || Directory.Exists(preparationDirectory)
                || File.Exists(preparationDirectory))
                continue;

            FileStream? pendingLock = null;
            var preparationRootOwned = false;
            var preparationDirectoryOwned = false;
            string? preparationProof = null;
            try
            {
                // Build the recoverable metadata in a launcher-owned preparation area first.
                // Publishing the directory into the pending namespace is then a same-volume atomic move,
                // so a crash can never expose a markerless pending directory created by this service.
                EnsureOrdinaryPathBelowRoot(minecraftDirectory, preparationRoot, "Install preparation root");
                Directory.CreateDirectory(preparationRoot);
                EnsureOrdinaryPathBelowRoot(minecraftDirectory, preparationRoot, "Install preparation root");
                preparationRootOwned = true;
                Directory.CreateDirectory(preparationDirectory);
                EnsureOrdinaryPathBelowRoot(minecraftDirectory, preparationRoot, "Install preparation root");
                EnsureOrdinaryDirectory(preparationDirectory, "Install preparation directory");
                preparationDirectoryOwned = true;
                preparationProof = InstanceDirectoryOwnership.Capture(preparationDirectory);
                var marker = new PendingInstanceInstallMarker(
                    2,
                    transactionId,
                    preparationProof,
                    logicalVersionName,
                    installKind,
                    initializeDefaultIfEmpty,
                    DateTimeOffset.UtcNow);

                await AtomicJsonFileWriter.WriteAsync(
                    Path.Combine(preparationDirectory, PendingInstanceInstallDirectory.MarkerFileName),
                    marker,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOrdinaryPathBelowRoot(minecraftDirectory, preparationRoot, "Install preparation root");
                EnsureOrdinaryDirectory(preparationDirectory, "Install preparation directory");
                Directory.Move(preparationDirectory, pendingDirectory);
                preparationDirectoryOwned = false;
                TryDeleteEmptyDirectory(preparationRoot);
                pendingLock = new FileStream(
                    Path.Combine(pendingDirectory, PendingInstanceInstallDirectory.PendingLockFileName),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete);
                logger.LogDebug(
                    "Instance installation staged. OwnerProof={OwnerProof} LogicalVersionName={LogicalVersionName} PendingDirectory={PendingDirectory}",
                    finalDirectory,
                    logicalVersionName,
                    pendingDirectory);
                return new Transaction(
                    minecraftDirectory,
                    logicalVersionName,
                    transactionId,
                    marker.OwnerProof,
                    pendingDirectory,
                    finalDirectory,
                    pendingLock,
                    settingsStore,
                    logger);
            }
            catch
            {
                if (pendingLock is not null)
                    await pendingLock.DisposeAsync().ConfigureAwait(false);
                if (preparationDirectoryOwned && InstanceDirectoryOwnership.Matches(preparationDirectory, preparationProof))
                    TryDeleteTree(preparationDirectory, logger);
                if (preparationRootOwned)
                    TryDeleteEmptyDirectory(preparationRoot);
                if (PendingInstanceInstallDirectory.TryReadValidPendingMarker(pendingDirectory, out var pendingMarker)
                    && string.Equals(pendingMarker.TransactionId, transactionId, StringComparison.Ordinal)
                    && InstanceDirectoryOwnership.Matches(pendingDirectory, pendingMarker.OwnerProof))
                {
                    TryDeleteTree(pendingDirectory, logger);
                }
                throw;
            }
        }

        throw new IOException($"Unable to allocate an install staging directory after {MaxPendingNameAttempts} attempts.");
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void EnsureOrdinaryDirectory(string directory, string description)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"{description} is missing: {directory}");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"{description} must not be a reparse point: {directory}");
    }

    internal static void EnsureOrdinaryPathBelowRoot(
        string rootDirectory,
        string candidateDirectory,
        string description)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateDirectory));
        var comparisonRoot = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(comparisonRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{description} resolved outside its root: {candidateDirectory}");

        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        var current = normalizedRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"{description} contains a reparse point: {current}");
        }
    }

    private sealed class Transaction : IInstanceInstallTransaction
    {
        private FileStream? pendingLock;
        private readonly GameInstanceSettingsStore settingsStore;
        private readonly ILogger logger;
        private readonly string transactionId;
        private readonly string ownerProof;
        private bool aborted;

        public Transaction(
            string minecraftDirectory,
            string logicalVersionName,
            string transactionId,
            string ownerProof,
            string pendingDirectory,
            string finalDirectory,
            FileStream pendingLock,
            GameInstanceSettingsStore settingsStore,
            ILogger logger)
        {
            MinecraftDirectory = minecraftDirectory;
            LogicalVersionName = logicalVersionName;
            this.transactionId = transactionId;
            this.ownerProof = ownerProof;
            PendingDirectory = pendingDirectory;
            FinalDirectory = finalDirectory;
            this.pendingLock = pendingLock;
            this.settingsStore = settingsStore;
            this.logger = logger;
        }

        public string MinecraftDirectory { get; }
        public string LogicalVersionName { get; }
        public string PendingDirectory { get; }
        public string FinalDirectory { get; }
        public bool IsCommitted { get; private set; }

        public async Task CommitAsync(GameInstance instance, CancellationToken cancellationToken = default)
        {
            if (IsCommitted)
                return;
            if (aborted)
                throw new InvalidOperationException("The installation transaction was already aborted.");
            if (!MinecraftDirectoryPath.Equals(instance.InstanceDirectory, FinalDirectory) && !MinecraftDirectoryPath.Equals(instance.InstanceDirectory, PendingDirectory))
                throw new GameInstanceMutationConflictException(ownerProof, LogicalVersionName);

            await ValidateAsync(cancellationToken).ConfigureAwait(false);
            await settingsStore.PrepareInstallAsync(
                PendingDirectory,
                FinalDirectory,
                LogicalVersionName,
                instance,
                ownerProof,
                cancellationToken).ConfigureAwait(false);

            var versionsDirectory = Path.GetDirectoryName(FinalDirectory)!;
            await using var coordinationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetInstallCoordinationPath(MinecraftDirectory),
                progress: null,
                cancellationToken).ConfigureAwait(false);
            await using var mutationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetMutationPath(MinecraftDirectory),
                progress: null,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(FinalDirectory) || File.Exists(FinalDirectory))
                throw new InstanceInstallNameConflictException(LogicalVersionName);

            await ReleasePendingLockAsync().ConfigureAwait(false);
            try
            {
                WindowsDirectoryHandleMover.MoveOwnedDirectory(
                    PendingDirectory,
                    FinalDirectory,
                    () => PendingInstanceInstallDirectory.TryReadValidPendingMarker(PendingDirectory, out var marker)
                          && string.Equals(marker.TransactionId, transactionId, StringComparison.Ordinal)
                          && string.Equals(marker.OwnerProof, ownerProof, StringComparison.Ordinal)
                          && GameInstanceSettingsStore.HasIdentity(PendingDirectory, ownerProof));
            }
            catch (InvalidOperationException)
            {
                throw new GameInstanceMutationConflictException(ownerProof, LogicalVersionName);
            }
            IsCommitted = true;
            instance.InstanceDirectory = FinalDirectory;
            InstanceDirectoryOwnership.Observe(instance);
            logger.LogDebug(
                "Instance installation committed. OwnerProof={OwnerProof} LogicalVersionName={LogicalVersionName} FinalDirectory={FinalDirectory}",
                instance.InstanceDirectory,
                LogicalVersionName,
                FinalDirectory);
        }

        public Task CompleteLogicalCommitAsync(CancellationToken cancellationToken = default)
        {
            if (!IsCommitted)
                throw new InvalidOperationException("The installation directory has not been committed.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!InstanceDirectoryOwnership.Matches(FinalDirectory, ownerProof))
                    throw new GameInstanceMutationConflictException(FinalDirectory, LogicalVersionName);
                File.Delete(Path.Combine(FinalDirectory, PendingInstanceInstallDirectory.PendingLockFileName));
                File.Delete(Path.Combine(FinalDirectory, PendingInstanceInstallDirectory.MarkerFileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Committed instance install marker remains for startup cleanup. FinalDirectory={FinalDirectory}", FinalDirectory);
            }
            return Task.CompletedTask;
        }

        public async Task AbortAsync(CancellationToken cancellationToken = default)
        {
            if (IsCommitted || aborted)
                return;
            var versionsDirectory = Path.GetDirectoryName(FinalDirectory)!;
            await using var coordinationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetInstallCoordinationPath(MinecraftDirectory),
                progress: null,
                cancellationToken).ConfigureAwait(false);
            await using var mutationLock = await CrossProcessVersionLock.AcquireAsync(
                CrossProcessVersionLock.GetMutationPath(MinecraftDirectory),
                progress: null,
                cancellationToken).ConfigureAwait(false);
            await ReleasePendingLockAsync().ConfigureAwait(false);
            if (InstanceDirectoryOwnership.Matches(PendingDirectory, ownerProof))
                TryDeleteTree(PendingDirectory, logger);
            else
                logger.LogWarning("Install staging was preserved because operation ownership changed. Directory={Directory}", PendingDirectory);
            aborted = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!IsCommitted && !aborted)
            {
                try
                {
                    await AbortAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to abort an incomplete instance installation. PendingDirectory={PendingDirectory}", PendingDirectory);
                }
            }
            await ReleasePendingLockAsync().ConfigureAwait(false);
        }

        private async Task ReleasePendingLockAsync()
        {
            if (pendingLock is null)
                return;
            await pendingLock.DisposeAsync().ConfigureAwait(false);
            pendingLock = null;
        }

        private async Task ValidateAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(PendingDirectory))
                throw new DirectoryNotFoundException($"Install staging directory is missing: {PendingDirectory}");
            if ((File.GetAttributes(PendingDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Install staging directory must not be a reparse point.");
            EnsureNoReparsePoints(PendingDirectory);

            var jsonPath = Path.Combine(PendingDirectory, $"{LogicalVersionName}.json");
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("Installed version JSON is missing.", jsonPath);
            await using var stream = File.OpenRead(jsonPath);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("id", out var id)
                || !string.Equals(id.GetString(), LogicalVersionName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Installed version JSON id does not match the logical version name.");
            }
        }

        private static void EnsureNoReparsePoints(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Install staging content must not contain reparse points: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    EnsureNoReparsePoints(entry);
            }
        }
    }

    internal static void TryDeleteTree(string directory, ILogger logger)
    {
        try
        {
            DeleteTree(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to delete install staging directory. Directory={Directory}", directory);
        }
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        foreach (var child in Directory.EnumerateDirectories(path))
            DeleteTree(child);
        foreach (var file in Directory.EnumerateFiles(path))
            File.Delete(file);
        Directory.Delete(path, recursive: false);
    }
}
