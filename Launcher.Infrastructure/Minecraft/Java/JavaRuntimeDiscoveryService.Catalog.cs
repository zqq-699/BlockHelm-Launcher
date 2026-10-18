/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class JavaRuntimeDiscoveryService
{
    private const string ImportedRuntimeCatalogFileName = "java-runtimes.json";
    private static readonly JsonSerializerOptions ImportedRuntimeCatalogJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim importedRuntimeCatalogLock = new(1, 1);
    private readonly SemaphoreSlim deepSearchLock = new(1, 1);
    private readonly string importedRuntimeCatalogPath;
    private readonly ILogger<JavaRuntimeDiscoveryService> logger;
    private readonly Func<string?, IReadOnlyList<JavaRuntimeCandidate>> configuredCandidateCollector;
    private readonly Func<IReadOnlyList<string>> localDriveRootsProvider;

    public JavaRuntimeDiscoveryService(
        LauncherPathProvider? pathProvider = null,
        ILogger<JavaRuntimeDiscoveryService>? logger = null)
        : this(pathProvider, logger, directory => CollectCandidatePaths(directory), GetLocalDriveRoots)
    {
    }

    internal JavaRuntimeDiscoveryService(
        LauncherPathProvider? pathProvider,
        ILogger<JavaRuntimeDiscoveryService>? logger,
        Func<string?, IReadOnlyList<JavaRuntimeCandidate>> configuredCandidateCollector,
        Func<IReadOnlyList<string>> localDriveRootsProvider)
    {
        var resolvedPathProvider = pathProvider ?? new LauncherPathProvider();
        importedRuntimeCatalogPath = Path.Combine(
            resolvedPathProvider.DefaultDataDirectory,
            ImportedRuntimeCatalogFileName);
        this.logger = logger ?? NullLogger<JavaRuntimeDiscoveryService>.Instance;
        this.configuredCandidateCollector = configuredCandidateCollector;
        this.localDriveRootsProvider = localDriveRootsProvider;
    }

    public async Task<JavaRuntimeInfo> ImportExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var runtime = await DiscoverExecutableAsync(executablePath, cancellationToken);
        await RememberImportedExecutableAsync(runtime.ExecutablePath, cancellationToken);
        return runtime;
    }

    private async Task<List<JavaRuntimeCandidate>> CollectRecordedCandidatesAsync(
        CancellationToken cancellationToken)
    {
        List<string> paths;
        await importedRuntimeCatalogLock.WaitAsync(cancellationToken);
        try
        {
            paths = (await LoadCatalogCoreAsync(cancellationToken)).DiscoveredExecutablePaths ?? [];
        }
        finally
        {
            importedRuntimeCatalogLock.Release();
        }

        return paths
            .Where(File.Exists)
            .Select(path => new JavaRuntimeCandidate(
                path, "RecordedDiscovery", NormalizePath(ResolveJavaExecutableIdentityPath(path))))
            .ToList();
    }

    internal async Task RememberDiscoveredExecutablesAsync(
        IReadOnlyList<string> executablePaths,
        CancellationToken cancellationToken)
    {
        await importedRuntimeCatalogLock.WaitAsync(cancellationToken);
        try
        {
            var catalog = await LoadCatalogCoreAsync(cancellationToken);
            catalog.DiscoveredExecutablePaths = executablePaths
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            catalog.FullScanCompleted = true;
            await SaveCatalogCoreAsync(catalog, cancellationToken);
            logger.LogInformation(
                "Completed local Java search. DiscoveredRuntimeCount={DiscoveredRuntimeCount}",
                catalog.DiscoveredExecutablePaths.Count);
        }
        finally
        {
            importedRuntimeCatalogLock.Release();
        }
    }

    internal async Task<IReadOnlyList<JavaRuntimeCandidate>> CollectImportedCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePaths = await LoadImportedExecutablePathsAsync(cancellationToken);
        var candidates = new List<JavaRuntimeCandidate>(executablePaths.Count);
        var missingPaths = new List<string>();

        foreach (var executablePath in executablePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(executablePath))
            {
                missingPaths.Add(executablePath);
                continue;
            }

            candidates.Add(new JavaRuntimeCandidate(
                executablePath,
                "ManualImport",
                NormalizePath(ResolveJavaExecutableIdentityPath(executablePath))));
        }

        if (missingPaths.Count > 0)
        {
            try
            {
                await RemoveImportedExecutablePathsAsync(missingPaths, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                // 清理目录失败不应让本次 Java 扫描整体变为空；当前列表仍按文件存在性过滤。
                logger.LogWarning(
                    exception,
                    "Failed to prune missing imported Java runtimes. MissingRuntimeCount={MissingRuntimeCount}",
                    missingPaths.Count);
            }
        }

        return CollapseDuplicateCandidates(candidates);
    }

    private async Task RememberImportedExecutableAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(executablePath);
        await importedRuntimeCatalogLock.WaitAsync(cancellationToken);
        try
        {
            var executablePaths = await LoadImportedExecutablePathsCoreAsync(cancellationToken);
            if (executablePaths.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
                return;

            executablePaths.Add(normalizedPath);
            await SaveImportedExecutablePathsCoreAsync(executablePaths, cancellationToken);
            logger.LogInformation(
                "Remembered an imported Java runtime. ImportedRuntimeCount={ImportedRuntimeCount}",
                executablePaths.Count);
        }
        finally
        {
            importedRuntimeCatalogLock.Release();
        }
    }

    private async Task<IReadOnlyList<string>> LoadImportedExecutablePathsAsync(
        CancellationToken cancellationToken)
    {
        await importedRuntimeCatalogLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadImportedExecutablePathsCoreAsync(cancellationToken);
        }
        finally
        {
            importedRuntimeCatalogLock.Release();
        }
    }

    private async Task RemoveImportedExecutablePathsAsync(
        IReadOnlyCollection<string> executablePaths,
        CancellationToken cancellationToken)
    {
        var pathsToRemove = executablePaths
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathsToRemove.Count == 0)
            return;

        await importedRuntimeCatalogLock.WaitAsync(cancellationToken);
        try
        {
            var currentPaths = await LoadImportedExecutablePathsCoreAsync(cancellationToken);
            var removedCount = currentPaths.RemoveAll(pathsToRemove.Contains);
            if (removedCount == 0)
                return;

            await SaveImportedExecutablePathsCoreAsync(currentPaths, cancellationToken);
            logger.LogInformation(
                "Removed missing imported Java runtimes. RemovedCount={RemovedCount} ImportedRuntimeCount={ImportedRuntimeCount}",
                removedCount,
                currentPaths.Count);
        }
        finally
        {
            importedRuntimeCatalogLock.Release();
        }
    }

    private async Task<List<string>> LoadImportedExecutablePathsCoreAsync(
        CancellationToken cancellationToken)
    {
        var catalog = await LoadCatalogCoreAsync(cancellationToken);
        return (catalog.ExecutablePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ImportedJavaRuntimeCatalog> LoadCatalogCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(importedRuntimeCatalogPath))
            return new ImportedJavaRuntimeCatalog();

        try
        {
            await using var stream = new FileStream(
                importedRuntimeCatalogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var catalog = await JsonSerializer.DeserializeAsync<ImportedJavaRuntimeCatalog>(
                stream,
                ImportedRuntimeCatalogJsonOptions,
                cancellationToken);
            return catalog ?? new ImportedJavaRuntimeCatalog();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Failed to load the imported Java runtime catalog. CatalogPath={CatalogPath}",
                importedRuntimeCatalogPath);
            return new ImportedJavaRuntimeCatalog();
        }
    }

    private async Task SaveImportedExecutablePathsCoreAsync(
        IReadOnlyList<string> executablePaths,
        CancellationToken cancellationToken)
    {
        var catalog = await LoadCatalogCoreAsync(cancellationToken);
        catalog.ExecutablePaths = executablePaths.ToList();
        await SaveCatalogCoreAsync(catalog, cancellationToken);
    }

    private Task SaveCatalogCoreAsync(
        ImportedJavaRuntimeCatalog catalog,
        CancellationToken cancellationToken)
    {
        return AtomicJsonFileWriter.WriteAsync(
            importedRuntimeCatalogPath, catalog, ImportedRuntimeCatalogJsonOptions, cancellationToken);
    }

    private sealed class ImportedJavaRuntimeCatalog
    {
        public List<string> ExecutablePaths { get; set; } = [];

        public List<string> DiscoveredExecutablePaths { get; set; } = [];

        public bool FullScanCompleted { get; set; }
    }
}
