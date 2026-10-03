/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

public sealed class ModUpdateFileTransaction : IModUpdateFileTransaction
{
    private readonly LocalFileFingerprintService fingerprintService;
    private readonly IResourceProjectInstallationService installationService;
    private readonly ILogger<ModUpdateFileTransaction> logger;

    public ModUpdateFileTransaction(
        LocalFileFingerprintService fingerprintService,
        IResourceProjectInstallationService installationService,
        ILogger<ModUpdateFileTransaction>? logger = null)
    {
        this.fingerprintService = fingerprintService;
        this.installationService = installationService;
        this.logger = logger ?? NullLogger<ModUpdateFileTransaction>.Instance;
    }

    public async Task<ModUpdateLocalFileState> CaptureAsync(
        LocalMod mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var path = Path.GetFullPath(mod.FullPath);
        var before = ReadFileState(path);
        var fingerprint = await fingerprintService.GetFingerprintAsync(path, cancellationToken).ConfigureAwait(false);
        var after = ReadFileState(path);
        if (before.Length != after.Length || before.LastWriteTimeUtcTicks != after.LastWriteTimeUtcTicks)
            throw new ModUpdateConflictException(path, ModUpdateConflictReason.SourceChanged);

        return new ModUpdateLocalFileState(
            path,
            fingerprint.Sha1,
            fingerprint.CurseForgeFingerprint,
            after.Length,
            after.LastWriteTimeUtcTicks,
            mod.IsEnabled);
    }

