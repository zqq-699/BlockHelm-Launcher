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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using System.Xml.Linq;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// 在隔离沙箱中安装 NeoForge，并通过元数据匹配、版本扁平化和修复生成可独立启动的版本。
/// </summary>
public sealed class NeoForgeLoaderProvider : ILoaderProvider, IStagedLoaderProvider, IDirectSharedContentStagedLoaderProvider
{
    private readonly HttpClient httpClient;
    private readonly IForgeInstallerRunner installerRunner;
    private readonly IFinalVersionInstaller finalVersionInstaller;
    private readonly ILoaderInstallerJavaRuntimeResolver? javaRuntimeResolver;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;
    private readonly string tempRootDirectory;

    public NeoForgeLoaderProvider(
        HttpClient? httpClient = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<NeoForgeLoaderProvider>? logger = null)
        : this(httpClient, installerRunner: null, finalVersionInstaller: null, tempRootDirectory: null, downloadSpeedLimitState, logger, javaRuntimeResolver: null)
    {
    }

    internal NeoForgeLoaderProvider(
        HttpClient? httpClient,
        IForgeInstallerRunner? installerRunner,
        string? tempRootDirectory,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null)
        : this(httpClient, installerRunner, finalVersionInstaller: null, tempRootDirectory, downloadSpeedLimitState, logger: null, javaRuntimeResolver: null)
    {
    }

    internal NeoForgeLoaderProvider(
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

    public LoaderKind Kind => LoaderKind.NeoForge;

    public bool IsImplemented => true;

    /// <summary>
    /// 查询与 Minecraft 版本前缀匹配的 NeoForge 构件，并按语义化数字部分排序。
    /// </summary>
    public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        CancellationToken cancellationToken = default,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        if (!NeoForgeArtifactResolver.TryResolveCatalog(minecraftVersion, out var catalog))
        {
            logger.LogDebug(
                "Skipping NeoForge version lookup because the Minecraft version is unsupported. MinecraftVersion={MinecraftVersion}",
                minecraftVersion);
            return [];
        }

        logger.LogDebug(
            "Loading NeoForge versions. MinecraftVersion={MinecraftVersion} Prefix={VersionPrefix}",
            minecraftVersion,
            catalog.VersionPrefix);

        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Metadata);
        var result = await executor.ExecuteLookupAsync(
            catalog.MetadataUrl,
            downloadSourcePreference,
            categoryHint: "NeoForge",
            async (context, token) =>
            {
                await using var stream = await context.Response.Content.ReadAsStreamAsync(token);
                var document = await XDocument.LoadAsync(stream, LoadOptions.None, token);
                if (document.Root?.Name.LocalName is not "metadata"
                    || document.Descendants("versions").FirstOrDefault() is null)
                {
                    throw new DownloadContentValidationException(
                        "NeoForge Maven metadata has an invalid structure.");
                }

                var versions = document.Descendants("version")
                    .Select(element => element.Value?.Trim())
                    .Where(version => !string.IsNullOrWhiteSpace(version))
                    .Select(version => version!)
                    .Where(catalog.Matches)
                    .Select(catalog.NormalizeVersion)
                    .Select(CreateLoaderVersionInfo)
                    .OrderByDescending(info => info.IsStable)
                    .ThenByDescending(info => ParseVersionKey(info.Version), NeoForgeVersionKeyComparer.Instance)
                    .ThenByDescending(info => info.Version, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (versions.Count == 0)
                    throw new DownloadNoResultException("NeoForge returned no matching loader versions.");

                return (IReadOnlyList<LoaderVersionInfo>)versions;
            },
            statusCode => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            cancellationToken);

        var resolvedVersions = result.Found ? result.Value! : [];
        logger.LogDebug(
            "Loaded NeoForge versions. MinecraftVersion={MinecraftVersion} Count={Count}",
            minecraftVersion,
            resolvedVersions.Count);
        return resolvedVersions;
    }

    /// <summary>
    /// 在临时 .minecraft 中安装 NeoForge，修复最终版本后只提交必要文件。
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

