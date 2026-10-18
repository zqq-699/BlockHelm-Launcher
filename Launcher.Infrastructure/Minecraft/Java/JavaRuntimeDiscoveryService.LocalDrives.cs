/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class JavaRuntimeDiscoveryService
{
    private static readonly EnumerationOptions LocalDriveDirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private static IReadOnlyList<string> GetLocalDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                    roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return roots;
    }

    internal static IReadOnlyList<JavaRuntimeCandidate> CollectLocalDriveCandidates(
        IEnumerable<string> driveRoots,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<JavaRuntimeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(driveRoots);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory, "*", LocalDriveDirectoryOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFileName(child), "bin", StringComparison.OrdinalIgnoreCase))
                    {
                        AddCandidate(
                            candidates,
                            seen,
                            Path.Combine(child, "java.exe"),
                            "LocalDrive",
                            File.Exists,
                            ResolveJavaExecutableIdentityPath);
                    }

                    pending.Push(child);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return candidates;
    }
}
