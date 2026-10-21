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
using System.Security.Cryptography;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Resources;

internal sealed class ResourceProjectStorage
{
    private readonly HttpClient httpClient;
    private readonly ILocalSaveService localSaveService;
    private readonly ILogger logger;
    private readonly ISettingsService? settingsService;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly IImportConcurrencyLimiter limiter;

    public ResourceProjectStorage(
        HttpClient httpClient,
        ILocalSaveService localSaveService,
        ILogger logger,
        ISettingsService? settingsService = null,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        IImportConcurrencyLimiter? limiter = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.localSaveService = localSaveService;
        this.logger = logger;
        this.settingsService = settingsService;
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.limiter = limiter ?? ImportConcurrencyLimiter.Shared;
    }

    public async Task<string> InstallAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.InstanceDirectory))
            throw new InvalidOperationException("The target instance directory is empty.");
        if (version.Kind is ResourceProjectKind.World)
            return await InstallWorldAsync(version, instance, progress, cancellationToken).ConfigureAwait(false);

        var installDirectory = ResolveInstallDirectory(instance, version.Kind);
        MinecraftPathGuard.EnsureSafeDirectory(
            installDirectory,
            installDirectory,
            "Resource project install directory");
        var target = Path.Combine(installDirectory, ResolveFileName(version));
        return await DownloadToUnconfirmedDestinationAsync(
                version,
                target,
                installDirectory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> InstallAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken) =>
        InstallAsync(version, instance, progress: null, cancellationToken);

    public Task<string> EnsureInstanceContentDirectoryAsync(
        ResourceProjectKind kind,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(instance.InstanceDirectory))
            throw new InvalidOperationException("The target instance directory is empty.");

        var directoryName = kind switch
        {
            ResourceProjectKind.Mod => "mods",
            ResourceProjectKind.ResourcePack => "resourcepacks",
            ResourceProjectKind.DataPack => throw new InvalidOperationException(
                "Data packs are downloaded to a user-selected destination instead of an instance content directory."),
            ResourceProjectKind.ShaderPack => "shaderpacks",
            ResourceProjectKind.World => "saves",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported instance resource kind.")
        };
        var instanceDirectory = Path.GetFullPath(instance.InstanceDirectory);
        if (!Directory.Exists(instanceDirectory))
            throw new DirectoryNotFoundException("The target instance directory does not exist.");
        var contentDirectory = Path.GetFullPath(Path.Combine(instanceDirectory, directoryName));
        var existed = Directory.Exists(contentDirectory);
        var ensuredDirectory = MinecraftPathGuard.EnsureSafeDirectory(
            contentDirectory,
            instanceDirectory,
            "Resource project instance content directory");
        if (!existed)
        {
            logger.LogInformation(
                "Resource project instance content directory created. Kind={Kind} InstanceId={InstanceId}",
                kind,
                instance.Id);
        }

        return Task.FromResult(ensuredDirectory);
    }

    public async Task<string> DownloadAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException("The target download directory is empty.");
        MinecraftPathGuard.EnsureSafeDirectory(
            targetDirectory,
            targetDirectory,
            "Resource project download directory");
        var target = Path.Combine(targetDirectory, ResolveFileName(version));
        return await DownloadToUnconfirmedDestinationAsync(
                version,
                target,
                targetDirectory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> DownloadAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        CancellationToken cancellationToken) =>
        DownloadAsync(version, targetDirectory, progress: null, cancellationToken);

    public Task<ResourceProjectDestinationState> CaptureDownloadDestinationAsync(
        ResourceProjectVersion version,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var (target, parentDirectory) = ValidateDownloadDestination(destinationPath);
        return CaptureDestinationStateAsync(target, parentDirectory, cancellationToken);
    }

    public Task<ResourceProjectDestinationState> CaptureInstallDestinationAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var (target, installDirectory) = ValidateInstallDestination(version, instance, destinationPath);
        return CaptureDestinationStateAsync(target, installDirectory, cancellationToken);
    }

    public Task<string> DownloadToDestinationAsync(
        ResourceProjectVersion version,
        string destinationPath,
        ResourceProjectDestinationState expectedState,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (target, parentDirectory) = ValidateDownloadDestination(destinationPath);
        return DownloadCoreAsync(version, target, parentDirectory, expectedState, progress, cancellationToken);
    }

    public Task<string> InstallToDestinationAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        string destinationPath,
        ResourceProjectDestinationState expectedState,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (target, installDirectory) = ValidateInstallDestination(version, instance, destinationPath);
        return DownloadCoreAsync(version, target, installDirectory, expectedState, progress, cancellationToken);
    }

    public Task<bool> DownloadExistsAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(targetDirectory)
            ? Task.FromResult(false)
            : ExistingFileMatchesAsync(
                version,
                Path.Combine(targetDirectory, ResolveFileName(version)),
                cancellationToken);
    }

    public Task<bool> InstallExistsAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        return version.Kind is ResourceProjectKind.World
            || string.IsNullOrWhiteSpace(instance.InstanceDirectory)
            ? Task.FromResult(false)
            : ExistingFileMatchesAsync(
                version,
                Path.Combine(
                    ResolveInstallDirectory(instance, version.Kind),
                    ResolveFileName(version)),
                cancellationToken);
    }

    private async Task<string> DownloadToUnconfirmedDestinationAsync(
        ResourceProjectVersion version,
        string target,
        string targetDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            if (await ExistingFileMatchesAsync(version, target, cancellationToken).ConfigureAwait(false))
                return target;

            throw new ResourceProjectDestinationConflictException(
                target,
                ResourceProjectDestinationConflictReason.ExistingDifferentContent);
        }

        var expectedState = await CaptureDestinationStateAsync(
                target,
                targetDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return await DownloadCoreAsync(
                version,
                target,
                targetDirectory,
                expectedState,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> DownloadCoreAsync(
        ResourceProjectVersion version,
        string target,
        string targetDirectory,
        ResourceProjectDestinationState expectedState,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        MinecraftPathGuard.EnsureSafeFileDestination(
            target,
            targetDirectory,
            "Resource project file");
        var expectation = ResolveIntegrityExpectation(version);
        var urls = new[] { version.PrimaryDownloadUrl }
            .Concat(version.FallbackDownloadUrls)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (urls.Length == 0)
            throw new InvalidOperationException($"Resource project version has no download URL: {version.VersionId}");

        var speedMeter = SpeedMeterProgress.TryGet(progress);

        Exception? lastException = null;
        var tempPath = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.download");
        var logScope = new ForegroundDownloadLogScope(
            logger,
            "ResourceInstall",
            Path.GetFileName(target),
            target,
            urls[0],
            expectation.FileSize);
        try
        {
            var resolution = await DownloadAndVerifyAsync(
                version,
                urls,
                tempPath,
                expectation,
                progress,
                speedMeter,
                logScope.BeginSource(CreateProgressReporter(version, progress)),
                logScope.ReportTransferredBytes,
                cancellationToken).ConfigureAwait(false);
            MinecraftPathGuard.EnsureSafeFileDestination(
                target,
                targetDirectory,
                "Resource project file");
            MinecraftPathGuard.EnsureNoReparsePoints(
                targetDirectory,
                tempPath,
                "Resource project temporary file");
            await CommitDownloadedFileAsync(
                    tempPath,
                    target,
                    targetDirectory,
                    expectedState,
                    cancellationToken)
                .ConfigureAwait(false);
            logScope.Complete(resolution);
            return target;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logScope.CompleteWithoutDownload("Canceled", urls[0]);
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            lastException = exception;
            logger.LogDebug(
                exception,
                "Resource project download timed out. VersionId={VersionId} CandidateCount={CandidateCount}",
                version.VersionId,
                urls.Length);
        }
        catch (ResourceProjectDestinationConflictException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ResourceProjectIntegrityException)
        {
            lastException = exception;
            logger.LogDebug(
                exception,
                "Failed to download or verify resource project. VersionId={VersionId} CandidateCount={CandidateCount}",
                version.VersionId,
                urls.Length);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath, targetDirectory, version.VersionId);
        }

        logScope.Fail(lastException ?? new InvalidOperationException("Resource project download failed."), urls[0]);
        if (lastException is ResourceProjectIntegrityException integrityException)
            throw integrityException;
        throw new InvalidOperationException($"Failed to download resource project version: {version.VersionId}", lastException);
    }

    private static (string Target, string ParentDirectory) ValidateDownloadDestination(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ResourceProjectDestinationConflictException(
                destinationPath ?? string.Empty,
                ResourceProjectDestinationConflictReason.InvalidFileName);

        var target = Path.GetFullPath(destinationPath);
        var parentDirectory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(parentDirectory)
            || string.IsNullOrWhiteSpace(Path.GetFileName(target)))
        {
            throw new ResourceProjectDestinationConflictException(
                target,
                ResourceProjectDestinationConflictReason.InvalidFileName);
        }

        MinecraftPathGuard.EnsureSafeFileDestination(
            target,
            parentDirectory,
            "Resource project download file");
        return (target, parentDirectory);
    }

    private static (string Target, string InstallDirectory) ValidateInstallDestination(
        ResourceProjectVersion version,
        GameInstance instance,
        string destinationPath)
    {
        if (version.Kind is ResourceProjectKind.World)
        {
            throw new ResourceProjectDestinationConflictException(
                destinationPath,
                ResourceProjectDestinationConflictReason.OutsideInstanceContentDirectory);
        }
        if (string.IsNullOrWhiteSpace(instance.InstanceDirectory)
            || string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ResourceProjectDestinationConflictException(
                destinationPath ?? string.Empty,
                ResourceProjectDestinationConflictReason.InvalidFileName);
        }

        var installDirectory = Path.GetFullPath(ResolveInstallDirectory(instance, version.Kind));
        var target = Path.GetFullPath(destinationPath);
        var selectedDirectory = Path.GetDirectoryName(target);
        if (!string.Equals(selectedDirectory, installDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ResourceProjectDestinationConflictException(
                target,
                ResourceProjectDestinationConflictReason.OutsideInstanceContentDirectory);
        }
        if (string.IsNullOrWhiteSpace(Path.GetFileName(target)))
        {
            throw new ResourceProjectDestinationConflictException(
                target,
                ResourceProjectDestinationConflictReason.InvalidFileName);
        }

        MinecraftPathGuard.EnsureSafeDirectory(
            installDirectory,
            installDirectory,
            "Resource project install directory");
        MinecraftPathGuard.EnsureSafeFileDestination(
            target,
            installDirectory,
            "Resource project install file");
        return (target, installDirectory);
    }

    private static async Task<ResourceProjectDestinationState> CaptureDestinationStateAsync(
        string target,
        string managedRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MinecraftPathGuard.EnsureSafeFileDestination(target, managedRoot, "Resource project destination");
        if (!File.Exists(target))
            return new ResourceProjectDestinationState(false, 0, 0, string.Empty);

        try
        {
            var fileInfo = new FileInfo(target);
            await using var source = new FileStream(
                target,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                hasher.AppendData(buffer.AsSpan(0, read));
            }

            return new ResourceProjectDestinationState(
                true,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                Convert.ToHexString(hasher.GetHashAndReset()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ResourceProjectDestinationConflictException(
                target,
                ResourceProjectDestinationConflictReason.ChangedAfterConfirmation,
                exception);
        }
    }

    private async Task CommitDownloadedFileAsync(
        string tempPath,
        string target,
        string managedRoot,
        ResourceProjectDestinationState expectedState,
        CancellationToken cancellationToken)
    {
        var currentState = await CaptureDestinationStateAsync(target, managedRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!DestinationStatesMatch(expectedState, currentState))
        {
            throw new ResourceProjectDestinationConflictException(
                target,
                ResourceProjectDestinationConflictReason.ChangedAfterConfirmation);
        }

        if (!expectedState.Exists)
        {
            try
            {
                File.Move(tempPath, target, overwrite: false);
                return;
            }
            catch (IOException exception) when (File.Exists(target))
            {
                throw new ResourceProjectDestinationConflictException(
                    target,
                    ResourceProjectDestinationConflictReason.ChangedAfterConfirmation,
                    exception);
            }
        }

        var backupPath = Path.Combine(
            managedRoot,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.previous");
        var committed = false;
        try
        {
            File.Replace(tempPath, target, backupPath, ignoreMetadataErrors: true);
            var replacedState = await CaptureDestinationStateAsync(backupPath, managedRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!DestinationStatesMatch(expectedState, replacedState))
            {
                File.Replace(backupPath, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                throw new ResourceProjectDestinationConflictException(
                    target,
                    ResourceProjectDestinationConflictReason.ChangedAfterConfirmation);
            }

            committed = true;
        }
        catch (Exception exception) when (!committed && File.Exists(backupPath))
        {
            try
            {
                File.Replace(backupPath, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Failed to restore the confirmed destination after resource file commit verification failed.",
                    new AggregateException(exception, rollbackException));
            }
            throw;
        }
        finally
        {
            if (committed && File.Exists(backupPath))
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to delete resource project replacement backup. FileName={FileName}",
                        Path.GetFileName(backupPath));
                }
            }
        }
    }

    private static bool DestinationStatesMatch(
        ResourceProjectDestinationState expected,
        ResourceProjectDestinationState actual)
    {
        if (expected.Exists != actual.Exists)
            return false;
        if (!expected.Exists)
            return true;
        return expected.Length == actual.Length
            && expected.LastWriteTimeUtcTicks == actual.LastWriteTimeUtcTicks
            && string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ExistingFileMatchesAsync(
        ResourceProjectVersion version,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return false;

        var expectation = ResolveIntegrityExpectation(version);
        if (expectation.FileSize is null && expectation.Hash is null)
            return false;

        try
        {
            var fileInfo = new FileInfo(path);
            if (expectation.FileSize.HasValue && fileInfo.Length != expectation.FileSize.Value)
                return false;
            if (expectation.Hash is null)
                return true;

            await using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(ToHashAlgorithmName(expectation.Hash.Algorithm));
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                hasher.AppendData(buffer.AsSpan(0, read));
            }
            return CryptographicOperations.FixedTimeEquals(hasher.GetHashAndReset(), expectation.Hash.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<ResolvedDownloadRequest> DownloadAndVerifyAsync(
        ResourceProjectVersion version,
        IReadOnlyList<string> urls,
        string tempPath,
        IntegrityExpectation expectation,
        IProgress<LauncherProgress>? progress,
        SpeedMeter? speedMeter,
        Action<int, long, long?> reportAttemptProgress,
        Action<long> reportTransferredBytes,
        CancellationToken cancellationToken)
    {
        var settings = settingsService is null
            ? null
            : await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(settings?.DownloadSpeedLimitMbPerSecond ?? 0, downloadSpeedLimitState),
            limiter,
            DownloadConcurrencyCategory.Modpack);
        ResolvedDownloadRequest resolution;
        try
        {
            if (expectation.Hash is { Algorithm: not ResourceFileHashAlgorithm.Md5 } hash)
            {
                resolution = await executor.DownloadFileAsync(
                    urls,
                    settings?.DownloadSourcePreference ?? LauncherDefaults.DefaultDownloadSourcePreference,
                    "ThirdParty",
                    tempPath,
                    new DownloadIntegrityExpectation(
                        expectation.FileSize,
                        [(ToHashAlgorithmName(hash.Algorithm), Convert.ToHexString(hash.Value))]),
                    cancellationToken,
                    reportAttemptProgress: reportAttemptProgress,
                    options: new DownloadFileOptions(ManagedRoot: Path.GetDirectoryName(tempPath)),
                    speedMeter: speedMeter,
                    reportTransferredBytes: reportTransferredBytes).ConfigureAwait(false);
            }
            else
            {
                resolution = await executor.DownloadFileAsync(
                    urls,
                    settings?.DownloadSourcePreference ?? LauncherDefaults.DefaultDownloadSourcePreference,
                    "ThirdParty",
                    tempPath,
                    expectedSha1: null,
                    expectedSize: expectation.FileSize,
                    cancellationToken,
                    reportAttemptProgress: reportAttemptProgress,
                    options: new DownloadFileOptions(ManagedRoot: Path.GetDirectoryName(tempPath)),
                    speedMeter: speedMeter,
                    reportTransferredBytes: reportTransferredBytes).ConfigureAwait(false);
            }
        }
        catch (DownloadLocalFileException exception)
        {
            throw new InvalidOperationException("Failed to create the resource project temporary file.", exception);
        }
        catch (MinecraftDownloadRequestExecutor.DownloadSourceRequestException exception)
            when (exception.InnerException is DownloadHashMismatchException)
        {
            throw CreateIntegrityException(version, ResourceProjectIntegrityFailureReason.HashMismatch, expectation.Hash?.Algorithm);
        }
        catch (MinecraftDownloadRequestExecutor.DownloadSourceRequestException exception)
            when (exception.InnerException is DownloadBodyInterruptedException && expectation.FileSize.HasValue)
        {
            throw CreateIntegrityException(version, ResourceProjectIntegrityFailureReason.LengthMismatch, expectation.Hash?.Algorithm);
        }
        catch (MinecraftDownloadRequestExecutor.DownloadSourceRequestException exception)
            when (exception.InnerException is DownloadLocalFileException)
        {
            throw new InvalidOperationException("Failed to create the resource project temporary file.", exception);
        }
        catch (MinecraftDownloadRequestExecutor.DownloadSourceRequestException exception)
            when (exception.Failures.Count > 0
                && exception.Failures.All(failure => failure is DownloadAttemptException
                    { StatusCode: System.Net.HttpStatusCode.Forbidden }))
        {
            throw new ResourceProjectDistributionRestrictedException(version.VersionId, exception);
        }
        catch (MinecraftDownloadRequestExecutor.DownloadSourceRequestException exception)
        {
            throw new HttpRequestException("Resource project download candidate failed.", exception, exception.InnerException is DownloadAttemptException { StatusCode: { } status } ? status : null);
        }

        if (expectation.Hash?.Algorithm is ResourceFileHashAlgorithm.Md5)
        {
            MinecraftPathGuard.EnsureNoReparsePoints(
                Path.GetDirectoryName(tempPath)!,
                tempPath,
                "Resource project verification file");
            await using var source = File.OpenRead(tempPath);
            var actual = await MD5.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, expectation.Hash.Value))
                throw CreateIntegrityException(version, ResourceProjectIntegrityFailureReason.HashMismatch, expectation.Hash.Algorithm);
        }

        return resolution;
    }

    private static Action<int, long, long?>? CreateProgressReporter(
        ResourceProjectVersion version,
        IProgress<LauncherProgress>? progress)
    {
        if (progress is null)
            return null;

        var fileName = ResolveFileName(version);
        return (_, transferredBytes, totalBytes) => progress.Report(new LauncherProgress(
            ModProgressStages.DownloadingFile,
            fileName,
            totalBytes is > 0
                ? Math.Clamp(transferredBytes * 100d / totalBytes.Value, 0, 100)
                : null));
    }

    private static IntegrityExpectation ResolveIntegrityExpectation(ResourceProjectVersion version)
    {
        if (version.ExpectedFileSize < 0)
            throw CreateIntegrityException(version, ResourceProjectIntegrityFailureReason.InvalidMetadata);

        var hashes = new Dictionary<ResourceFileHashAlgorithm, byte[]>();
        foreach (var group in version.FileHashes.GroupBy(hash => hash.Algorithm))
        {
            var values = group
                .Select(hash => hash.Value?.Trim() ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (values.Length != 1 || !TryParseHash(group.Key, values[0], out var value))
                throw CreateIntegrityException(version, ResourceProjectIntegrityFailureReason.InvalidMetadata, group.Key);
            hashes[group.Key] = value;
        }

        ExpectedHash? expectedHash = null;
        foreach (var algorithm in new[]
                 {
                     ResourceFileHashAlgorithm.Sha512,
                     ResourceFileHashAlgorithm.Sha1,
                     ResourceFileHashAlgorithm.Md5
                 })
        {
            if (hashes.TryGetValue(algorithm, out var value))
            {
                expectedHash = new ExpectedHash(algorithm, value);
                break;
            }
        }

        var requiresTrustedHash = version.Kind is ResourceProjectKind.Mod
            || string.Equals(Path.GetExtension(ResolveFileName(version)), ".jar", StringComparison.OrdinalIgnoreCase);
        if (requiresTrustedHash
            && expectedHash?.Algorithm is not ResourceFileHashAlgorithm.Sha512 and not ResourceFileHashAlgorithm.Sha1)
        {
            throw CreateIntegrityException(version, ResourceProjectIntegrityFailureReason.MissingTrustedHash, expectedHash?.Algorithm);
        }
        return new IntegrityExpectation(version.ExpectedFileSize, expectedHash);
    }

    private static bool TryParseHash(ResourceFileHashAlgorithm algorithm, string value, out byte[] result)
    {
        var expectedLength = algorithm switch
        {
            ResourceFileHashAlgorithm.Sha512 => 128,
            ResourceFileHashAlgorithm.Sha1 => 40,
            ResourceFileHashAlgorithm.Md5 => 32,
            _ => 0
        };
        if (value.Length != expectedLength)
        {
            result = [];
            return false;
        }
        try
        {
            result = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            result = [];
            return false;
        }
    }

    private static HashAlgorithmName ToHashAlgorithmName(ResourceFileHashAlgorithm algorithm)
    {
        return algorithm switch
        {
            ResourceFileHashAlgorithm.Sha512 => HashAlgorithmName.SHA512,
            ResourceFileHashAlgorithm.Sha1 => HashAlgorithmName.SHA1,
            ResourceFileHashAlgorithm.Md5 => HashAlgorithmName.MD5,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
    }

    private static ResourceProjectIntegrityException CreateIntegrityException(
        ResourceProjectVersion version,
        ResourceProjectIntegrityFailureReason reason,
        ResourceFileHashAlgorithm? algorithm = null)
    {
        return new ResourceProjectIntegrityException(version.VersionId, reason, algorithm);
    }

    private void TryDeleteTemporaryFile(string path, string managedRoot, string versionId)
    {
        try
        {
            MinecraftPathGuard.EnsureNoReparsePoints(
                managedRoot,
                path,
                "Resource project cleanup file");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(
                exception,
                "Failed to delete temporary resource project download. VersionId={VersionId} FileName={FileName}",
                versionId,
                Path.GetFileName(path));
        }
    }

    private async Task<string> InstallWorldAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"launcher-world-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var archivePath = await DownloadToUnconfirmedDestinationAsync(
                    version,
                    Path.Combine(tempDirectory, ResolveFileName(version)),
                    tempDirectory,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            var result = await localSaveService.ImportFromArchiveAsync(instance, archivePath, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess || result.ImportedSave is null)
                throw new InvalidOperationException($"Failed to import world archive. FailureReason={result.FailureReason}");
            return result.ImportedSave.FullPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "Failed to delete temporary resource world directory. Directory={Directory}",
                    tempDirectory);
            }
        }
    }

    private static string ResolveFileName(ResourceProjectVersion version)
    {
        var fileName = Path.GetFileName(version.FileName);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"{version.VersionId}{ResolveDefaultExtension(version.Kind)}"
            : fileName;
    }

    private static string ResolveDefaultExtension(ResourceProjectKind kind)
    {
        return kind switch
        {
            ResourceProjectKind.Modpack => ".mrpack",
            ResourceProjectKind.ResourcePack or ResourceProjectKind.DataPack or ResourceProjectKind.ShaderPack or ResourceProjectKind.World => ".zip",
            _ => ".jar"
        };
    }

    private static string ResolveInstallDirectory(GameInstance instance, ResourceProjectKind kind)
    {
        var directoryName = kind switch
        {
            ResourceProjectKind.ResourcePack => "resourcepacks",
            ResourceProjectKind.DataPack => throw new InvalidOperationException(
                "Data packs are downloaded to a user-selected destination instead of an instance content directory."),
            ResourceProjectKind.ShaderPack => "shaderpacks",
            ResourceProjectKind.World => "saves",
            _ => "mods"
        };
        return Path.Combine(instance.InstanceDirectory, directoryName);
    }

    private sealed record IntegrityExpectation(long? FileSize, ExpectedHash? Hash);

    private sealed record ExpectedHash(ResourceFileHashAlgorithm Algorithm, byte[] Value);
}
