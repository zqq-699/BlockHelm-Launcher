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
using System.Text;
using Launcher.Application;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.File;

namespace Launcher.App.Logging;

internal static class LauncherLogConfiguration
{
    public const int RetainedDays = 30;
    public const int MaxRetainedLauncherLogFiles = 20;
    public const long FileSizeLimitBytes = 20 * 1024 * 1024;
    public const bool RollOnFileSizeLimit = true;
    public const string LogFileNamePrefix = "bhl-";

    private const string LogDirectoryName = "log";
    private static readonly string[] LogFileSearchPatterns = ["bhl*.log", "updater-*.log"];

    public static Serilog.ILogger CreateLogger(
        LoggingLevelSwitch levelSwitch,
        LoggingLevelSwitch microsoftLevelSwitch)
    {
        ArgumentNullException.ThrowIfNull(levelSwitch);
        ArgumentNullException.ThrowIfNull(microsoftLevelSwitch);
        var logDirectory = ResolveLogDirectory();
        Directory.CreateDirectory(logDirectory);
        var startedAt = DateTimeOffset.Now;
        // Reserve one slot before the new startup file is opened so the directory
        // contains at most the configured number of launcher logs afterwards.
        PruneOldLogFiles(logDirectory, startedAt, MaxRetainedLauncherLogFiles - 1);

        var logPath = Path.Combine(
            logDirectory,
            CreateLogFileName(startedAt, Environment.ProcessId));
        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft", microsoftLevelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                new RedactingLogFormatter(),
                logPath,
                rollingInterval: RollingInterval.Infinite,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: RollOnFileSizeLimit,
                shared: false,
                hooks: new LauncherLogFileLifecycleHooks(logDirectory))
            .CreateLogger();
    }

    internal static string CreateLogFileName(DateTimeOffset startedAt, int processId)
    {
        if (processId < 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        return $"{LogFileNamePrefix}{startedAt:yyyyMMdd-HHmmss-fff}-p{processId}.log";
    }

    public static string ResolveLogDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, LauncherApplicationIdentity.StorageDirectoryName, LogDirectoryName);
    }

    public static void PruneOldLogFiles(
        string logDirectory,
        DateTimeOffset now,
        int maxLauncherLogFiles = MaxRetainedLauncherLogFiles)
    {
        if (maxLauncherLogFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLauncherLogFiles));
        if (!Directory.Exists(logDirectory))
            return;

        var cutoff = now.AddDays(-RetainedDays).UtcDateTime;
        foreach (var searchPattern in LogFileSearchPatterns)
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, searchPattern, SearchOption.TopDirectoryOnly))
                DeleteIfExpired(path, cutoff);
        }

        var launcherLogs = Directory
            .EnumerateFiles(logDirectory, "bhl*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(maxLauncherLogFiles)
            .ToArray();
        foreach (var file in launcherLogs)
            TryDeleteFile(file.FullName);
    }

    /// <summary>
    /// Deletes every launcher and updater log file in the directory. The log file the current
    /// process is writing to is held with an exclusive handle, so its delete fails and it is
    /// reported as retained instead of surfacing an error.
    /// </summary>
    public static LauncherLogCleanupResult ClearLogFiles(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        if (!Directory.Exists(logDirectory))
            return LauncherLogCleanupResult.Empty;

        var deletedFileCount = 0;
        var retainedFileCount = 0;
        foreach (var searchPattern in LogFileSearchPatterns)
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (TryDeleteFile(path))
                    deletedFileCount++;
                else
                    retainedFileCount++;
            }
        }

        return new LauncherLogCleanupResult(deletedFileCount, retainedFileCount);
    }

    private static void DeleteIfExpired(string path, DateTime cutoff)
    {
        try
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            if (lastWriteTimeUtc < cutoff)
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class LauncherLogFileLifecycleHooks(string logDirectory) : FileLifecycleHooks
    {
        public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
        {
            // Re-apply the global cap whenever the active startup log rolls by size.
            PruneOldLogFiles(logDirectory, DateTimeOffset.Now);
            return underlyingStream;
        }
    }
}
