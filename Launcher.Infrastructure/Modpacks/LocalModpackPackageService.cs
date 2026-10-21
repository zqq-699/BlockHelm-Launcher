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
using System.IO.Compression;
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// 识别并准备 Modrinth/CurseForge 本地整合包，委托文件解析、覆盖复制和工作区清理。
/// </summary>
public sealed class LocalModpackPackageService : IModpackPackageService
{
    private readonly ModpackFileResolutionService fileResolutionService;
    private readonly ILogger<LocalModpackPackageService> logger;

    public LocalModpackPackageService(
        LauncherPathProvider pathProvider,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        IImportConcurrencyLimiter? limiter = null,
        HttpClient? httpClient = null,
        CurseForgeApiClient? curseForgeApiClient = null,
        ICurseForgeApiKeyResolver? curseForgeApiKeyResolver = null,
        ILogger<LocalModpackPackageService>? logger = null,
        ISettingsService? settingsService = null)
    {
        this.logger = logger ?? NullLogger<LocalModpackPackageService>.Instance;
        var resolvedLimiter = limiter ?? ImportConcurrencyLimiter.Shared;
        var resolvedHttpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        var resolvedApiClient = curseForgeApiClient ?? new CurseForgeApiClient(resolvedHttpClient, resolvedLimiter);
        var resolvedApiKeyResolver = curseForgeApiKeyResolver
            ?? new CurseForgeApiKeyResolver(pathProvider, settingsService);
        var downloader = new ModpackFileDownloader(
            resolvedHttpClient,
            downloadSpeedLimitState,
            resolvedLimiter,
            this.logger);
        fileResolutionService = new ModpackFileResolutionService(
            resolvedApiClient,
            resolvedApiKeyResolver,
            downloader,
            this.logger);
    }

