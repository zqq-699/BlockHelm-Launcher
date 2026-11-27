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
using System.Text.Json;

namespace Launcher.Infrastructure.Persistence;

internal static class AtomicJsonFileWriter
{
    public static Task WriteAsync<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken,
        Action? validateDestination = null,
        bool replaceOpenDestination = false) =>
        WriteAsync(
            destinationPath,
            value,
            options,
            cancellationToken,
            AtomicFileMove.MaximumAttempts,
            AtomicFileMove.DelayBeforeRetryAsync,
            validateDestination,
            replaceOpenDestination);

    internal static async Task WriteAsync<T>(
        string destinationPath,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken,
        int maxMoveAttempts,
        Func<CancellationToken, Task> retryAsync,
        Action? validateDestination = null,
        bool replaceOpenDestination = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMoveAttempts, 1);
        ArgumentNullException.ThrowIfNull(retryAsync);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("The JSON destination must have a parent directory.");
        validateDestination?.Invoke();
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await AtomicFileMove.MoveAsync(
                    temporaryPath,
                    fullDestinationPath,
                    overwrite: true,
                    cancellationToken,
                    maxMoveAttempts,
                    retryAsync,
                    validateDestination,
                    replaceOpenDestination)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
