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
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Modrinth.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Modrinth;

/// <summary>
/// 提供 Modrinth Mod 搜索与兼容版本安装，并封装 Fabric API/Quilt 标准库快捷流程。
/// </summary>
public sealed class ModrinthService : IModrinthService
{
    // 兼容性始终同时约束 Minecraft 版本和 Loader，不能只按项目最新版本安装。
    private const string BaseUrl = "https://api.modrinth.com/v2";
    private const string FabricApiProjectSlug = "fabric-api";
    private const string FabricApiProjectId = "P7dR8mSH";
    private const string FabricApiTitle = "Fabric API";
    private const string QuiltStandardLibraryProjectSlug = "qsl";
    private const string QuiltStandardLibraryProjectId = "qvIfYCYJ";
    private const string QuiltStandardLibraryTitle = "QFAPI / QSL";
    private readonly HttpClient httpClient;
    private readonly ILogger<ModrinthService> logger;
    private readonly ISettingsService? settingsService;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly IImportConcurrencyLimiter limiter;
    private readonly DownloadRetryOptions? retryOptions;

    public ModrinthService(HttpClient? httpClient = null, ILogger<ModrinthService>? logger = null)
        : this(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient(),
            logger,
            settingsService: null,
            downloadSpeedLimitState: null,
            limiter: null,
            retryOptions: null)
    {
    }

    internal ModrinthService(
        HttpClient httpClient,
        ILogger<ModrinthService>? logger,
        ISettingsService? settingsService,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        IImportConcurrencyLimiter? limiter,
        DownloadRetryOptions? retryOptions = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.logger = logger ?? NullLogger<ModrinthService>.Instance;
        this.settingsService = settingsService;
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.limiter = limiter ?? ImportConcurrencyLimiter.Shared;
        this.retryOptions = retryOptions;
    }

    public async Task<IReadOnlyList<ModrinthProject>> SearchModsAsync(string query, string minecraftVersion, LoaderKind loader, CancellationToken cancellationToken = default)
    {
        // 搜索 facets 在服务边界构造，UI 无需了解 Modrinth 查询语法。
        var facets = new List<List<string>>
        {
            new() { "project_type:mod" }
        };

        if (!string.IsNullOrWhiteSpace(minecraftVersion))
            facets.Add(new List<string> { $"versions:{minecraftVersion}" });

        if (loader is not LoaderKind.Vanilla)
            facets.Add(new List<string> { $"categories:{loader.ToString().ToLowerInvariant()}" });

        logger.LogDebug(
            "Searching Modrinth mods. Query={Query} MinecraftVersion={MinecraftVersion} Loader={Loader}",
            query,
            minecraftVersion,
            loader);
        var url = $"{BaseUrl}/search?limit=24&query={Uri.EscapeDataString(query ?? string.Empty)}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        var response = await httpClient.GetFromJsonAsync<ModrinthSearchResponse>(url, cancellationToken);
        var projects = response?.Hits.Select(hit => new ModrinthProject
        {
            ProjectId = hit.ProjectId,
            Slug = hit.Slug,
            Title = hit.Title,
            Description = hit.Description,
            IconUrl = hit.IconUrl,
            Downloads = hit.Downloads
        }).ToList() ?? [];
        logger.LogDebug("Modrinth search completed. ResultCount={ResultCount}", projects.Count);
        return projects;
    }