    /// <summary>
    /// 只读取识别所需元数据判断归档类型和有效性，不创建长期工作目录。
    /// </summary>
    public async Task<ModpackRecognitionResult> RecognizeAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(archivePath);
        if (!File.Exists(normalizedPath))
            return ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.FileNotFound);
        try
        {
            return Path.GetExtension(normalizedPath).ToLowerInvariant() switch
            {
                ".mrpack" => await RecognizeModrinthAsync(normalizedPath, cancellationToken).ConfigureAwait(false),
                ".zip" => await RecognizeZipAsync(normalizedPath, cancellationToken).ConfigureAwait(false),
                _ => ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnsupportedArchive)
            };
        }
        catch (ModpackImportException exception)
        {
            return ModpackRecognitionResult.Failure(MapRecognitionFailureReason(exception.FailureReason));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected modpack archive recognition failure. ArchivePath={ArchivePath}", normalizedPath);
            return ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnexpectedError);
        }
    }

    /// <summary>
    /// 解析受支持归档并展开到独立工作区，返回后续导入阶段所需的统一模型。
    /// </summary>
    public async Task<PreparedModpack> PrepareAsync(
        string archivePath,
        CancellationToken cancellationToken = default,
        IProgress<LauncherProgress>? progress = null)
    {
        return await PrepareAsync(
            archivePath,
            ModpackInstallEnvironment.Client,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    public async Task<PreparedModpack> PrepareAsync(
        string archivePath,
        ModpackInstallEnvironment environment,
        CancellationToken cancellationToken = default,
        IProgress<LauncherProgress>? progress = null)
    {
        var normalizedPath = Path.GetFullPath(archivePath);
        if (!File.Exists(normalizedPath))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.FileNotFound,
                $"Modpack archive does not exist: {normalizedPath}");
        }
        logger.LogDebug("Preparing local modpack archive. ArchivePath={ArchivePath}", normalizedPath);
        return Path.GetExtension(normalizedPath).ToLowerInvariant() switch
        {
            ".mrpack" => await PrepareModrinthAsync(normalizedPath, environment, cancellationToken).ConfigureAwait(false),
            ".zip" => await PrepareZipAsync(normalizedPath, environment, cancellationToken, progress).ConfigureAwait(false),
            _ => throw new ModpackImportException(
                ModpackImportFailureReason.UnsupportedArchive,
                $"Unsupported modpack archive type: {normalizedPath}")
        };
    }

    public Task<IReadOnlyList<ManualModpackDownload>> DownloadFilesAsync(
        PreparedModpack preparedModpack,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(preparedModpack);
        ArgumentNullException.ThrowIfNull(instance);
        return fileResolutionService.DownloadFilesAsync(
            preparedModpack,
            instance,
            progress,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond,
            cancellationToken);
    }

    /// <summary>
    /// 将整合包 overrides 安全复制到实例目录；没有 overrides 时直接完成。
    /// </summary>
    public Task CopyOverridesAsync(
        PreparedModpack preparedModpack,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedModpack);
        ArgumentNullException.ThrowIfNull(instance);
        if (!preparedModpack.HasOverrides)
            return Task.CompletedTask;
        progress?.Report(new LauncherProgress(ImportProgressStages.CopyingOverrides, string.Empty, 100));
        return ModpackOverrideExtractor.CopyOverridesAsync(
            preparedModpack,
            instance.InstanceDirectory,
            cancellationToken);
    }

    public Task<string?> WriteManualDownloadsFileAsync(
        PreparedModpack preparedModpack,
        GameInstance instance,
        IReadOnlyList<ManualModpackDownload> manualDownloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedModpack);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(manualDownloads);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(manualDownloads.Count > 0
            ? WriteManualDownloadsFile(instance, preparedModpack, manualDownloads)
            : null);
    }

    /// <summary>
    /// 按“下载文件、复制 overrides、写手动清单”的顺序安装整合包内容。
    /// </summary>
    public async Task InstallContentAsync(
        PreparedModpack preparedModpack,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        var manualDownloads = await DownloadFilesAsync(
            preparedModpack,
            instance,
            progress,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond).ConfigureAwait(false);
        await CopyOverridesAsync(preparedModpack, instance, progress, cancellationToken).ConfigureAwait(false);
        preparedModpack.ManualDownloads = manualDownloads;
        preparedModpack.ManualDownloadsFilePath = await WriteManualDownloadsFileAsync(
            preparedModpack,
            instance,
            manualDownloads,
            cancellationToken).ConfigureAwait(false);
    }

    public Task CleanupAsync(PreparedModpack preparedModpack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedModpack);
        if (string.IsNullOrWhiteSpace(preparedModpack.WorkingDirectory))
            return Task.CompletedTask;
        // WorkingDirectory 只包含本次准备阶段展开的临时内容，不触碰已经提交的实例目录。
        return Task.Run(() => TryDeleteDirectory(preparedModpack.WorkingDirectory), cancellationToken);
    }

    private async Task<ModpackRecognitionResult> RecognizeModrinthAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        await ModrinthModpackFormatReader.ValidateAsync(archive, cancellationToken).ConfigureAwait(false);
        return ModpackRecognitionResult.Success();
    }

    private async Task<ModpackRecognitionResult> RecognizeZipAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (CurseForgeModpackFormatReader.HasManifest(archive))
        {
            await CurseForgeModpackFormatReader.ValidateAsync(archive, cancellationToken).ConfigureAwait(false);
            return ModpackRecognitionResult.Success();
        }
        if (ModrinthModpackFormatReader.HasManifest(archive))
        {
            await ModrinthModpackFormatReader.ValidateAsync(archive, cancellationToken).ConfigureAwait(false);
            return ModpackRecognitionResult.Success();
        }
        var embedded = FindEmbeddedModrinthEntries(archive);
        if (embedded.Count == 1)
        {
            await ValidateEmbeddedModrinthAsync(embedded[0], cancellationToken).ConfigureAwait(false);
            return ModpackRecognitionResult.Success();
        }
        if (embedded.Count > 1)
            throw MultipleEmbeddedModrinthArchives();
        return ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.InvalidManifest);
    }

    private async Task<PreparedModpack> PrepareModrinthAsync(
        string archivePath,
        ModpackInstallEnvironment environment,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return await ModrinthModpackFormatReader.ReadAsync(
            archive,
            archivePath,
            embeddedEntryName: null,
            environment,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreparedModpack> PrepareZipAsync(
        string archivePath,
        ModpackInstallEnvironment environment,
        CancellationToken cancellationToken,
        IProgress<LauncherProgress>? progress)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (CurseForgeModpackFormatReader.HasManifest(archive))
        {
            return await CurseForgeModpackFormatReader.ReadAsync(
                archive,
                archivePath,
                cancellationToken).ConfigureAwait(false);
        }
        if (ModrinthModpackFormatReader.HasManifest(archive))
        {
            return await ModrinthModpackFormatReader.ReadAsync(
                archive,
                archivePath,
                embeddedEntryName: null,
                environment,
                cancellationToken).ConfigureAwait(false);
        }
        var embedded = FindEmbeddedModrinthEntries(archive);
        if (embedded.Count == 1)
        {
            logger.LogWarning(
                "Falling back to embedded Modrinth archive inside zip wrapper. ArchivePath={ArchivePath} EmbeddedEntry={EmbeddedEntry}",
                archivePath,
                embedded[0].FullName);
            return await PrepareEmbeddedModrinthAsync(embedded[0], archivePath, environment, cancellationToken)
                .ConfigureAwait(false);
        }
        if (embedded.Count > 1)
            throw MultipleEmbeddedModrinthArchives();
        throw new ModpackImportException(
            ModpackImportFailureReason.InvalidManifest,
            "manifest.json was not found.");
    }

    private static List<ZipArchiveEntry> FindEmbeddedModrinthEntries(ZipArchive archive) =>
        archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                && entry.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// 从 CurseForge ZIP 中提取唯一嵌入的 mrpack，并把两层包信息合并为统一准备结果。
    /// </summary>
    private async Task<PreparedModpack> PrepareEmbeddedModrinthAsync(
        ZipArchiveEntry entry,
        string sourceArchivePath,
        ModpackInstallEnvironment environment,
        CancellationToken cancellationToken)
    {
        await using var stream = await ModpackArchiveUtility.CopyZipEntryToMemoryAsync(
            entry,
            ModpackArchiveUtility.MaxEmbeddedModpackBytes,
            cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return await ModrinthModpackFormatReader.ReadAsync(
            archive,
            sourceArchivePath,
            entry.FullName,
            environment,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateEmbeddedModrinthAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = await ModpackArchiveUtility.CopyZipEntryToMemoryAsync(
            entry,
            ModpackArchiveUtility.MaxEmbeddedModpackBytes,
            cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        await ModrinthModpackFormatReader.ValidateAsync(archive, cancellationToken).ConfigureAwait(false);
    }

    private string WriteManualDownloadsFile(
        GameInstance instance,
        PreparedModpack preparedModpack,
        IReadOnlyList<ManualModpackDownload> manualDownloads)
    {
        var filePath = Path.Combine(instance.InstanceDirectory, ModpackManualDownloads.FileName);
        Directory.CreateDirectory(instance.InstanceDirectory);
        using var writer = new StreamWriter(filePath, append: false);
        writer.WriteLine($"instance={instance.Name}");
        writer.WriteLine($"package={preparedModpack.PackageName}");
        writer.WriteLine($"generatedAt={DateTimeOffset.Now:O}");
        writer.WriteLine();
        foreach (var download in manualDownloads)
        {
            writer.WriteLine($"fileName={download.FileName}");
            writer.WriteLine($"displayName={download.DisplayName}");
            writer.WriteLine($"projectId={download.ProjectId}");
            writer.WriteLine($"fileId={download.FileId}");
            writer.WriteLine($"suggestedUrl={download.SuggestedUrl}");
            writer.WriteLine($"failure={download.FailureSummary}");
            writer.WriteLine();
        }
        logger.LogInformation(
            "Wrote modpack manual downloads file. InstanceId={InstanceId} ManualDownloadCount={ManualDownloadCount}",
            instance.Id,
            manualDownloads.Count);
        logger.LogDebug(
            "Modpack manual downloads file path resolved. InstanceId={InstanceId} InstanceDirectory={InstanceDirectory} FilePath={FilePath}",
            instance.Id,
            instance.InstanceDirectory,
            filePath);
        return filePath;
    }

    private static ModpackImportException MultipleEmbeddedModrinthArchives() => new(
        ModpackImportFailureReason.InvalidManifest,
        "Multiple embedded .mrpack files were found.");

    private static ModpackRecognitionFailureReason MapRecognitionFailureReason(ModpackImportFailureReason reason) => reason switch
    {
        ModpackImportFailureReason.FileNotFound => ModpackRecognitionFailureReason.FileNotFound,
        ModpackImportFailureReason.UnsupportedArchive => ModpackRecognitionFailureReason.UnsupportedArchive,
        ModpackImportFailureReason.InvalidManifest => ModpackRecognitionFailureReason.InvalidManifest,
        ModpackImportFailureReason.UnsupportedLoader => ModpackRecognitionFailureReason.UnsupportedLoader,
        _ => ModpackRecognitionFailureReason.UnexpectedError
    };

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
