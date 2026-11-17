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
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Infrastructure.FileSystem;

public sealed class InstanceDirectoryMonitor(
    ILogger<InstanceDirectoryMonitor>? logger = null) : IInstanceDirectoryMonitor
{
    public bool IsSameWatchTarget(GameInstance previous, GameInstance current) =>
        ReferenceEquals(previous, current)
        || InstanceDirectoryOwnership.IsSameDirectorySnapshot(previous, current)
        || (!InstanceDirectoryOwnership.IsObserved(previous) && !InstanceDirectoryOwnership.IsObserved(current)
            && string.Equals(previous.InstanceDirectory, current.InstanceDirectory, StringComparison.OrdinalIgnoreCase));
    private readonly ILogger<InstanceDirectoryMonitor> logger = logger ?? NullLogger<InstanceDirectoryMonitor>.Instance;

    public IInstanceDirectoryWatch Watch(GameInstance instance, InstanceDirectoryKind directoryKind)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(instance.InstanceDirectory))
            return EmptyInstanceDirectoryWatch.Instance;

        var instanceDirectory = Path.GetFullPath(instance.InstanceDirectory);
        if (!Directory.Exists(instanceDirectory))
            return EmptyInstanceDirectoryWatch.Instance;

        try
        {
            return new InstanceDirectoryWatch(
                instanceDirectory,
                ResolveDirectoryName(directoryKind),
                directoryKind,
                logger);
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
            || exception is IOException && !Directory.Exists(instanceDirectory))
        {
            logger.LogDebug(
                exception,
                "Instance directory disappeared while starting its watcher. InstanceDirectory={InstanceDirectory} DirectoryKind={DirectoryKind}",
                instance.InstanceDirectory,
                directoryKind);
            return EmptyInstanceDirectoryWatch.Instance;
        }
    }

    private static string ResolveDirectoryName(InstanceDirectoryKind directoryKind) => directoryKind switch
    {
        InstanceDirectoryKind.Mods => "mods",
        InstanceDirectoryKind.Saves => "saves",
        InstanceDirectoryKind.ResourcePacks => "resourcepacks",
        InstanceDirectoryKind.ShaderPacks => "shaderpacks",
        _ => throw new ArgumentOutOfRangeException(nameof(directoryKind), directoryKind, null)
    };

    private sealed class InstanceDirectoryWatch : IInstanceDirectoryWatch
    {
        private readonly object gate = new();
        private readonly FileSystemWatcher rootWatcher;
        private readonly ILogger logger;
        private readonly string targetDirectory;
        private readonly string instanceDirectory;
        private readonly InstanceDirectoryKind directoryKind;
        private readonly List<FileSystemWatcher> targetWatchers = [];
        private bool disposed;

        public InstanceDirectoryWatch(
            string instanceDirectory,
            string targetDirectoryName,
            InstanceDirectoryKind directoryKind,
            ILogger logger)
        {
            this.directoryKind = directoryKind;
            this.instanceDirectory = instanceDirectory;
            this.logger = logger;
            targetDirectory = Path.Combine(instanceDirectory, targetDirectoryName);

            rootWatcher = new FileSystemWatcher(instanceDirectory, targetDirectoryName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };
            rootWatcher.Created += RootWatcher_Changed;
            rootWatcher.Deleted += RootWatcher_Changed;
            rootWatcher.Renamed += RootWatcher_Renamed;
            rootWatcher.Error += Watcher_Error;

            rootWatcher.EnableRaisingEvents = true;
            lock (gate)
                RebuildTargetWatchersLocked();
        }

        public event EventHandler<InstanceDirectoryChangedEventArgs>? Changed;

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;

                disposed = true;
                rootWatcher.EnableRaisingEvents = false;
                rootWatcher.Created -= RootWatcher_Changed;
                rootWatcher.Deleted -= RootWatcher_Changed;
                rootWatcher.Renamed -= RootWatcher_Renamed;
                rootWatcher.Error -= Watcher_Error;
                rootWatcher.Dispose();
                DisposeTargetWatchersLocked();
            }
        }

        private void RootWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            RebuildTargetWatchers();
            RaiseChanged(new InstanceDirectoryChangedEventArgs(e.ChangeType.ToString(), e.FullPath));
        }

        private void RootWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            RebuildTargetWatchers();
            RaiseChanged(new InstanceDirectoryChangedEventArgs("Renamed", e.FullPath, e.OldFullPath));
        }

        private void TargetWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (!IsRelevantTargetChange(e.FullPath))
                return;

            RaiseChanged(new InstanceDirectoryChangedEventArgs(e.ChangeType.ToString(), e.FullPath));
        }

        private void TargetWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            if (!IsRelevantTargetChange(e.FullPath) && !IsRelevantTargetChange(e.OldFullPath))
                return;

            RaiseChanged(new InstanceDirectoryChangedEventArgs("Renamed", e.FullPath, e.OldFullPath));
        }

        private bool IsRelevantTargetChange(string path)
        {
            if (directoryKind is InstanceDirectoryKind.Saves)
            {
                var relative = Path.GetRelativePath(targetDirectory, path);
                if (Path.IsPathRooted(relative)
                    || relative.Equals("..", StringComparison.Ordinal)
                    || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    return false;
                }

                var parent = Path.GetDirectoryName(relative);
                if (string.IsNullOrEmpty(parent))
                    return true;

                return Path.GetFileName(path).Equals("icon.png", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(Path.GetDirectoryName(parent));
            }

            var extension = Path.GetExtension(path);
            return directoryKind is InstanceDirectoryKind.Mods
                ? path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                : extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        }

        private void Watcher_Error(object sender, ErrorEventArgs e)
        {
            logger.LogWarning(
                e.GetException(),
                "Instance directory watcher reported an error. InstanceDirectory={InstanceDirectory} DirectoryKind={DirectoryKind}",
                instanceDirectory,
                directoryKind);
            RaiseChanged(new InstanceDirectoryChangedEventArgs("Error", targetDirectory));
        }

        private void RebuildTargetWatchers()
        {
            lock (gate)
            {
                if (!disposed)
                    RebuildTargetWatchersLocked();
            }
        }

        private void RebuildTargetWatchersLocked()
        {
            DisposeTargetWatchersLocked();
            if (!Directory.Exists(targetDirectory))
                return;

            if (directoryKind is InstanceDirectoryKind.Saves)
            {
                AddTargetWatcher(new FileSystemWatcher(targetDirectory, "*")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.DirectoryName
                });
                AddTargetWatcher(new FileSystemWatcher(targetDirectory, "icon.png")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                });
                return;
            }

            AddTargetWatcher(new FileSystemWatcher(targetDirectory, "*")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            });
        }

        private void AddTargetWatcher(FileSystemWatcher watcher)
        {
            watcher.Created += TargetWatcher_Changed;
            watcher.Changed += TargetWatcher_Changed;
            watcher.Deleted += TargetWatcher_Changed;
            watcher.Renamed += TargetWatcher_Renamed;
            watcher.Error += Watcher_Error;
            targetWatchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }

        private void DisposeTargetWatchersLocked()
        {
            foreach (var watcher in targetWatchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= TargetWatcher_Changed;
                watcher.Changed -= TargetWatcher_Changed;
                watcher.Deleted -= TargetWatcher_Changed;
                watcher.Renamed -= TargetWatcher_Renamed;
                watcher.Error -= Watcher_Error;
                watcher.Dispose();
            }

            targetWatchers.Clear();
        }

        private void RaiseChanged(InstanceDirectoryChangedEventArgs args)
        {
            lock (gate)
            {
                if (disposed)
                    return;
            }

            Changed?.Invoke(this, args);
        }
    }

    private sealed class EmptyInstanceDirectoryWatch : IInstanceDirectoryWatch
    {
        public static EmptyInstanceDirectoryWatch Instance { get; } = new();

        public event EventHandler<InstanceDirectoryChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }
}
