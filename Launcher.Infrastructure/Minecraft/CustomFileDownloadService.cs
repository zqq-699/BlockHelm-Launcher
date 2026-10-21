/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Net.Http;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed class CustomFileDownloadService : ICustomFileDownloadService, IDisposable
{
    private readonly ISettingsService settingsService;
    private readonly IDownloadSpeedLimitState? downloadSpeedLimitState;
    private readonly IImportConcurrencyLimiter limiter;
    private readonly ILogger<CustomFileDownloadService> logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public CustomFileDownloadService(
        ISettingsService settingsService,
        IImportConcurrencyLimiter limiter,
        IDownloadSpeedLimitState? downloadSpeedLimitState = null,
        ILogger<CustomFileDownloadService>? logger = null,
        HttpClient? httpClient = null)
    {
        this.settingsService = settingsService;
        this.limiter = limiter;
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.logger = logger ?? NullLogger<CustomFileDownloadService>.Instance;
        this.httpClient = LauncherHttpUserAgent.Apply(
            httpClient ?? MinecraftHttpClientFactory.CreateTransportClient());
        ownsHttpClient = httpClient is null;
    }

    public async Task DownloadAsync(
        string sourceUrl,
        string destinationPath,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeHttpAddress(sourceUrl, out var normalizedSourceUrl))
        {
            throw new ArgumentException(
                "The custom download address must be an absolute HTTP or HTTPS URL.",
                nameof(sourceUrl));
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A custom download destination is required.", nameof(destinationPath));

        var normalizedDestination = Path.GetFullPath(destinationPath);
        var fileName = Path.GetFileName(normalizedDestination);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("The custom download destination must include a file name.", nameof(destinationPath));

        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var executor = new MinecraftDownloadRequestExecutor(
            httpClient,
            logger,
            DownloadBandwidthLimiter.Create(
                settings.DownloadSpeedLimitMbPerSecond,
                downloadSpeedLimitState),
            limiter,
            DownloadConcurrencyCategory.Modpack);
        var logScope = new ForegroundDownloadLogScope(
            logger,
            "CustomFileDownload",
            fileName,
            normalizedDestination,
            normalizedSourceUrl);

        try
        {
            var resolution = await executor.DownloadUnverifiedFileAsync(
                normalizedSourceUrl,
                normalizedDestination,
                cancellationToken,
                reportAttemptProgress: logScope.BeginSource((_, transferredBytes, totalBytes) =>
                    progress?.Report(new LauncherProgress(
                        "CustomFileDownload",
                        string.Empty,
                        totalBytes is > 0
                            ? Math.Clamp(transferredBytes * 100d / totalBytes.Value, 0, 100)
                            : null))),
                speedMeter: SpeedMeterProgress.TryGet(progress),
                reportTransferredBytes: logScope.ReportTransferredBytes).ConfigureAwait(false);
            logScope.Complete(resolution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logScope.CompleteWithoutDownload("Canceled", normalizedSourceUrl);
            throw;
        }
        catch (Exception exception)
        {
            logScope.Fail(exception, normalizedSourceUrl);
            logger.LogError(
                exception,
                "Custom file download failed. FileName={FileName} DestinationPath={DestinationPath} SourceUrl={SourceUrl}",
                fileName,
                normalizedDestination,
                DownloadUriLogSanitizer.Sanitize(normalizedSourceUrl));
            throw;
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
            httpClient.Dispose();
    }

    internal static bool TryNormalizeHttpAddress(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }
}
