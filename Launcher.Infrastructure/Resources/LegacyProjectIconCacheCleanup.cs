/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.RegularExpressions;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Resources;

public sealed class LegacyProjectIconCacheCleanup(
    LauncherPathProvider paths,
    ILogger<LegacyProjectIconCacheCleanup> logger)
{
    private static readonly Regex ManagedCacheFile = new(
        @"\A(?:[0-9a-f]{64}(?:\.original)?\.png(?:\.[0-9a-f]{32}\.tmp)?|index\.json|\.index\.json\.[0-9a-f]{32}\.tmp|thumbnails-(?:32|64)\.migrated)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TrialThumbnail = new(
        @"\Av1-[0-9a-f]{64}\.png(?:\.[0-9a-f]{32}\.tmp)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public Task CleanupAsync() => Task.Run(() =>
    {
        var cacheRoot = Path.Combine(paths.DefaultDataDirectory, "cache");
        var thumbnails = Path.Combine(cacheRoot, "resources", "thumbnails");
        var versionDirectory = Path.Combine(thumbnails, ResourceThumbnailCacheService.CacheVersion);
        var marker = Path.Combine(versionDirectory, "legacy-cleanup.complete");
        MinecraftPathGuard.EnsureNoReparsePoints(paths.ApplicationBaseDirectory, marker, "Legacy icon cache cleanup marker");
        if (File.Exists(marker))
            return;

        var localComplete = CleanupDirectory(Path.Combine(cacheRoot, "mods", "remote-icons"), removeWhenEmpty: true);
        var onlineComplete = CleanupDirectory(thumbnails, removeWhenEmpty: false, includeTrialThumbnails: true);
        if (localComplete && onlineComplete)
        {
            MinecraftPathGuard.EnsureSafeDirectory(versionDirectory, paths.ApplicationBaseDirectory, "Thumbnail cache");
            using var completed = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        }
    });

    private bool CleanupDirectory(string path, bool removeWhenEmpty, bool includeTrialThumbnails = false)
    {
        var directory = new DirectoryInfo(path);
        var deleted = 0;
        var complete = true;
        try
        {
            MinecraftPathGuard.EnsureNoReparsePoints(paths.ApplicationBaseDirectory, path, "Legacy icon cache");
            foreach (var file in directory.EnumerateFiles())
            {
                var managed = ManagedCacheFile.IsMatch(file.Name)
                    || (includeTrialThumbnails && TrialThumbnail.IsMatch(file.Name));
                if (!managed || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    logger.LogDebug(exception, "Failed to remove a legacy icon cache file. CacheDirectory={CacheDirectory}", path);
                }
            }

            if (removeWhenEmpty && !directory.EnumerateFileSystemInfos().Any())
                directory.Delete();
        }
        catch (DirectoryNotFoundException)
        {
            return complete;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(exception, "Failed to clean up a legacy icon cache. CacheDirectory={CacheDirectory}", path);
            complete = false;
        }

        if (deleted > 0)
            logger.LogInformation("Legacy icon cache cleaned. CacheDirectory={CacheDirectory} DeletedCount={DeletedCount}", path, deleted);
        return complete;
    }
}
