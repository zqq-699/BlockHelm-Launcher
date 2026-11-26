/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using Launcher.Application;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.FileSystem;

internal sealed class InstanceCleanupService(InstanceActivityCoordinator activity, ILogger<InstanceCleanupService> logger) : IInstanceCleanupService
{
    private sealed record ScanReceipt(GameInstance Instance, string Root, string RootIdentity, InstanceCleanupEntry[] Entries);
    private readonly ConditionalWeakTable<InstanceCleanupPreview, ScanReceipt> receipts = new();

    public Task<InstanceCleanupPreview> ScanAsync(GameInstance instance, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(instance, cancellationToken), cancellationToken);

    public Task<InstanceCleanupResult> CleanAsync(InstanceCleanupPreview preview, IReadOnlySet<InstanceCleanupCategory> categories, CancellationToken cancellationToken = default) =>
        Task.Run(() => Clean(preview, categories, cancellationToken), cancellationToken);

    private InstanceCleanupPreview Scan(GameInstance instance, CancellationToken token)
    {
        var root = Path.GetFullPath(instance.InstanceDirectory);
        ValidateInstance(instance, root);
        using var active = activity.EnterCleanup(root);
        using var rootLease = InstanceDirectoryOwnership.AcquireReadLease(instance);
        ValidateInstance(instance, root);
        InstanceDirectoryOwnership.EnsureCurrent(instance);
        var identity = InstanceDirectoryOwnership.Capture(root);
        var entries = new List<InstanceCleanupEntry>();
        logger.LogInformation("Instance cleanup scan started. InstanceDirectory={InstanceDirectory}", root);
        ScanDirectory(root, InstanceCleanupCategory.GameLogs, Path.Combine(root, "logs"), entries, token);
        ScanDirectory(root, InstanceCleanupCategory.CrashReports, Path.Combine(root, "crash-reports"), entries, token);
        foreach (var path in Directory.EnumerateFiles(root))
            if (IsJvmCrashLog(Path.GetFileName(path)))
                AddFile(root, path, InstanceCleanupCategory.RootLogs, entries);
        var launcherLogs = Path.Combine(root, LauncherApplicationIdentity.StorageDirectoryName, "logs");
        if (Directory.Exists(launcherLogs) && !HasReparsePoint(root, launcherLogs))
        {
            using var bhlLease = WindowsDirectoryHandleMover.AcquireReadLease(Path.GetDirectoryName(launcherLogs)!);
            using var logsLease = WindowsDirectoryHandleMover.AcquireReadLease(launcherLogs);
            MinecraftPathGuard.EnsureNoReparsePoints(root, launcherLogs, "Launcher log directory");
            foreach (var path in Directory.EnumerateFiles(launcherLogs))
            {
                var name = Path.GetFileName(path);
                if (IsLauncherOutput(name))
                    AddFile(root, path, InstanceCleanupCategory.LauncherOutput, entries);
                else if (IsLauncherDiagnostic(name))
                    AddFile(root, path, InstanceCleanupCategory.LauncherDiagnostics, entries);
            }
        }
        logger.LogInformation("Instance cleanup scan completed. InstanceDirectory={InstanceDirectory} FileCount={FileCount}", root, entries.Count);
        var snapshot = entries.ToArray();
        var preview = new InstanceCleanupPreview(instance, identity, snapshot.ToArray());
        receipts.Add(preview, new ScanReceipt(instance, root, identity, snapshot));
        return preview;
    }

