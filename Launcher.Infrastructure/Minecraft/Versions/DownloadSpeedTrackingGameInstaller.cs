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

using System.Net.Http;
using System.IO;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class DownloadSpeedTrackingGameInstaller : ParallelGameInstaller
{
    private const int MaximumCheckerConcurrency = 4;
    private const int DownloadQueueCapacity = 2048;

    private readonly SpeedMeter? speedMeter;
    private readonly MinecraftDownloadRequestExecutor downloadExecutor;
    private readonly DownloadSourcePreference downloadSourcePreference;
    private readonly MinecraftPath? minecraftPath;
    private readonly MinecraftDownloadOperationContext? operationContext;
    private readonly ILogger? logger;

    internal int ConfiguredMaxChecker { get; }
    internal int ConfiguredMaxDownloader { get; }

    private DownloadSpeedTrackingGameInstaller(
        int maxChecker,
        int maxDownloader,
        int boundedCapacity,
        HttpClient httpClient,
        MinecraftDownloadRequestExecutor downloadExecutor,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        MinecraftPath? minecraftPath,
        MinecraftDownloadOperationContext? operationContext,
        SpeedMeter? sharedSpeedMeter,
        ILogger? logger)
        : base(maxChecker, maxDownloader, boundedCapacity, httpClient)
    {
        ConfiguredMaxChecker = maxChecker;
        ConfiguredMaxDownloader = maxDownloader;
        this.downloadExecutor = downloadExecutor;
        this.downloadSourcePreference = downloadSourcePreference;
        this.minecraftPath = minecraftPath;
        this.operationContext = operationContext;
        this.logger = logger;
        speedMeter = sharedSpeedMeter ?? SpeedMeterProgress.TryGet(progress);
    }

    public static DownloadSpeedTrackingGameInstaller CreateAsCoreCount(
        HttpClient httpClient,
        MinecraftDownloadRequestExecutor downloadExecutor,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        MinecraftPath? minecraftPath = null,
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? sharedSpeedMeter = null,
        ILogger? logger = null)
    {
        var maxChecker = Environment.ProcessorCount;
        maxChecker = Math.Max(1, maxChecker);
        maxChecker = Math.Min(MaximumCheckerConcurrency, maxChecker);

        return new DownloadSpeedTrackingGameInstaller(
            maxChecker,
            ImportConcurrencyLimiter.MaximumDownloadConcurrency,
            DownloadQueueCapacity,
            httpClient,
            downloadExecutor,
            downloadSourcePreference,
            progress,
            minecraftPath,
            operationContext,
            sharedSpeedMeter,
            logger);
    }

    protected override Task Download(
        GameFile file,
        IProgress<ByteProgress>? progress,
        CancellationToken cancellationToken)
    {
        return DownloadGameFileAsync(file, progress, cancellationToken);
    }

    internal async Task DownloadGameFileAsync(
        GameFile file,
        IProgress<ByteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileUrl = file.Url
                ?? throw SafeExceptionLog.Describe(new InvalidDataException($"CmlLib game file URL is missing: {file.Name}"), "GameFileMetadataMissing", ("FileName", file.Name), ("Field", "url"));
        var filePath = file.Path
                ?? throw SafeExceptionLog.Describe(new InvalidDataException($"CmlLib game file path is missing: {file.Name}"), "GameFileMetadataMissing", ("FileName", file.Name), ("Field", "path"));
        if (minecraftPath is not null)
            filePath = MinecraftPathGuard.EnsureInstallFilePath(minecraftPath, filePath);
        long? expectedSize = file.Size > 0 ? file.Size : null;
        var options = operationContext?.TryGetAsset(filePath, file.Hash, expectedSize) is true
            ? new DownloadFileOptions(DownloadPersistenceMode.LightweightAtomic, operationContext)
            : operationContext is not null && MinecraftFileIntegrity.IsSha1(file.Hash)
                ? new DownloadFileOptions(DownloadPersistenceMode.TaskScopedResumable, operationContext)
                : null;
        NetworkDownloadProgress? attemptProgress = null;
        var currentAttempt = 0;
        var logScope = new ForegroundDownloadLogScope(
            logger,
            "MinecraftInstall",
            file.Name,
            filePath,
            fileUrl,
            expectedSize);
        using var diagnosticLogScope = logScope.RestoreContext();
        var reportProgress = logScope.BeginSource((attempt, progressedBytes, totalBytes) =>
        {
            if (attempt != currentAttempt)
            {
                currentAttempt = attempt;
                attemptProgress = new NetworkDownloadProgress(progress);
            }

            attemptProgress!.Report(new ByteProgress(
                progressedBytes,
                totalBytes.GetValueOrDefault(file.Size)));
        });
        try
        {
            var resolution = await downloadExecutor.DownloadFileAsync(
                fileUrl,
                downloadSourcePreference,
                categoryHint: null,
                filePath,
                file.Hash,
                expectedSize,
                cancellationToken,
                reportProgress,
                options: options,
                speedMeter: speedMeter,
                reportTransferredBytes: logScope.ReportTransferredBytes).ConfigureAwait(false);
            logScope.Complete(resolution);
        }
        catch (OperationCanceledException)
        {
            logScope.CompleteWithoutDownload("Canceled", fileUrl);
            throw;
        }
        catch (Exception exception)
        {
            logScope.Fail(exception, fileUrl);
            throw;
        }
    }

    private sealed class NetworkDownloadProgress : IProgress<ByteProgress>
    {
        private static readonly TimeSpan InnerProgressInterval = TimeSpan.FromMilliseconds(100);

        private readonly IProgress<ByteProgress>? innerProgress;
        private long lastReportedProgressedBytes;
        private DateTimeOffset lastInnerProgressReportedAt = DateTimeOffset.MinValue;

        public NetworkDownloadProgress(
            IProgress<ByteProgress>? innerProgress)
        {
            this.innerProgress = innerProgress;
        }

        public void Report(ByteProgress value)
        {
            if (ShouldReportInnerProgress(value))
                innerProgress?.Report(value);

        }

        private bool ShouldReportInnerProgress(ByteProgress value)
        {
            if (innerProgress is null)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (value.TotalBytes > 0 && value.ProgressedBytes >= value.TotalBytes)
            {
                lastReportedProgressedBytes = value.ProgressedBytes;
                lastInnerProgressReportedAt = now;
                return true;
            }

            var bytesDelta = value.ProgressedBytes - lastReportedProgressedBytes;
            var minBytesDelta = value.TotalBytes <= 0
                ? 256 * 1024
                : Math.Max(64 * 1024, value.TotalBytes / 200);

            if (bytesDelta < minBytesDelta && now - lastInnerProgressReportedAt < InnerProgressInterval)
                return false;

            lastReportedProgressedBytes = value.ProgressedBytes;
            lastInnerProgressReportedAt = now;
            return true;
        }
    }

}
