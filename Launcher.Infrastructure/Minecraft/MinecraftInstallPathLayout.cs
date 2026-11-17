/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using CmlLib.Core;

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class MinecraftInstallPathLayout
{
    private MinecraftInstallPathLayout(string workspaceMinecraftDirectory, string sharedMinecraftDirectory)
    {
        WorkspaceMinecraftDirectory = Normalize(workspaceMinecraftDirectory);
        SharedMinecraftDirectory = Normalize(sharedMinecraftDirectory);
        Path = new MinecraftPath(WorkspaceMinecraftDirectory)
        {
            Versions = Normalize(System.IO.Path.Combine(WorkspaceMinecraftDirectory, "versions")),
            Library = Normalize(System.IO.Path.Combine(SharedMinecraftDirectory, "libraries")),
            Assets = Normalize(System.IO.Path.Combine(SharedMinecraftDirectory, "assets")),
            Resource = Normalize(System.IO.Path.Combine(SharedMinecraftDirectory, "resources")),
            Runtime = Normalize(System.IO.Path.Combine(SharedMinecraftDirectory, "runtime"))
        };

        Validate();
    }

    public string WorkspaceMinecraftDirectory { get; }

    public string SharedMinecraftDirectory { get; }

    public MinecraftPath Path { get; }

    public static MinecraftInstallPathLayout Create(
        string workspaceMinecraftDirectory,
        string sharedMinecraftDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceMinecraftDirectory))
            throw new ArgumentException("The install workspace directory is required.", nameof(workspaceMinecraftDirectory));
        if (string.IsNullOrWhiteSpace(sharedMinecraftDirectory))
            throw new ArgumentException("The shared Minecraft directory is required.", nameof(sharedMinecraftDirectory));

        return new MinecraftInstallPathLayout(workspaceMinecraftDirectory, sharedMinecraftDirectory);
    }

    private void Validate()
    {
        var realVersionsDirectory = Normalize(System.IO.Path.Combine(SharedMinecraftDirectory, "versions"));
        if (!IsSameOrDescendant(Path.Versions, WorkspaceMinecraftDirectory))
            throw SafeExceptionLog.Describe(new InvalidOperationException("The private versions directory escaped the install workspace."), "DownloadValidation", ("Reason", "The private versions directory escaped the install workspace."));
        if (IsSameOrDescendant(Path.Versions, realVersionsDirectory)
            || IsSameOrDescendant(realVersionsDirectory, Path.Versions))
        {
            throw SafeExceptionLog.Describe(new InvalidOperationException("The private and real versions directories overlap."), "DownloadValidation", ("Reason", "The private and real versions directories overlap."));
        }

        foreach (var sharedPath in new[] { Path.Library, Path.Assets, Path.Resource, Path.Runtime })
        {
            if (!IsSameOrDescendant(sharedPath, SharedMinecraftDirectory))
                throw SafeExceptionLog.Describe(new InvalidOperationException("A shared runtime path escaped the Minecraft directory."), "DownloadValidation", ("Reason", "A shared runtime path escaped the Minecraft directory."));
            if (IsSameOrDescendant(sharedPath, WorkspaceMinecraftDirectory)
                || IsSameOrDescendant(WorkspaceMinecraftDirectory, sharedPath))
            {
                throw SafeExceptionLog.Describe(new InvalidOperationException("A shared runtime path overlaps the install workspace."), "DownloadValidation", ("Reason", "A shared runtime path overlaps the install workspace."));
            }
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedParent = Normalize(parent);
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(
                   normalizedParent + System.IO.Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        return System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}
