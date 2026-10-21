/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Multiplayer;

internal sealed class TerracottaProvisioningService : ITerracottaProvisioningService
{
    internal const string GiteeLatestReleaseApi =
        "https://gitee.com/api/v5/repos/burningtnt/Terracotta/releases/latest";
    internal const string GitHubLatestReleaseApi =
        "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";

    private const long MaximumMetadataBytes = 2L * 1024 * 1024;
    private const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private const long MaximumExtractedFileBytes = 64L * 1024 * 1024;
    private const string ManifestFileName = ".blockhelm-module.json";
    private const string ExecutableFileName = "terracotta.exe";
    private const string RuntimeFileName = "VCRUNTIME140.DLL";

    private static readonly string[] RequiredFileNames = [ExecutableFileName, RuntimeFileName];
    private static readonly IReadOnlyDictionary<string, string> KnownDigests =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0.4.2/x86_64"] = "07ebe139e3ca5f74576e58b1a96efe59abdfbe148d3f1a49bfdca8b6f70745f0",
            ["0.4.2/arm64"] = "acfab0a87a02dedc6dab7c05303186c8907f56f815548b693fb3324358da7d14"
        };

    private readonly MinecraftDownloadTransport transport;
    private readonly string moduleRoot;
    private readonly string architecture;
    private readonly ILogger<TerracottaProvisioningService> logger;
    private readonly SemaphoreSlim provisioningLock = new(1, 1);
    private TerracottaRelease? cachedRelease;

    public TerracottaProvisioningService(
        LauncherPathProvider pathProvider,
        ILogger<TerracottaProvisioningService>? logger = null)
        : this(
            MinecraftHttpClientFactory.CreateTransportClient(),
            Path.Combine(pathProvider.DefaultDataDirectory, "tools", "terracotta"),
            ResolveArchitecture(RuntimeInformation.OSArchitecture),
            logger)
    {
    }

    internal TerracottaProvisioningService(
        HttpClient httpClient,
        string moduleRoot,
        string architecture,
        ILogger<TerracottaProvisioningService>? logger = null)
    {
        this.moduleRoot = Path.GetFullPath(moduleRoot);
        this.architecture = architecture;
        this.logger = logger ?? NullLogger<TerracottaProvisioningService>.Instance;
        transport = new MinecraftDownloadTransport(
            LauncherHttpUserAgent.Apply(httpClient),
            new DownloadRetryOptions
            {
                MaxRedirects = 10,
                ResponseHeadersTimeout = TimeSpan.FromSeconds(20)
            });
    }

    public TerracottaModule? TryGetAvailable() => FindBestAvailable(excludedVersion: null);

    public async Task<TerracottaModule> EnsureAvailableAsync(
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await provisioningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installed = TryGetAvailable();
            TerracottaRelease release;
            try
            {
                release = await GetLatestReleaseOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (installed is not null && IsRecoverableSourceFailure(exception))
            {
                logger.LogWarning(exception,
                    "Terracotta release metadata is unavailable; using the installed module. Version={Version}",
                    installed.Version);
                return installed;
            }

            if (installed is not null)
            {
                var comparison = CompareVersions(installed.Version, release.Version);
                if (comparison > 0)
                    return installed;

                if (comparison == 0)
                {
                    var manifest = ReadManifest(installed.DirectoryPath);
                    if (release.ExpectedSha256 is null
                        || string.Equals(
                            manifest?.ArchiveSha256,
                            release.ExpectedSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (release.ExpectedSha256 is not null
                            && manifest is { PublisherDigestVerified: false })
                        {
                            await TryMarkPublisherVerifiedAsync(installed.DirectoryPath, manifest, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        return installed;
                    }

                    logger.LogWarning(
                        "The installed Terracotta archive does not match the newly available publisher digest. Version={Version}",
                        release.Version);
                    installed = FindBestAvailable(release.Version);
                }
            }

            try
            {
                return await DownloadAndInstallAsync(release, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (installed is not null && IsRecoverableSourceFailure(exception))
            {
                logger.LogWarning(exception,
                    "Terracotta update failed; retaining the previous module. InstalledVersion={InstalledVersion} TargetVersion={TargetVersion}",
                    installed.Version,
                    release.Version);
                return installed;
            }
        }
        finally
        {
            provisioningLock.Release();
        }
    }

    private async Task<TerracottaRelease> GetLatestReleaseOnceAsync(CancellationToken cancellationToken)
    {
        if (cachedRelease is not null)
            return cachedRelease;

        var giteeTask = TryReadReleaseAsync(GiteeLatestReleaseApi, ReleaseSource.Gitee, cancellationToken);
        var githubTask = TryReadReleaseAsync(GitHubLatestReleaseApi, ReleaseSource.GitHub, cancellationToken);
        await Task.WhenAll(giteeTask, githubTask).ConfigureAwait(false);
        var gitee = await giteeTask.ConfigureAwait(false);
        var github = await githubTask.ConfigureAwait(false);
        var selected = gitee ?? github
            ?? throw new HttpRequestException("Terracotta release metadata is unavailable from all official sources.");

        var assetName = BuildAssetName(selected.Version, architecture);
        var selectedAsset = selected.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, assetName, StringComparison.Ordinal));
        if (selectedAsset is null)
            throw new InvalidDataException("The Terracotta release does not contain the required Windows package.");

        var githubAsset = github is not null
            && string.Equals(github.Version, selected.Version, StringComparison.Ordinal)
                ? github.Assets.FirstOrDefault(asset =>
                    string.Equals(asset.Name, assetName, StringComparison.Ordinal))
                : null;
        var githubDigest = NormalizeSha256(githubAsset?.Digest);
        if (!string.IsNullOrWhiteSpace(githubAsset?.Digest) && githubDigest is null)
            throw new InvalidDataException("The Terracotta publisher digest is malformed.");
        var expectedSha256 = githubDigest ?? ResolveKnownDigest(selected.Version, architecture);
        var sources = new List<Uri>();
        AddOfficialSource(sources, selectedAsset.DownloadUri);
        if (selected.Source is ReleaseSource.Gitee)
        {
            if (githubAsset is not null)
                AddOfficialSource(sources, githubAsset.DownloadUri);
            else
                AddOfficialSource(
                    sources,
                    new Uri($"https://github.com/burningtnt/Terracotta/releases/download/v{selected.Version}/{assetName}"));
        }

        if (expectedSha256 is null)
        {
            logger.LogWarning(
                "Terracotta publisher digest is unavailable; only transport, package, and local integrity checks will be applied. Version={Version} Architecture={Architecture}",
                selected.Version,
                architecture);
        }

        cachedRelease = new TerracottaRelease(
            selected.Version,
            architecture,
            assetName,
            sources,
            expectedSha256);
        return cachedRelease;
    }

    private async Task<ReleaseMetadata?> TryReadReleaseAsync(
        string url,
        ReleaseSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var result = await transport.SendAsync(url, cancellationToken)
                .ConfigureAwait(false);
            using var response = result.Response;
            response.EnsureSuccessStatusCode();
            EnsureHttps(result.FinalUri, "Terracotta release metadata");
            if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
                throw new InvalidDataException("The Terracotta release metadata is too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var limited = new LengthLimitedReadStream(stream, MaximumMetadataBytes);
            using var document = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ParseRelease(document.RootElement, source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableSourceFailure(exception))
        {
            logger.LogDebug(exception,
                "Unable to read Terracotta release metadata. Source={Source}",
                source);
            return null;
        }
    }

    private static ReleaseMetadata ParseRelease(JsonElement root, ReleaseSource source)
    {
        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagElement))
        {
            throw new InvalidDataException("The Terracotta release metadata is invalid.");
        }

        var version = NormalizeVersion(tagElement.GetString());
        if ((root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            || (source is ReleaseSource.GitHub
                && root.TryGetProperty("draft", out var draft)
                && draft.GetBoolean()))
        {
            throw new InvalidDataException("The Terracotta latest release is not a stable published release.");
        }

        if (!root.TryGetProperty("assets", out var assetsElement)
            || assetsElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException("The Terracotta release assets are missing.");
        }

        var assets = new List<ReleaseAsset>();
        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement)
                || !asset.TryGetProperty("browser_download_url", out var urlElement)
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var downloadUri))
            {
                continue;
            }

            EnsureHttps(downloadUri, "Terracotta release asset");
            var digest = asset.TryGetProperty("digest", out var digestElement)
                && digestElement.ValueKind is JsonValueKind.String
                    ? digestElement.GetString()
                    : null;
            assets.Add(new ReleaseAsset(nameElement.GetString() ?? string.Empty, downloadUri, digest));
        }

        return new ReleaseMetadata(version, source, assets);
    }

    private async Task<TerracottaModule> DownloadAndInstallAsync(
        TerracottaRelease release,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installationDirectory = GetInstallationDirectory(release.Version);
        var archivePath = Path.Combine(moduleRoot, $".download-{Guid.NewGuid():N}.tar.gz");
        var stagingDirectory = Path.Combine(moduleRoot, $".install-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(moduleRoot, $".backup-{Guid.NewGuid():N}");
        MinecraftPathGuard.EnsureSafeDirectory(moduleRoot, moduleRoot, "Terracotta module root");

        try
        {
            var archiveSha256 = await DownloadArchiveFromOfficialSourcesAsync(
                release,
                archivePath,
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new LauncherProgress("terracotta-extract", "Installing Terracotta module", 92));
            var files = await ExtractPackageAsync(
                release,
                archivePath,
                stagingDirectory,
                cancellationToken).ConfigureAwait(false);
            await WriteManifestAsync(
                stagingDirectory,
                new ModuleManifest(
                    release.Version,
                    architecture,
                    archiveSha256,
                    release.ExpectedSha256 is not null,
                    files),
                cancellationToken).ConfigureAwait(false);
            PublishInstallation(stagingDirectory, installationDirectory, backupDirectory);

            var module = ValidateInstallation(installationDirectory)
                ?? throw new InvalidDataException("The installed Terracotta module did not pass validation.");
            progress?.Report(new LauncherProgress("terracotta-ready", "Terracotta module is ready", 100));
            logger.LogInformation(
                "Terracotta module prepared. Version={Version} Architecture={Architecture} PublisherDigestVerified={PublisherDigestVerified}",
                release.Version,
                architecture,
                release.ExpectedSha256 is not null);
            return module;
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
            TryDeleteDirectory(backupDirectory);
        }
    }

    private async Task<string> DownloadArchiveFromOfficialSourcesAsync(
        TerracottaRelease release,
        string archivePath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var source in release.DownloadUris)
        {
            try
            {
                TryDeleteFile(archivePath);
                progress?.Report(new LauncherProgress("terracotta-download", "Downloading Terracotta module", 0));
                await DownloadArchiveAsync(source, archivePath, progress, cancellationToken).ConfigureAwait(false);
                var actual = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
                if (release.ExpectedSha256 is not null
                    && !string.Equals(actual, release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The Terracotta archive SHA-256 checksum does not match.");
                }
                await ValidatePackageStructureAsync(release, archivePath, cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Terracotta archive downloaded. SourceHost={SourceHost} PublisherDigestVerified={PublisherDigestVerified}",
                    source.Host,
                    release.ExpectedSha256 is not null);
                return actual;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableSourceFailure(exception))
            {
                lastFailure = exception;
                logger.LogWarning(exception,
                    "Terracotta download source failed; trying the next official source. SourceHost={SourceHost}",
                    source.Host);
            }
        }

        TryDeleteFile(archivePath);
        if (lastFailure is not null)
            ExceptionDispatchInfo.Capture(lastFailure).Throw();
        throw new InvalidOperationException("No Terracotta download source is configured.");
    }

    private async Task DownloadArchiveAsync(
        Uri source,
        string archivePath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        MinecraftPathGuard.EnsureSafeFileDestination(archivePath, moduleRoot, "Terracotta temporary archive");
        await using var result = await transport.SendAsync(source.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        using var response = result.Response;
        response.EnsureSuccessStatusCode();
        EnsureHttps(result.FinalUri, "Terracotta download redirect");
        if (response.Content.Headers.ContentLength is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("The Terracotta archive size is invalid.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > MaximumArchiveBytes)
                throw new InvalidDataException("The Terracotta archive is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            var expected = response.Content.Headers.ContentLength;
            if (expected is > 0)
            {
                progress?.Report(new LauncherProgress(
                    "terracotta-download",
                    "Downloading Terracotta module",
                    Math.Clamp(total * 90d / expected.Value, 0, 90)));
            }
        }

        if (total == 0)
            throw new InvalidDataException("The Terracotta archive is empty.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, ModuleFile>> ExtractPackageAsync(
        TerracottaRelease release,
        string archivePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        MinecraftPathGuard.EnsureSafeDirectory(stagingDirectory, moduleRoot, "Terracotta staging directory");
        var expectedExecutable = $"terracotta-{release.Version}-windows-{architecture}.exe";
        var extracted = new Dictionary<string, ModuleFile>(StringComparer.OrdinalIgnoreCase);
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory)
                continue;
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
                throw new InvalidDataException("The Terracotta archive contains an unsupported entry type.");

            var normalized = entry.Name.Replace('\\', '/');
            if (normalized.Contains('/') || normalized is "." or "..")
                throw new InvalidDataException("The Terracotta archive contains a nested or unsafe path.");

            var destinationName = string.Equals(normalized, expectedExecutable, StringComparison.OrdinalIgnoreCase)
                ? ExecutableFileName
                : string.Equals(normalized, RuntimeFileName, StringComparison.OrdinalIgnoreCase)
                    ? RuntimeFileName
                    : throw new InvalidDataException("The Terracotta archive contains an unexpected file.");
            if (entry.Length <= 0 || entry.Length > MaximumExtractedFileBytes || entry.DataStream is null)
                throw new InvalidDataException("The Terracotta archive contains an invalid file.");
            if (extracted.ContainsKey(destinationName))
                throw new InvalidDataException("The Terracotta archive contains a duplicate file.");

            var destination = Path.Combine(stagingDirectory, destinationName);
            MinecraftPathGuard.EnsureSafeFileDestination(destination, stagingDirectory, $"Terracotta {destinationName}");
            await using (var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithLimitAsync(
                    entry.DataStream,
                    output,
                    MaximumExtractedFileBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            extracted[destinationName] = new ModuleFile(
                new FileInfo(destination).Length,
                await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false));
        }

        if (RequiredFileNames.Any(fileName => !extracted.ContainsKey(fileName))
            || extracted.Count != RequiredFileNames.Length)
        {
            throw new InvalidDataException("The Terracotta archive is incomplete.");
        }

        return extracted;
    }

    private static async Task ValidatePackageStructureAsync(
        TerracottaRelease release,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var expectedExecutable = $"terracotta-{release.Version}-windows-{release.Architecture}.exe";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory)
                continue;
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
                throw new InvalidDataException("The Terracotta archive contains an unsupported entry type.");
            var normalized = entry.Name.Replace('\\', '/');
            if (normalized.Contains('/') || normalized is "." or "..")
                throw new InvalidDataException("The Terracotta archive contains a nested or unsafe path.");
            if (!string.Equals(normalized, expectedExecutable, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, RuntimeFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The Terracotta archive contains an unexpected file.");
            }
            if (!seen.Add(normalized)
                || entry.Length <= 0
                || entry.Length > MaximumExtractedFileBytes
                || entry.DataStream is null)
            {
                throw new InvalidDataException("The Terracotta archive contains an invalid or duplicate file.");
            }
        }

        if (!seen.Contains(expectedExecutable)
            || !seen.Contains(RuntimeFileName)
            || seen.Count != RequiredFileNames.Length)
        {
            throw new InvalidDataException("The Terracotta archive is incomplete.");
        }
    }

    private TerracottaModule? FindBestAvailable(string? excludedVersion)
    {
        try
        {
            if (!Directory.Exists(moduleRoot))
                return null;
            MinecraftPathGuard.EnsureNoReparsePoints(moduleRoot, moduleRoot, "Terracotta module root");
            return Directory.EnumerateDirectories(moduleRoot)
                .Select(versionDirectory => Path.Combine(
                    versionDirectory,
                    $"terracotta-windows-{architecture}"))
                .Where(Directory.Exists)
                .Select(ValidateInstallation)
                .Where(module => module is not null
                    && !string.Equals(module.Version, excludedVersion, StringComparison.Ordinal))
                .OrderByDescending(module => ParseVersion(module!.Version))
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            logger.LogDebug(exception, "Ignoring invalid Terracotta module installations.");
            return null;
        }
    }

    private TerracottaModule? ValidateInstallation(string directory)
    {
        try
        {
            MinecraftPathGuard.EnsureWithin(directory, moduleRoot, "Terracotta installation directory");
            MinecraftPathGuard.EnsureNoReparsePoints(moduleRoot, directory, "Terracotta installation directory");
            var manifest = ReadManifest(directory);
            if (manifest is null
                || !string.Equals(manifest.Architecture, architecture, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(manifest.Version)
                || !string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(directory)),
                    manifest.Version,
                    StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var fileName in RequiredFileNames)
            {
                var path = Path.Combine(directory, fileName);
                MinecraftPathGuard.EnsureSafeFileDestination(path, directory, $"Terracotta {fileName}");
                if (!manifest.Files.TryGetValue(fileName, out var expected)
                    || expected.Size <= 0
                    || !File.Exists(path)
                    || new FileInfo(path).Length != expected.Size
                    || !string.Equals(
                        ComputeSha256(path),
                        expected.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return new TerracottaModule(
                manifest.Version,
                architecture,
                directory,
                Path.Combine(directory, ExecutableFileName));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            logger.LogDebug(exception, "Ignoring an invalid Terracotta module installation.");
            return null;
        }
    }

    private static async Task WriteManifestAsync(
        string directory,
        ModuleManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ManifestFileName);
        var temporaryPath = Path.Combine(directory, $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        MinecraftPathGuard.EnsureSafeFileDestination(path, directory, "Terracotta module manifest");
        MinecraftPathGuard.EnsureSafeFileDestination(
            temporaryPath,
            directory,
            "Terracotta temporary module manifest");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(manifest), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task TryMarkPublisherVerifiedAsync(
        string directory,
        ModuleManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteManifestAsync(
                directory,
                manifest with { PublisherDigestVerified = true },
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "An installed Terracotta module was verified against a newly available publisher digest. Version={Version}",
                manifest.Version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Unable to record Terracotta publisher digest verification.");
        }
    }

    private static ModuleManifest? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path))
            : null;
    }

    private void PublishInstallation(string stagingDirectory, string installationDirectory, string backupDirectory)
    {
        MinecraftPathGuard.EnsureWithin(installationDirectory, moduleRoot, "Terracotta installation directory");
        MinecraftPathGuard.EnsureNoReparsePoints(moduleRoot, installationDirectory, "Terracotta installation directory");
        Directory.CreateDirectory(Path.GetDirectoryName(installationDirectory)!);
        var movedExisting = false;
        try
        {
            if (Directory.Exists(installationDirectory))
            {
                Directory.Move(installationDirectory, backupDirectory);
                movedExisting = true;
            }

            Directory.Move(stagingDirectory, installationDirectory);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(installationDirectory) && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, installationDirectory);
            throw;
        }
    }

    private string GetInstallationDirectory(string version) =>
        Path.Combine(moduleRoot, version, $"terracotta-windows-{architecture}");

    private static string BuildAssetName(string version, string architecture) =>
        $"terracotta-{version}-windows-{architecture}-pkg.tar.gz";

    private static string ResolveArchitecture(Architecture value) => value switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("Terracotta is supported only on Windows x64 and Arm64.")
    };

    private static string NormalizeVersion(string? tag)
    {
        var version = tag?.Trim();
        if (version?.StartsWith('v') is true)
            version = version[1..];
        if (string.IsNullOrWhiteSpace(version)
            || version.Length > 64
            || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new InvalidDataException("The Terracotta release version is invalid.");
        }

        return version;
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        const string prefix = "sha256:";
        var value = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : digest;
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : null;
    }

    internal static string? ResolveKnownDigest(string version, string architecture) =>
        KnownDigests.TryGetValue($"{version}/{architecture}", out var digest) ? digest : null;

    private static void AddOfficialSource(ICollection<Uri> sources, Uri source)
    {
        EnsureHttps(source, "Terracotta download source");
        if (!sources.Contains(source))
            sources.Add(source);
    }

    private static void EnsureHttps(Uri uri, string description)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description} must use HTTPS.");
        }
    }

    private static int CompareVersions(string left, string right) =>
        ParseVersion(left).CompareTo(ParseVersion(right));

    private static Version ParseVersion(string version) =>
        Version.TryParse(version.Split('-', 2)[0], out var parsed) ? parsed : new Version(0, 0);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static async Task CopyWithLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("A Terracotta archive entry is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRecoverableSourceFailure(Exception exception) => exception is
        HttpRequestException
        or DownloadAttemptException
        or IOException
        or InvalidDataException
        or JsonException
        or OperationCanceledException;

    private void TryDeleteFile(string path)
    {
        try
        {
            MinecraftPathGuard.EnsureSafeFileDestination(path, moduleRoot, "Terracotta temporary file");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogDebug(exception, "Unable to remove a Terracotta temporary file.");
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            MinecraftPathGuard.EnsureWithin(path, moduleRoot, "Terracotta temporary directory");
            MinecraftPathGuard.EnsureNoReparsePoints(moduleRoot, path, "Terracotta temporary directory");
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogDebug(exception, "Unable to remove a Terracotta temporary directory.");
        }
    }

    internal sealed record TerracottaRelease(
        string Version,
        string Architecture,
        string AssetName,
        IReadOnlyList<Uri> DownloadUris,
        string? ExpectedSha256);

    private sealed record ReleaseMetadata(
        string Version,
        ReleaseSource Source,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, Uri DownloadUri, string? Digest);

    private sealed record ModuleManifest(
        string Version,
        string Architecture,
        string ArchiveSha256,
        bool PublisherDigestVerified,
        Dictionary<string, ModuleFile> Files);

    private sealed record ModuleFile(long Size, string Sha256);

    private enum ReleaseSource
    {
        Gitee,
        GitHub
    }

    private sealed class LengthLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long totalRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => totalRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            EnsureLimit(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            EnsureLimit(read);
            return read;
        }

        private void EnsureLimit(int read)
        {
            totalRead += read;
            if (totalRead > maximumBytes)
                throw new InvalidDataException("The Terracotta response is too large.");
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
