/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Infrastructure.Modpacks;

internal sealed class ServerDeploymentTransactionService : IServerDeploymentTransactionService
{
    internal const string MarkerFileNamePrefix = ".blockhelm-server-install-";

    public Task<IServerDeploymentTransaction> BeginAsync(
        string parentDirectory,
        string directoryName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);

        var parent = Path.GetFullPath(parentDirectory);
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException($"Server deployment parent directory does not exist: {parent}");
        MinecraftPathGuard.EnsureNoReparsePoints(parent, parent, "Server deployment parent directory");

        var finalDirectory = MinecraftPathGuard.EnsureWithin(
            Path.Combine(parent, directoryName),
            parent,
            "Server deployment directory");
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            throw new ServerDeploymentDirectoryExistsException(finalDirectory);

        var transactionId = Guid.NewGuid().ToString("N");
        var stagingDirectory = MinecraftPathGuard.EnsureWithin(
            Path.Combine(parent, $".blockhelm-server-installing-{transactionId}"),
            parent,
            "Server deployment staging directory");
        Directory.CreateDirectory(stagingDirectory);
        var markerFileName = $"{MarkerFileNamePrefix}{transactionId}.json";
        var markerPath = Path.Combine(stagingDirectory, markerFileName);
        File.WriteAllText(
            markerPath,
            JsonSerializer.Serialize(new ServerDeploymentMarker(1, transactionId, directoryName)));
        IServerDeploymentTransaction transaction = new ServerDeploymentTransaction(
            parent,
            stagingDirectory,
            finalDirectory,
            transactionId,
            markerFileName);
        return Task.FromResult(transaction);
    }

    private sealed record ServerDeploymentMarker(int SchemaVersion, string TransactionId, string DirectoryName);

    private sealed class ServerDeploymentTransaction(
        string parentDirectory,
        string stagingDirectory,
        string finalDirectory,
        string transactionId,
        string markerFileName) : IServerDeploymentTransaction
    {
        private bool completed;

        public string StagingDirectory { get; } = stagingDirectory;

        public string FinalDirectory { get; } = finalDirectory;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed)
                throw SafeExceptionLog.Describe(new InvalidOperationException("Server deployment transaction is already complete."), "DownloadValidation", ("Reason", "Server deployment transaction is already complete."));
            if (Directory.Exists(FinalDirectory) || File.Exists(FinalDirectory))
                throw new ServerDeploymentDirectoryExistsException(FinalDirectory);
            ValidateOwnedStagingDirectory();
            Directory.Move(StagingDirectory, FinalDirectory);
            completed = true;
            try
            {
                File.Delete(Path.Combine(FinalDirectory, markerFileName));
            }
            catch (IOException)
            {
                // The deployment is already committed; a stale ownership marker is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // The deployment is already committed; a stale ownership marker is harmless.
            }
            return Task.CompletedTask;
        }

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed || !Directory.Exists(StagingDirectory))
                return Task.CompletedTask;
            ValidateOwnedStagingDirectory();
            Directory.Delete(StagingDirectory, recursive: true);
            completed = true;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (!completed)
                await AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private void ValidateOwnedStagingDirectory()
        {
            MinecraftPathGuard.EnsureWithin(StagingDirectory, parentDirectory, "Server deployment staging directory");
            MinecraftPathGuard.EnsureNoReparsePoints(
                parentDirectory,
                StagingDirectory,
                "Server deployment staging directory");
            var markerPath = Path.Combine(StagingDirectory, markerFileName);
            if (!File.Exists(markerPath))
                throw SafeExceptionLog.Describe(new InvalidDataException("Server deployment staging marker is missing."), "DownloadValidation", ("Reason", "Server deployment staging marker is missing."));
            var marker = JsonSerializer.Deserialize<ServerDeploymentMarker>(File.ReadAllText(markerPath));
            if (marker is null || !string.Equals(marker.TransactionId, transactionId, StringComparison.Ordinal))
                throw SafeExceptionLog.Describe(new InvalidDataException("Server deployment staging marker does not match the active transaction."), "DownloadValidation", ("Reason", "Server deployment staging marker does not match the active transaction."));
            if (Directory.EnumerateFileSystemEntries(StagingDirectory, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw SafeExceptionLog.Describe(new InvalidDataException("Server deployment staging directory contains a reparse point."), "DownloadValidation", ("Reason", "Server deployment staging directory contains a reparse point."));
            }
        }
    }
}
