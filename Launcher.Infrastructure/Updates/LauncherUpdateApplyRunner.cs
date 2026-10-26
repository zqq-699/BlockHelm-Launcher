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

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Launcher.Infrastructure.Updates;

public sealed class LauncherUpdateApplyRunner
{
    private static readonly TimeSpan DefaultConfirmationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultConfirmationPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly ILauncherUpdateFileOperations files;
    private readonly ILauncherUpdateProcessOperations processes;
    private readonly TimeSpan confirmationTimeout;
    private readonly TimeSpan confirmationPollInterval;

    public LauncherUpdateApplyRunner()
        : this(
            new LauncherUpdateFileOperations(),
            new LauncherUpdateProcessOperations(),
            DefaultConfirmationTimeout,
            DefaultConfirmationPollInterval)
    {
    }

    internal LauncherUpdateApplyRunner(
        ILauncherUpdateFileOperations files,
        ILauncherUpdateProcessOperations processes,
        TimeSpan confirmationTimeout,
        TimeSpan confirmationPollInterval)
    {
        this.files = files;
        this.processes = processes;
        this.confirmationTimeout = confirmationTimeout;
        this.confirmationPollInterval = confirmationPollInterval;
    }

    public int Run(LauncherUpdateApplyOptions options)
    {
        var logger = new LauncherUpdateApplyLogger(options.LogDirectory);
        LauncherUpdateTransaction? transaction = null;
        ILauncherUpdateProcess? updatedProcess = null;
        var committed = false;
        try
        {
            logger.Info("Launcher update apply mode started.");
            logger.Info($"Source={options.SourcePath}");
            logger.Info($"Target={options.TargetPath}");

            ValidateSource(options.SourcePath);
            ValidateTarget(options.TargetPath);
            WaitForLauncherExit(options.ProcessId, logger);
            ReconcilePreviousTransaction(options.TargetPath, logger);

            transaction = LauncherUpdateTransaction.Create(options);
            logger.Info($"Preparing launcher update transaction. TransactionId={transaction.TransactionId}");
            files.CopyCandidateAndFlush(options.SourcePath, transaction.CandidatePath);
            VerifyCandidate(options.SourcePath, transaction.CandidatePath);
            files.WriteTransaction(transaction);

            ReplaceWithRetry(
                transaction.CandidatePath,
                options.TargetPath,
                transaction.BackupPath,
                logger);
            committed = true;
            transaction = transaction with { Phase = LauncherUpdateTransactionPhase.Committed };
            files.WriteTransaction(transaction);
            logger.Info($"Launcher executable atomically committed. TransactionId={transaction.TransactionId}");

            if (!options.Restart)
            {
                CompleteTransaction(transaction, logger);
                logger.Info("Launcher update apply mode completed without restart.");
                return 0;
            }

            updatedProcess = processes.Start(CreateRestartInfo(
                options.TargetPath,
                LauncherUpdateStartupCoordinator.ConfirmationArgument,
                transaction.TransactionId));
            logger.Info($"Waiting for launcher startup confirmation. TransactionId={transaction.TransactionId}");
            WaitForConfirmation(transaction, updatedProcess);
            CompleteTransaction(transaction, logger);
            logger.Info("Launcher update apply mode completed.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("Launcher update apply mode failed.", ex);
            if (committed && transaction is not null)
            {
                try
                {
                    RollBack(transaction, updatedProcess, options.Restart, logger);
                }
                catch (Exception rollbackException)
                {
                    logger.Error(
                        $"Launcher update rollback failed; recovery files were preserved. TransactionId={transaction.TransactionId}",
                        rollbackException);
                }
            }
            else if (transaction is not null)
            {
                CleanupUncommittedTransaction(transaction, logger);
            }

            return 1;
        }
        finally
        {
            updatedProcess?.Dispose();
        }
    }

    public int RunRecovery(LauncherUpdateRecoveryOptions options)
    {
        var logger = new LauncherUpdateApplyLogger(options.LogDirectory);
        try
        {
            logger.Info("Launcher update recovery mode started.");
            WaitForLauncherExit(options.ProcessId, logger);
            var transaction = files.ReadTransaction(LauncherUpdateTransaction.GetMarkerPath(options.TargetPath));
            if (transaction is null)
                throw new InvalidOperationException("Pending launcher update transaction was not found.");

            ValidateTransactionForTarget(transaction, options.TargetPath);
            if (IsTransactionConfirmed(transaction))
            {
                CleanupConfirmedTransaction(transaction, logger);
            }
            else
            {
                RestoreBackup(transaction, logger);
            }

            if (options.Restart)
            {
                using var process = processes.Start(CreateRestartInfo(options.TargetPath));
            }

            logger.Info("Launcher update recovery mode completed.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("Launcher update recovery mode failed; recovery files were preserved.", ex);
            return 1;
        }
    }

    private void WaitForConfirmation(
        LauncherUpdateTransaction transaction,
        ILauncherUpdateProcess updatedProcess)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed <= confirmationTimeout)
        {
            if (files.Exists(transaction.ConfirmationPath)
                && string.Equals(
                    files.ReadAllText(transaction.ConfirmationPath).Trim(),
                    transaction.TransactionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (updatedProcess.HasExited)
                throw new InvalidOperationException("Updated launcher exited before confirming startup.");

            processes.Delay(confirmationPollInterval);
            elapsed += confirmationPollInterval;
        }

        throw new TimeoutException("Timed out waiting for updated launcher startup confirmation.");
    }

    private void CompleteTransaction(
        LauncherUpdateTransaction transaction,
        LauncherUpdateApplyLogger logger)
    {
        var confirmed = transaction with { Phase = LauncherUpdateTransactionPhase.Confirmed };
        files.WriteTransaction(confirmed);
        CleanupConfirmedTransaction(confirmed, logger);
    }

    private void RollBack(
        LauncherUpdateTransaction transaction,
        ILauncherUpdateProcess? updatedProcess,
        bool restart,
        LauncherUpdateApplyLogger logger)
    {
        if (updatedProcess is not null && !updatedProcess.HasExited)
        {
            logger.Info($"Stopping unconfirmed launcher. TransactionId={transaction.TransactionId}");
            updatedProcess.Kill();
            if (!updatedProcess.WaitForExit(10_000))
                throw new TimeoutException("Timed out stopping the unconfirmed launcher.");
        }

        RestoreBackup(transaction, logger);
        if (restart)
        {
            using var process = processes.Start(CreateRestartInfo(transaction.TargetPath));
            logger.Info("Previous launcher version restarted after rollback.");
        }
    }

    private void RestoreBackup(
        LauncherUpdateTransaction transaction,
        LauncherUpdateApplyLogger logger)
    {
        if (!files.Exists(transaction.BackupPath))
            throw new FileNotFoundException("Launcher update backup was not found.", transaction.BackupPath);

        ReplaceWithRetry(
            transaction.BackupPath,
            transaction.TargetPath,
            destinationBackupPath: null,
            logger);
        logger.Info($"Previous launcher version atomically restored. TransactionId={transaction.TransactionId}");
        CleanupPath(transaction.CandidatePath, logger);
        CleanupPath(transaction.ConfirmationPath, logger);
        CleanupPath(transaction.MarkerPath, logger);
    }

    private void CleanupConfirmedTransaction(
        LauncherUpdateTransaction transaction,
        LauncherUpdateApplyLogger logger)
    {
        CleanupPath(transaction.BackupPath, logger);
        CleanupPath(transaction.CandidatePath, logger);
        CleanupPath(transaction.ConfirmationPath, logger);
        CleanupPath(transaction.MarkerPath, logger);
        logger.Info($"Launcher update transaction cleaned. TransactionId={transaction.TransactionId}");
    }

    private void CleanupUncommittedTransaction(
        LauncherUpdateTransaction transaction,
        LauncherUpdateApplyLogger logger)
    {
        CleanupPath(transaction.CandidatePath, logger);
        CleanupPath(transaction.ConfirmationPath, logger);
        if (!files.Exists(transaction.BackupPath))
            CleanupPath(transaction.MarkerPath, logger);
    }

    private void ReconcilePreviousTransaction(string targetPath, LauncherUpdateApplyLogger logger)
    {
        var markerPath = LauncherUpdateTransaction.GetMarkerPath(targetPath);
        var previous = files.ReadTransaction(markerPath);
        if (previous is null)
            return;

        ValidateTransactionForTarget(previous, targetPath);
        logger.Info($"Reconciling previous launcher update transaction. TransactionId={previous.TransactionId} Phase={previous.Phase}");
        if (IsTransactionConfirmed(previous))
        {
            CleanupConfirmedTransaction(previous, logger);
            return;
        }

        if (files.Exists(previous.BackupPath))
            RestoreBackup(previous, logger);
        else
            CleanupUncommittedTransaction(previous, logger);
    }

    private bool IsTransactionConfirmed(LauncherUpdateTransaction transaction)
    {
        return transaction.Phase == LauncherUpdateTransactionPhase.Confirmed
            || (files.Exists(transaction.ConfirmationPath)
                && string.Equals(
                    files.ReadAllText(transaction.ConfirmationPath).Trim(),
                    transaction.TransactionId,
                    StringComparison.Ordinal));
    }

    private static void ValidateSource(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Downloaded update executable was not found.", sourcePath);

        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length <= 0)
            throw new InvalidOperationException("Downloaded update executable is empty.");

        if (!Path.GetExtension(sourcePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded update file is not an executable.");
    }

    private static void ValidateTarget(string targetPath)
    {
        if (!File.Exists(targetPath))
            throw new FileNotFoundException("Launcher executable to replace was not found.", targetPath);
        if (new FileInfo(targetPath).Length <= 0)
            throw new InvalidOperationException("Launcher executable to replace is empty.");
    }

    private void VerifyCandidate(string sourcePath, string candidatePath)
    {
        if (files.GetLength(sourcePath) != files.GetLength(candidatePath))
            throw new InvalidOperationException("Staged launcher executable length does not match the downloaded update.");

        var sourceHash = files.ComputeSha256(sourcePath);
        var candidateHash = files.ComputeSha256(candidatePath);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, candidateHash))
            throw new InvalidOperationException("Staged launcher executable hash does not match the downloaded update.");
    }

    private static void ValidateTransactionForTarget(
        LauncherUpdateTransaction transaction,
        string targetPath)
    {
        if (!string.Equals(
                Path.GetFullPath(transaction.TargetPath),
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pending launcher update transaction does not match the current executable.");
        }

        if (!transaction.HasValidDerivedPaths())
            throw new InvalidOperationException("Pending launcher update transaction contains invalid paths.");
    }

    private static void WaitForLauncherExit(int processId, LauncherUpdateApplyLogger logger)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            logger.Info($"Waiting for launcher process to exit. ProcessId={processId}");
            if (!process.WaitForExit(60_000))
                throw new TimeoutException("Timed out waiting for launcher process to exit.");
        }
        catch (ArgumentException)
        {
            logger.Info("Launcher process has already exited.");
        }
    }

