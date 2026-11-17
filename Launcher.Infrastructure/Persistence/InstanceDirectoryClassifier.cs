/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Persistence;

internal static class InstanceDirectoryClassifier
{
    internal const string RestoreDirectoryPrefix = ".launcher-restore-";

    internal static bool IsTransactionDirectory(string directory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return name.StartsWith(".bhl-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(RestoreDirectoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInstanceDirectory(string directory) =>
        VersionDirectoryName.IsSafeDirectoryName(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
        && !IsTransactionDirectory(directory);

    internal static bool HasPendingTransactions(IEnumerable<string> directories) =>
        directories.Any(directory => IsTransactionDirectory(directory)
            || (IsInstanceDirectory(directory) && Directory.EnumerateFiles(directory, ".bhl-*.json").Any()));
}
