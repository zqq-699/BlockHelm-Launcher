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
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Application.Services;

/// <summary>
/// 以可清理的导入会话编排整合包准备、实例暂存、游戏下载、内容下载和最终提交。
/// </summary>
public sealed class LocalModpackImportService : ILocalModpackImportService
{
    private readonly IModpackPackageService modpackPackageService;
    private readonly IModpackGameInstaller modpackGameInstaller;
    private readonly IModpackInstanceStagingService stagingService;
    private readonly IGameInstallCoordinator installCoordinator;
    private readonly ModpackImportCleanupCoordinator cleanupCoordinator;
    private readonly ILogger<LocalModpackImportService> logger;

    public LocalModpackImportService(
        IGameInstanceService instanceService,
        IModpackPackageService modpackPackageService,
        IModpackGameInstaller modpackGameInstaller,
        IModpackInstanceStagingService stagingService,
        ILogger<LocalModpackImportService>? logger = null)
        : this(
            instanceService,
            modpackPackageService,
            modpackGameInstaller,
            stagingService,
            new GameInstallCoordinator(),
            logger)
    {
    }

    public LocalModpackImportService(
        IGameInstanceService instanceService,
        IModpackPackageService modpackPackageService,
        IModpackGameInstaller modpackGameInstaller,
        IModpackInstanceStagingService stagingService,
        IGameInstallCoordinator installCoordinator,
        ILogger<LocalModpackImportService>? logger = null)
    {
        this.modpackPackageService = modpackPackageService;
        this.modpackGameInstaller = modpackGameInstaller;
        this.stagingService = stagingService;
        this.installCoordinator = installCoordinator;
        this.logger = logger ?? NullLogger<LocalModpackImportService>.Instance;
        cleanupCoordinator = new ModpackImportCleanupCoordinator(
            instanceService,
            modpackPackageService,
            stagingService,
            this.logger);
    }

