/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed partial class LoaderInstallerArtifactService
{
    public async Task<(LoaderInstallerWorkspaceSnapshot Workspace, ForgeInstallerPlan Plan)> PrepareAndRunClientInstallerAsync(
        MinecraftInstallPathLayout layout,
        string minecraftVersion,
        string installerPath,
        string loaderCategory,
        Func<CancellationToken, Task> downloadInstaller,
        Func<PreparedLoaderInstaller, CancellationToken, Task> runInstaller,
        LoaderInstallerPrerequisiteSeeder seeder,
        IFinalVersionInstaller versionInstaller,
        DownloadSourcePreference preference,
        int speedLimit,
        MinecraftDownloadOperationContext operation,
        IProgress<LauncherProgress>? progress,
        SpeedMeter? speedMeter,
        CancellationToken cancellationToken)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress?.Report(new LauncherProgress(InstallProgressStages.DownloadingLoaderInstaller, string.Empty));

        var token = preparation.Token;
        ExceptionDispatchInfo? firstFailure = null;
        var split = versionInstaller.SupportsSplitBaseContent;
        var parallelProgress = split && progress is IParallelInstallProgress { TracksParallelInstall: true } tracked
            ? tracked
            : null;
        var installerTask = RunBranchAsync(PrepareInstallerAsync);
        var metadataTask = RunBranchAsync(PrepareMetadataAsync);
        var baseTask = RunBranchAsync(PrepareBaseAsync);
        var vanillaTask = RunBranchAsync(() => InstallVanillaAsync(assetsOnly: false));
        var assetsTask = RunBranchAsync(() => InstallVanillaAsync(assetsOnly: true));
        var mappingPlanTask = RunBranchAsync(ReadMappingsAsync);
        var mappingsTask = RunBranchAsync(PrepareMappingsAsync);
        var dependenciesTask = RunBranchAsync(PrepareInstallerDependenciesAsync);
        var javaTask = RunBranchAsync(PrepareAndRunJavaAsync);
        try
        {
            await Task.WhenAll(installerTask, metadataTask, baseTask, vanillaTask, assetsTask, mappingPlanTask,
                mappingsTask, dependenciesTask, javaTask).ConfigureAwait(false);
        }
        catch
        {
            firstFailure?.Throw();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = await javaTask.ConfigureAwait(false);
        var snapshot = await baseTask.ConfigureAwait(false);
        var client = snapshot.VerifiedVersionMetadata?["downloads"]?["client"];
        var relativeJar = $"versions/{minecraftVersion}/{minecraftVersion}.jar";
        if (snapshot.CanPrepareBaseContent
            && snapshot.SeededFiles.TryGetValue(relativeJar, out var sha1)
            && client?["size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var size) && size > 0)
        {
            operation.ReusableVanillaClient = new(Path.Combine(layout.WorkspaceMinecraftDirectory, relativeJar), sha1, size);
        }
        return workspace;

        async Task<T> RunBranchAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(ref firstFailure, ExceptionDispatchInfo.Capture(exception), null) is null)
                {
                    try { await preparation.CancelAsync().ConfigureAwait(false); }
                    catch (AggregateException callbackError) { logger.LogWarning(callbackError, "A loader installation cancellation callback failed."); }
                }
                throw;
            }
        }

        async Task<(LoaderInstallerWorkspaceSnapshot, ForgeInstallerPlan)> PrepareAndRunJavaAsync()
        {
            await Task.WhenAll(baseTask, vanillaTask, mappingsTask, dependenciesTask).ConfigureAwait(false);
            var client = await baseTask.ConfigureAwait(false);
            var libraries = await dependenciesTask.ConfigureAwait(false);
            var installer = await mappingsTask.ConfigureAwait(false);
            var mappingPlan = await mappingPlanTask.ConfigureAwait(false);
            if (mappingPlan is not null && ReferenceEquals(installer.Plan, await installerTask.ConfigureAwait(false)))
            {
                // The mappings branch already staged these files. Check them against
                // the original plan without seeding over them from the shared cache.
                await DownloadDiagnostics.RunStageAsync(logger, "InstallerFallbackDependencies", () =>
                    MaterializePrerequisitesAsync(installerPath, installer.Plan, layout.WorkspaceMinecraftDirectory,
                        preference, speedLimit, token, operation, speedMeter, parallelDownloads: true,
                        includeLibrary: mappingPlan.Libraries.ContainsKey)).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.InstallerDependencies, 100);
            await runInstaller(installer, token).ConfigureAwait(false);
            await DownloadDiagnostics.RunStageAsync(logger, "RuntimeLibraries", () =>
                MaterializeRuntimeLibrariesAsync(installerPath, installer.Plan, layout.WorkspaceMinecraftDirectory,
                    preference, speedLimit, token, operation, speedMeter, parallelDownloads: true)).ConfigureAwait(false);
            parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.Loader, 100);
            if (parallelProgress is null && !assetsTask.IsCompleted)
                progress?.Report(new LauncherProgress(ImportProgressStages.InstallingMinecraftBase, string.Empty));
            return (MergeSeededFiles(client, libraries), installer.Plan);
        }

        async Task<InstallerMappingsPlan?> ReadMappingsAsync()
        {
            var plan = await installerTask.ConfigureAwait(false);
            var metadata = await metadataTask.ConfigureAwait(false);
            return await DownloadDiagnostics.RunStageAsync(logger, "MappingsPlan", () =>
                ReadInstallerMappingsPlanAsync(installerPath, plan, minecraftVersion, layout.WorkspaceMinecraftDirectory,
                    metadata.VerifiedVersionMetadata, ModpackInstallEnvironment.Client, token)).ConfigureAwait(false);
        }

        async Task<PreparedLoaderInstaller> PrepareMappingsAsync()
        {
            var mappings = await mappingPlanTask.ConfigureAwait(false);
            var plan = await installerTask.ConfigureAwait(false);
            var result = await DownloadDiagnostics.RunStageAsync(logger, "MappingsPreparation", () =>
                PrepareInstallerMappingsAsync(installerPath, plan, mappings, minecraftVersion, layout.WorkspaceMinecraftDirectory,
                    preference, speedLimit, token, operation, speedMeter, parallelDownloads: true)).ConfigureAwait(false);
            parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.Mappings, 100);
            return result;
        }

        async Task<LoaderInstallerWorkspaceSnapshot> PrepareInstallerDependenciesAsync()
        {
            var mappings = await mappingPlanTask.ConfigureAwait(false);
            var plan = mappings?.PreparedPlan ?? await installerTask.ConfigureAwait(false);
            var metadata = await metadataTask.ConfigureAwait(false);
            var result = await DownloadDiagnostics.RunStageAsync(logger, "InstallerDependencies", () =>
                PrepareInstallerLibrariesAsync(metadata, plan, mappings)).ConfigureAwait(false);
            // A fallback to the original installer may still need additional libraries before Java.
            parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.InstallerDependencies, 80);
            return result;
        }

        async Task<LoaderInstallerWorkspaceSnapshot> PrepareInstallerLibrariesAsync(
            LoaderInstallerWorkspaceSnapshot metadata, ForgeInstallerPlan plan, InstallerMappingsPlan? mappings)
        {
            var sharedPaths = split && metadata.CanPrepareBaseContent
                ? GetExplicitVanillaLibraryPaths(metadata.VerifiedVersionMetadata)
                : null;
            bool Include(string path) => mappings?.Libraries.ContainsKey(path) != true;
            if (sharedPaths is null)
            {
                await vanillaTask.ConfigureAwait(false);
                return await PrepareLibrariesAsync(metadata, plan, Include).ConfigureAwait(false);
            }

            // Installer-only files can download in the sandbox while vanilla
            // writes shared libraries. Wait only for files we can seed from there.
            var independent = RunBranchAsync(() =>
                PrepareLibrariesAsync(metadata, plan, path => Include(path) && !sharedPaths.Contains(path)));
            if (sharedPaths.Count == 0)
                return await independent.ConfigureAwait(false);

            var shared = RunBranchAsync(async () =>
            {
                await vanillaTask.ConfigureAwait(false);
                return await PrepareLibrariesAsync(metadata, plan, path => Include(path) && sharedPaths.Contains(path))
                    .ConfigureAwait(false);
            });
            await Task.WhenAll(independent, shared).ConfigureAwait(false);
            return MergeSeededFiles(await independent.ConfigureAwait(false), await shared.ConfigureAwait(false));
        }

        async Task<LoaderInstallerWorkspaceSnapshot> PrepareLibrariesAsync(
            LoaderInstallerWorkspaceSnapshot snapshot, ForgeInstallerPlan plan, Func<string, bool> include)
        {
            var seeded = await DownloadDiagnostics.RunStageAsync(logger, "InstallerLibrarySeeding", () =>
                seeder.SeedInstallerLibrariesAsync(layout.SharedMinecraftDirectory, installerPath, snapshot, token, include))
                .ConfigureAwait(false);
            await MaterializePrerequisitesAsync(installerPath, plan, layout.WorkspaceMinecraftDirectory,
                preference, speedLimit, token, operation, speedMeter, parallelDownloads: true, includeLibrary: include).ConfigureAwait(false);
            return seeded;
        }

        async Task<ForgeInstallerPlan> PrepareInstallerAsync()
        {
            using var branch = DownloadDiagnostics.BeginBranch(logger, "LoaderInstaller");
            await DownloadDiagnostics.RunStageAsync(logger, "InstallerDownload", () => downloadInstaller(token)).ConfigureAwait(false);
            var plan = await DownloadDiagnostics.RunStageAsync(logger, "InstallerPlan", () => ReadPlanAsync(installerPath, token))
                .ConfigureAwait(false);
            parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.Installer, 100);
            return plan;
        }

        async Task<LoaderInstallerWorkspaceSnapshot> PrepareBaseAsync()
        {
            using var branch = DownloadDiagnostics.BeginBranch(logger, "VanillaContent");
            var snapshot = await DownloadDiagnostics.RunStageAsync(logger, "BasePrerequisites", async () =>
            {
                var metadata = await metadataTask.ConfigureAwait(false);
                return await seeder.PrepareBaseClientAsync(layout.SharedMinecraftDirectory, metadata, minecraftVersion,
                    preference, speedLimit, token, operation, speedMeter).ConfigureAwait(false);
            }).ConfigureAwait(false);
            parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.Client, 100);
            return snapshot;
        }

        async Task<LoaderInstallerWorkspaceSnapshot> PrepareMetadataAsync()
        {
            using var branch = DownloadDiagnostics.BeginBranch(logger, "VanillaContent");
            var snapshot = await DownloadDiagnostics.RunStageAsync(logger, "BaseMetadata", () =>
                seeder.PrepareBaseMetadataAsync(layout.WorkspaceMinecraftDirectory, minecraftVersion,
                    preference, speedLimit, token, loaderCategory)).ConfigureAwait(false);
            if (snapshot.CanPrepareBaseContent)
                parallelProgress?.ReportParallelInstallProgress(ParallelInstallPart.Metadata, 100);
            return snapshot;
        }

        async Task<bool> InstallVanillaAsync(bool assetsOnly)
        {
            if (assetsOnly && !split)
                return false;
            var snapshot = await (split ? metadataTask : baseTask).ConfigureAwait(false);
            if (!snapshot.CanPrepareBaseContent)
            {
                if (!assetsOnly)
                    logger.LogDebug("Base content preparation deferred. MinecraftVersion={MinecraftVersion}", minecraftVersion);
                return false;
            }
            var part = assetsOnly ? ParallelInstallPart.Assets : ParallelInstallPart.VanillaLibraries;
            IProgress<LauncherProgress>? baseProgress = null;
            if (progress is not null && parallelProgress is not null)
            {
                baseProgress = SpeedMeterProgress.Forward(progress, value =>
                {
                    if (value.Percent is { } percent)
                        parallelProgress.ReportParallelInstallProgress(part, Math.Min(99, percent));
                });
            }
            else if (progress is not null && !assetsOnly)
            {
                baseProgress = SpeedMeterProgress.Forward(progress, value =>
                    progress.Report(value with { Stage = ImportProgressStages.InstallingMinecraftBase }));
                baseProgress.Report(new LauncherProgress(ImportProgressStages.InstallingMinecraftBase, string.Empty, 0));
            }
            using var branch = DownloadDiagnostics.BeginBranch(logger, assetsOnly ? "VanillaAssets" : "VanillaContent");
            try
            {
                await DownloadDiagnostics.RunStageAsync(logger, assetsOnly ? "BaseAssets" : "BaseContent", () => split
                    ? versionInstaller.InstallBasePartAsync(layout.Path, minecraftVersion, operation, preference, baseProgress,
                        token, speedLimit, speedMeter, assetsOnly ? LoaderBaseContentPart.Assets : LoaderBaseContentPart.WithoutClientOrAssets)
                    : versionInstaller.InstallAsync(layout.Path, minecraftVersion, operation, preference, baseProgress,
                        token, speedLimit, speedMeter)).ConfigureAwait(false);
            }
            catch (Exception exception) when (assetsOnly && (exception is not OperationCanceledException || !token.IsCancellationRequested))
            {
                throw new MinecraftAssetInstallationException(exception);
            }
            parallelProgress?.ReportParallelInstallProgress(part, 100);
            return true;
        }
    }

    private static LoaderInstallerWorkspaceSnapshot MergeSeededFiles(
        LoaderInstallerWorkspaceSnapshot first, LoaderInstallerWorkspaceSnapshot second)
    {
        var seededFiles = new Dictionary<string, string>(first.SeededFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in second.SeededFiles)
            seededFiles.TryAdd(file.Key, file.Value);
        return first with { SeededFiles = seededFiles };
    }

    private static HashSet<string>? GetExplicitVanillaLibraryPaths(JsonObject? metadata)
    {
        if (metadata?["inheritsFrom"] is not null || metadata?["libraries"] is not JsonArray libraries)
            return null;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            if (library is not JsonObject descriptor || descriptor["downloads"] is not JsonObject downloads)
                return null;

            var artifacts = new List<JsonNode?>();
            if (downloads["artifact"] is { } artifact)
                artifacts.Add(artifact);
            if (downloads["classifiers"] is JsonObject classifiers)
                artifacts.AddRange(classifiers.Select(pair => pair.Value));
            if (artifacts.Count == 0)
                return null;

            foreach (var item in artifacts)
            {
                if (item is not JsonObject file || MappingString(file["path"]) is not { } path || !IsSafeRelativePath(path))
                    return null;
                paths.Add(path.Replace('\\', '/'));
            }
        }
        return paths;
    }
}