    private void CleanupPath(string path, LauncherUpdateApplyLogger logger)
    {
        try
        {
            files.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Error($"Failed to clean launcher update transaction file. Path={path}", exception);
        }
    }

    private void ReplaceWithRetry(
        string sourcePath,
        string targetPath,
        string? destinationBackupPath,
        LauncherUpdateApplyLogger logger)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                files.Replace(sourcePath, targetPath, destinationBackupPath);
                return;
            }
            catch (Exception exception) when (
                attempt < 20 && exception is IOException or UnauthorizedAccessException)
            {
                logger.Info($"Atomic launcher executable replacement will be retried. Attempt={attempt}");
                processes.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        files.Replace(sourcePath, targetPath, destinationBackupPath);
    }

    private static ProcessStartInfo CreateRestartInfo(string targetPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}

public sealed record LauncherUpdateApplyOptions(
    int ProcessId,
    string SourcePath,
    string TargetPath,
    string LogDirectory,
    bool Restart)
{
    public static LauncherUpdateApplyOptions? Parse(string[] args)
    {
        if (!args.Contains("--apply-update", StringComparer.OrdinalIgnoreCase))
            return null;

        int processId = 0;
        string? sourcePath = null;
        string? targetPath = null;
        string? logDirectory = null;
        var restart = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedProcessId):
                    processId = parsedProcessId;
                    i++;
                    break;
                case "--source" when i + 1 < args.Length:
                    sourcePath = args[++i];
                    break;
                case "--target" when i + 1 < args.Length:
                    targetPath = args[++i];
                    break;
                case "--log-dir" when i + 1 < args.Length:
                    logDirectory = args[++i];
                    break;
                case "--restart":
                    restart = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(targetPath)
            || string.IsNullOrWhiteSpace(logDirectory))
        {
            return null;
        }

        return new LauncherUpdateApplyOptions(
            processId,
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(targetPath),
            Path.GetFullPath(logDirectory),
            restart);
    }
}

