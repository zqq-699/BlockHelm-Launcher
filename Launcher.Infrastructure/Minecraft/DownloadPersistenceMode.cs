/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>Internal storage lifetime for a binary download.</summary>
internal enum DownloadPersistenceMode
{
    LightweightAtomic,
    TaskScopedResumable
}

internal sealed record DownloadFileOptions(
    DownloadPersistenceMode PersistenceMode = DownloadPersistenceMode.TaskScopedResumable,
    MinecraftDownloadOperationContext? OperationContext = null,
    string? ManagedRoot = null,
    bool AllowUnverifiedSegmentedDownload = false,
    bool UseHiddenTemporaryFile = true,
    Func<string, CancellationToken, Task>? ValidateUnverifiedFile = null)
{
    public string? ResolveManagedRoot(string destinationPath)
    {
        if (!string.IsNullOrWhiteSpace(ManagedRoot))
            return Path.GetFullPath(ManagedRoot);

        return OperationContext?.ResolveManagedRoot(destinationPath);
    }
}