    public async Task<ModUpdateResult> ApplyAsync(
        GameInstance instance,
        ModUpdateCandidate candidate,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(candidate);
        progress?.Report(new LauncherProgress(ModUpdateProgressStages.Preparing, string.Empty, 0));

        var modsDirectory = Path.GetFullPath(await installationService
            .EnsureInstanceContentDirectoryAsync(ResourceProjectKind.Mod, instance, cancellationToken)
            .ConfigureAwait(false));
        var sourcePath = ValidateDirectChild(candidate.LocalFile.FullPath, modsDirectory);
        var targetPath = ResolveTargetPath(candidate.TargetVersion.FileName, candidate.LocalFile.IsEnabled, modsDirectory);
        var backupPath = ResolveBackupPath(sourcePath, modsDirectory);
        var sameTarget = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
        if (!sameTarget && File.Exists(targetPath))
            throw new ModUpdateConflictException(targetPath, ModUpdateConflictReason.TargetExists);

        await EnsureSourceUnchangedAsync(candidate.LocalFile, cancellationToken).ConfigureAwait(false);
        var stagingPath = ReserveStagingPath(modsDirectory);
        try
        {
            var request = new ResourceProjectInstallationRequest(
                candidate.TargetVersion,
                ResourceProjectInstallationTargetKind.ExistingInstance,
                Instance: instance,
                DestinationPath: stagingPath);
            var preparation = await installationService.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            request = request with { ExpectedDestinationState = preparation.DestinationState };
            var downloadProgress = progress is null
                ? null
                : DownloadSpeedTaskProgress.Forward(progress, value =>
            {
                double? percent = value.Percent is { } raw ? Math.Clamp(raw, 0, 100) * 0.95 : null;
                progress.Report(value with
                {
                    Stage = ModUpdateProgressStages.Downloading,
                    Message = string.Empty,
                    Percent = percent
                });
            });
            await installationService.ExecuteAsync(request, downloadProgress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureSourceUnchangedAsync(candidate.LocalFile, cancellationToken).ConfigureAwait(false);
            if (!sameTarget && File.Exists(targetPath))
                throw new ModUpdateConflictException(targetPath, ModUpdateConflictReason.TargetExists);

            progress?.Report(new LauncherProgress(ModUpdateProgressStages.Applying, string.Empty, 98));
            Commit(stagingPath, sourcePath, targetPath, backupPath, modsDirectory, sameTarget);
            fingerprintService.Invalidate(sourcePath);
            fingerprintService.Invalidate(targetPath);
            logger.LogInformation(
                "Mod update committed. SourceFileName={SourceFileName} TargetFileName={TargetFileName} BackupFileName={BackupFileName} Provider={Provider} VersionId={VersionId}",
                Path.GetFileName(sourcePath),
                Path.GetFileName(targetPath),
                Path.GetFileName(backupPath),
                candidate.Source,
                candidate.TargetVersion.VersionId);
            return new ModUpdateResult(targetPath, backupPath);
        }
        finally
        {
            TryDelete(stagingPath);
        }
    }

    private async Task EnsureSourceUnchangedAsync(
        ModUpdateLocalFileState expected,
        CancellationToken cancellationToken)
    {
        var before = ReadFileState(expected.FullPath);
        if (before.Length != expected.Length || before.LastWriteTimeUtcTicks != expected.LastWriteTimeUtcTicks)
            throw new ModUpdateConflictException(expected.FullPath, ModUpdateConflictReason.SourceChanged);

        var fingerprint = await fingerprintService
            .GetFreshFingerprintAsync(expected.FullPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(fingerprint.Sha1, expected.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new ModUpdateConflictException(expected.FullPath, ModUpdateConflictReason.SourceChanged);
    }

    private void Commit(
        string stagingPath,
        string sourcePath,
        string targetPath,
        string backupPath,
        string modsDirectory,
        bool sameTarget)
    {
        MinecraftPathGuard.EnsureSafeFileDestination(stagingPath, modsDirectory, "Mod update staging file");
        MinecraftPathGuard.EnsureSafeFileDestination(sourcePath, modsDirectory, "Mod update source file");
        MinecraftPathGuard.EnsureSafeFileDestination(targetPath, modsDirectory, "Mod update target file");
        MinecraftPathGuard.EnsureSafeFileDestination(backupPath, modsDirectory, "Mod update backup file");

        var previousBackupPath = Path.Combine(
            modsDirectory,
            $".{Path.GetFileName(backupPath)}.{Guid.NewGuid():N}.previous");
        var previousBackupMoved = false;
        try
        {
            if (File.Exists(backupPath))
            {
                File.Move(backupPath, previousBackupPath, overwrite: false);
                previousBackupMoved = true;
            }

            if (sameTarget)
            {
                try
                {
                    File.Replace(stagingPath, sourcePath, backupPath, ignoreMetadataErrors: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    RestorePreviousBackup(previousBackupPath, backupPath, previousBackupMoved);
                    throw;
                }
            }
            else
            {
                File.Move(sourcePath, backupPath, overwrite: false);
                try
                {
                    File.Move(stagingPath, targetPath, overwrite: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    File.Move(backupPath, sourcePath, overwrite: false);
                    RestorePreviousBackup(previousBackupPath, backupPath, previousBackupMoved);
                    if (File.Exists(targetPath))
                        throw new ModUpdateConflictException(targetPath, ModUpdateConflictReason.TargetExists, exception);
                    throw;
                }
            }

            if (previousBackupMoved)
                TryDelete(previousBackupPath);
        }
        catch
        {
            if (previousBackupMoved && !File.Exists(backupPath))
                RestorePreviousBackup(previousBackupPath, backupPath, previousBackupMoved);
            throw;
        }
    }

    private static void RestorePreviousBackup(string previousPath, string backupPath, bool moved)
    {
        if (moved && File.Exists(previousPath) && !File.Exists(backupPath))
            File.Move(previousPath, backupPath, overwrite: false);
    }

    private static string ResolveTargetPath(string remoteFileName, bool enabled, string modsDirectory)
    {
        var fileName = Path.GetFileName(remoteFileName);
        if (!string.Equals(fileName, remoteFileName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fileName))
        {
            throw new ModUpdateConflictException(remoteFileName, ModUpdateConflictReason.InvalidPath);
        }

        if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^".disabled".Length];
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ModUpdateConflictException(remoteFileName, ModUpdateConflictReason.InvalidPath);
        if (!enabled)
            fileName += ".disabled";
        return ValidateDirectChild(Path.Combine(modsDirectory, fileName), modsDirectory);
    }

    private static string ReserveStagingPath(string modsDirectory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Combine(modsDirectory, $".blockhelm-mod-update-{Guid.NewGuid():N}.tmp");
            try
            {
                using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new IOException("Unable to reserve a unique mod update staging file.");
    }

    private static string ResolveBackupPath(string sourcePath, string modsDirectory)
    {
        var enabledName = sourcePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? sourcePath[..^".disabled".Length]
            : sourcePath;
        return ValidateDirectChild(enabledName + ".old", modsDirectory);
    }

    private static string ValidateDirectChild(string path, string parent)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase))
                throw new ModUpdateConflictException(fullPath, ModUpdateConflictReason.InvalidPath);
            return MinecraftPathGuard.EnsureSafeFileDestination(fullPath, parent, "Mod update file");
        }
        catch (ModUpdateConflictException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ModUpdateConflictException(path, ModUpdateConflictReason.InvalidPath, exception);
        }
    }

    private static (long Length, long LastWriteTimeUtcTicks) ReadFileState(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                throw new FileNotFoundException("The mod file was not found.", path);
            return (file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModUpdateConflictException(path, ModUpdateConflictReason.SourceChanged, exception);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to remove mod update temporary file. FileName={FileName}", Path.GetFileName(path));
        }
    }
}
