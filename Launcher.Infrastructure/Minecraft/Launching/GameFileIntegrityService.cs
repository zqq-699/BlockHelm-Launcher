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
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core.ProcessBuilder;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// The single entry point for game-file validation and recovery. The existing
/// downloader and installer recovery code remains the executor; this service
/// owns the resolved manifest and the mandatory post-repair validation gate.
/// </summary>
internal sealed class GameFileIntegrityService : IGameFileIntegrityService
{
    private readonly ManagedVersionRepairService repairService;
    private readonly RequiredGameFileManifestBuilder manifestBuilder;
    private readonly ILogger logger;

    public GameFileIntegrityService(
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<GameFileIntegrityService>? logger = null)
        : this(httpClient: null, downloadSpeedLimitState, logger)
    {
    }

    internal GameFileIntegrityService(
        IEnumerable<ILoaderProvider> loaderProviders,
        IGameInstallCoordinator gameInstallCoordinator,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<GameFileIntegrityService>? logger = null,
        IEnumerable<ILoaderFileManifestContributor>? manifestContributors = null)
        : this(
            httpClient: null,
            downloadSpeedLimitState,
            logger,
            loaderProviders,
            gameInstallCoordinator,
            manifestContributors)
    {
    }

    internal GameFileIntegrityService(
        HttpClient? httpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger? logger = null,
        IEnumerable<ILoaderProvider>? loaderProviders = null,
        IGameInstallCoordinator? gameInstallCoordinator = null,
        IEnumerable<ILoaderFileManifestContributor>? manifestContributors = null)
    {
        this.logger = logger ?? NullLogger.Instance;
        repairService = new ManagedVersionRepairService(
            httpClient,
            downloadSpeedLimitState,
            this.logger,
            loaderProviders: loaderProviders,
            gameInstallCoordinator: gameInstallCoordinator);
        manifestBuilder = new RequiredGameFileManifestBuilder(
            repairService,
            this.logger,
            manifestContributors);
    }