    private void ScanDirectory(string root, InstanceCleanupCategory category, string directory, List<InstanceCleanupEntry> entries, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory) || HasReparsePoint(root, directory)) return;
        using var lease = WindowsDirectoryHandleMover.AcquireReadLease(directory);
        MinecraftPathGuard.EnsureNoReparsePoints(root, directory, "Cleanup directory");
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            token.ThrowIfCancellationRequested();
            if (IsKnownDiagnosticFile(category, Path.GetFileName(path))) AddFile(root, path, category, entries);
        }
        foreach (var path in Directory.EnumerateDirectories(directory)) ScanDirectory(root, category, path, entries, token);
    }

    private static void AddFile(string root, string path, InstanceCleanupCategory category, List<InstanceCleanupEntry> entries)
    {
        if (HasReparsePoint(root, path)) return;
        var snapshot = WindowsDirectoryHandleMover.CaptureCleanupFile(path);
        if (snapshot is { } file)
            entries.Add(new(category, Path.GetRelativePath(root, path), file.Length, file.LastWriteTimeUtcTicks, file.Identity));
    }

    private InstanceCleanupResult Clean(InstanceCleanupPreview preview, IReadOnlySet<InstanceCleanupCategory> categories, CancellationToken token)
    {
        ScanReceipt receipt;
        lock (receipts)
        {
            if (!receipts.TryGetValue(preview, out receipt!))
                throw new InvalidDataException("Cleanup preview is not owned by this service or has already been used.");
            receipts.Remove(preview);
        }
        var root = Path.GetFullPath(receipt.Instance.InstanceDirectory);
        if (!MinecraftDirectoryPath.Equals(root, receipt.Root)) throw new IOException("Instance directory changed after cleanup scan.");
        ValidateInstance(receipt.Instance, root);
        using var active = activity.EnterCleanup(root);
        using var rootLease = InstanceDirectoryOwnership.AcquireReadLease(receipt.Instance);
        ValidateInstance(receipt.Instance, root);
        InstanceDirectoryOwnership.EnsureCurrent(receipt.Instance);
        if (!InstanceDirectoryOwnership.Matches(root, receipt.RootIdentity)) throw new IOException("Instance directory changed after cleanup scan.");
        var deleted = 0;
        var skipped = 0;
        logger.LogInformation("Instance cleanup started. InstanceDirectory={InstanceDirectory}", root);
        foreach (var entry in receipt.Entries.Where(e => categories.Contains(e.Category)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = MinecraftPathGuard.EnsureWithin(Path.Combine(root, entry.RelativePath), root, "Cleanup file");
                if (!MatchesCategory(entry, root, path)) { skipped++; continue; }
                var leases = new Stack<IDisposable>();
                try
                {
                    var parent = Path.GetDirectoryName(path)!;
                    var relativeParent = Path.GetRelativePath(root, parent);
                    var current = root;
                    if (relativeParent != ".")
                        foreach (var part in relativeParent.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        {
                            current = Path.Combine(current, part);
                            if (HasReparsePoint(root, current)) throw new IOException("Cleanup path contains a reparse point.");
                            leases.Push(WindowsDirectoryHandleMover.AcquireReadLease(current));
                            MinecraftPathGuard.EnsureNoReparsePoints(root, current, "Cleanup directory");
                        }
                    if (HasReparsePoint(root, path) || !WindowsDirectoryHandleMover.DeleteFileIfIdentityMatches(path, entry.Identity, entry.Size, entry.LastWriteTimeUtcTicks)) skipped++;
                    else deleted++;
                }
                finally { while (leases.TryPop(out var lease)) lease.Dispose(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                skipped++;
                logger.LogWarning(ex, "Instance cleanup could not delete a file. RelativePath={RelativePath}", entry.RelativePath);
            }
        }
        logger.LogInformation("Instance cleanup finished. InstanceDirectory={InstanceDirectory} Deleted={Deleted} Skipped={Skipped}", root, deleted, skipped);
        return new(deleted, skipped);
    }

    private static bool MatchesCategory(InstanceCleanupEntry entry, string root, string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var bhlLogs = Path.Combine(root, LauncherApplicationIdentity.StorageDirectoryName, "logs");
        return entry.Category switch
        {
            InstanceCleanupCategory.GameLogs => MinecraftPathGuard.IsWithin(path, Path.Combine(root, "logs")) && IsKnownDiagnosticFile(entry.Category, name),
            InstanceCleanupCategory.CrashReports => MinecraftPathGuard.IsWithin(path, Path.Combine(root, "crash-reports")) && IsKnownDiagnosticFile(entry.Category, name),
            InstanceCleanupCategory.RootLogs => string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) && IsJvmCrashLog(name),
            InstanceCleanupCategory.LauncherOutput => string.Equals(parent, bhlLogs, StringComparison.OrdinalIgnoreCase) && IsLauncherOutput(name),
            InstanceCleanupCategory.LauncherDiagnostics => string.Equals(parent, bhlLogs, StringComparison.OrdinalIgnoreCase) && IsLauncherDiagnostic(name),
            _ => false
        };
    }

    private static bool IsKnownDiagnosticFile(InstanceCleanupCategory category, string name) => category switch
    {
        InstanceCleanupCategory.GameLogs => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase),
        InstanceCleanupCategory.CrashReports => IsMinecraftCrashReport(name),
        _ => false
    };

    private static bool IsMinecraftCrashReport(string name)
    {
        const string prefix = "crash-";
        const int timestampLength = 19;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || name.Length <= prefix.Length + timestampLength) return false;
        var suffix = name.AsSpan(prefix.Length + timestampLength);
        if (!suffix.Equals("-client.txt", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("-server.txt", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("-fml.txt", StringComparison.OrdinalIgnoreCase)) return false;
        return DateTime.TryParseExact(name.AsSpan(prefix.Length, timestampLength), "yyyy-MM-dd_HH.mm.ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsLauncherOutput(string name)
    {
        const string prefix = "launch-output-";
        const string suffix = ".log";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || name.Length != prefix.Length + 32 + suffix.Length) return false;
        return Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _);
    }

    private static bool IsLauncherDiagnostic(string name)
    {
        const string prefix = "launch-diagnostics-";
        const string suffix = ".log";
        const int timestampLength = 19;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || name.Length != prefix.Length + timestampLength + 1 + 32 + suffix.Length
            || name[prefix.Length + timestampLength] != '-') return false;
        return DateTime.TryParseExact(name.AsSpan(prefix.Length, timestampLength), "yyyyMMdd-HHmmss-fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && Guid.TryParseExact(name.AsSpan(prefix.Length + timestampLength + 1, 32), "N", out _);
    }

    private static bool IsJvmCrashLog(string name)
    {
        const string prefix = "hs_err_pid";
        const string suffix = ".log";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var processId = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return !processId.IsEmpty && processId.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static void ValidateInstance(GameInstance instance, string root)
    {
        var parent = Path.GetDirectoryName(root);
        if (!InstanceDirectoryOwnership.IsObserved(instance)
            || !MinecraftDirectoryPath.Equals(root, instance.InstanceDirectory)
            || !InstanceDirectoryClassifier.IsInstanceDirectory(root)
            || !string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(root), instance.VersionName, StringComparison.OrdinalIgnoreCase))
            throw new GameInstanceMutationConflictException(root, instance.VersionName);
    }

    private static bool HasReparsePoint(string root, string path)
    {
        try { MinecraftPathGuard.EnsureNoReparsePoints(root, path, "Cleanup path"); return false; }
        catch (InvalidDataException) { return true; }
    }
}
