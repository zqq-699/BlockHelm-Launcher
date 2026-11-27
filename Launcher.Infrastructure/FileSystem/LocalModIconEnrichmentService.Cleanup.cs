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

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    /// <summary>
    /// 每个服务生命周期清理一次本地索引，图标文件由共享资源缓存维护。
    /// </summary>
    private async Task CleanupCacheOnceAsync(CancellationToken cancellationToken)
    {
        if (cleanupCompleted)
            return;

        await cleanupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cleanupCompleted)
                return;

            var now = DateTimeOffset.UtcNow;
            var removed = 0;
            var remaining = 0;
            await cacheIndexStore.UpdateAsync(index =>
            {
                foreach (var pair in index.Entries.ToArray())
                {
                    var entry = pair.Value;
                    if (now - entry.LastUsedAt > UnusedExpiration || GetCachedIcon(entry) is null)
                    {
                        index.Entries.Remove(pair.Key);
                        removed++;
                    }
                }

                var changed = removed > 0;
                foreach (var aliases in new[] { index.Aliases, index.FileAliases })
                {
                    foreach (var alias in aliases.Where(pair => !index.Entries.ContainsKey(pair.Value)).ToArray())
                        changed |= aliases.Remove(alias.Key);
                }

                remaining = index.Entries.Count;
                return changed;
            }, cancellationToken).ConfigureAwait(false);

            cleanupCompleted = true;
            logger.LogInformation(
                "Remote local mod icon index cleanup completed. RemovedCount={RemovedCount} RemainingCount={RemainingCount}",
                removed,
                remaining);
        }
        finally
        {
            cleanupLock.Release();
        }
    }

    /// <summary>
    /// 单次流式读取 Mod 文件，同时计算 SHA-1 和 CurseForge 去空白 MurmurHash2 指纹。
    /// </summary>
    private async Task<ModIconLookupCandidate?> CreateLookupCandidateAsync(
        LocalMod mod,
        CancellationToken cancellationToken)
    {
        try
        {
            var fingerprint = await fingerprintService
                .GetFingerprintAsync(mod.FullPath, cancellationToken)
                .ConfigureAwait(false);
            var fileAlias = TryCreateFileAlias(mod.FullPath);
            if (fileAlias is null)
                return null;

            return new ModIconLookupCandidate(
                mod.FullPath,
                fingerprint.Sha1,
                fileAlias,
                fingerprint.CurseForgeFingerprint,
                ResourceProjectKind.Mod);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            logger.LogWarning(
                exception,
                "Failed to hash local mod for remote icon lookup. FileName={FileName}",
                mod.FileName);
            return null;
        }
    }
}
