/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Files;
using CmlLib.Core.Rules;
using CmlLib.Core.Tasks;
using CmlLib.Core.Version;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class SafeAssetFileExtractor : IFileExtractor
{
    private readonly MinecraftDownloadRequestExecutor downloadExecutor;
    private readonly DownloadSourcePreference downloadSourcePreference;
    private readonly MinecraftDownloadOperationContext? operationContext;
    private readonly SpeedMeter? speedMeter;
    private readonly ILogger? logger;
    private readonly AssetInstallProgress? assetProgress;

    public SafeAssetFileExtractor(
        MinecraftDownloadRequestExecutor downloadExecutor,
        DownloadSourcePreference downloadSourcePreference,
        MinecraftDownloadOperationContext? operationContext,
        SpeedMeter? speedMeter = null,
        ILogger? logger = null,
        AssetInstallProgress? assetProgress = null)
    {
        this.downloadExecutor = downloadExecutor;
        this.downloadSourcePreference = downloadSourcePreference;
        this.operationContext = operationContext;
        this.speedMeter = speedMeter;
        this.logger = logger;
        this.assetProgress = assetProgress;
    }

    public async ValueTask<IEnumerable<GameFile>> Extract(
        MinecraftPath path,
        IVersion version,
        RulesEvaluatorContext rulesContext,
        CancellationToken cancellationToken)
    {
        var metadata = version.AssetIndex;
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id))
            return [];

        var indexPath = MinecraftPathGuard.EnsureWithin(
            path.GetIndexFilePath(metadata.Id),
            path.Assets,
            "Asset index");
        var indexIsValid = await MinecraftFileIntegrity.IsValidAsync(
                indexPath,
                metadata.GetSha1(),
                metadata.Size,
                MinecraftFileVerification.Full,
                cancellationToken).ConfigureAwait(false);
        if (indexIsValid)
        {
            if (operationContext is not null && MinecraftFileIntegrity.IsSha1(metadata.GetSha1()))
            {
                operationContext.MarkVerified(
                    indexPath,
                    DownloadIntegrityExpectation.Sha1(
                        metadata.GetSha1()!,
                        metadata.Size > 0 ? metadata.Size : null));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(metadata.Url))
                throw new InvalidDataException($"Asset index URL is missing: {metadata.Id}");

            var logScope = new ForegroundDownloadLogScope(
                logger,
                "MinecraftInstall",
                Path.GetFileName(indexPath),
                indexPath,
                metadata.Url,
                metadata.Size > 0 ? metadata.Size : null);
            using var diagnosticLogScope = logScope.RestoreContext();
            try
            {
                var resolution = await downloadExecutor.DownloadFileAsync(
                metadata.Url,
                downloadSourcePreference,
                categoryHint: "Mojang",
                indexPath,
                metadata.GetSha1(),
                metadata.Size > 0 ? metadata.Size : null,
                cancellationToken,
                reportAttemptProgress: logScope.BeginSource(),
                options: operationContext is not null && MinecraftFileIntegrity.IsSha1(metadata.GetSha1())
                    ? new DownloadFileOptions(DownloadPersistenceMode.TaskScopedResumable, operationContext, path.Assets)
                    : new DownloadFileOptions(ManagedRoot: path.Assets),
                speedMeter: speedMeter,
                reportTransferredBytes: logScope.ReportTransferredBytes).ConfigureAwait(false);
                logScope.Complete(resolution);
            }
            catch (OperationCanceledException)
            {
                logScope.CompleteWithoutDownload("Canceled", metadata.Url);
                throw;
            }
            catch (Exception exception)
            {
                logScope.Fail(exception, metadata.Url);
                throw;
            }
        }

        await using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var assetIndex = new JsonAssetIndex(metadata.Id, document);
        return CreateFiles(assetIndex, path);
    }

    private IEnumerable<GameFile> CreateFiles(IAssetIndex assetIndex, MinecraftPath path)
    {
        try
        {
            foreach (var item in assetIndex.EnumerateAssetObjects())
            {
                var relativeObjectPath = $"{item.Hash[..2]}/{item.Hash}";
                var objectPath = MinecraftPathGuard.EnsureWithin(
                    Path.Combine(path.GetAssetObjectPath(assetIndex.Id), relativeObjectPath),
                    path.Assets,
                    "Asset object");
                operationContext?.RegisterAsset(objectPath, item.Hash, item.Size > 0 ? item.Size : null);
                var updateTasks = new List<IUpdateTask>(2);
                if (assetIndex.IsVirtual)
                {
                    updateTasks.Add(new AtomicAssetCopyTask(
                        MinecraftPathGuard.EnsureWithin(
                            Path.Combine(path.GetAssetLegacyPath(assetIndex.Id), item.Name),
                            path.Assets,
                            "Virtual asset"),
                        item.Hash,
                        path.Assets, logger));
                }
                if (assetIndex.MapToResources)
                {
                    updateTasks.Add(new AtomicAssetCopyTask(
                        MinecraftPathGuard.EnsureWithin(
                            Path.Combine(path.Resource, item.Name),
                            path.Resource,
                            "Legacy resource"),
                        item.Hash,
                        path.Resource, logger));
                }

                if (assetProgress is not null)
                    updateTasks.Add(assetProgress);

                var file = new GameFile(item.Name)
                {
                    Path = objectPath,
                    Hash = item.Hash,
                    Size = item.Size,
                    Url = $"https://resources.download.minecraft.net/{relativeObjectPath}",
                    UpdateTask = updateTasks
                };
                assetProgress?.Register(file);
                yield return file;
            }
        }
        finally
        {
            if (assetIndex is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private sealed class AtomicAssetCopyTask(
        string destinationPath,
        string expectedSha1,
        string managedRoot, ILogger? logger) : IUpdateTask
    {
        public async ValueTask Execute(GameFile file, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                throw SafeExceptionLog.Describe(new InvalidDataException("Asset object path is missing."), "DownloadValidation", ("Reason", "Asset object path is missing."));

            await AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
                file.Path,
                destinationPath,
                expectedSha1,
                cancellationToken,
                managedRoot, logger).ConfigureAwait(false);
        }
    }
}