    internal GameFileIntegrityService(
        ManagedVersionRepairService repairService,
        RequiredGameFileManifestBuilder manifestBuilder,
        ILogger? logger = null)
    {
        this.repairService = repairService;
        this.manifestBuilder = manifestBuilder;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<GameFileRepairResult> ValidateAndRepairAsync(
        GameFileIntegrityRequest request,
        GameFileRepairOptions options,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(new LauncherProgress(
                LaunchProgressStages.CheckingFiles,
                "Resolving and checking required game files",
                4));
            GameFileRepairResult before;
            try
            {
                before = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                options.AllowRepair
                && request.LoaderIdentity is not null
                && exception is IOException or InvalidDataException or JsonException)
            {
                logger.LogWarning(
                    exception,
                    "Game file manifest could not be resolved; rebuilding from loader identity. VersionName={VersionName} Loader={Loader}",
                    request.VersionName,
                    request.LoaderIdentity.LoaderKind);
                progress?.Report(new LauncherProgress(
                    LaunchProgressStages.CheckingFiles,
                    "Initial game file validation completed",
                    12));
                await RepairAsync(request, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new LauncherProgress(
                    LaunchProgressStages.RevalidatingFiles,
                    "Revalidating repaired game files",
                    84));
                var recovered = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
                progress?.Report(new LauncherProgress(
                    LaunchProgressStages.RevalidatingFiles,
                    "Required game file validation completed",
                    90));
                LogReport("Game file metadata recovery validation completed.", request, recovered, repairedCount: 1);
                return recovered with { RepairedCount = recovered.LaunchAllowed ? 1 : 0 };
            }
            progress?.Report(new LauncherProgress(
                LaunchProgressStages.CheckingFiles,
                "Initial game file validation completed",
                12));
            LogReport("Game file preflight completed.", request, before, repairedCount: 0);
            if (before.LaunchAllowed)
            {
                progress?.Report(new LauncherProgress(
                    LaunchProgressStages.CheckingFiles,
                    "Required game file validation completed",
                    90));
                return before;
            }
            if (!options.AllowRepair)
                return before;

            await RepairAsync(request, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new LauncherProgress(
                LaunchProgressStages.RevalidatingFiles,
                "Revalidating repaired game files",
                84));
            var after = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            progress?.Report(new LauncherProgress(
                LaunchProgressStages.RevalidatingFiles,
                "Required game file validation completed",
                90));
            var repairedCount = Math.Max(0, before.FailedCount - after.FailedCount);
            LogReport("Game file post-repair validation completed.", request, after, repairedCount);
            return after with { RepairedCount = repairedCount };
        }
        catch (InstanceRepairException exception)
        {
            return FailureResult(
                request,
                ClassifyFailure(exception),
                "Repair",
                "PlannedRecovery",
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            logger.LogWarning(exception, "Game file integrity planning failed. VersionName={VersionName}", request.VersionName);
            return FailureResult(
                request,
                GameFileRepairFailureReason.MetadataIncomplete,
                "Metadata",
                "None",
                exception.Message);
        }
    }

    /// <summary>
    /// Validates a version immediately after it was installed by the current
    /// download operation. Files whose exact expected SHA-1 and size were
    /// already verified and atomically published by that operation are not
    /// hashed again; all other files retain the normal full verification and
    /// repair behavior.
    /// </summary>
    internal async Task<GameFileRepairResult> ValidateInstalledVersionAsync(
        GameFileIntegrityRequest request,
        MinecraftDownloadOperationContext operationContext,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationContext);
        try
        {
            var before = await ValidateAsync(request, operationContext, cancellationToken).ConfigureAwait(false);
            LogReport("Game file post-install validation completed.", request, before, repairedCount: 0);
            if (before.LaunchAllowed)
                return before;

            await repairService.RepairWithOperationAsync(
                    request.MinecraftDirectory,
                    request.VersionName,
                    request.InstanceDirectory,
                    progress,
                    allowRepair: true,
                    operationContext,
                    cancellationToken,
                    request.DownloadSourcePreference,
                    request.DownloadSpeedLimitMbPerSecond,
                    request.LoaderIdentity)
                .ConfigureAwait(false);

            var after = await ValidateAsync(request, operationContext, cancellationToken).ConfigureAwait(false);
            var repairedCount = Math.Max(0, before.FailedCount - after.FailedCount);
            LogReport("Game file post-install repair validation completed.", request, after, repairedCount);
            return after with { RepairedCount = repairedCount };
        }
        catch (InstanceRepairException exception)
        {
            return FailureResult(
                request,
                ClassifyFailure(exception),
                "Repair",
                "PlannedRecovery",
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            logger.LogWarning(exception, "Game file post-install validation planning failed. VersionName={VersionName}", request.VersionName);
            return FailureResult(
                request,
                GameFileRepairFailureReason.MetadataIncomplete,
                "Metadata",
                "None",
                exception.Message);
        }
    }

    public async Task<GameFileRepairResult> ValidateFinalLaunchCommandAsync(
        GameFileIntegrityRequest request,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        var plan = await manifestBuilder.ResolveFinalCommandAsync(request, cancellationToken).ConfigureAwait(false);
        var knownFiles = plan.Manifest.Files
            .Select(file => Path.GetFullPath(file.TargetPath))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var allowedAdditionalFiles = request.AllowedAdditionalCommandFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeCommandPath(path, startInfo.WorkingDirectory))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var failures = new List<GameFileRepairFailure>();
        foreach (var reference in FinalLaunchCommandPathReader.Read(startInfo))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizeCommandPath(reference.Path, startInfo.WorkingDirectory);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var exists = reference.IsDirectory ? Directory.Exists(path) : File.Exists(path);
            var insideMinecraftDirectory = IsWithin(path, request.MinecraftDirectory);
            var known = knownFiles.Contains(path);
            var acceptsAdditionalFile = reference.Category is "JavaAgent" or "LoggingConfiguration";
            var explicitlyAllowed = acceptsAdditionalFile && allowedAdditionalFiles.Contains(path);
            string? failureReason = null;
            if (!exists)
                failureReason = "Referenced path does not exist.";
            else if (explicitlyAllowed && !IsOrdinaryCommandFile(path))
                failureReason = "Allowed additional path is not an ordinary file.";
            else if (acceptsAdditionalFile && !known && !explicitlyAllowed)
                failureReason = "Path was not explicitly allowed by the launch request.";
            else if (insideMinecraftDirectory && !reference.IsDirectory && !known && !explicitlyAllowed)
                failureReason = "Path was not part of the resolved manifest.";

            if (failureReason is not null)
            {
                failures.Add(new GameFileRepairFailure(
                    path,
                    reference.Category,
                    GameFileRepairFailureReason.FinalLaunchPlanInvalid,
                    "None",
                    failureReason));
            }
        }

