/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class InstanceBackupService
{
    private static string GetRestoreActiveLockPath(string minecraftDirectory, string transactionId) =>
        CrossProcessVersionLock.GetMutationPath(minecraftDirectory) + ".restore-" + transactionId;

    private static FileStream? TryAcquireRestoreActiveLock(string minecraftDirectory, string transactionId)
    {
        try
        {
            return new FileStream(GetRestoreActiveLockPath(minecraftDirectory, transactionId), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
    }

    private void CleanupInterruptedBackupFiles(string directory)
    {
        const string prefix = ".launcher-backup.";
        const string suffix = ".tmp";
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*" + suffix))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(name[prefix.Length..^suffix.Length], "N", out _))
                continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    continue;
                using var activeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                logger.LogInformation("Removed interrupted instance backup temporary file. BackupDirectory={BackupDirectory}", directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Interrupted backup cleanup skipped an active or inaccessible file. Path={Path}", path);
            }
        }
    }

    private bool TryRemoveLegacyRestoreExtraction(string directory, string versionsDirectory)
    {
        var name = Path.GetFileName(directory);
        if (!name.StartsWith(RestoreDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(name[RestoreDirectoryPrefix.Length..], "N", out var transactionId))
            return false;
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Any(entry => (!string.Equals(Path.GetFileName(entry), "extract", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(Path.GetFileName(entry), "restored", StringComparison.OrdinalIgnoreCase))
                                 || !Directory.Exists(entry)))
            return false;
        DeleteLocallyOwnedRestoreStaging(directory, versionsDirectory, transactionId.ToString("N"));
        logger.LogInformation("Removed interrupted legacy restore extraction. Directory={Directory}", directory);
        return true;
    }
}
