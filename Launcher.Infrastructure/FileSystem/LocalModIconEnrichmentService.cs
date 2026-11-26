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

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 通过本地缓存、Modrinth 和 CurseForge 逐级补全本地 Mod 图标，并维护有界的持久缓存。
/// </summary>
public sealed partial class LocalModIconEnrichmentService : ILocalModIconEnrichmentService
{
    private const int ProviderBatchSize = 50;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan UnusedExpiration = TimeSpan.FromDays(30);

    private readonly ResourceThumbnailCacheService thumbnailCache;
    private readonly RemoteModIconProviderClient providerClient;
    private readonly LocalFileFingerprintService fingerprintService;
    private readonly ILogger<LocalModIconEnrichmentService> logger;
    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private int cacheMigrationStarted;
    private readonly string cacheDirectory;
    private readonly RemoteIconCacheIndexStore cacheIndexStore;
    private bool cleanupCompleted;

    public LocalModIconEnrichmentService(
        LauncherPathProvider? pathProvider = null,
        HttpClient? httpClient = null,
        ICurseForgeApiKeyResolver? curseForgeApiKeyResolver = null,
        ILogger<LocalModIconEnrichmentService>? logger = null,
        IImportConcurrencyLimiter? limiter = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        LocalFileFingerprintService? fingerprintService = null,
        ResourceThumbnailCacheService? thumbnailCache = null)
    {
        var resolvedPathProvider = pathProvider ?? new LauncherPathProvider();
        var resolvedHttpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.logger = logger ?? NullLogger<LocalModIconEnrichmentService>.Instance;
        this.thumbnailCache = thumbnailCache ?? new ResourceThumbnailCacheService(
            resolvedPathProvider,
            resolvedHttpClient,
            limiter,
            downloadSpeedLimitState,
            this.logger);
        var apiKeyResolver = curseForgeApiKeyResolver ?? new CurseForgeApiKeyResolver(resolvedPathProvider);
        providerClient = new RemoteModIconProviderClient(resolvedHttpClient, apiKeyResolver, this.logger);
        this.fingerprintService = fingerprintService ?? new LocalFileFingerprintService();
        cacheDirectory = Path.Combine(resolvedPathProvider.DefaultDataDirectory, "cache", "mods", "remote-icons");
        cacheIndexStore = new RemoteIconCacheIndexStore(cacheDirectory, this.logger);
    }
}