public sealed record LauncherUpdateRecoveryOptions(
    int ProcessId,
    string TargetPath,
    string LogDirectory,
    bool Restart)
{
    public static LauncherUpdateRecoveryOptions? Parse(string[] args)
    {
        if (!args.Contains("--recover-update", StringComparer.OrdinalIgnoreCase))
            return null;

        int processId = 0;
        string? targetPath = null;
        string? logDirectory = null;
        var restart = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedProcessId):
                    processId = parsedProcessId;
                    i++;
                    break;
                case "--target" when i + 1 < args.Length:
                    targetPath = args[++i];
                    break;
                case "--log-dir" when i + 1 < args.Length:
                    logDirectory = args[++i];
                    break;
                case "--restart":
                    restart = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(logDirectory))
            return null;

        return new LauncherUpdateRecoveryOptions(
            processId,
            Path.GetFullPath(targetPath),
            Path.GetFullPath(logDirectory),
            restart);
    }
}

internal enum LauncherUpdateTransactionPhase
{
    Prepared,
    Committed,
    Confirmed
}

internal sealed record LauncherUpdateTransaction(
    string TransactionId,
    string UpdaterPath,
    string TargetPath,
    string LogDirectory,
    string CandidatePath,
    string BackupPath,
    string MarkerPath,
    string ConfirmationPath,
    LauncherUpdateTransactionPhase Phase)
{
    public static LauncherUpdateTransaction Create(LauncherUpdateApplyOptions options)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var targetPath = Path.GetFullPath(options.TargetPath);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Target executable directory is unavailable.");
        var targetName = Path.GetFileName(targetPath);
        return new LauncherUpdateTransaction(
            transactionId,
            Path.GetFullPath(options.SourcePath),
            targetPath,
            Path.GetFullPath(options.LogDirectory),
            Path.Combine(targetDirectory, $".{targetName}.{transactionId}.candidate"),
            GetBackupPath(targetPath),
            GetMarkerPath(targetPath),
            Path.Combine(targetDirectory, $".{targetName}.{transactionId}.confirmed"),
            LauncherUpdateTransactionPhase.Prepared);
    }

    public static string GetBackupPath(string targetPath) => Path.GetFullPath(targetPath) + ".update-backup";

    public static string GetMarkerPath(string targetPath) => Path.GetFullPath(targetPath) + ".update-pending.json";

    public bool HasValidDerivedPaths()
    {
        if (!Guid.TryParseExact(TransactionId, "N", out _))
            return false;
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(TargetPath));
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return false;
        var targetName = Path.GetFileName(TargetPath);
        return string.Equals(BackupPath, GetBackupPath(TargetPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(MarkerPath, GetMarkerPath(TargetPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                CandidatePath,
                Path.Combine(targetDirectory, $".{targetName}.{TransactionId}.candidate"),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                ConfirmationPath,
                Path.Combine(targetDirectory, $".{targetName}.{TransactionId}.confirmed"),
                StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(UpdaterPath).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }
}

internal interface ILauncherUpdateFileOperations
{
    bool Exists(string path);
    void CopyCandidateAndFlush(string sourcePath, string candidatePath);
    long GetLength(string path);
    byte[] ComputeSha256(string path);
    void Replace(string sourcePath, string targetPath, string? destinationBackupPath);
    void WriteTransaction(LauncherUpdateTransaction transaction);
    LauncherUpdateTransaction? ReadTransaction(string markerPath);
    string ReadAllText(string path);
    void WriteAllTextAndFlush(string path, string content);
    void Delete(string path);
}

internal sealed class LauncherUpdateFileOperations : ILauncherUpdateFileOperations
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public bool Exists(string path) => File.Exists(path);

    public void CopyCandidateAndFlush(string sourcePath, string candidatePath)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(
            candidatePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    public long GetLength(string path) => new FileInfo(path).Length;

    public byte[] ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    public void Replace(string sourcePath, string targetPath, string? destinationBackupPath) =>
        File.Replace(sourcePath, targetPath, destinationBackupPath);

    public void WriteTransaction(LauncherUpdateTransaction transaction)
    {
        var temporaryPath = transaction.MarkerPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteAllTextAndFlush(temporaryPath, JsonSerializer.Serialize(transaction, SerializerOptions));
            if (File.Exists(transaction.MarkerPath))
                File.Replace(temporaryPath, transaction.MarkerPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, transaction.MarkerPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public LauncherUpdateTransaction? ReadTransaction(string markerPath)
    {
        if (!File.Exists(markerPath))
            return null;
        return JsonSerializer.Deserialize<LauncherUpdateTransaction>(File.ReadAllText(markerPath), SerializerOptions)
            ?? throw new InvalidOperationException("Pending launcher update transaction is invalid.");
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllTextAndFlush(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public void Delete(string path) => TryDelete(path);

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

internal interface ILauncherUpdateProcessOperations
{
    ILauncherUpdateProcess Start(ProcessStartInfo startInfo);
    void Delay(TimeSpan delay);
}

internal interface ILauncherUpdateProcess : IDisposable
{
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(int milliseconds);
}

internal sealed class LauncherUpdateProcessOperations : ILauncherUpdateProcessOperations
{
    public ILauncherUpdateProcess Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start launcher process.");
        return new LauncherUpdateProcess(process);
    }

    public void Delay(TimeSpan delay) => Thread.Sleep(delay);
}

internal sealed class LauncherUpdateProcess(Process process) : ILauncherUpdateProcess
{
    public bool HasExited => process.HasExited;

    public void Kill() => process.Kill(entireProcessTree: true);

    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

    public void Dispose() => process.Dispose();
}

internal sealed class LauncherUpdateApplyLogger
{
    private readonly string logPath;

    public LauncherUpdateApplyLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        logPath = Path.Combine(logDirectory, $"updater-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
    }

    public void Info(string message) => Write("INF", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null
            ? message
            : $"{message} ErrorType={exception.GetType().Name} HResult={exception.HResult}");

    private void Write(string level, string message)
    {
        File.AppendAllText(
            logPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {Launcher.Application.Services.SensitiveLogText.Redact(message)}{Environment.NewLine}");
    }
}