    public async Task<ModpackRecognitionResult> RecognizeArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await modpackPackageService
                .RecognizeAsync(archivePath, cancellationToken)
                .ConfigureAwait(false);
            logger.LogDebug(
                "Modpack archive recognition completed. ArchivePath={ArchivePath} IsSuccess={IsSuccess} FailureReason={FailureReason}",
                archivePath,
                result.IsSuccess,
                result.FailureReason);
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected modpack archive recognition failure. ArchivePath={ArchivePath}",
                archivePath);
            return ModpackRecognitionResult.Failure(ModpackRecognitionFailureReason.UnexpectedError);
        }
    }

    /// <summary>
    /// 执行完整整合包导入；已知失败返回业务结果，取消继续抛出，所有失败都会先清理会话资源。
    /// </summary>
    public async Task<ModpackImportResult> ImportFromArchiveAsync(
        string archivePath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken = default,
        DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
        int downloadSpeedLimitMbPerSecond = 0,
        Func<StagedModpackInstance, Task>? prepareInstanceAsync = null)
    {
        using var diagnosticOperation = DownloadDiagnostics.BeginOperation(logger, "ModpackImport");
        // 会话记录每个阶段已经取得的资源，使成功与任意失败点都能按实际进度执行对应清理。
        var importStopwatch = Stopwatch.StartNew();
        var importProgress = progress is null ? null : new OverallModpackImportProgress(progress);
        var session = new ModpackImportSession(importProgress);

        try
        {
            await PrepareAndStageAsync(session, archivePath, cancellationToken).ConfigureAwait(false);
            var manualDownloads = await InstallGameAndContentAsync(
                    session,
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond,
                    cancellationToken)
                .ConfigureAwait(false);
            await FinalizeImportAsync(session, manualDownloads, prepareInstanceAsync, cancellationToken).ConfigureAwait(false);

            var preparedModpack = session.PreparedModpack!;
            var importedInstance = session.ImportedInstance!;
            await cleanupCoordinator.CleanupSuccessfulImportAsync(preparedModpack).ConfigureAwait(false);

            logger.LogInformation(
                "Modpack import completed. InstanceDirectory={InstanceDirectory} InstanceName={InstanceName} ManualDownloadCount={ManualDownloadCount} DurationMs={DurationMs}",
                importedInstance.InstanceDirectory,
                importedInstance.Name,
                preparedModpack.ManualDownloads.Count,
                importStopwatch.ElapsedMilliseconds);
            logger.LogDebug("Imported modpack archive. ArchivePath={ArchivePath}", archivePath);
            return preparedModpack.ManualDownloads.Count > 0
                ? ModpackImportResult.PartialSuccess(importedInstance, preparedModpack.ManualDownloads)
                : ModpackImportResult.Success(importedInstance);
        }
        catch (ModpackImportException exception)
        {
            logger.LogError(
                exception,
                "Modpack import failed. FailureReason={FailureReason} DurationMs={DurationMs}",
                exception.FailureReason,
                importStopwatch.ElapsedMilliseconds);
            logger.LogDebug("Failed modpack archive path. ArchivePath={ArchivePath}", archivePath);
            await cleanupCoordinator.CleanupFailedImportAsync(session).ConfigureAwait(false);
            return ModpackImportResult.Failure(exception.FailureReason);
        }
        catch (JavaRuntimeSelectionException exception)
        {
            logger.LogError(
                exception,
                "Modpack import could not select a compatible Java runtime. FailureReason={FailureReason} RequiredMajorVersion={RequiredMajorVersion} CurrentMajorVersion={CurrentMajorVersion} DurationMs={DurationMs}",
                exception.Reason,
                exception.RequiredMajorVersion,
                exception.CurrentMajorVersion,
                importStopwatch.ElapsedMilliseconds);
            logger.LogDebug("Failed modpack archive path. ArchivePath={ArchivePath}", archivePath);
            await cleanupCoordinator.CleanupFailedImportAsync(session).ConfigureAwait(false);
            return ModpackImportResult.Failure(ModpackImportFailureReason.JavaRuntimeUnavailable);
        }
        catch (OperationCanceledException)
        {
            await cleanupCoordinator.CleanupFailedImportAsync(session).ConfigureAwait(false);
            logger.LogInformation("Modpack import canceled. DurationMs={DurationMs}", importStopwatch.ElapsedMilliseconds);
            logger.LogDebug("Canceled modpack archive path. ArchivePath={ArchivePath}", archivePath);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected modpack import failure. DurationMs={DurationMs}",
                importStopwatch.ElapsedMilliseconds);
            logger.LogDebug("Failed modpack archive path. ArchivePath={ArchivePath}", archivePath);
            await cleanupCoordinator.CleanupFailedImportAsync(session).ConfigureAwait(false);
            return ModpackImportResult.Failure(ModpackImportFailureReason.UnexpectedError);
        }
    }

    /// <summary>
    /// 解析归档并创建尚未持久化的暂存实例，为后续并行安装准备工作目录。
    /// </summary>
    private async Task PrepareAndStageAsync(
        ModpackImportSession session,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var prepareStopwatch = Stopwatch.StartNew();
        session.Progress?.Report(new LauncherProgress(ImportProgressStages.PreparingArchive, string.Empty));
        session.Progress?.Report(new LauncherProgress(ImportProgressStages.ParsingManifest, string.Empty));
        session.PreparedModpack = await modpackPackageService
            .PrepareAsync(archivePath, cancellationToken, session.Progress)
            .ConfigureAwait(false);
        var preferredInstanceName = NormalizePreferredInstanceName(session.PreparedModpack.PackageName);

        logger.LogInformation(
            "Modpack archive prepared. ArchivePath={ArchivePath} ModpackName={ModpackName} MinecraftVersion={MinecraftVersion} Loader={Loader} LoaderVersion={LoaderVersion} DeclaredFileCount={DeclaredFileCount} HasOverrides={HasOverrides} PreferredInstanceName={PreferredInstanceName} DurationMs={DurationMs}",
            archivePath,
            session.PreparedModpack.PackageName,
            session.PreparedModpack.MinecraftVersion,
            session.PreparedModpack.Loader,
            session.PreparedModpack.LoaderVersion,
            session.PreparedModpack.Files.Count,
            session.PreparedModpack.HasOverrides,
            preferredInstanceName,
            prepareStopwatch.ElapsedMilliseconds);
        logger.LogDebug("Importing modpack archive. ArchivePath={ArchivePath}", archivePath);

        session.Progress?.Report(new LauncherProgress(ImportProgressStages.CreatingInstance, string.Empty));
        session.StagedInstance = await stagingService
            .StageAsync(session.PreparedModpack, preferredInstanceName, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation(
            "Modpack instance staging completed. ArchivePath={ArchivePath} PreferredInstanceName={PreferredInstanceName} ResolvedInstanceName={ResolvedInstanceName} InstanceDirectory={InstanceDirectory}",
            archivePath,
            preferredInstanceName,
            session.StagedInstance.ResolvedInstanceName,
            session.StagedInstance.InstanceDirectory);
        session.Progress?.Report(new LauncherProgress(ImportProgressStages.CreatingInstance, string.Empty, 100));
    }

    /// <summary>
    /// 并行安装游戏版本和下载整合包内容，任一分支失败时联动取消另一分支。
    /// </summary>
    private async Task<IReadOnlyList<ManualModpackDownload>> InstallGameAndContentAsync(
        ModpackImportSession session,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken)
    {
        var preparedModpack = session.PreparedModpack!;
        var stagedInstance = session.StagedInstance!;
        // 游戏安装与内容下载并行执行；任一分支失败都通过链接 CTS 尽快停止另一分支。
        using var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var importCancellationToken = importCancellation.Token;
        var importProgress = session.ImportProgress;
        var packFileProgress = importProgress?.CreateParallelBranch(ModpackImportProgressBranch.PackFiles);
        var loaderProgress = importProgress?.CreateParallelBranch(ModpackImportProgressBranch.LoaderInstall);

        logger.LogInformation(
            "Modpack parallel installation branches started. InstanceName={InstanceName} ContentFileCount={ContentFileCount} Loader={Loader}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Files.Count,
            preparedModpack.Loader);

        var failureTracker = new ParallelDownloadFailureTracker(logger, cancellationToken, () => SafeCancel(importCancellation));
        var downloadModsTask = CompleteParallelBranchAsync(
            failureTracker.RunAsync("PackFiles", () => DownloadModpackFilesWithTimingAsync(
                preparedModpack,
                stagedInstance,
                packFileProgress,
                importCancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond)),
            importProgress,
            ModpackImportProgressBranch.PackFiles);

        var loaderInstallTask = CompleteParallelBranchAsync(
            failureTracker.RunAsync("LoaderInstall", () => InstallLoaderIntoStagingAsync(
                preparedModpack,
                stagedInstance,
                loaderProgress,
                importCancellationToken,
                downloadSourcePreference,
                downloadSpeedLimitMbPerSecond)),
            importProgress,
            ModpackImportProgressBranch.LoaderInstall);

        await AwaitImportBranchesAsync(importCancellation, loaderInstallTask, downloadModsTask)
            .ConfigureAwait(false);
        session.FinalVersionName = await loaderInstallTask.ConfigureAwait(false);
        return await downloadModsTask.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ManualModpackDownload>> DownloadModpackFilesWithTimingAsync(
        PreparedModpack preparedModpack,
        StagedModpackInstance stagedInstance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Modpack content download started. InstanceName={InstanceName} DeclaredFileCount={DeclaredFileCount}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Files.Count);
        var manualDownloads = await CompleteWithProgressAsync(
                modpackPackageService.DownloadFilesAsync(
                    preparedModpack,
                    stagedInstance.Instance,
                    progress,
                    cancellationToken,
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond),
                progress,
                new LauncherProgress(ImportProgressStages.ProcessingPackFiles, string.Empty, 100))
            .ConfigureAwait(false);
        logger.LogInformation(
            "Modpack content download completed. InstanceName={InstanceName} DeclaredFileCount={DeclaredFileCount} ManualDownloadCount={ManualDownloadCount} DurationMs={DurationMs}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Files.Count,
            manualDownloads.Count,
            stopwatch.ElapsedMilliseconds);
        return manualDownloads;
    }

    /// <summary>
    /// 复制 overrides、写入手动下载清单并持久化最终实例，是导入的提交阶段。
    /// </summary>
    private async Task FinalizeImportAsync(
        ModpackImportSession session,
        IReadOnlyList<ManualModpackDownload> manualDownloads,
        Func<StagedModpackInstance, Task>? prepareInstanceAsync,
        CancellationToken cancellationToken)
    {
        var preparedModpack = session.PreparedModpack!;
        var stagedInstance = session.StagedInstance!;
        var finalizeStopwatch = Stopwatch.StartNew();
        var overridesStopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Modpack overrides application started. InstanceName={InstanceName} HasOverrides={HasOverrides}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.HasOverrides);
        await CompleteWithProgressAsync(
                modpackPackageService.CopyOverridesAsync(
                    preparedModpack,
                    stagedInstance.Instance,
                    session.Progress,
                    cancellationToken),
                session.Progress,
                new LauncherProgress(ImportProgressStages.CopyingOverrides, string.Empty, 100))
            .ConfigureAwait(false);
        logger.LogInformation(
            "Modpack overrides application completed. InstanceName={InstanceName} DurationMs={DurationMs}",
            stagedInstance.ResolvedInstanceName,
            overridesStopwatch.ElapsedMilliseconds);
        preparedModpack.ManualDownloads = manualDownloads;
        preparedModpack.ManualDownloadsFilePath = await modpackPackageService
            .WriteManualDownloadsFileAsync(
                preparedModpack,
                stagedInstance.Instance,
                manualDownloads,
                cancellationToken)
            .ConfigureAwait(false);
        if (prepareInstanceAsync is not null)
            await prepareInstanceAsync(stagedInstance).ConfigureAwait(false);
        logger.LogInformation(
            "Modpack instance commit started. InstanceName={InstanceName} VersionName={VersionName} ManualDownloadCount={ManualDownloadCount}",
            stagedInstance.ResolvedInstanceName,
            session.FinalVersionName,
            manualDownloads.Count);
        session.ImportedInstance = await stagingService
            .FinalizeAsync(stagedInstance, session.FinalVersionName!, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation(
            "Modpack instance commit completed. InstanceDirectory={InstanceDirectory} InstanceName={InstanceName} DurationMs={DurationMs}",
            session.ImportedInstance.InstanceDirectory,
            session.ImportedInstance.Name,
            finalizeStopwatch.ElapsedMilliseconds);
    }

    private async Task<string> InstallLoaderIntoStagingAsync(
        PreparedModpack preparedModpack,
        StagedModpackInstance stagedInstance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond)
    {
        var installStageProgress = CreateInstallStageProgress(progress, ImportProgressStages.InstallingLoader);
        var installProgress = installStageProgress is null
            ? null
            : new InstallCardProgressMapper(installStageProgress, preparedModpack.Loader, hasOptionalContent: false);
        // 整合包文件下载已在并行分支中启动；租约只覆盖会写入共享 Minecraft 内容的 Loader 安装阶段。
        var coordinatorWait = Stopwatch.StartNew();
        logger.LogDebug(
            "Modpack loader installation waiting for shared install coordinator. InstanceName={InstanceName} Loader={Loader}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Loader);
        await using var installLease = await installCoordinator
            .AcquireInstallAsync(
                stagedInstance.MinecraftDirectory,
                stagedInstance.ResolvedInstanceName,
                installProgress,
                cancellationToken)
            .ConfigureAwait(false);
        logger.LogDebug(
            "Modpack loader installation acquired shared install coordinator. InstanceName={InstanceName} Loader={Loader} WaitDurationMs={WaitDurationMs}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Loader,
            coordinatorWait.ElapsedMilliseconds);

        var installerStopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Modpack loader installation started. InstanceName={InstanceName} Loader={Loader} MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Loader,
            preparedModpack.MinecraftVersion,
            preparedModpack.LoaderVersion);
        var versionName = await modpackGameInstaller.InstallLoaderAsync(
            preparedModpack.MinecraftVersion,
            preparedModpack.Loader,
            preparedModpack.LoaderVersion,
            new LoaderInstallTarget(
                stagedInstance.MinecraftDirectory,
                stagedInstance.ResolvedInstanceName,
                stagedInstance.InstanceDirectory),
            installProgress,
            cancellationToken,
            downloadSourcePreference,
            downloadSpeedLimitMbPerSecond).ConfigureAwait(false);
        installProgress?.ReportBaseInstallCompleted();
        logger.LogInformation(
            "Modpack loader installation completed. InstanceName={InstanceName} Loader={Loader} VersionName={VersionName} DurationMs={DurationMs}",
            stagedInstance.ResolvedInstanceName,
            preparedModpack.Loader,
            versionName,
            installerStopwatch.ElapsedMilliseconds);
        return versionName;
    }

    private static IProgress<LauncherProgress>? CreateInstallStageProgress(
        IProgress<LauncherProgress>? innerProgress,
        string installStage)
    {
        if (innerProgress is null)
            return null;

        return SpeedMeterProgress.Forward(innerProgress, progress =>
        {
            if (progress.Stage is InstallProgressStages.Preparing)
            {
                innerProgress.Report(progress with { Stage = installStage });
                return;
            }

            innerProgress.Report(progress);
        });
    }

    private static async Task<T> CompleteWithProgressAsync<T>(
        Task<T> task,
        IProgress<LauncherProgress>? progress,
        LauncherProgress completionProgress)
    {
        var result = await task.ConfigureAwait(false);
        progress?.Report(completionProgress);
        return result;
    }

    private static async Task CompleteWithProgressAsync(
        Task task,
        IProgress<LauncherProgress>? progress,
        LauncherProgress completionProgress)
    {
        await task.ConfigureAwait(false);
        progress?.Report(completionProgress);
    }

    private static async Task<T> CompleteParallelBranchAsync<T>(
        Task<T> task,
        OverallModpackImportProgress? progress,
        ModpackImportProgressBranch branch)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            progress?.CompleteParallelBranch(branch);
        }
    }

    private static string NormalizePreferredInstanceName(string preferredName)
    {
        var normalizedName = VersionDirectoryName.Sanitize(preferredName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ModpackImportException(
                ModpackImportFailureReason.InvalidManifest,
                "Prepared modpack package name is missing.");
        }

        return normalizedName;
    }

    /// <summary>
    /// 等待所有并行分支；任一异常都会取消共享令牌并保留原始异常。
    /// </summary>
    private static async Task AwaitImportBranchesAsync(
        CancellationTokenSource importCancellation,
        params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // WhenAll 不会自动取消尚未失败的分支，显式取消可缩短失败后的清理等待时间。
            SafeCancel(importCancellation);
            throw;
        }
    }

    private static async Task CancelImportOnBranchFailureAsync(
        Task branchTask,
        CancellationTokenSource importCancellation)
    {
        try
        {
            await branchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SafeCancel(importCancellation);
        }
    }

    private static void SafeCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

}