    public async Task<IReadOnlyList<ModrinthVersionInfo>> GetFabricApiVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Loading Fabric API versions. MinecraftVersion={MinecraftVersion}",
            minecraftVersion);

        var versions = await GetCompatibleVersionsAsync(
            FabricApiProjectSlug,
            minecraftVersion,
            LoaderKind.Fabric,
            cancellationToken);
        var result = MapVersionInfos(versions);

        logger.LogDebug(
            "Loaded Fabric API versions. MinecraftVersion={MinecraftVersion} Count={Count}",
            minecraftVersion,
            result.Count);
        return result;
    }

    public async Task<IReadOnlyList<ModrinthVersionInfo>> GetQuiltStandardLibraryVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug(
            "Loading Quilt standard library versions. MinecraftVersion={MinecraftVersion}",
            minecraftVersion);

        var versions = await GetCompatibleVersionsAsync(
            QuiltStandardLibraryProjectSlug,
            minecraftVersion,
            LoaderKind.Quilt,
            cancellationToken);
        var result = MapVersionInfos(versions);

        logger.LogDebug(
            "Loaded Quilt standard library versions. MinecraftVersion={MinecraftVersion} Count={Count}",
            minecraftVersion,
            result.Count);
        return result;
    }

    public async Task<string> InstallLatestCompatibleAsync(ModrinthProject project, GameInstance instance, IProgress<LauncherProgress>? progress, CancellationToken cancellationToken = default)
    {
        // “最新”只在实例兼容版本集合内选择，没有匹配时明确失败而不安装错误 Loader 文件。
        var loader = instance.Loader is LoaderKind.Vanilla ? "fabric" : instance.Loader.ToString().ToLowerInvariant();
        logger.LogDebug(
            "Installing compatible Modrinth project. ProjectId={ProjectId} MinecraftVersion={MinecraftVersion} Loader={Loader}",
            project.ProjectId,
            instance.MinecraftVersion,
            instance.Loader);
        var versions = await GetCompatibleVersionsAsync(project.ProjectId, instance.MinecraftVersion, loader, cancellationToken);
        var selected = versions.FirstOrDefault(version => version.Files.Count > 0);
        if (selected is null)
            throw new NoCompatibleModFileException(project.ProjectId, instance.MinecraftVersion, instance.Loader);

        return await InstallVersionFileAsync(project.ProjectId, project.Title, selected, instance, progress, cancellationToken);
    }

    public Task<string> InstallFabricApiAsync(GameInstance instance, IProgress<LauncherProgress>? progress, CancellationToken cancellationToken = default)
    {
        return InstallLatestCompatibleAsync(
            new ModrinthProject
            {
                ProjectId = FabricApiProjectSlug,
                Slug = FabricApiProjectSlug,
                Title = FabricApiTitle
            },
            instance,
            progress,
            cancellationToken);
    }

    public async Task<string> InstallFabricApiAsync(
        GameInstance instance,
        string versionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        // 用户指定版本仍需验证属于当前实例兼容集合，防止跨 Minecraft 版本安装。
        if (string.IsNullOrWhiteSpace(versionId))
            throw new InvalidOperationException("Fabric API version id is required.");

        logger.LogDebug(
            "Installing Fabric API. VersionId={VersionId} MinecraftVersion={MinecraftVersion} InstanceId={InstanceId}",
            versionId,
            instance.MinecraftVersion,
            instance.Id);
        var version = await httpClient.GetFromJsonAsync<ModrinthVersion>(
            $"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}",
            cancellationToken)
            ?? throw new InvalidOperationException($"Modrinth version metadata is empty: {versionId}");

        if (!string.Equals(version.ProjectId, FabricApiProjectId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(version.ProjectId, FabricApiProjectSlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Modrinth version is not a Fabric API version: {versionId}");
        }

        return await InstallVersionFileAsync(
            FabricApiProjectSlug,
            FabricApiTitle,
            version,
            instance,
            progress,
            cancellationToken);
    }

    public async Task<string> InstallQuiltStandardLibraryAsync(
        GameInstance instance,
        string versionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            throw new InvalidOperationException("Quilt standard library version id is required.");

        logger.LogDebug(
            "Installing Quilt standard library. VersionId={VersionId} MinecraftVersion={MinecraftVersion} InstanceId={InstanceId}",
            versionId,
            instance.MinecraftVersion,
            instance.Id);
        var version = await httpClient.GetFromJsonAsync<ModrinthVersion>(
            $"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}",
            cancellationToken)
            ?? throw new InvalidOperationException($"Modrinth version metadata is empty: {versionId}");

        if (!string.Equals(version.ProjectId, QuiltStandardLibraryProjectId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(version.ProjectId, QuiltStandardLibraryProjectSlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Modrinth version is not a QFAPI/QSL version: {versionId}");
        }

        return await InstallVersionFileAsync(
            QuiltStandardLibraryProjectSlug,
            QuiltStandardLibraryTitle,
            version,
            instance,
            progress,
            cancellationToken);
    }

    private Task<List<ModrinthVersion>> GetCompatibleVersionsAsync(
        string projectIdOrSlug,
        string minecraftVersion,
        LoaderKind loader,
        CancellationToken cancellationToken)
    {
        var loaderName = loader is LoaderKind.Vanilla ? "fabric" : loader.ToString().ToLowerInvariant();
        return GetCompatibleVersionsAsync(projectIdOrSlug, minecraftVersion, loaderName, cancellationToken);
    }

    private async Task<List<ModrinthVersion>> GetCompatibleVersionsAsync(
        string projectIdOrSlug,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken)
    {
        var versionsUrl = $"{BaseUrl}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }))}";
        return await httpClient.GetFromJsonAsync<List<ModrinthVersion>>(versionsUrl, cancellationToken) ?? [];
    }

    private static List<ModrinthVersionInfo> MapVersionInfos(IEnumerable<ModrinthVersion> versions)
    {
        return versions
            .Where(version => !string.IsNullOrWhiteSpace(version.Id) && version.Files.Count > 0)
            .Select(version => new ModrinthVersionInfo
            {
                VersionId = version.Id,
                Name = string.IsNullOrWhiteSpace(version.Name) ? version.VersionNumber : version.Name,
                VersionNumber = version.VersionNumber,
                IsStable = string.Equals(version.VersionType, "release", StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private async Task<string> InstallVersionFileAsync(
        string projectId,
        string projectTitle,
        ModrinthVersion version,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 优先 primary 文件并使用唯一目标路径；下载完成后才返回本地文件名供列表刷新。
        if (version.Files.Count == 0)
            throw new NoCompatibleModFileException(projectId, instance.MinecraftVersion, instance.Loader);

        var file = version.Files.FirstOrDefault(f => f.IsPrimary) ?? version.Files[0];
        var modsDirectory = Path.Combine(instance.InstanceDirectory, "mods");
        var fileName = ValidateFileName(file.FileName);
        var target = MinecraftPathGuard.EnsureSafeFileDestination(
            Path.Combine(modsDirectory, fileName),
            modsDirectory,
            "Modrinth mod file");
        var integrity = CreateIntegrityExpectation(file);
        var settings = settingsService is null
            ? null
            : await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(
                settings?.DownloadSpeedLimitMbPerSecond ?? 0,
            downloadSpeedLimitState),
            limiter,
            DownloadConcurrencyCategory.Modpack,
            retryOptions: retryOptions);

        progress?.Report(new LauncherProgress(ModProgressStages.DownloadingFile, $"{projectTitle} {version.VersionNumber}"));
        var logScope = new ForegroundDownloadLogScope(
            logger,
            "ResourceInstall",
            fileName,
            target,
            file.Url,
            integrity.ExpectedSize);
        try
        {
            var resolution = await executor.DownloadFileAsync(
                file.Url,
                settings?.DownloadSourcePreference ?? LauncherDefaults.DefaultDownloadSourcePreference,
                "ThirdParty",
                target,
                integrity,
                cancellationToken,
                reportAttemptProgress: logScope.BeginSource(),
                options: new DownloadFileOptions(
                    DownloadPersistenceMode.TaskScopedResumable,
                    OperationContext: null,
                    ManagedRoot: modsDirectory),
                speedMeter: SpeedMeterProgress.TryGet(progress),
                reportTransferredBytes: logScope.ReportTransferredBytes)
            .ConfigureAwait(false);
            logScope.Complete(resolution);
        }
        catch (OperationCanceledException)
        {
            logScope.CompleteWithoutDownload("Canceled", file.Url);
            throw;
        }
        catch (Exception exception)
        {
            logScope.Fail(exception, file.Url);
            throw;
        }
        logger.LogDebug(
            "Modrinth project installed. ProjectId={ProjectId} VersionId={VersionId} VersionNumber={VersionNumber} FileName={FileName} Target={Target}",
            projectId,
            version.Id,
            version.VersionNumber,
            file.FileName,
            target);
        return target;
    }

    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException("Modrinth returned an unsafe file name.");
        }
        return fileName;
    }

    private static DownloadIntegrityExpectation CreateIntegrityExpectation(ModrinthFile file)
    {
        if (file.Size is null or < 0)
            throw new InvalidDataException("Modrinth returned an invalid file size.");
        if (file.Hashes is null || !IsHexHash(file.Hashes.Sha512, 128))
            throw new InvalidDataException("Modrinth returned no valid SHA-512 for the selected file.");

        var hashes = new List<(HashAlgorithmName Algorithm, string Value)>
        {
            (HashAlgorithmName.SHA512, file.Hashes.Sha512)
        };
        if (!string.IsNullOrWhiteSpace(file.Hashes.Sha1))
        {
            if (!IsHexHash(file.Hashes.Sha1, 40))
                throw new InvalidDataException("Modrinth returned an invalid SHA-1 for the selected file.");
            hashes.Add((HashAlgorithmName.SHA1, file.Hashes.Sha1));
        }
        return new DownloadIntegrityExpectation(file.Size.Value, hashes);
    }

    private static bool IsHexHash(string? value, int length) =>
        value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}
