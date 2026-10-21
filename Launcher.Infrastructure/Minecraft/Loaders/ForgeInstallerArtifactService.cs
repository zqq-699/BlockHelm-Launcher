/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record ForgeInstallerLibrary(ManagedLibraryArtifact Artifact, string? EmbeddedEntryName);

internal sealed record ForgeProcessorOutput(string RelativePath, string? TrustedSha1);

internal sealed record ForgeInstallerPlan(
    IReadOnlyList<ForgeInstallerLibrary> PrerequisiteLibraries,
    IReadOnlyList<ForgeInstallerLibrary> RuntimeLibraries,
    IReadOnlyList<ForgeProcessorOutput> ProcessorOutputs,
    JsonArray RuntimeLibraryMetadata)
{
    public IEnumerable<ForgeInstallerLibrary> AllLibraries => PrerequisiteLibraries.Concat(RuntimeLibraries);
}

/// <summary>
/// Materializes the libraries declared by a Forge installer and normalizes the final
/// version JSON. All requirements come from installer metadata; the installer archive
/// only chooses between an embedded copy and the normal Maven download path.
/// </summary>
internal sealed partial class LoaderInstallerArtifactService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HttpClient httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;

    public LoaderInstallerArtifactService(
        HttpClient httpClient,
        IForgeInstallerRunner? installerRunner = null,
        IFinalVersionInstaller? finalVersionInstaller = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger? logger = null,
        string? tempRootDirectory = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
    }

    public Task<ForgeInstallerPlan> ReadPlanAsync(string installerJarPath, CancellationToken cancellationToken) =>
        ReadPlanAsync(installerJarPath, ModpackInstallEnvironment.Client, cancellationToken);

    public async Task<ForgeInstallerPlan> ReadPlanAsync(
        string installerJarPath,
        ModpackInstallEnvironment environment,
        CancellationToken cancellationToken)
    {
        await using var installerStream = new FileStream(
            installerJarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(installerStream, ZipArchiveMode.Read, leaveOpen: false);
        var profile = await ReadObjectAsync(archive, "install_profile.json", required: true, cancellationToken).ConfigureAwait(false);
        var installerVersion = await ReadObjectAsync(archive, "version.json", required: false, cancellationToken).ConfigureAwait(false);

        var embeddedEntries = archive.Entries
            .Where(entry => entry.Length > 0 && entry.FullName.StartsWith("maven/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                entry => entry.FullName["maven/".Length..].Replace('\\', '/'),
                entry => entry.FullName,
                StringComparer.OrdinalIgnoreCase);
        AddLegacyInstallerPayload(profile, archive, embeddedEntries);

        var prerequisiteLibraries = new Dictionary<string, ForgeInstallerLibrary>(StringComparer.OrdinalIgnoreCase);
        AddLibraries(profile["libraries"] as JsonArray, embeddedEntries, prerequisiteLibraries);
        var processorOutputs = ReadProcessorRequirements(profile, embeddedEntries, prerequisiteLibraries, environment);

        var runtimeLibraries = new Dictionary<string, ForgeInstallerLibrary>(StringComparer.OrdinalIgnoreCase);
        var runtimeMetadata = installerVersion["libraries"] as JsonArray
            ?? profile["versionInfo"]?["libraries"] as JsonArray
            ?? new JsonArray();
        AddLibraries(runtimeMetadata, embeddedEntries, runtimeLibraries);

        return new ForgeInstallerPlan(
            prerequisiteLibraries.Values.OrderBy(item => item.Artifact.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            runtimeLibraries.Values.OrderBy(item => item.Artifact.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            processorOutputs.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            (JsonArray)runtimeMetadata.DeepClone());
    }

    public async Task MaterializePrerequisitesAsync(
        string installerJarPath,
        ForgeInstallerPlan plan,
        string minecraftDirectory,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? speedMeter = null)
    {
        await MaterializeLibrariesAsync(installerJarPath, plan.PrerequisiteLibraries, minecraftDirectory,
            downloadSourcePreference, downloadSpeedLimitMbPerSecond, cancellationToken, operationContext, speedMeter).ConfigureAwait(false);
    }

    public async Task MaterializeRuntimeLibrariesAsync(
        string installerJarPath,
        ForgeInstallerPlan plan,
        string minecraftDirectory,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? speedMeter = null)
    {
        await MaterializeLibrariesAsync(installerJarPath, plan.RuntimeLibraries, minecraftDirectory,
            downloadSourcePreference, downloadSpeedLimitMbPerSecond, cancellationToken, operationContext, speedMeter).ConfigureAwait(false);
    }

    public async Task ValidatePublishedArtifactsAsync(
        string minecraftDirectory,
        ForgeInstallerPlan plan,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext = null)
    {
        var fullHashVerificationCount = 0;
        var currentOperationVerificationReuseCount = 0;
        foreach (var library in plan.AllLibraries)
        {
            var path = ResolveLibraryPath(minecraftDirectory, library.Artifact.RelativePath);
            using var verifiedLease = AcquireCurrentOperationVerificationLease(path, library.Artifact, operationContext);
            var verifiedByCurrentOperation = verifiedLease is not null;
            var verification = verifiedByCurrentOperation
                ? MinecraftFileVerification.SizeOnly
                : MinecraftFileVerification.Full;
            await EnsureValidAsync(path, library.Artifact.Sha1, library.Artifact.Size, "Forge library", cancellationToken, verification)
                .ConfigureAwait(false);
            if (verifiedByCurrentOperation)
                currentOperationVerificationReuseCount++;
            else if (MinecraftFileIntegrity.IsSha1(library.Artifact.Sha1))
                fullHashVerificationCount++;
        }

        foreach (var output in plan.ProcessorOutputs)
        {
            var path = ResolveLibraryPath(minecraftDirectory, output.RelativePath);
            await EnsureValidAsync(path, output.TrustedSha1, expectedSize: null, "Forge processor output", cancellationToken)
                .ConfigureAwait(false);
            if (MinecraftFileIntegrity.IsSha1(output.TrustedSha1))
                fullHashVerificationCount++;
        }

        logger.LogDebug(
            "Loader installer artifact validation completed. FullHashVerificationCount={FullHashVerificationCount} CurrentOperationVerificationReuseCount={CurrentOperationVerificationReuseCount}",
            fullHashVerificationCount,
            currentOperationVerificationReuseCount);
    }

    public static IReadOnlyDictionary<string, VerifiedSharedFileExpectation> CreateTrustedSharedLibraryExpectations(
        ForgeInstallerPlan plan)
    {
        var expectations = new Dictionary<string, VerifiedSharedFileExpectation>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in plan.AllLibraries
            .Where(library => MinecraftFileIntegrity.IsSha1(library.Artifact.Sha1))
            .OrderBy(library => library.Artifact.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = $"libraries/{library.Artifact.RelativePath.Replace('\\', '/')}";
            var expectation = new VerifiedSharedFileExpectation(library.Artifact.Sha1!, library.Artifact.Size);
            if (expectations.TryGetValue(relativePath, out var existing)
                && (!string.Equals(existing.Sha1, expectation.Sha1, StringComparison.OrdinalIgnoreCase)
                    || existing.Size != expectation.Size))
            {
                throw new InvalidDataException($"Forge installer plan contains conflicting expectations for {relativePath}.");
            }

            expectations[relativePath] = expectation;
        }

        return expectations;
    }

    public static IReadOnlyDictionary<string, string?> CreateReplaceableProcessorOutputExpectations(
        ForgeInstallerPlan plan)
    {
        var expectations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in plan.ProcessorOutputs
                     .OrderBy(output => output.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            expectations[$"libraries/{output.RelativePath.Replace('\\', '/')}"] = output.TrustedSha1;
        }

        return expectations;
    }

    public static async Task ApplyRuntimeLibrariesAsync(
        string versionJsonPath,
        ForgeInstallerPlan plan,
        string legacyMetadataKey,
        CancellationToken cancellationToken)
    {
        var versionJson = await ReadVersionJsonAsync(versionJsonPath, cancellationToken).ConfigureAwait(false);
        ApplyRuntimeLibraries(versionJson, plan, legacyMetadataKey);
        await AtomicJsonFileWriter.WriteAsync(versionJsonPath, versionJson, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static void ApplyRuntimeLibraries(JsonObject versionJson, ForgeInstallerPlan plan, string legacyMetadataKey)
    {
        versionJson["libraries"] = VersionJsonMergeHelper.MergeLibraries(
            plan.RuntimeLibraryMetadata,
            versionJson["libraries"] as JsonArray);
        RemoveLegacyManifest(versionJson, legacyMetadataKey);
    }

    private async Task MaterializeLibrariesAsync(
        string installerJarPath,
        IEnumerable<ForgeInstallerLibrary> libraries,
        string minecraftDirectory,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext,
        SpeedMeter? speedMeter)
    {
        var archiveEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        await using (var stream = new FileStream(installerJarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (var library in libraries.Where(item => item.EmbeddedEntryName is not null))
            {
                var entry = archive.GetEntry(library.EmbeddedEntryName!);
                if (entry is null || entry.Length == 0)
                    throw new InvalidDataException($"Forge installer embedded library is missing: {library.Artifact.RelativePath}");
                archiveEntries[library.Artifact.RelativePath] = entry;
            }

            foreach (var library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = ResolveLibraryPath(minecraftDirectory, library.Artifact.RelativePath);
                using var verifiedLease = AcquireCurrentOperationVerificationLease(destinationPath, library.Artifact, operationContext);
                var verification = verifiedLease is not null
                    ? MinecraftFileVerification.SizeOnly
                    : MinecraftFileVerification.Full;
                var status = await MinecraftFileIntegrity.EvaluateAsync(destinationPath, library.Artifact.Sha1, library.Artifact.Size,
                    verification, cancellationToken).ConfigureAwait(false);
                if (status == MinecraftFileIntegrityStatus.Valid)
                    continue;

                if (archiveEntries.TryGetValue(library.Artifact.RelativePath, out var entry))
                {
                    await CopyEmbeddedLibraryAsync(
                        entry,
                        destinationPath,
                        library.Artifact,
                        minecraftDirectory,
                        cancellationToken,
                        operationContext).ConfigureAwait(false);
                    continue;
                }

                var executor = new MinecraftDownloadRequestExecutor(httpClient, logger,
                    DownloadBandwidthLimiter.Create(downloadSpeedLimitMbPerSecond, downloadSpeedLimitState),
                    category: DownloadConcurrencyCategory.Runtime);
                var logScope = new ForegroundDownloadLogScope(
                    logger,
                    "ForgeInstall",
                    Path.GetFileName(destinationPath),
                    destinationPath,
                    library.Artifact.Url,
                    library.Artifact.Size);
                try
                {
                    var resolution = await executor.DownloadFileAsync(
                        library.Artifact.Url,
                        downloadSourcePreference,
                        library.Artifact.ResourceCategory,
                        destinationPath,
                        library.Artifact.Sha1,
                        library.Artifact.Size,
                        cancellationToken,
                        reportAttemptProgress: logScope.BeginSource(),
                        options: CreateDownloadOptions(library.Artifact, operationContext, minecraftDirectory),
                        speedMeter: speedMeter,
                        reportTransferredBytes: logScope.ReportTransferredBytes).ConfigureAwait(false);
                    logScope.Complete(resolution);
                }
                catch (OperationCanceledException)
                {
                    logScope.CompleteWithoutDownload("Canceled", library.Artifact.Url);
                    throw;
                }
                catch (Exception exception)
                {
                    logScope.Fail(exception, library.Artifact.Url);
                    throw;
                }
            }
        }
    }

    private static async Task CopyEmbeddedLibraryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        ManagedLibraryArtifact artifact,
        string minecraftDirectory,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext)
    {
        var librariesRoot = Path.Combine(minecraftDirectory, "libraries");
        MinecraftPathGuard.EnsureSafeDirectory(
            Path.GetDirectoryName(destinationPath)!,
            librariesRoot,
            "Forge library directory");
        MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, librariesRoot, "Forge library");
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            MinecraftPathGuard.EnsureNoReparsePoints(librariesRoot, temporaryPath, "Forge library temporary file");
            await using (var source = entry.Open())
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            await EnsureValidAsync(temporaryPath, artifact.Sha1, artifact.Size, "Embedded Forge library", cancellationToken)
                .ConfigureAwait(false);
            MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, librariesRoot, "Forge library");
            MinecraftPathGuard.EnsureNoReparsePoints(librariesRoot, temporaryPath, "Forge library temporary file");
            File.Move(temporaryPath, destinationPath, overwrite: true);
            MarkVerified(destinationPath, artifact, operationContext);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task EnsureValidAsync(
        string path,
        string? expectedSha1,
        long? expectedSize,
        string kind,
        CancellationToken cancellationToken,
        MinecraftFileVerification verification = MinecraftFileVerification.Full)
    {
        var status = await MinecraftFileIntegrity.EvaluateAsync(path, expectedSha1, expectedSize, verification, cancellationToken)
            .ConfigureAwait(false);
        if (status != MinecraftFileIntegrityStatus.Valid)
            throw new InvalidDataException($"{kind} is missing or invalid ({status}): {path}");
    }

    private static DownloadFileOptions? CreateDownloadOptions(
        ManagedLibraryArtifact artifact,
        MinecraftDownloadOperationContext? operationContext,
        string minecraftDirectory)
    {
        return operationContext is not null && MinecraftFileIntegrity.IsSha1(artifact.Sha1)
            ? new DownloadFileOptions(
                DownloadPersistenceMode.TaskScopedResumable,
                operationContext,
                Path.Combine(minecraftDirectory, "libraries"))
            : new DownloadFileOptions(ManagedRoot: Path.Combine(minecraftDirectory, "libraries"));
    }

    private static MinecraftVerifiedFileLease? AcquireCurrentOperationVerificationLease(
        string destinationPath,
        ManagedLibraryArtifact artifact,
        MinecraftDownloadOperationContext? operationContext)
    {
        return operationContext is not null && MinecraftFileIntegrity.IsSha1(artifact.Sha1)
            ? operationContext.AcquireVerifiedFileLease(
                destinationPath,
                DownloadIntegrityExpectation.Sha1(artifact.Sha1!, artifact.Size))
            : null;
    }

    private static void MarkVerified(
        string destinationPath,
        ManagedLibraryArtifact artifact,
        MinecraftDownloadOperationContext? operationContext)
    {
        if (operationContext is not null && MinecraftFileIntegrity.IsSha1(artifact.Sha1))
        {
            operationContext.MarkVerified(
                destinationPath,
                DownloadIntegrityExpectation.Sha1(artifact.Sha1!, artifact.Size));
        }
    }

    private static async Task<JsonObject> ReadObjectAsync(ZipArchive archive, string entryName, bool required, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            if (required)
                throw new InvalidDataException($"Forge installer does not contain {entryName}.");
            return new JsonObject();
        }
        await using var stream = entry.Open();
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException($"Forge installer {entryName} is empty or invalid.");
    }

    private static void AddLibraries(
        JsonArray? libraries,
        IReadOnlyDictionary<string, string> embeddedEntries,
        IDictionary<string, ForgeInstallerLibrary> destination)
    {
        if (libraries is null)
            return;
        foreach (var library in libraries.OfType<JsonObject>())
        {
            if (!ManagedLibraryArtifactResolver.IsAllowed(library))
                continue;
            var resolved = ManagedLibraryArtifactResolver.EnumerateDownloads(library).ToList();
            if (resolved.Count == 0)
                throw new InvalidDataException($"Forge installer library cannot be resolved: {library["name"]?.GetValue<string>() ?? library.ToJsonString()}");
            foreach (var artifact in resolved)
                AddLibrary(artifact, embeddedEntries, destination);
        }
    }

    private static void AddLegacyInstallerPayload(
        JsonObject profile,
        ZipArchive archive,
        IDictionary<string, string> embeddedEntries)
    {
        if (profile["install"] is not JsonObject install)
            return;
        var coordinate = install["path"]?.GetValue<string>()?.Trim();
        var entryName = install["filePath"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(coordinate)
            || string.IsNullOrWhiteSpace(entryName)
            || archive.GetEntry(entryName) is null
            || !ManagedLibraryArtifactResolver.TryBuildMavenPath(
                coordinate,
                classifierOverride: null,
                out var relativePath)
            || !IsSafeRelativePath(relativePath))
        {
            return;
        }
        embeddedEntries[relativePath.Replace('\\', '/')] = entryName;
    }

    private static IReadOnlyList<ForgeProcessorOutput> ReadProcessorRequirements(
        JsonObject profile,
        IReadOnlyDictionary<string, string> embeddedEntries,
        IDictionary<string, ForgeInstallerLibrary> prerequisites,
        ModpackInstallEnvironment environment)
    {
        if (profile["processors"] is not JsonArray processors)
            return [];
        var data = ReadSideData(profile["data"] as JsonObject, environment);
        var outputs = new Dictionary<string, ForgeProcessorOutput>(StringComparer.OrdinalIgnoreCase);
        var sideProcessors = processors
            .OfType<JsonObject>()
            .Where(processor => RunsOnSide(processor, environment))
            .ToArray();

        // Forge processor chains commonly feed an earlier generated Maven-shaped
        // output into a later processor. Resolve the complete output set first so
        // those values are not mistaken for remotely downloadable prerequisites.
        foreach (var processor in sideProcessors)
        {
            AddCoordinate(processor["jar"]?.GetValue<string>(), embeddedEntries, prerequisites);
            if (processor["classpath"] is JsonArray classpath)
            {
                foreach (var item in classpath)
                    AddCoordinate(item?.GetValue<string>(), embeddedEntries, prerequisites);
            }
            if (processor["outputs"] is JsonObject declaredOutputs)
            {
                foreach (var output in declaredOutputs)
                    AddOutput(outputs, output.Key, output.Value?.GetValue<string>(), data);
            }
            if (processor["args"] is not JsonArray arguments)
                continue;
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index]?.GetValue<string>();
                if (IsProcessorOutputOption(argument) && index + 1 < arguments.Count)
                {
                    TryAddArgumentOutput(outputs, arguments[index + 1]?.GetValue<string>(), data);
                    index++;
                }
            }
        }

        foreach (var processor in sideProcessors)
        {
            if (processor["args"] is not JsonArray arguments)
                continue;
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index]?.GetValue<string>();
                if (IsProcessorOutputOption(argument) && index + 1 < arguments.Count)
                {
                    index++;
                    continue;
                }
                AddArgumentCoordinate(argument, data, outputs, embeddedEntries, prerequisites);
            }
        }
        return outputs.Values.ToArray();
    }

    private static bool IsProcessorOutputOption(string? argument) =>
        argument is "--output"
            or "--slim"
            or "--extra"
            or "--out-jar"
            or "--jar-out";

    private static void AddCoordinate(string? coordinate, IReadOnlyDictionary<string, string> embeddedEntries,
        IDictionary<string, ForgeInstallerLibrary> prerequisites)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
            return;
        AddLibraries(new JsonArray(new JsonObject { ["name"] = coordinate.Trim() }), embeddedEntries, prerequisites);
    }

    private static void AddArgumentCoordinate(
        string? expression,
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, ForgeProcessorOutput> outputs,
        IReadOnlyDictionary<string, string> embeddedEntries,
        IDictionary<string, ForgeInstallerLibrary> prerequisites)
    {
        var resolved = ResolveExpression(expression, data).Trim();
        if (resolved.Length < 3 || resolved[0] != '[' || resolved[^1] != ']')
            return;
        var coordinate = resolved[1..^1];
        if (!ManagedLibraryArtifactResolver.TryBuildMavenPath(
                coordinate,
                classifierOverride: null,
                out var relativePath)
            || !IsSafeRelativePath(relativePath))
        {
            throw new InvalidDataException($"Forge processor input cannot be resolved: {expression}");
        }
        if (outputs.ContainsKey(relativePath))
            return;
        AddCoordinate(coordinate, embeddedEntries, prerequisites);
    }

    private static void AddLibrary(ManagedLibraryArtifact artifact, IReadOnlyDictionary<string, string> embeddedEntries,
        IDictionary<string, ForgeInstallerLibrary> destination)
    {
        if (!IsSafeRelativePath(artifact.RelativePath))
            throw new InvalidDataException($"Forge installer declared an unsafe library path: {artifact.RelativePath}");
        var normalizedPath = artifact.RelativePath.Replace('\\', '/');
        embeddedEntries.TryGetValue(normalizedPath, out var embeddedEntryName);
        var candidate = new ForgeInstallerLibrary(artifact with { RelativePath = normalizedPath }, embeddedEntryName);
        if (!destination.TryGetValue(normalizedPath, out var existing))
        {
            destination[normalizedPath] = candidate;
            return;
        }
        if (!Compatible(existing.Artifact.Sha1, candidate.Artifact.Sha1)
            || !Compatible(existing.Artifact.Size, candidate.Artifact.Size))
        {
            throw new InvalidDataException($"Forge installer declared conflicting library metadata: {normalizedPath}");
        }
        var selected = new ManagedLibraryArtifact(
            existing.Artifact.Url,
            normalizedPath,
            existing.Artifact.LibraryName ?? candidate.Artifact.LibraryName,
            existing.Artifact.ResourceCategory,
            existing.Artifact.Sha1 ?? candidate.Artifact.Sha1,
            existing.Artifact.Size ?? candidate.Artifact.Size);
        destination[normalizedPath] = new ForgeInstallerLibrary(selected, existing.EmbeddedEntryName ?? candidate.EmbeddedEntryName);
    }

    private static bool Compatible(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Compatible(long? left, long? right) => left is null || right is null || left == right;

    private static Dictionary<string, string> ReadSideData(
        JsonObject? data,
        ModpackInstallEnvironment environment)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data is null)
            return values;
        var sideName = environment is ModpackInstallEnvironment.Server ? "server" : "client";
        foreach (var pair in data)
        {
            if (pair.Value is JsonObject sides
                && sides[sideName] is JsonValue side
                && side.TryGetValue<string>(out var value)
                && !string.IsNullOrWhiteSpace(value))
                values[pair.Key] = value;
        }
        return values;
    }

    private static void AddOutput(IDictionary<string, ForgeProcessorOutput> outputs, string? pathExpression,
        string? hashExpression, IReadOnlyDictionary<string, string> data)
    {
        if (string.IsNullOrWhiteSpace(pathExpression))
            return;
        var token = TryReadSinglePlaceholder(pathExpression);
        var coordinateExpression = ResolveExpression(pathExpression, data).Trim();
        if (coordinateExpression.Length < 3 || coordinateExpression[0] != '[' || coordinateExpression[^1] != ']'
            || !ManagedLibraryArtifactResolver.TryBuildMavenPath(coordinateExpression[1..^1], classifierOverride: null, out var relativePath)
            || !IsSafeRelativePath(relativePath))
            throw new InvalidDataException($"Forge processor output cannot be resolved: {pathExpression}");
        var resolvedHash = TrimLiteral(ResolveExpression(hashExpression, data));
        if (!MinecraftFileIntegrity.IsSha1(resolvedHash) && token is not null && data.TryGetValue($"{token}_SHA", out var tokenHash))
            resolvedHash = TrimLiteral(tokenHash);
        if (!MinecraftFileIntegrity.IsSha1(resolvedHash))
            resolvedHash = null;
        var isPatchedOutput = IsPatchedOutput(token, relativePath, data);
        if (isPatchedOutput)
            resolvedHash = null;
        if (outputs.TryGetValue(relativePath, out var existing) && !Compatible(existing.TrustedSha1, resolvedHash))
            throw new InvalidDataException($"Forge processor declared conflicting output checksums: {relativePath}");
        outputs[relativePath] = new ForgeProcessorOutput(
            relativePath,
            isPatchedOutput
                ? null
                : existing?.TrustedSha1 ?? resolvedHash);
    }

    private static bool IsPatchedOutput(
        string? token,
        string relativePath,
        IReadOnlyDictionary<string, string> data)
    {
        if (string.Equals(token, "PATCHED", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!data.TryGetValue("PATCHED", out var patchedExpression))
            return false;

        var resolved = ResolveExpression(patchedExpression, data).Trim();
        return resolved.Length >= 3
            && resolved[0] == '['
            && resolved[^1] == ']'
            && ManagedLibraryArtifactResolver.TryBuildMavenPath(
                resolved[1..^1],
                classifierOverride: null,
                out var patchedRelativePath)
            && string.Equals(relativePath, patchedRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAddArgumentOutput(
        IDictionary<string, ForgeProcessorOutput> outputs,
        string? pathExpression,
        IReadOnlyDictionary<string, string> data)
    {
        if (string.IsNullOrWhiteSpace(pathExpression))
            return;
        var resolved = ResolveExpression(pathExpression, data).Trim();
        if (resolved.Length < 2 || resolved[0] != '[' || resolved[^1] != ']')
            return;
        AddOutput(outputs, pathExpression, hashExpression: null, data);
    }

    private static void RemoveLegacyManifest(JsonObject versionJson, string metadataKey)
    {
        if (versionJson["launcher"] is not JsonObject launcher)
            return;
        launcher.Remove(metadataKey);
        if (launcher.Count == 0)
            versionJson.Remove("launcher");
    }

    private static async Task<JsonObject> ReadVersionJsonAsync(
        string versionJsonPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            versionJsonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException($"Version metadata is empty: {versionJsonPath}");
    }

    private static bool RunsOnSide(JsonObject processor, ModpackInstallEnvironment environment)
    {
        var sideName = environment is ModpackInstallEnvironment.Server ? "server" : "client";
        return processor["sides"] is not JsonArray sides || sides.Count == 0
            || sides.Any(side => string.Equals(side?.GetValue<string>(), sideName, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex PlaceholderRegex = new("\\{(?<name>[A-Z0-9_]+)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string ResolveExpression(string? expression, IReadOnlyDictionary<string, string> values) =>
        string.IsNullOrWhiteSpace(expression) ? string.Empty : PlaceholderRegex.Replace(expression,
            match => values.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);

    private static string? TryReadSinglePlaceholder(string expression)
    {
        var trimmed = expression.Trim();
        var match = PlaceholderRegex.Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length ? match.Groups["name"].Value : null;
    }

    private static string? TrimLiteral(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('\'', '"');

    private static string ResolveLibraryPath(string minecraftDirectory, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException($"Unsafe Forge library path: {relativePath}");
        var root = Path.Combine(minecraftDirectory, "libraries");
        var path = MinecraftPathGuard.EnsureWithin(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            root,
            "Forge library");
        return MinecraftPathGuard.EnsureSafeFileDestination(path, root, "Forge library");
    }

    private static bool IsSafeRelativePath(string relativePath) => !string.IsNullOrWhiteSpace(relativePath)
        && !Path.IsPathRooted(relativePath) && !relativePath.Split('/', '\\').Any(segment => segment is "" or "." or "..");

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
