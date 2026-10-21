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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 在隔离沙箱中安装 Forge，兼容现代与旧版安装器，并只把自包含的最终版本提交到用户目录。
/// </summary>
public sealed partial class ForgeLoaderProvider : ILoaderProvider, IStagedLoaderProvider, IDirectSharedContentStagedLoaderProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex InstallerUrlRegex = new(
        @"https://maven\.minecraftforge\.net/net/minecraftforge/forge/(?<fullVersion>[^""'<>\s]+)/forge-(?<artifactVersion>[^""'<>\s]+)-installer\.jar",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient httpClient;
    private readonly IForgeInstallerRunner installerRunner;
    private readonly IFinalVersionInstaller finalVersionInstaller;
    private readonly ILoaderInstallerJavaRuntimeResolver? javaRuntimeResolver;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;
    private readonly string tempRootDirectory;
    private readonly SemaphoreSlim catalogLock = new(1, 1);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ForgeCatalogEntry>> catalogCache = new(StringComparer.OrdinalIgnoreCase);

    public ForgeLoaderProvider(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<ForgeLoaderProvider>? logger = null)
        : this(httpClient, installerRunner: null, finalVersionInstaller: null, tempRootDirectory: null, downloadSpeedLimitState, logger, javaRuntimeResolver: null)
    {
    }

    internal ForgeLoaderProvider(
        HttpClient? httpClient,
        IForgeInstallerRunner? installerRunner,
        string? tempRootDirectory,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null)
        : this(httpClient, installerRunner, finalVersionInstaller: null, tempRootDirectory, downloadSpeedLimitState, logger: null, javaRuntimeResolver: null)
    {
    }

    internal ForgeLoaderProvider(
        HttpClient? httpClient,
        IForgeInstallerRunner? installerRunner,
        IFinalVersionInstaller? finalVersionInstaller,
        string? tempRootDirectory,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        ILoaderInstallerJavaRuntimeResolver? javaRuntimeResolver = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        this.installerRunner = installerRunner ?? new ForgeInstallerRunner();
        this.finalVersionInstaller = finalVersionInstaller ?? new FinalVersionInstaller();
        this.javaRuntimeResolver = javaRuntimeResolver;
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
        this.tempRootDirectory = string.IsNullOrWhiteSpace(tempRootDirectory) ? Path.GetTempPath() : tempRootDirectory;
    }

    public LoaderKind Kind => LoaderKind.Forge;

    public bool IsImplemented => true;

    /// <summary>
    /// 获取指定 Minecraft 版本的 Forge 清单，并按解析后的版本号降序返回。
    /// </summary>
    public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        var catalog = await GetCatalogAsync(
            minecraftVersion,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
        return catalog.Values
            .OrderByDescending(entry => ParseForgeVersion(entry.ForgeVersion))
            .ThenByDescending(entry => entry.ForgeVersion, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new LoaderVersionInfo(entry.ForgeVersion))
            .ToList();
    }

    /// <summary>
    /// 在临时 .minecraft 中运行 Forge 安装器，生成自包含隔离版本后提交到目标目录。
    /// </summary>
    public Task<string> InstallAsync(
        string minecraftVersion,
        string gameDirectory,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        return InstallCoreAsync(
            minecraftVersion,
            gameDirectory,
            gameDirectory,
            isolatedVersionName,
            loaderVersion,
            progress,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }

    public Task<string> InstallStagedAsync(
        string minecraftVersion,
        string outputGameDirectory,
        string sharedMinecraftDirectory,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond)
    {
        return InstallCoreAsync(
            minecraftVersion,
            outputGameDirectory,
            sharedMinecraftDirectory,
            isolatedVersionName,
            loaderVersion,
            progress,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);
    }
}