    private async Task<string> InstallCoreAsync(
        string minecraftVersion,
        string gameDirectory,
        string sharedMinecraftDirectory,
        string isolatedVersionName,
        string? loaderVersion,
        IProgress<LauncherProgress>? progress,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond)
    {
        progress?.Report(new LauncherProgress(InstallProgressStages.Preparing, string.Empty));

        var selectedLoaderVersion = loaderVersion;
        if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
        {
            var availableVersions = await GetLoaderVersionsAsync(
                minecraftVersion,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond);
            selectedLoaderVersion = availableVersions.FirstOrDefault()?.Version;
        }

        if (string.IsNullOrWhiteSpace(selectedLoaderVersion))
            throw new InvalidOperationException($"No NeoForge loader version available for {minecraftVersion}.");
        var installerArtifact = NeoForgeArtifactResolver.ResolveInstaller(
            minecraftVersion,
            selectedLoaderVersion);
        selectedLoaderVersion = installerArtifact.LoaderVersion;

        logger.LogInformation(
            "Installing NeoForge. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion} TargetVersionName={TargetVersionName}",
            minecraftVersion,
            selectedLoaderVersion,
            isolatedVersionName);
        var speedMeter = SpeedMeterProgress.TryGet(progress);

        // 记录目标目录原有版本，清理阶段只移除本次安装引入的中间目录。
        var existingVersionNames = LoaderVersionDirectoryTransaction.CaptureExistingVersions(gameDirectory);
        var installerSessionDirectory = Path.Combine(tempRootDirectory, "launcher-neoforge", Guid.NewGuid().ToString("N"));
        var installerJarPath = Path.Combine(installerSessionDirectory, $"neoforge-{selectedLoaderVersion}-installer.jar");
        var installerMinecraftDirectory = Path.Combine(installerSessionDirectory, ".minecraft");
        var installPathLayout = MinecraftInstallPathLayout.Create(
            installerMinecraftDirectory,
            sharedMinecraftDirectory);
        using var downloadOperation = VanillaLoaderProvider.CreateDownloadOperationContext(installPathLayout.Path);
        Directory.CreateDirectory(installerSessionDirectory);

        try
        {
            LoaderVersionDirectoryTransaction.EnsureLauncherProfileExists(installerMinecraftDirectory);

            progress?.Report(new LauncherProgress(InstallProgressStages.DownloadingLoaderInstaller, string.Empty));
            await DownloadInstallerAsync(
                installerArtifact,
                installerJarPath,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond,
                speedMeter);

            var installerArtifactService = new LoaderInstallerArtifactService(
                httpClient,
                installerRunner,
                finalVersionInstaller,
                downloadSpeedLimitState,
                logger,
                tempRootDirectory);
            var installerPlan = await installerArtifactService.ReadPlanAsync(installerJarPath, cancellationToken)
                .ConfigureAwait(false);

            var prerequisiteSeeder = new LoaderInstallerPrerequisiteSeeder(
                httpClient,
                downloadSpeedLimitState,
                logger);
            var workspaceSnapshot = await prerequisiteSeeder.SeedAsync(
                sharedMinecraftDirectory,
                installerMinecraftDirectory,
                minecraftVersion,
                installerJarPath,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                cancellationToken,
                loaderCategory: "NeoForge").ConfigureAwait(false);
            await installerArtifactService.MaterializePrerequisitesAsync(
                installerJarPath,
                installerPlan,
                installerMinecraftDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                cancellationToken,
                downloadOperation,
                speedMeter).ConfigureAwait(false);

            progress?.Report(new LauncherProgress(InstallProgressStages.CheckingJava, string.Empty));
            var resolver = javaRuntimeResolver
                ?? throw new InvalidOperationException("The loader installer Java runtime resolver is not configured.");
            var javaRuntime = await resolver
                .ResolveAsync(
                    new LoaderInstallerJavaRuntimeRequest(
                        minecraftVersion,
                        isolatedVersionName,
                        LoaderKind.NeoForge,
                        selectedLoaderVersion,
                        sharedMinecraftDirectory,
                        downloadSourcePreference,
                        downloadSpeedLimitMbPerSecond,
                        progress),
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new LauncherProgress(InstallProgressStages.RunningLoaderInstaller, string.Empty));
            await installerRunner.RunInstallerAsync(
                javaRuntime.ExecutablePath,
                installerJarPath,
                installerMinecraftDirectory,
                cancellationToken);

            await installerArtifactService.MaterializeRuntimeLibrariesAsync(
                installerJarPath,
                installerPlan,
                installerMinecraftDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                cancellationToken,
                downloadOperation,
                speedMeter).ConfigureAwait(false);

            await installerArtifactService.ValidatePublishedArtifactsAsync(
                installerMinecraftDirectory,
                installerPlan,
                cancellationToken).ConfigureAwait(false);

            var sourceVersionName = FindInstalledSourceVersionName(
                installerMinecraftDirectory,
                installerArtifact,
                existingVersionNames);

            progress?.Report(new LauncherProgress(InstallProgressStages.FinalizingVersion, string.Empty));
            var finalVersionName = await CreateFinalVersionAsync(
                installerMinecraftDirectory,
                sourceVersionName,
                isolatedVersionName,
                minecraftVersion,
                cancellationToken);

            await EnsureFinalVersionIsSelfContainedAsync(
                installerMinecraftDirectory,
                finalVersionName,
                downloadSourcePreference,
                cancellationToken,
                downloadSpeedLimitMbPerSecond);
            await LoaderInstallerArtifactService.ApplyRuntimeLibrariesAsync(
                Path.Combine(installerMinecraftDirectory, "versions", finalVersionName, $"{finalVersionName}.json"),
                installerPlan,
                "neoForgeProcessorArtifacts",
                cancellationToken).ConfigureAwait(false);

            await prerequisiteSeeder.PublishDeltaAsync(
                workspaceSnapshot,
                sharedMinecraftDirectory,
                cancellationToken,
                LoaderInstallerArtifactService.CreateTrustedSharedLibraryExpectations(installerPlan),
                downloadOperation,
                LoaderInstallerArtifactService.CreateReplaceableProcessorOutputExpectations(installerPlan)).ConfigureAwait(false);

            progress?.Report(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty));
            await finalVersionInstaller.InstallAsync(
                installPathLayout.Path,
                finalVersionName,
                downloadOperation,
                downloadSourcePreference,
                progress,
                cancellationToken,
                downloadSpeedLimitMbPerSecond,
                speedMeter);
            await installerArtifactService.MaterializeRuntimeLibrariesAsync(
                installerJarPath,
                installerPlan,
                sharedMinecraftDirectory,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond,
                cancellationToken,
                downloadOperation,
                speedMeter).ConfigureAwait(false);
            await installerArtifactService.ValidatePublishedArtifactsAsync(
                sharedMinecraftDirectory,
                installerPlan,
                cancellationToken,
                downloadOperation).ConfigureAwait(false);
            await LoaderArtifactManifestStore.WriteAsync(
                Path.Combine(installerMinecraftDirectory, "versions", finalVersionName),
                sharedMinecraftDirectory,
                new GameFileLoaderIdentity(LoaderKind.NeoForge, minecraftVersion, selectedLoaderVersion),
                installerJarPath,
                installerPlan,
                cancellationToken).ConfigureAwait(false);

            LoaderVersionDirectoryTransaction.CopyFinalVersionDirectory(
                installerMinecraftDirectory,
                gameDirectory,
                finalVersionName,
                cancellationToken);
            await installerArtifactService.ValidatePublishedArtifactsAsync(
                sharedMinecraftDirectory,
                installerPlan,
                cancellationToken,
                downloadOperation).ConfigureAwait(false);

            LoaderVersionDirectoryTransaction.CleanupCreatedVersionDirectories(gameDirectory, existingVersionNames, finalVersionName);
            logger.LogInformation(
                "NeoForge installation completed. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion} FinalVersionName={FinalVersionName}",
                minecraftVersion,
                selectedLoaderVersion,
                finalVersionName);
            return finalVersionName;
        }
        catch
        {
            LoaderVersionDirectoryTransaction.CleanupCreatedVersionDirectories(gameDirectory, existingVersionNames, preserveVersionName: null);
            throw;
        }
        finally
        {
            LoaderVersionDirectoryTransaction.TryDeleteDirectory(installerSessionDirectory);
        }
    }

    private async Task DownloadInstallerAsync(
        NeoForgeInstallerArtifact artifact,
        string destinationPath,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond,
        SpeedMeter? speedMeter = null)
    {
        var expectedSha1 = await LoaderInstallerChecksumResolver.ResolveRequiredSha1Async(
            httpClient,
            downloadSpeedLimitState,
            logger,
            artifact.Url,
            downloadSourcePreference,
            "NeoForge",
            downloadSpeedLimitMbPerSecond,
            cancellationToken).ConfigureAwait(false);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Runtime);
        var sourceUrl = artifact.Url;
        var logScope = new ForegroundDownloadLogScope(
            logger,
            "NeoForgeInstall",
            Path.GetFileName(destinationPath),
            destinationPath,
            sourceUrl);
        try
        {
            var resolution = await executor.DownloadFileAsync(
            sourceUrl,
            downloadSourcePreference,
            categoryHint: "NeoForge",
            destinationPath,
            expectedSha1,
            expectedSize: null,
            cancellationToken,
            reportAttemptProgress: logScope.BeginSource(),
            speedMeter: speedMeter,
            reportTransferredBytes: logScope.ReportTransferredBytes);
            logScope.Complete(resolution);
        }
        catch (OperationCanceledException)
        {
            logScope.CompleteWithoutDownload("Canceled", sourceUrl);
            throw;
        }
        catch (Exception exception)
        {
            logScope.Fail(exception, sourceUrl);
            throw;
        }
    }

    private async Task EnsureFinalVersionIsSelfContainedAsync(
        string gameDirectory,
        string finalVersionName,
        DownloadSourcePreference downloadSourcePreference,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond = 0)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", finalVersionName);
        var repairService = new ManagedVersionRepairService(httpClient, downloadSpeedLimitState, logger);
        await repairService.FinalizePreparedVersionAsync(
            gameDirectory,
            finalVersionName,
            versionDirectory,
            downloadSourcePreference,
            cancellationToken,
            downloadSpeedLimitMbPerSecond: downloadSpeedLimitMbPerSecond);
    }

    private static LoaderVersionInfo CreateLoaderVersionInfo(string version)
    {
        return new LoaderVersionInfo(version, IsStableVersion(version));
    }

    private static bool IsStableVersion(string version)
    {
        return version.IndexOf("-beta", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("-alpha", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("+snapshot", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("+pre", StringComparison.OrdinalIgnoreCase) < 0
               && version.IndexOf("+rc", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static NeoForgeVersionKey ParseVersionKey(string version)
    {
        var numericPart = version;
        var suffixIndex = version.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
            numericPart = version[..suffixIndex];

        var values = numericPart
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();
        return new NeoForgeVersionKey(values);
    }

    /// <summary>
    /// 通过精确库坐标、预期版本身份和受控文本特征识别安装器输出版本。
    /// </summary>
    private static string FindInstalledSourceVersionName(
        string gameDirectory,
        NeoForgeInstallerArtifact artifact,
        HashSet<string> existingVersionNames)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
            throw new InvalidOperationException("NeoForge installation did not create a version directory.");

        var candidates = Directory.GetDirectories(versionsDirectory)
            .Select(directory => new DirectoryInfo(directory))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();

        // 安装器输出名称会随版本变化，先限定新目录，再按库坐标和版本身份验证候选。
        var sourceVersion = candidates
            .Where(directory => !existingVersionNames.Contains(directory.Name))
            .Select(directory => TryCreateSourceMatch(directory.FullName, directory.Name, artifact))
            .FirstOrDefault(match => match is not null)
            ?? candidates
                .Select(directory => TryCreateSourceMatch(directory.FullName, directory.Name, artifact))
                .FirstOrDefault(match => match is not null);

        return sourceVersion?.VersionName
            ?? throw new InvalidOperationException(
                $"NeoForge installer did not produce a usable version for {artifact.LoaderVersion}.");
    }

    private static NeoForgeSourceMatch? TryCreateSourceMatch(
        string versionDirectory,
        string versionName,
        NeoForgeInstallerArtifact artifact)
    {
        var metadata = TryReadVersionMetadata(versionDirectory, versionName);
        if (metadata is null)
            return null;

        var hasExactRuntimeLibrary = metadata.LibraryNames.Any(library =>
            string.Equals(library, artifact.RuntimeLibraryCoordinate, StringComparison.OrdinalIgnoreCase)
            || library.StartsWith(
                artifact.RuntimeLibraryCoordinate + ":",
                StringComparison.OrdinalIgnoreCase));
        var hasExpectedVersionId =
            string.Equals(metadata.Id, artifact.ExpectedVersionId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.InheritsFrom, artifact.ExpectedVersionId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(metadata.Jar, artifact.ExpectedVersionId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(versionName, artifact.ExpectedVersionId, StringComparison.OrdinalIgnoreCase);

        if (!hasExactRuntimeLibrary && !hasExpectedVersionId)
            return null;

        return new NeoForgeSourceMatch(versionName, metadata);
    }

    /// <summary>
    /// 将 NeoForge 输出转换为指定名称的自包含隔离版本，并写入 Launcher 元数据。
    /// </summary>
    private static async Task<string> CreateFinalVersionAsync(
        string gameDirectory,
        string sourceVersionName,
        string isolatedVersionName,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.Combine(gameDirectory, "versions", sourceVersionName);
        var metadata = TryReadVersionMetadata(sourceDirectory, sourceVersionName)
            ?? throw new InvalidOperationException($"NeoForge version metadata is missing for {sourceVersionName}.");

        string finalVersionName;
        if (!string.IsNullOrWhiteSpace(metadata.InheritsFrom))
        {
            try
            {
                // 派生版本优先与父版本合并为自包含 JSON，父文件缺失时才使用源目录隔离兜底。
                finalVersionName = await VanillaVersionIsolator.CreateFlattenedDerivedVersionAsync(
                    metadata.InheritsFrom,
                    sourceVersionName,
                    isolatedVersionName,
                    gameDirectory,
                    cancellationToken);
                await LoaderVersionDirectoryTransaction.WriteLauncherMetadataAsync(
                    gameDirectory,
                    finalVersionName,
                    minecraftVersion,
                    cancellationToken);
                return finalVersionName;
            }
            catch (FileNotFoundException)
            {
            }
        }

        finalVersionName = await VanillaVersionIsolator.CreateIsolatedVersionFromSourceAsync(
            sourceVersionName,
            isolatedVersionName,
            gameDirectory,
            cancellationToken: cancellationToken);
        await LoaderVersionDirectoryTransaction.WriteLauncherMetadataAsync(
            gameDirectory,
            finalVersionName,
            minecraftVersion,
            cancellationToken);
        return finalVersionName;
    }

    private static VersionJsonMetadata? TryReadVersionMetadata(string versionDirectory, string versionName)
    {
        var versionJsonPath = Path.Combine(versionDirectory, $"{versionName}.json");
        if (!File.Exists(versionJsonPath))
            return null;

        try
        {
            using var stream = File.OpenRead(versionJsonPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return new VersionJsonMetadata(
                GetStringProperty(root, "id"),
                GetStringProperty(root, "inheritsFrom"),
                GetStringProperty(root, "jar"),
                ReadLibraryNames(root));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadLibraryNames(JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var library in libraries.EnumerateArray())
        {
            var name = GetStringProperty(library, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    private static string GetStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
               && property.ValueKind is JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed record NeoForgeSourceMatch(string VersionName, VersionJsonMetadata Metadata);

    private sealed record VersionJsonMetadata(
        string Id,
        string InheritsFrom,
        string Jar,
        IReadOnlyList<string> LibraryNames);

    private readonly record struct NeoForgeVersionKey(int[] Parts);

    private sealed class NeoForgeVersionKeyComparer : IComparer<NeoForgeVersionKey>
    {
        public static NeoForgeVersionKeyComparer Instance { get; } = new();

        public int Compare(NeoForgeVersionKey left, NeoForgeVersionKey right)
        {
            var length = Math.Max(left.Parts.Length, right.Parts.Length);
            for (var index = 0; index < length; index++)
            {
                var leftValue = index < left.Parts.Length ? left.Parts[index] : 0;
                var rightValue = index < right.Parts.Length ? right.Parts[index] : 0;
                var comparison = leftValue.CompareTo(rightValue);
                if (comparison != 0)
                    return comparison;
            }

            return 0;
        }
    }
}
