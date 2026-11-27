/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;

namespace Launcher.Infrastructure.Persistence;

internal static class AtomicFileMove
{
    // 杀毒软件和索引器可能短暂持有文件句柄，重试只发布已经写完的临时文件。
    public const int MaximumAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    public static Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        Task.Delay(RetryDelay, cancellationToken);

    public static async Task<bool> MoveAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken,
        int maximumAttempts = MaximumAttempts,
        Func<CancellationToken, Task>? retryAsync = null,
        Action? validateDestination = null,
        bool replaceOpenDestination = false)
    {
        retryAsync ??= DelayBeforeRetryAsync;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateDestination?.Invoke();
            try
            {
                // Windows 的覆盖式移动会被仍打开的目标文件阻塞，即使读者允许删除共享。
                if (overwrite && replaceOpenDestination && File.Exists(destination))
                    File.Replace(source, destination, destinationBackupFileName: null);
                else
                    File.Move(source, destination, overwrite);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (!overwrite && File.Exists(destination))
                    return false;
                if (attempt >= maximumAttempts)
                    throw;

                await retryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
