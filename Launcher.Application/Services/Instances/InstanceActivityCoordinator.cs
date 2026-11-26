/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Launcher.Application.Services;

/// <summary>Coordinates launches and cleanup for instances managed by this launcher process.</summary>
public sealed class InstanceActivityCoordinator
{
    private readonly object sync = new();
    private readonly Dictionary<string, int> launches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> cleanups = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable EnterLaunch(string directory)
    {
        var key = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (sync)
        {
            if (cleanups.Contains(key)) throw new InvalidOperationException("Instance cleanup is in progress.");
            launches.TryGetValue(key, out var count);
            launches[key] = count + 1;
        }
        return new Lease(() => { lock (sync) { if (--launches[key] == 0) launches.Remove(key); } });
    }

    public IDisposable EnterCleanup(string directory)
    {
        var key = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        lock (sync)
        {
            if (launches.ContainsKey(key) || !cleanups.Add(key)) throw new InstanceCleanupBusyException();
        }
        return new Lease(() => { lock (sync) cleanups.Remove(key); });
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

public sealed class InstanceCleanupBusyException : Exception;
