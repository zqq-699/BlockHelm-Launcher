/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class LoaderInstallerPrerequisiteSeeder
{
    private static readonly string[] SharedDirectoryNames = ["libraries", "assets", "resources"];
    private readonly HttpClient? httpClient;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly ILogger logger;

    public LoaderInstallerPrerequisiteSeeder(ILogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
    }

    internal LoaderInstallerPrerequisiteSeeder(
        HttpClient httpClient,
        IDownloadSpeedLimitState? downloadSpeedLimitState,
        ILogger? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger.Instance;
    }

    internal async Task<LoaderInstallerWorkspaceSnapshot> PrepareBaseAsync(
        string sharedMinecraftDirectory,
        string installerMinecraftDirectory,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken,
        string loaderCategory = "Loader",
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? speedMeter = null)
    {
        var metadata = await PrepareBaseMetadataAsync(installerMinecraftDirectory, minecraftVersion,
            downloadSourcePreference, downloadSpeedLimitMbPerSecond, cancellationToken, loaderCategory).ConfigureAwait(false);
        return await PrepareBaseClientAsync(sharedMinecraftDirectory, metadata, minecraftVersion,
            downloadSourcePreference, downloadSpeedLimitMbPerSecond, cancellationToken, operationContext, speedMeter).ConfigureAwait(false);
    }

    internal async Task<LoaderInstallerWorkspaceSnapshot> SeedInstallerLibrariesAsync(
        string sharedMinecraftDirectory,
        string installerJarPath,
        LoaderInstallerWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken,
        Func<string, bool>? includeLibrary = null)
    {
        var seededFiles = new Dictionary<string, string>(snapshot.SeededFiles, StringComparer.OrdinalIgnoreCase);
        await SeedInstallerLibrariesAsync(sharedMinecraftDirectory, snapshot.WorkspaceMinecraftDirectory, installerJarPath,
            seededFiles, cancellationToken, includeLibrary).ConfigureAwait(false);
        return snapshot with { SeededFiles = seededFiles };
    }

    public async Task PublishDeltaAsync(
        LoaderInstallerWorkspaceSnapshot snapshot,
        string destinationMinecraftDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, VerifiedSharedFileExpectation>? trustedFileExpectations = null,
        MinecraftDownloadOperationContext? operationContext = null,
        IReadOnlyDictionary<string, string?>? replaceableFileExpectations = null)
    {
        var verifiedSharedFiles = await ReadVerifiedSharedFileExpectationsAsync(
            snapshot.WorkspaceMinecraftDirectory,
            cancellationToken).ConfigureAwait(false);

        foreach (var directoryName in SharedDirectoryNames)
        {
            var sourceDirectory = Path.Combine(snapshot.WorkspaceMinecraftDirectory, directoryName);
            if (!Directory.Exists(sourceDirectory))
                continue;

            foreach (var sourcePath in EnumerateOrdinaryFiles(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var confinedSourcePath = MinecraftPathGuard.EnsureWithin(
                    sourcePath,
                    sourceDirectory,
                    "Loader installer shared output");
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(snapshot.WorkspaceMinecraftDirectory, sourcePath));
                var destinationRoot = Path.Combine(destinationMinecraftDirectory, directoryName);
                var destinationPath = MinecraftPathGuard.EnsureWithin(
                    Path.Combine(destinationMinecraftDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                    destinationRoot,
                    "Loader installer shared destination");
                EnsureOrdinaryExistingPath(destinationRoot, destinationPath);
                if (replaceableFileExpectations?.TryGetValue(relativePath, out var replaceableExpectedSha1) is true)
                {
                    var stableSourceSha1 = MinecraftFileIntegrity.IsSha1(replaceableExpectedSha1)
                        ? replaceableExpectedSha1!
                        : AtomicSharedFilePublisher.ComputeSha1(confinedSourcePath);
                    var result = await AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
                        confinedSourcePath,
                        destinationPath,
                        stableSourceSha1,
                        cancellationToken,
                        destinationRoot, logger).ConfigureAwait(false);
                    if (result.Disposition == SharedFilePublishDisposition.Replaced)
                    {
                        logger.LogDebug(
                            "Replaced shared loader processor output. RelativePath={RelativePath}",
                            relativePath);
                    }
                    continue;
                }
                if (snapshot.SeededFiles.TryGetValue(relativePath, out var seededSha1))
                {
                    VerifiedSharedFileExpectation? trustedExpectation = null;
                    var hasTrustedExpectation = trustedFileExpectations is not null
                        && trustedFileExpectations.TryGetValue(relativePath, out trustedExpectation);
                    var sourceSha1 = AtomicSharedFilePublisher.ComputeSha1(confinedSourcePath);
                    if (!string.Equals(sourceSha1, seededSha1, StringComparison.OrdinalIgnoreCase))
                        throw SafeExceptionLog.Describe(new InvalidDataException($"Loader installer modified a seeded prerequisite: {relativePath}"), "SeededPrerequisiteChanged", ("Artifact", relativePath), ("ExpectedHash", seededSha1), ("ActualHash", sourceSha1));

                    if (hasTrustedExpectation
                        && !string.Equals(sourceSha1, trustedExpectation!.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        throw SafeExceptionLog.Describe(new InvalidDataException($"Loader installer seeded prerequisite did not match the trusted installation plan: {relativePath}"), "SeededPrerequisiteHashMismatch", ("Artifact", relativePath), ("ExpectedHash", trustedExpectation!.Sha1), ("ActualHash", sourceSha1));
                    }

                    await AtomicSharedFilePublisher.PublishCopyAsync(
                        confinedSourcePath,
                        destinationPath,
                        trustedExpectation?.Sha1 ?? seededSha1,
                        cancellationToken,
                        destinationRoot, logger).ConfigureAwait(false);
                    MarkVerified(destinationPath, trustedExpectation, operationContext);
                    continue;
                }

                if (trustedFileExpectations?.TryGetValue(relativePath, out var plannedExpectation) is true)
                {
                    await AtomicSharedFilePublisher.PublishCopyAsync(
                        confinedSourcePath,
                        destinationPath,
                        plannedExpectation.Sha1,
                        cancellationToken,
                        destinationRoot, logger).ConfigureAwait(false);
                    MarkVerified(destinationPath, plannedExpectation, operationContext);
                    continue;
                }

                if (verifiedSharedFiles.TryGetValue(relativePath, out var expectation))
                {
                    if (expectation.Size is > 0 && new FileInfo(confinedSourcePath).Length != expectation.Size.Value)
                    {
                        throw SafeExceptionLog.Describe(new InvalidDataException($"Loader installer shared file size did not match version metadata: {relativePath}"), "SharedFileSizeMismatch", ("Artifact", relativePath), ("ExpectedBytes", expectation.Size), ("ActualBytes", new FileInfo(confinedSourcePath).Length));
                    }

                    var result = await AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
                        confinedSourcePath,
                        destinationPath,
                        expectation.Sha1,
                        cancellationToken,
                        destinationRoot, logger).ConfigureAwait(false);
                    if (result.Disposition == SharedFilePublishDisposition.Replaced)
                    {
                        logger.LogDebug(
                            "Replaced shared Minecraft file after metadata validation. RelativePath={RelativePath} ExpectedSha1={ExpectedSha1}",
                            relativePath,
                            expectation.Sha1);
                    }
                    MarkVerified(destinationPath, expectation, operationContext);
                }
                else
                {
                    await AtomicSharedFilePublisher.PublishCopyAsync(
                        confinedSourcePath,
                        destinationPath,
                        expectedSha1: null,
                        cancellationToken: cancellationToken,
                        managedRoot: destinationRoot, logger: logger).ConfigureAwait(false);
                }
            }
        }
    }

    private static void MarkVerified(
        string destinationPath,
        VerifiedSharedFileExpectation? expectation,
        MinecraftDownloadOperationContext? operationContext)
    {
        if (expectation is not null && MinecraftFileIntegrity.IsSha1(expectation.Sha1))
        {
            operationContext?.MarkVerified(
                destinationPath,
                DownloadIntegrityExpectation.Sha1(expectation.Sha1, expectation.Size));
        }
    }

    private static async Task<IReadOnlyDictionary<string, VerifiedSharedFileExpectation>> ReadVerifiedSharedFileExpectationsAsync(
        string workspaceMinecraftDirectory,
        CancellationToken cancellationToken)
    {
        var expectations = new Dictionary<string, VerifiedSharedFileExpectation>(StringComparer.OrdinalIgnoreCase);
        var ambiguousPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionsDirectory = Path.Combine(workspaceMinecraftDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
            return expectations;

        foreach (var versionJsonPath in EnumerateOrdinaryFiles(versionsDirectory)
                     .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(
                    await File.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false)) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (root is null)
            {
                continue;
            }

            if (root["assetIndex"] is JsonObject assetIndex
                && GetString(assetIndex["id"]) is { } id
                && IsOrdinaryFileName(id)
                && GetString(assetIndex["sha1"]) is { } assetIndexSha1
                && MinecraftFileIntegrity.IsSha1(assetIndexSha1))
            {
                var relativeIndexPath = $"assets/indexes/{id}.json";
                var indexExpectation = new VerifiedSharedFileExpectation(
                    assetIndexSha1,
                    GetLong(assetIndex["size"]));
                AddExpectation(
                    relativeIndexPath,
                    indexExpectation,
                    expectations,
                    ambiguousPaths);
            }

            if (root["logging"]?["client"]?["file"] is JsonObject loggingFile
                && GetString(loggingFile["id"]) is { } loggingId
                && IsOrdinaryFileName(loggingId)
                && GetString(loggingFile["sha1"]) is { } loggingSha1
                && MinecraftFileIntegrity.IsSha1(loggingSha1))
            {
                AddExpectation(
                    $"assets/log_configs/{loggingId}",
                    new VerifiedSharedFileExpectation(loggingSha1, GetLong(loggingFile["size"])),
                    expectations,
                    ambiguousPaths);
            }
        }

        foreach (var item in expectations
                     .Where(item => item.Key.StartsWith("assets/indexes/", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var assetIndexId = Path.GetFileNameWithoutExtension(item.Key);
            await AddDerivedAssetExpectationsAsync(
                workspaceMinecraftDirectory,
                assetIndexId,
                item.Key,
                item.Value,
                expectations,
                ambiguousPaths,
                cancellationToken).ConfigureAwait(false);
        }

        return expectations;
    }

    private static async Task AddDerivedAssetExpectationsAsync(
        string workspaceMinecraftDirectory,
        string assetIndexId,
        string relativeIndexPath,
        VerifiedSharedFileExpectation indexExpectation,
        IDictionary<string, VerifiedSharedFileExpectation> expectations,
        ISet<string> ambiguousPaths,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(
            workspaceMinecraftDirectory,
            relativeIndexPath.Replace('/', Path.DirectorySeparatorChar));
        if (!await MinecraftFileIntegrity.IsValidAsync(
                indexPath,
                indexExpectation.Sha1,
                indexExpectation.Size,
                MinecraftFileVerification.Full,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        JsonObject? indexRoot;
        try
        {
            await using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            indexRoot = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (indexRoot?["objects"] is not JsonObject objects)
            return;

        var isVirtual = GetBoolean(indexRoot["virtual"]);
        var mapsToResources = GetBoolean(indexRoot["map_to_resources"]);
        if (!isVirtual && !mapsToResources)
            return;

        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Value is not JsonObject asset
                || GetString(asset["hash"]) is not { } hash
                || !MinecraftFileIntegrity.IsSha1(hash))
            {
                continue;
            }

            var expectation = new VerifiedSharedFileExpectation(hash, GetLong(asset["size"]));
            if (isVirtual
                && TryCreateDerivedRelativePath(
                    workspaceMinecraftDirectory,
                    Path.Combine("assets", "virtual", assetIndexId),
                    item.Key,
                    out var virtualPath))
            {
                AddExpectation(virtualPath, expectation, expectations, ambiguousPaths);
            }

            if (mapsToResources
                && TryCreateDerivedRelativePath(
                    workspaceMinecraftDirectory,
                    "resources",
                    item.Key,
                    out var resourcePath))
            {
                AddExpectation(resourcePath, expectation, expectations, ambiguousPaths);
            }
        }
    }

    private static bool TryCreateDerivedRelativePath(
        string workspaceMinecraftDirectory,
        string relativeRoot,
        string assetName,
        out string relativePath)
    {
        relativePath = string.Empty;
        try
        {
            var root = Path.Combine(workspaceMinecraftDirectory, relativeRoot);
            var candidate = MinecraftPathGuard.EnsureWithin(
                Path.Combine(root, assetName.Replace('/', Path.DirectorySeparatorChar)),
                root,
                "Derived asset output");
            relativePath = NormalizeRelativePath(Path.GetRelativePath(workspaceMinecraftDirectory, candidate));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void AddExpectation(
        string relativePath,
        VerifiedSharedFileExpectation expectation,
        IDictionary<string, VerifiedSharedFileExpectation> expectations,
        ISet<string> ambiguousPaths)
    {
        if (ambiguousPaths.Contains(relativePath))
            return;

        if (!expectations.TryGetValue(relativePath, out var existing))
        {
            expectations[relativePath] = expectation;
            return;
        }

        if (!string.Equals(existing.Sha1, expectation.Sha1, StringComparison.OrdinalIgnoreCase)
            || existing.Size is > 0 && expectation.Size is > 0 && existing.Size != expectation.Size)
        {
            expectations.Remove(relativePath);
            ambiguousPaths.Add(relativePath);
            return;
        }

        if (existing.Size is null && expectation.Size is > 0)
            expectations[relativePath] = expectation;
    }

    private static bool IsOrdinaryFileName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value is not "." and not ".."
            && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool GetBoolean(JsonNode? node)
    {
        try
        {
            return node?.GetValue<bool>() ?? false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw SafeExceptionLog.Describe(new InvalidDataException($"Loader installer output root is a reparse point: {directory}"), "UnsafeReparsePoint", ("Artifact", directory));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw SafeExceptionLog.Describe(new InvalidDataException($"Loader installer output contains a reparse point: {entry}"), "UnsafeReparsePoint", ("Artifact", entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateOrdinaryFiles(entry))
                    yield return file;
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static void EnsureOrdinaryExistingPath(string rootDirectory, string targetPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var target = Path.GetFullPath(targetPath);
        MinecraftPathGuard.EnsureWithin(target, root, "Loader installer shared destination");
        var current = root;
        if (Directory.Exists(current)
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw SafeExceptionLog.Describe(new InvalidDataException($"Shared destination root is a reparse point: {current}"), "UnsafeReparsePoint", ("Artifact", current));
        }
        var relative = Path.GetRelativePath(root, target);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw SafeExceptionLog.Describe(new InvalidDataException($"Shared destination contains a reparse point: {current}"), "UnsafeReparsePoint", ("Artifact", current));
        }
    }

    internal async Task<LoaderInstallerWorkspaceSnapshot> PrepareBaseMetadataAsync(
        string destinationGameDirectory,
        string minecraftVersion,
        DownloadSourcePreference downloadSourcePreference,
        int downloadSpeedLimitMbPerSecond,
        CancellationToken cancellationToken,
        string loaderCategory)
    {
        if (httpClient is null)
            throw SafeExceptionLog.Describe(new InvalidOperationException("Official version metadata is required to seed a loader installer workspace."), "DownloadValidation", ("Reason", "Official version metadata is required to seed a loader installer workspace."));

        logger.LogDebug(
            "Loader installer official base metadata preparation started. LoaderCategory={LoaderCategory} MinecraftVersion={MinecraftVersion}",
            loaderCategory,
            minecraftVersion);
        // Prepare only the base metadata/client here; final content resolves the
        // remaining vanilla library graph against the shared directory.
        try
        {
            var root = await VanillaVersionMetadataClient.DownloadVerifiedVersionJsonAsync(
                    httpClient,
                    minecraftVersion,
                    downloadSourcePreference,
                    downloadSpeedLimitMbPerSecond,
                    downloadSpeedLimitState,
                    logger,
                    cancellationToken)
                .ConfigureAwait(false);
            var destinationJsonPath = Path.Combine(
                destinationGameDirectory,
                "versions",
                minecraftVersion,
                $"{minecraftVersion}.json");
            MinecraftPathGuard.EnsureSafeFileDestination(
                destinationJsonPath,
                destinationGameDirectory,
                "Loader installer base metadata");
            await AtomicJsonFileWriter.WriteAsync(
                    destinationJsonPath,
                    root,
                    new JsonSerializerOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            var seededFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [NormalizeRelativePath(Path.GetRelativePath(destinationGameDirectory, destinationJsonPath))] =
                    AtomicSharedFilePublisher.ComputeSha1(destinationJsonPath)
            };
            var client = root["downloads"]?["client"] as JsonObject;
            logger.LogInformation(
                "Loader installer official base metadata preparation completed. LoaderCategory={LoaderCategory} MinecraftVersion={MinecraftVersion}",
                loaderCategory,
                minecraftVersion);
            return new LoaderInstallerWorkspaceSnapshot(destinationGameDirectory, seededFiles)
            {
                VerifiedVersionMetadata = root,
                CanPrepareBaseContent = TryGetDownloadUri(client, out _)
                    && MinecraftFileIntegrity.IsSha1(GetString(client?["sha1"]))
                    && GetLong(client?["size"]) is > 0
            };
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "Loader installer official base metadata preparation canceled. LoaderCategory={LoaderCategory} MinecraftVersion={MinecraftVersion}",
                loaderCategory,
                minecraftVersion);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Loader installer official base metadata preparation failed. LoaderCategory={LoaderCategory} MinecraftVersion={MinecraftVersion}",
                loaderCategory,
                minecraftVersion);
            throw;
        }
    }

    internal async Task<LoaderInstallerWorkspaceSnapshot> PrepareBaseClientAsync(
        string sourceGameDirectory,
        LoaderInstallerWorkspaceSnapshot metadata,
        string minecraftVersion,
        DownloadSourcePreference preference,
        int speedLimit,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext,
        SpeedMeter? speedMeter)
    {
        var client = metadata.VerifiedVersionMetadata?["downloads"]?["client"] as JsonObject;
        var sha1 = GetString(client?["sha1"]);
        var size = GetLong(client?["size"]);
        if (!MinecraftFileIntegrity.IsSha1(sha1) || size is not > 0)
            return metadata;

        var workspace = metadata.WorkspaceMinecraftDirectory;
        var relativeJar = $"versions/{minecraftVersion}/{minecraftVersion}.jar";
        var destinationJar = Path.Combine(workspace, relativeJar);
        var seededFiles = new Dictionary<string, string>(metadata.SeededFiles, StringComparer.OrdinalIgnoreCase);
        await SeedFileAsync(Path.Combine(sourceGameDirectory, relativeJar), destinationJar,
            sha1, size, workspace, seededFiles, cancellationToken).ConfigureAwait(false);
        if (!seededFiles.ContainsKey(relativeJar) && TryGetDownloadUri(client, out var clientUri))
        {
            await DownloadBaseClientAsync(httpClient!, clientUri!, destinationJar, sha1!, size.Value,
                workspace, preference, speedLimit, operationContext, speedMeter, cancellationToken).ConfigureAwait(false);
            seededFiles[relativeJar] = sha1!;
        }
        return metadata with { SeededFiles = seededFiles };
    }

    private static bool TryGetDownloadUri(JsonObject? metadata, out Uri? uri) =>
        Uri.TryCreate(GetString(metadata?["url"]), UriKind.Absolute, out uri) && uri.Scheme is "https" or "http";

    private async Task DownloadBaseClientAsync(
        HttpClient client, Uri url, string destination, string sha1, long size, string managedRoot,
        DownloadSourcePreference preference, int speedLimit, MinecraftDownloadOperationContext? operationContext,
        SpeedMeter? speedMeter, CancellationToken cancellationToken)
    {
        var executor = new MinecraftDownloadRequestExecutor(client, logger,
            DownloadBandwidthLimiter.Create(speedLimit, downloadSpeedLimitState),
            category: DownloadConcurrencyCategory.Runtime, hostHealthTracker: operationContext?.HostHealth);
        var download = new ForegroundDownloadLogScope(logger, "LoaderBaseClient", Path.GetFileName(destination),
            destination, url.AbsoluteUri, size);
        using var diagnostic = download.RestoreContext();
        try
        {
            var resolution = await executor.DownloadFileAsync(url.AbsoluteUri, preference, "Mojang", destination,
                sha1, size, cancellationToken, reportAttemptProgress: download.BeginSource(),
                options: new DownloadFileOptions(ManagedRoot: managedRoot), speedMeter: speedMeter,
                reportTransferredBytes: download.ReportTransferredBytes).ConfigureAwait(false);
            download.Complete(resolution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            download.CompleteWithoutDownload("Canceled", url.AbsoluteUri);
            throw;
        }
        catch (Exception exception)
        {
            download.Fail(exception, url.AbsoluteUri);
            throw;
        }
    }

    private async Task SeedInstallerLibrariesAsync(
        string sourceGameDirectory,
        string destinationGameDirectory,
        string installerJarPath,
        IDictionary<string, string> seededFiles,
        CancellationToken cancellationToken,
        Func<string, bool>? includeLibrary)
    {
        if (!File.Exists(installerJarPath))
            return;

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(installerJarPath);
        }
        catch (InvalidDataException)
        {
            return;
        }

        using (archive)
        {
            foreach (var entryName in new[] { "install_profile.json", "version.json" })
            {
                var entry = archive.GetEntry(entryName);
                if (entry is null)
                    continue;

                await using var stream = entry.Open();
                JsonNode? node;
                try
                {
                    node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node is not null)
                {
                    await SeedLibrariesFromJsonAsync(
                        node,
                        sourceGameDirectory,
                        destinationGameDirectory,
                        seededFiles,
                        cancellationToken,
                        includeLibrary).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task SeedLibrariesFromJsonAsync(
        JsonNode root,
        string sourceGameDirectory,
        string destinationGameDirectory,
        IDictionary<string, string> seededFiles,
        CancellationToken cancellationToken,
        Func<string, bool>? includeLibrary)
    {
        var destinationLibraryDirectory = Path.Combine(destinationGameDirectory, "libraries");
        foreach (var library in EnumerateLibraryObjects(root))
        {
            var artifacts = new List<JsonObject>();
            if (library["downloads"]?["artifact"] is JsonObject artifact)
                artifacts.Add(artifact);
            if (library["downloads"]?["classifiers"] is JsonObject classifiers)
                artifacts.AddRange(classifiers.Select(item => item.Value).OfType<JsonObject>());

            if (artifacts.Count == 0 && GetString(library["name"]) is { } coordinate)
            {
                var legacyPath = TryCreateMavenPath(coordinate);
                if (legacyPath is not null)
                    artifacts.Add(new JsonObject { ["path"] = legacyPath });
            }

            foreach (var item in artifacts)
            {
                var relativeLibraryPath = GetString(item["path"]);
                if (string.IsNullOrWhiteSpace(relativeLibraryPath))
                    continue;

                var normalizedRelativePath = relativeLibraryPath.Replace('/', Path.DirectorySeparatorChar);
                string sourcePath;
                string destinationPath;
                try
                {
                    sourcePath = MinecraftPathGuard.EnsureWithin(
                        Path.Combine(sourceGameDirectory, "libraries", normalizedRelativePath),
                        Path.Combine(sourceGameDirectory, "libraries"),
                        "Loader prerequisite source");
                    destinationPath = MinecraftPathGuard.EnsureWithin(
                        Path.Combine(destinationLibraryDirectory, normalizedRelativePath),
                        destinationLibraryDirectory,
                        "Loader prerequisite destination");
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                if (includeLibrary is not null && !includeLibrary(NormalizeRelativePath(
                    Path.GetRelativePath(destinationLibraryDirectory, destinationPath))))
                    continue;

                await SeedFileAsync(
                    sourcePath,
                    destinationPath,
                    GetString(item["sha1"]),
                    GetLong(item["size"]),
                    destinationGameDirectory,
                    seededFiles,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<JsonObject> EnumerateLibraryObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["libraries"] is JsonArray libraries)
            {
                foreach (var library in libraries.OfType<JsonObject>())
                    yield return library;
            }

            foreach (var child in obj.Select(item => item.Value).Where(value => value is not null))
            {
                foreach (var library in EnumerateLibraryObjects(child!))
                    yield return library;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(value => value is not null))
            {
                foreach (var library in EnumerateLibraryObjects(child!))
                    yield return library;
            }
        }
    }

    private async Task SeedFileAsync(
        string sourcePath,
        string destinationPath,
        string? expectedSha1,
        long? expectedSize,
        string destinationGameDirectory,
        IDictionary<string, string> seededFiles,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            return;
        if (expectedSize is > 0 && new FileInfo(sourcePath).Length != expectedSize.Value)
            return;

        string actualSha1;
        try
        {
            actualSha1 = await AtomicSharedFilePublisher.PublishCopyAsync(
                sourcePath,
                destinationPath,
                expectedSha1,
                cancellationToken,
                destinationGameDirectory, logger).ConfigureAwait(false);
        }
        catch (InvalidDataException) when (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            return;
        }
        seededFiles[NormalizeRelativePath(Path.GetRelativePath(destinationGameDirectory, destinationPath))] = actualSha1;
    }

    private static string? TryCreateMavenPath(string coordinate)
    {
        var extensionSplit = coordinate.Split('@', 2);
        var extension = extensionSplit.Length == 2 ? extensionSplit[1] : "jar";
        var parts = extensionSplit[0].Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace))
            return null;

        var groupPath = parts[0].Replace('.', '/');
        var classifier = parts.Length == 4 ? $"-{parts[3]}" : string.Empty;
        return $"{groupPath}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{classifier}.{extension}";
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string? GetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long? GetLong(JsonNode? node)
    {
        try
        {
            return node?.GetValue<long>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

internal sealed record LoaderInstallerWorkspaceSnapshot(
    string WorkspaceMinecraftDirectory,
    IReadOnlyDictionary<string, string> SeededFiles)
{
    public JsonObject? VerifiedVersionMetadata { get; init; }
    public bool CanPrepareBaseContent { get; init; }
}

internal sealed record VerifiedSharedFileExpectation(string Sha1, long? Size);