        if (!string.IsNullOrWhiteSpace(startInfo.FileName) && !File.Exists(startInfo.FileName))
        {
            failures.Add(new GameFileRepairFailure(
                startInfo.FileName,
                "JavaRuntime",
                GameFileRepairFailureReason.FinalLaunchPlanInvalid,
                "None",
                "Java executable does not exist."));
        }

        var result = failures.Count == 0
            ? new GameFileRepairResult(
                LaunchAllowed: true,
                plan.Manifest.Files.Count,
                MissingCount: 0,
                CorruptedCount: 0,
                UnverifiableCount: 0,
                RepairableCount: 0,
                RepairedCount: 0,
                FailedCount: 0,
                Failures: [])
            : new GameFileRepairResult(
                LaunchAllowed: false,
                plan.Manifest.Files.Count,
                failures.Count(failure => failure.Reason is GameFileRepairFailureReason.Missing or GameFileRepairFailureReason.FinalLaunchPlanInvalid),
                failures.Count(failure => failure.Reason == GameFileRepairFailureReason.Corrupted),
                failures.Count(failure => failure.Reason == GameFileRepairFailureReason.MetadataIncomplete),
                RepairableCount: 0,
                RepairedCount: 0,
                FailedCount: failures.Count,
                Failures: failures);
        logger.LogInformation(
            "Final launch command validated. VersionName={VersionName} FinalCommandMissingPathCount={FinalCommandMissingPathCount} LaunchAllowed={LaunchAllowed}",
            request.VersionName,
            failures.Count,
            result.LaunchAllowed);
        return result;
    }

    private Task RepairAsync(
        GameFileIntegrityRequest request,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var repairProgress = progress is null
            ? null
            : new LaunchRepairProgressAdapter(progress);
        return repairService.RepairWithIdentityAsync(
            request.MinecraftDirectory,
            request.VersionName,
            request.InstanceDirectory,
            repairProgress,
            allowRepair: true,
            cancellationToken,
            request.DownloadSourcePreference,
            request.DownloadSpeedLimitMbPerSecond,
            request.LoaderIdentity);
    }

    private async Task<GameFileRepairResult> ValidateAsync(
        GameFileIntegrityRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await manifestBuilder.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        LogManifestComposition(request, plan.Manifest);
        var report = await GameFileManifestValidator.ValidateAsync(plan.Manifest, cancellationToken).ConfigureAwait(false);
        return new GameFileRepairResult(
            report.Failures.Count == 0,
            plan.Manifest.Files.Count,
            report.MissingCount,
            report.CorruptedCount,
            report.UnverifiableCount,
            report.RepairableCount,
            RepairedCount: 0,
            report.Failures.Count,
            report.Failures);
    }

    private async Task<GameFileRepairResult> ValidateAsync(
        GameFileIntegrityRequest request,
        MinecraftDownloadOperationContext operationContext,
        CancellationToken cancellationToken)
    {
        var plan = await manifestBuilder.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        LogManifestComposition(request, plan.Manifest);
        var report = await GameFileManifestValidator.ValidateAsync(plan.Manifest, operationContext, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Game file manifest verification completed. VersionName={VersionName} FullVerificationCount={FullVerificationCount} CurrentOperationVerificationReuseCount={CurrentOperationVerificationReuseCount}",
            request.VersionName,
            report.FullVerificationCount,
            report.CurrentOperationVerificationReuseCount);
        return new GameFileRepairResult(
            report.Failures.Count == 0,
            plan.Manifest.Files.Count,
            report.MissingCount,
            report.CorruptedCount,
            report.UnverifiableCount,
            report.RepairableCount,
            RepairedCount: 0,
            report.Failures.Count,
            report.Failures);
    }

    private static GameFileRepairFailureReason ClassifyFailure(InstanceRepairException exception)
    {
        var exceptions = EnumerateExceptionChain(exception).ToArray();
        if (exceptions.Any(item => item is LoaderArtifactPublicationRollbackException)
            || exceptions.Any(item => ContainsAny(item.Message, "publication", "publish")))
        {
            return GameFileRepairFailureReason.PublicationFailed;
        }
        if (exceptions.Any(item => item is DownloadAttemptException or HttpRequestException)
            || exceptions.Any(item => ContainsAny(item.Message, "download", "HTTP 4", "HTTP 5")))
        {
            return GameFileRepairFailureReason.DownloadFailed;
        }
        if (exceptions.Any(item => ContainsAny(item.Message, "processor", "installer process", "installer exited")))
            return GameFileRepairFailureReason.ProcessorRegenerationFailed;
        if (exceptions.Any(item => item.Message.Contains("missing", StringComparison.OrdinalIgnoreCase)))
            return GameFileRepairFailureReason.Missing;
        return GameFileRepairFailureReason.Corrupted;
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(EnumerateExceptionChain))
                yield return inner;
            yield break;
        }
        if (exception.InnerException is not null)
        {
            foreach (var inner in EnumerateExceptionChain(exception.InnerException))
                yield return inner;
        }
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private void LogManifestComposition(GameFileIntegrityRequest request, RequiredGameFileManifest manifest)
    {
        logger.LogInformation(
            "Game file manifest resolved. VersionName={VersionName} StandardCount={StandardCount} LoaderPrerequisiteCount={LoaderPrerequisiteCount} LoaderRuntimeCount={LoaderRuntimeCount} LoaderProcessorOutputCount={LoaderProcessorOutputCount}",
            request.VersionName,
            manifest.Files.Count(file => !file.Category.StartsWith("Loader", StringComparison.Ordinal)),
            manifest.Files.Count(file => file.Category == "LoaderPrerequisite"),
            manifest.Files.Count(file => file.Category == "LoaderRuntimeLibrary"),
            manifest.Files.Count(file => file.Category == "LoaderProcessorOutput"));
    }

    private static GameFileRepairResult FailureResult(
        GameFileIntegrityRequest request,
        GameFileRepairFailureReason reason,
        string category,
        string recoveryMethod,
        string? source)
    {
        return new GameFileRepairResult(
            LaunchAllowed: false,
            RequiredCount: 0,
            MissingCount: reason == GameFileRepairFailureReason.Missing ? 1 : 0,
            CorruptedCount: reason == GameFileRepairFailureReason.Corrupted ? 1 : 0,
            UnverifiableCount: reason == GameFileRepairFailureReason.MetadataIncomplete ? 1 : 0,
            RepairableCount: 0,
            RepairedCount: 0,
            FailedCount: 1,
            Failures: [new GameFileRepairFailure(request.VersionName, category, reason, recoveryMethod, source)]);
    }

    private void LogReport(string message, GameFileIntegrityRequest request, GameFileRepairResult result, int repairedCount)
    {
        logger.LogInformation(
            "{Message} VersionName={VersionName} RequiredCount={RequiredCount} MissingCount={MissingCount} CorruptedCount={CorruptedCount} UnverifiableCount={UnverifiableCount} RepairableCount={RepairableCount} RepairedCount={RepairedCount} FailedCount={FailedCount} LaunchAllowed={LaunchAllowed}",
            message,
            request.VersionName,
            result.RequiredCount,
            result.MissingCount,
            result.CorruptedCount,
            result.UnverifiableCount,
            result.RepairableCount,
            repairedCount,
            result.FailedCount,
            result.LaunchAllowed);
    }

    private static string NormalizeCommandPath(string path, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return Path.GetFullPath(path, string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory);
    }

    private static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsOrdinaryCommandFile(string path)
    {
        try
        {
            return File.Exists(path)
                && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed record ResolvedLaunchPlan(string VersionName, JsonObject VersionJson, RequiredGameFileManifest Manifest);

internal sealed record RequiredGameFileManifest(IReadOnlyList<RequiredGameFile> Files);

internal sealed record RequiredGameFile(
    string TargetPath,
    string Category,
    string? Source,
    string? Sha1,
    long? Size,
    bool Required,
    string RecoveryMethod,
    string Reason,
    GameFileRepairFailureReason? ForcedFailureReason = null,
    string? Sha256 = null,
    string? ManagedRoot = null,
    MinecraftFileVerification Verification = MinecraftFileVerification.Full);

internal sealed record GameFileValidationReport(
    int MissingCount,
    int CorruptedCount,
    int UnverifiableCount,
    int RepairableCount,
    IReadOnlyList<GameFileRepairFailure> Failures,
    int FullVerificationCount,
    int CurrentOperationVerificationReuseCount);

internal sealed record GameFileRepairPlan(IReadOnlyList<RequiredGameFile> FilesToRepair);

internal sealed class RequiredGameFileManifestBuilder
{
    private readonly ManagedVersionRepairService repairService;
    private readonly ILogger logger;
    private readonly IReadOnlyDictionary<LoaderKind, ILoaderFileManifestContributor> loaderContributors;

    internal RequiredGameFileManifestBuilder(
        ManagedVersionRepairService repairService,
        ILogger? logger = null,
        IEnumerable<ILoaderFileManifestContributor>? loaderContributors = null)
    {
        this.repairService = repairService;
        this.logger = logger ?? NullLogger.Instance;
        this.loaderContributors = (loaderContributors ?? LoaderFileManifestContributors.CreateDefault())
            .GroupBy(contributor => contributor.Kind)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public async Task<ResolvedLaunchPlan> ResolveAsync(GameFileIntegrityRequest request, CancellationToken cancellationToken)
    {
        return await ResolveAsync(request, includeAssets: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResolvedLaunchPlan> ResolveFinalCommandAsync(
        GameFileIntegrityRequest request,
        CancellationToken cancellationToken)
    {
        return await ResolveAsync(request, includeAssets: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedLaunchPlan> ResolveAsync(
        GameFileIntegrityRequest request,
        bool includeAssets,
        CancellationToken cancellationToken)
    {
        var versionDirectory = Path.GetFullPath(request.InstanceDirectory);
        var versionResolution = await repairService.ResolveVersionForRepairAsync(
                request.MinecraftDirectory,
                request.VersionName,
                versionDirectory,
                request.DownloadSourcePreference,
                cancellationToken,
                allowRemoteParentResolution: true,
                request.DownloadSpeedLimitMbPerSecond)
            .ConfigureAwait(false);
        var versionJson = versionResolution.VersionJson;
        var files = new Dictionary<string, RequiredGameFile>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var metadataPath in versionResolution.LocalMetadataPaths)
        {
            Add(files, new RequiredGameFile(
                metadataPath,
                "VersionMetadata",
                null,
                null,
                null,
                true,
                "LoaderInstallerSandbox",
                "Resolved version metadata chain",
                ManagedRoot: Path.GetDirectoryName(metadataPath)));
        }
        AddClientJar(files, versionDirectory, request.VersionName, versionResolution);
        AddLibraries(files, request.MinecraftDirectory, versionJson);
        if (includeAssets)
            await AddAssetsAsync(files, request.MinecraftDirectory, versionJson, cancellationToken).ConfigureAwait(false);
        AddLogging(files, request.MinecraftDirectory, versionJson);
        await AddLoaderArtifactsAsync(files, request, versionDirectory, cancellationToken).ConfigureAwait(false);
        return new ResolvedLaunchPlan(request.VersionName, versionJson, new RequiredGameFileManifest(files.Values.ToList()));
    }

    private async Task AddLoaderArtifactsAsync(
        IDictionary<string, RequiredGameFile> files,
        GameFileIntegrityRequest request,
        string versionDirectory,
        CancellationToken cancellationToken)
    {
        var identity = request.LoaderIdentity;
        if (identity is null)
            return;

        var manifestPath = LoaderArtifactManifestStore.GetPath(versionDirectory);
        MinecraftPathGuard.EnsureSafeFileDestination(
            manifestPath,
            versionDirectory,
            "Managed loader manifest");
        if (!loaderContributors.TryGetValue(identity.LoaderKind, out var contributor))
        {
            Add(files, new RequiredGameFile(
                manifestPath,
                "LoaderManifest",
                null,
                null,
                null,
                true,
                "Unavailable",
                $"No artifact manifest contributor is registered for {identity.LoaderKind}.",
                GameFileRepairFailureReason.MetadataIncomplete,
                ManagedRoot: versionDirectory));
            return;
        }

        var contribution = await contributor.ResolveAsync(versionDirectory, identity, cancellationToken)
            .ConfigureAwait(false);
        if (!contribution.RequiresManifest)
            return;
        manifestPath = contribution.ManifestPath ?? manifestPath;
        if (contribution.Manifest is null)
        {
            Add(files, new RequiredGameFile(
                manifestPath,
                "LoaderManifest",
                null,
                null,
                null,
                true,
                "LoaderInstallerSandbox",
                contribution.Error ?? "Loader artifact manifest is invalid.",
                GameFileRepairFailureReason.MetadataIncomplete,
                ManagedRoot: versionDirectory));
            logger.LogWarning(
                "Loader artifact manifest is unavailable. VersionName={VersionName} Loader={Loader} Reason={Reason}",
                request.VersionName,
                identity.LoaderKind,
                contribution.Error);
            return;
        }

        Add(files, new RequiredGameFile(
            manifestPath,
            "LoaderManifest",
            null,
            null,
            null,
            true,
            "LoaderInstallerSandbox",
            "Loader artifact manifest",
            ManagedRoot: versionDirectory));
        foreach (var artifact in contribution.Manifest.Artifacts)
        {
            var usesRecordedHashes = LoaderArtifactVerificationPolicy.UsesRecordedHashes(artifact);
            Add(files, new RequiredGameFile(
                LoaderArtifactManifestStore.ResolveManagedPath(request.MinecraftDirectory, artifact.RelativePath),
                artifact.Kind switch
                {
                    LoaderArtifactKind.InstallerPrerequisite => "LoaderPrerequisite",
                    LoaderArtifactKind.RuntimeLibrary => "LoaderRuntimeLibrary",
                    LoaderArtifactKind.ProcessorOutput => "LoaderProcessorOutput",
                    _ => "LoaderArtifact"
                },
                artifact.Source,
                usesRecordedHashes ? artifact.Sha1 : null,
                artifact.Size,
                true,
                "LoaderInstallerSandbox",
                artifact.RelativePath,
                Sha256: usesRecordedHashes ? artifact.Sha256 : null,
                ManagedRoot: request.MinecraftDirectory,
                Verification: usesRecordedHashes
                    ? MinecraftFileVerification.Full
                    : MinecraftFileVerification.SizeOnly));
        }
    }

    private static async Task<JsonObject> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException($"Version metadata is empty: {path}");
    }

    private static void AddClientJar(IDictionary<string, RequiredGameFile> files, string versionDirectory, string versionName, ManagedVersionRepairService.ResolvedVersionMetadata versionResolution)
    {
        var jarPath = InstanceVersionFiles.ResolveJarPath(versionDirectory, versionName);
        Add(files, new RequiredGameFile(
            jarPath,
            "ClientJar",
            versionResolution.ClientJarUrl,
            versionResolution.ClientJarSha1,
            versionResolution.ClientJarSize,
            true,
            string.IsNullOrWhiteSpace(versionResolution.ClientJarUrl) ? "Unavailable" : "DirectDownload",
            "Final version client jar",
            ManagedRoot: Path.GetDirectoryName(jarPath)));
    }

    private static void AddLibraries(IDictionary<string, RequiredGameFile> files, string minecraftDirectory, JsonObject versionJson)
    {
        if (versionJson["libraries"] is not JsonArray libraries)
            return;
        var librariesRoot = Path.Combine(minecraftDirectory, "libraries");
        foreach (var library in libraries.OfType<JsonObject>())
        {
            if (!ManagedLibraryArtifactResolver.IsAllowed(library))
                continue;
            foreach (var artifact in ManagedLibraryArtifactResolver.EnumerateDownloads(library))
            {
                var target = MinecraftPathGuard.EnsureWithin(
                    Path.Combine(librariesRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    librariesRoot,
                    "Resolved library");
                Add(files, new RequiredGameFile(
                    target,
                    "Library",
                    artifact.Url,
                    artifact.Sha1,
                    artifact.Size,
                    true,
                    string.IsNullOrWhiteSpace(artifact.Url) ? "Unavailable" : "DirectDownload",
                    artifact.LibraryName ?? artifact.RelativePath,
                    ManagedRoot: librariesRoot));
            }
        }
    }

    private static async Task AddAssetsAsync(
        IDictionary<string, RequiredGameFile> files,
        string minecraftDirectory,
        JsonObject versionJson,
        CancellationToken cancellationToken)
    {
        if (versionJson["assetIndex"] is not JsonObject assetIndex)
            return;
        var id = GetString(assetIndex["id"]);
        // Mojang asset index identifiers commonly contain dots (for example
        // "1.18"), so this is a file-name safety check rather than an
        // extension check.
        if (string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw SafeExceptionLog.Describe(new InvalidDataException("Asset index id is invalid."), "DownloadValidation", ("Reason", "Asset index id is invalid."));
        var indexesRoot = Path.Combine(minecraftDirectory, "assets", "indexes");
        var indexPath = MinecraftPathGuard.EnsureWithin(Path.Combine(indexesRoot, $"{id}.json"), indexesRoot, "Asset index");
        MinecraftPathGuard.EnsureSafeFileDestination(indexPath, indexesRoot, "Asset index");
        Add(files, new RequiredGameFile(indexPath, "AssetIndex", GetString(assetIndex["url"]), GetString(assetIndex["sha1"]), GetLong(assetIndex["size"]), true, "DirectDownload", "Version asset index", ManagedRoot: indexesRoot));
        if (!File.Exists(indexPath))
            return;

        JsonObject index;
        try
        {
            index = await ReadJsonAsync(indexPath, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Asset index cannot be parsed: {indexPath}", exception);
        }

        if (index["objects"] is not JsonObject objects)
            return;
        var objectsRoot = Path.Combine(minecraftDirectory, "assets", "objects");
        foreach (var objectEntry in objects)
        {
            if (objectEntry.Value is not JsonObject asset)
                continue;
            var hash = GetString(asset["hash"]);
            if (hash is null || !MinecraftFileIntegrity.IsSha1(hash))
                continue;
            var target = MinecraftPathGuard.EnsureWithin(Path.Combine(objectsRoot, hash[..2], hash), objectsRoot, "Asset object");
            Add(files, new RequiredGameFile(
                target,
                "AssetObject",
                $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                hash,
                GetLong(asset["size"]),
                true,
                "DirectDownload",
                objectEntry.Key,
                ManagedRoot: objectsRoot,
                Verification: MinecraftFileVerification.SizeOnly));
        }
    }

    private static void AddLogging(IDictionary<string, RequiredGameFile> files, string minecraftDirectory, JsonObject versionJson)
    {
        if (versionJson["logging"]?["client"]?["file"] is not JsonObject logging)
            return;
        var id = GetString(logging["id"]);
        if (string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw SafeExceptionLog.Describe(new InvalidDataException("Logging configuration id is invalid."), "DownloadValidation", ("Reason", "Logging configuration id is invalid."));
        var root = Path.Combine(minecraftDirectory, "assets", "log_configs");
        Add(files, new RequiredGameFile(
            MinecraftPathGuard.EnsureWithin(Path.Combine(root, id), root, "Logging configuration"),
            "LoggingConfiguration",
            GetString(logging["url"]),
            GetString(logging["sha1"]),
            GetLong(logging["size"]),
            true,
            "DirectDownload",
            "Client logging configuration",
            ManagedRoot: root));
    }

    private static void Add(IDictionary<string, RequiredGameFile> files, RequiredGameFile file)
    {
        var path = Path.GetFullPath(file.TargetPath);
        if (files.TryGetValue(path, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(existing.Sha1)
                && !string.IsNullOrWhiteSpace(file.Sha1)
                && !string.Equals(existing.Sha1, file.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Conflicting checksums were declared for {path}.");
            }
            if (string.IsNullOrWhiteSpace(existing.Sha1) && !string.IsNullOrWhiteSpace(file.Sha1))
                files[path] = file;
            return;
        }
        files[path] = file;
    }

    private static string? GetString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? GetLong(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}

internal static class GameFileManifestValidator
{
    public static async Task<GameFileValidationReport> ValidateAsync(RequiredGameFileManifest manifest, CancellationToken cancellationToken)
    {
        return await ValidateAsync(manifest, operationContext: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task<GameFileValidationReport> ValidateAsync(
        RequiredGameFileManifest manifest,
        MinecraftDownloadOperationContext? operationContext,
        CancellationToken cancellationToken)
    {
        var failures = new List<GameFileRepairFailure>();
        var missing = 0;
        var corrupted = 0;
        var unverifiable = 0;
        var repairable = 0;
        var fullVerificationCount = 0;
        var currentOperationVerificationReuseCount = 0;
        foreach (var file in manifest.Files.Where(file => file.Required))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(file.ManagedRoot))
            {
                MinecraftPathGuard.EnsureSafeFileDestination(
                    file.TargetPath,
                    file.ManagedRoot,
                    $"Required {file.Category} file");
            }
            if (file.ForcedFailureReason is { } forcedReason)
            {
                if (forcedReason == GameFileRepairFailureReason.MetadataIncomplete)
                    unverifiable++;
                else if (forcedReason == GameFileRepairFailureReason.Missing)
                    missing++;
                else
                    corrupted++;
                if (!string.Equals(file.RecoveryMethod, "Unavailable", StringComparison.Ordinal))
                    repairable++;
                failures.Add(new GameFileRepairFailure(
                    file.TargetPath,
                    file.Category,
                    forcedReason,
                    file.RecoveryMethod,
                    file.Reason));
                continue;
            }
            using var verifiedLease = AcquireCurrentOperationVerificationLease(file, operationContext);
            var verifiedByCurrentOperation = verifiedLease is not null;
            var verification = verifiedByCurrentOperation
                ? MinecraftFileVerification.SizeOnly
                : file.Verification;
            if (verifiedByCurrentOperation)
                currentOperationVerificationReuseCount++;
            else if (verification == MinecraftFileVerification.Full)
                fullVerificationCount++;
            var status = await MinecraftFileIntegrity.EvaluateAsync(file.TargetPath, file.Sha1, file.Size, verification, cancellationToken).ConfigureAwait(false);
            if (status == MinecraftFileIntegrityStatus.Valid
                && file.Sha256 is { Length: 64 } expectedSha256
                && !await MatchesSha256Async(file.TargetPath, expectedSha256, cancellationToken).ConfigureAwait(false))
            {
                status = MinecraftFileIntegrityStatus.HashMismatch;
            }
            if (status == MinecraftFileIntegrityStatus.Valid
                && file.Category.StartsWith("Loader", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(file.Sha1)
                && IsArchivePath(file.TargetPath)
                && !await IsReadableArchiveAsync(file.TargetPath, cancellationToken).ConfigureAwait(false))
            {
                status = MinecraftFileIntegrityStatus.HashMismatch;
            }
            if (status == MinecraftFileIntegrityStatus.Valid && IsOrdinaryFile(file.TargetPath))
            {
                if (string.IsNullOrWhiteSpace(file.Sha1) && file.Size is null)
                    unverifiable++;
                continue;
            }

            var reason = status == MinecraftFileIntegrityStatus.Missing
                ? GameFileRepairFailureReason.Missing
                : GameFileRepairFailureReason.Corrupted;
            if (reason == GameFileRepairFailureReason.Missing)
                missing++;
            else
                corrupted++;
            if (!string.Equals(file.RecoveryMethod, "Unavailable", StringComparison.Ordinal))
                repairable++;
            failures.Add(new GameFileRepairFailure(file.TargetPath, file.Category, reason, file.RecoveryMethod, file.Source));
        }
        return new GameFileValidationReport(
            missing,
            corrupted,
            unverifiable,
            repairable,
            failures,
            fullVerificationCount,
            currentOperationVerificationReuseCount);
    }

    private static MinecraftVerifiedFileLease? AcquireCurrentOperationVerificationLease(
        RequiredGameFile file,
        MinecraftDownloadOperationContext? operationContext)
    {
        return operationContext is not null && MinecraftFileIntegrity.IsSha1(file.Sha1)
            ? operationContext.AcquireVerifiedFileLease(
                file.TargetPath,
                DownloadIntegrityExpectation.Sha1(file.Sha1!, file.Size))
            : null;
    }

    private static bool IsOrdinaryFile(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsArchivePath(string path) =>
        Path.GetExtension(path) is ".jar" or ".zip";

    private static async Task<bool> MatchesSha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsReadableArchiveAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length < 0)
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return false;
        }
    }
}

internal sealed record FinalLaunchCommandPath(string Path, string Category, bool IsDirectory);

internal static partial class FinalLaunchCommandPathReader
{
    private static readonly HashSet<string> PathListOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-cp", "-classpath", "--class-path", "--module-path", "-p"
    };

    public static IEnumerable<FinalLaunchCommandPath> Read(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList.ToList()
            : Tokenize(startInfo.Arguments).ToList();
        return Read(arguments);
    }

    public static IReadOnlyList<string> ReadAllowedUserFilePaths(string? arguments, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var baseDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
        return Read(MArgument.FromCommandLine(arguments).Values.ToList())
            .Where(reference => reference.Category is "JavaAgent" or "LoggingConfiguration")
            .Select(reference => Path.GetFullPath(reference.Path, baseDirectory))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<FinalLaunchCommandPath> Read(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (PathListOptions.Contains(argument) && index + 1 < arguments.Count)
            {
                foreach (var path in arguments[++index].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return new FinalLaunchCommandPath(path, "Classpath", false);
                continue;
            }
            if (argument.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument["-javaagent:".Length..];
                var separator = value.IndexOf('=');
                yield return new FinalLaunchCommandPath(separator < 0 ? value : value[..separator], "JavaAgent", false);
                continue;
            }
            const string nativePrefix = "-Djava.library.path=";
            if (argument.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var path in argument[nativePrefix.Length..].Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return new FinalLaunchCommandPath(path, "NativeDirectory", true);
                continue;
            }
            if (argument.StartsWith("-Dlog4j.configurationFile=", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-Dlog4j2.configurationFile=", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument[(argument.IndexOf('=') + 1)..];
                yield return new FinalLaunchCommandPath(value, "LoggingConfiguration", false);
            }
        }
    }

    private static IEnumerable<string> Tokenize(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];
        return ArgumentRegex().Matches(arguments)
            .Select(match => match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value);
    }

    [GeneratedRegex("(?:\\\"(?<quoted>[^\\\"]*)\\\")|(?<plain>\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentRegex();
}
