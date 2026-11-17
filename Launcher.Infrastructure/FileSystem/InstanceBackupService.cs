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

using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 通过临时归档和暂存目录创建、删除及恢复实例备份，避免失败时破坏现有实例。
/// </summary>
public sealed partial class InstanceBackupService : IInstanceBackupService
{
    private const string ManifestFileName = "launcher-backups.json";
    private const string DirectoryLockFileName = ".launcher-backups.lock";
    private static readonly TimeSpan DirectoryLockRetryDelay = TimeSpan.FromMilliseconds(100);
    private const string RestoreDirectoryPrefix = InstanceDirectoryClassifier.RestoreDirectoryPrefix;
    private const string RestoreMarkerFileName = ".launcher-restore-transaction.json";
    private const string RestoreConflictMarkerFileName = ".launcher-restore-conflict.json";
    private const string RestoreOwnerFileName = ".launcher-restore-owner.json";
    private const string RestoreStatePrepared = "prepared";
    private const string RestoreStateCommitted = "committed";
    private const string RestoreStateExtracting = "extracting";
    private const int MaxRestoreArchiveEntries = 1_000_000;
    private const long RestoreFreeSpaceReserveBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InstanceBackupService> logger;
    private readonly IUserFileDeletionService userFileDeletionService;

    public InstanceBackupService(
        ILogger<InstanceBackupService>? logger = null,
        IUserFileDeletionService? userFileDeletionService = null)
    {
        this.logger = logger ?? NullLogger<InstanceBackupService>.Instance;
        this.userFileDeletionService = userFileDeletionService ?? new UserFileDeletionService();
    }

    public Task<string> EnsureBackupDirectoryAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedDirectory = Path.GetFullPath(backupDirectory);
                Directory.CreateDirectory(normalizedDirectory);
                logger.LogDebug("Instance backup directory ensured. BackupDirectory={BackupDirectory}", normalizedDirectory);
                return normalizedDirectory;
            },
            cancellationToken);
    }

    public Task<int> CountBackupEntriesAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            return Task.FromResult(0);

        return CountBackupEntriesCoreAsync(backupDirectory, cancellationToken);
    }

    public Task<IReadOnlyList<InstanceBackupRecord>> GetBackupsAsync(
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            return Task.FromResult<IReadOnlyList<InstanceBackupRecord>>([]);

        return Task.Run(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var normalizedDirectory = Path.GetFullPath(backupDirectory);
                    if (!Directory.Exists(normalizedDirectory))
                        return (IReadOnlyList<InstanceBackupRecord>)[];

                    var directoryLock = GetDirectoryLock(normalizedDirectory);
                    await directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    List<InstanceBackupRecord> filteredRecords;
                    try
                    {
                        await using var crossProcessLock = await AcquireBackupDirectoryLockAsync(
                                normalizedDirectory,
                                cancellationToken)
                            .ConfigureAwait(false);
                        CleanupInterruptedBackupFiles(normalizedDirectory);
                        var manifestPath = GetManifestPath(normalizedDirectory);
                        var records = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                        filteredRecords = NormalizeAndFilterRecords(normalizedDirectory, records);
                        if (filteredRecords.Count != records.Count)
                            await WriteManifestAsync(manifestPath, filteredRecords, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        directoryLock.Release();
                    }

                    logger.LogDebug(
                        "Instance backups loaded. BackupDirectory={BackupDirectory} Count={BackupCount}",
                        normalizedDirectory,
                        filteredRecords.Count);
                    return (IReadOnlyList<InstanceBackupRecord>)filteredRecords;
                }
                catch (Exception exception) when (exception is IOException
                                                 or UnauthorizedAccessException
                                                 or ArgumentException
                                                 or NotSupportedException
                                                 or JsonException)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to load instance backups. BackupDirectory={BackupDirectory}",
                        backupDirectory);
                    return (IReadOnlyList<InstanceBackupRecord>)[];
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// 在备份目录生成临时 ZIP，原子提交后更新清单；失败时删除所有半成品。
    /// </summary>
    public Task<InstanceBackupRecord> CreateBackupAsync(
        GameInstance instance,
        string backupDirectory,
        string backupName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupName);

        return Task.Run(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedBackupDirectory = Path.GetFullPath(backupDirectory);
                var normalizedInstanceDirectory = Path.GetFullPath(instance.InstanceDirectory);
                var expectedOwnerProof = InstanceDirectoryOwnership.EnsureCurrent(instance);
                var minecraftDirectory = GetMinecraftDirectory(normalizedInstanceDirectory);

                logger.LogInformation("Creating instance backup. OwnerProof={OwnerProof}", instance.InstanceDirectory);
                logger.LogDebug(
                    "Instance backup paths resolved. OwnerProof={OwnerProof} InstanceDirectory={InstanceDirectory} BackupDirectory={BackupDirectory}",
                    instance.InstanceDirectory,
                    normalizedInstanceDirectory,
                    normalizedBackupDirectory);

                if (!Directory.Exists(normalizedInstanceDirectory))
                {
                    throw new InstanceBackupException(
                        InstanceBackupFailureReason.InstanceDirectoryNotFound,
                        "Instance directory does not exist.");
                }

                if (IsSameOrChildPath(normalizedInstanceDirectory, normalizedBackupDirectory))
                {
                    throw new InstanceBackupException(
                        InstanceBackupFailureReason.BackupDirectoryInsideInstance,
                        "Backup directory cannot be inside the instance directory.");
                }

                Directory.CreateDirectory(normalizedBackupDirectory);
                var sanitizedBackupName = NormalizeBackupName(backupName);
                var tempPath = Path.Combine(normalizedBackupDirectory, $".launcher-backup.{Guid.NewGuid():N}.tmp");
                using var activeBackup = new FileStream(tempPath + ".lock", FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                string? finalPath = null;
                var shouldRollbackFinalPath = false;

                try
                {
                    await using (var mutationLock = await CrossProcessVersionLock.AcquireAsync(
                                     CrossProcessVersionLock.GetMutationPath(minecraftDirectory),
                                     progress: null,
                                     cancellationToken).ConfigureAwait(false))
                    {
                        await EnsureCurrentInstanceIdentityAsync(
                                normalizedInstanceDirectory,
                                expectedOwnerProof,
                                cancellationToken)
                            .ConfigureAwait(false);
                        EnsureNoReparsePoints(normalizedInstanceDirectory);
                        CreateInstanceZip(normalizedInstanceDirectory, instance.Name, tempPath, cancellationToken);
                        await EnsureCurrentInstanceIdentityAsync(
                                normalizedInstanceDirectory,
                                expectedOwnerProof,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    var directoryLock = GetDirectoryLock(normalizedBackupDirectory);
                    await directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await using var crossProcessLock = await AcquireBackupDirectoryLockAsync(
                                normalizedBackupDirectory,
                                cancellationToken)
                            .ConfigureAwait(false);
                        var manifestPath = GetManifestPath(normalizedBackupDirectory);
                        var records = NormalizeAndFilterRecords(
                            normalizedBackupDirectory,
                            await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                        var fileName = ResolveUniqueBackupFileName(normalizedBackupDirectory, records, sanitizedBackupName);
                        finalPath = Path.Combine(normalizedBackupDirectory, fileName);

                        File.Move(tempPath, finalPath);
                        shouldRollbackFinalPath = true;

                        var record = new InstanceBackupRecord
                        {
                            Name = backupName.Trim(),
                            FileName = fileName,
                            FullPath = finalPath,
                            SizeBytes = new FileInfo(finalPath).Length,
                            CreatedAt = DateTimeOffset.UtcNow
                        };

                        records.Add(record);
                        await WriteManifestAsync(manifestPath, records, cancellationToken).ConfigureAwait(false);
                        shouldRollbackFinalPath = false;

                        logger.LogInformation("Instance backup created. OwnerProof={OwnerProof}", instance.InstanceDirectory);
                        logger.LogDebug(
                            "Instance backup file created. OwnerProof={OwnerProof} BackupFile={BackupFile}",
                            instance.InstanceDirectory,
                            finalPath);
                        return record;
                    }
                    catch
                    {
                        if (shouldRollbackFinalPath && finalPath is not null)
                            TryDeleteFileIfExists(finalPath);
                        throw;
                    }
                    finally
                    {
                        directoryLock.Release();
                    }
                }
                catch (Exception exception) when (exception is not InstanceBackupException)
                {
                    logger.LogError(
                        exception,
                        "Failed to create instance backup. OwnerProof={OwnerProof} BackupDirectory={BackupDirectory}",
                        instance.InstanceDirectory,
                        normalizedBackupDirectory);
                    throw;
                }
                finally
                {
                    TryDeleteFileIfExists(tempPath);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// 仅允许删除备份根目录内的文件，并同步移除清单记录。
    /// </summary>
    public Task DeleteBackupAsync(
        string backupDirectory,
        string backupFullPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFullPath);

        return Task.Run(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedBackupDirectory = Path.GetFullPath(backupDirectory);
                var normalizedBackupFullPath = Path.GetFullPath(backupFullPath);
                if (!IsSameOrChildPath(normalizedBackupDirectory, normalizedBackupFullPath))
                    throw new ArgumentException("Backup file must be inside the backup directory.", nameof(backupFullPath));

                logger.LogInformation("Deleting instance backup.");
                logger.LogDebug(
                    "Instance backup deletion paths resolved. BackupDirectory={BackupDirectory} BackupFile={BackupFile}",
                    normalizedBackupDirectory,
                    normalizedBackupFullPath);

                try
                {
                    var directoryLock = GetDirectoryLock(normalizedBackupDirectory);
                    await directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await using var crossProcessLock = await AcquireBackupDirectoryLockAsync(
                                normalizedBackupDirectory,
                                cancellationToken)
                            .ConfigureAwait(false);
                        var manifestPath = GetManifestPath(normalizedBackupDirectory);
                        if (File.Exists(normalizedBackupFullPath))
                            userFileDeletionService.DeleteFile(normalizedBackupFullPath);

                        var records = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                        var remainingRecords = NormalizeAndFilterRecords(normalizedBackupDirectory, records)
                            .Where(record => !string.Equals(record.FullPath, normalizedBackupFullPath, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        await WriteManifestAsync(manifestPath, remainingRecords, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        directoryLock.Release();
                    }

                    logger.LogInformation("Instance backup deleted.");
                }
                catch (Exception exception) when (exception is IOException
                                                 or UnauthorizedAccessException
                                                 or ArgumentException
                                                 or NotSupportedException
                                                 or JsonException)
                {
                    logger.LogError(
                        exception,
                        "Failed to delete instance backup. BackupDirectory={BackupDirectory} BackupFile={BackupFile}",
                        normalizedBackupDirectory,
                        normalizedBackupFullPath);
                    throw;
                }
            },
            cancellationToken);
    }
}
