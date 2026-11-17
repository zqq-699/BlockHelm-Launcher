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
using System.Text.Json.Serialization;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Modpacks;

internal sealed class CurseForgeModpackExporter
{
    private readonly ModpackExportCandidateCollector candidateCollector;
    private readonly ModpackExportArchiveWriter archiveWriter;
    private readonly ICurseForgeApiKeyResolver apiKeyResolver;
    private readonly CurseForgeApiClient apiClient;
    private readonly ILogger logger;

    public CurseForgeModpackExporter(
        ModpackExportCandidateCollector candidateCollector,
        ModpackExportArchiveWriter archiveWriter,
        ICurseForgeApiKeyResolver apiKeyResolver,
        CurseForgeApiClient apiClient,
        ILogger logger)
    {
        this.candidateCollector = candidateCollector;
        this.archiveWriter = archiveWriter;
        this.apiKeyResolver = apiKeyResolver;
        this.apiClient = apiClient;
        this.logger = logger;
    }

    public async Task<ModpackExportResult> ExportAsync(
        ModpackExportRequest request,
        CancellationToken cancellationToken,
        Func<bool>? isCurrent = null)
    {
        logger.LogInformation(
            "CurseForge modpack export started. InstanceDirectory={InstanceDirectory} IncludeMods={IncludeMods} IncludeDisabledMods={IncludeDisabledMods} IncludeResourcePacks={IncludeResourcePacks} IncludeShaderPacks={IncludeShaderPacks} IncludeConfig={IncludeConfig}",
            request.Instance.InstanceDirectory,
            request.IncludeMods,
            request.IncludeDisabledMods,
            request.IncludeResourcePacks,
            request.IncludeShaderPacks,
            request.IncludeConfig);

        try
        {
            var candidates = await candidateCollector.CollectAsync(request, cancellationToken).ConfigureAwait(false);
            var fingerprintMatches = await ResolveFingerprintMatchesAsync(candidates, cancellationToken)
                .ConfigureAwait(false);

            var manifestFiles = new List<CurseForgeManifestFile>();
            var manifestFileKeys = new HashSet<string>(StringComparer.Ordinal);
            var overrideFiles = new List<ModpackExportArchiveFile>();
            foreach (var candidate in candidates)
            {
                if (candidate.Fingerprint is { } fingerprint
                    && fingerprintMatches.TryGetValue(fingerprint, out var match)
                    && manifestFileKeys.Add($"{match.ProjectId}:{match.FileId}"))
                {
                    manifestFiles.Add(new CurseForgeManifestFile(match.ProjectId, match.FileId, true));
                    continue;
                }

                overrideFiles.Add(new ModpackExportArchiveFile(candidate.SourcePath, candidate.OverridePath));
            }

            if (request.IncludeConfig)
                overrideFiles.AddRange(ModpackExportCandidateCollector.CollectConfigFiles(request.Instance.InstanceDirectory));

            var outputPath = await archiveWriter.WriteAsync(
                    request.OutputArchivePath,
                    "manifest.json",
                    CreateManifest(request, manifestFiles),
                    overrideFiles,
                    cancellationToken,
                    isCurrent)
                .ConfigureAwait(false);

            logger.LogInformation(
                "CurseForge modpack export completed. InstanceDirectory={InstanceDirectory} ManifestFileCount={ManifestFileCount} OverrideFileCount={OverrideFileCount}",
                request.Instance.InstanceDirectory,
                manifestFiles.Count,
                overrideFiles.Count);
            logger.LogDebug("CurseForge modpack export path resolved. OutputArchivePath={OutputArchivePath}", outputPath);

            return new ModpackExportResult(
                true,
                OutputArchivePath: outputPath,
                ManifestFileCount: manifestFiles.Count,
                OverrideFileCount: overrideFiles.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MissingApiKeyException)
        {
            return Failure(ModpackExportFailureReason.MissingCurseForgeApiKey);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "CurseForge modpack export API request failed. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.CurseForgeApiFailed);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "CurseForge modpack export file operation failed. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.FileSystemError);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "CurseForge modpack export file access failed. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.FileSystemError);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "CurseForge modpack export failed unexpectedly. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.UnexpectedError);
        }
    }

    private async Task<IReadOnlyDictionary<long, CurseForgeApiClient.CurseForgeFingerprintMatch>> ResolveFingerprintMatchesAsync(
        IReadOnlyList<ModpackExportFileCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var fingerprints = candidates
            .Select(candidate => candidate.Fingerprint)
            .OfType<long>()
            .Distinct()
            .ToArray();
        if (fingerprints.Length == 0)
            return new Dictionary<long, CurseForgeApiClient.CurseForgeFingerprintMatch>();

        var apiKey = await apiKeyResolver.TryResolveAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("CurseForge modpack export could not start because API key is not configured.");
            throw new MissingApiKeyException();
        }

        return await apiClient.GetFingerprintMatchesAsync(fingerprints, apiKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private static CurseForgeManifest CreateManifest(
        ModpackExportRequest request,
        IReadOnlyList<CurseForgeManifestFile> files)
    {
        var modLoaders = new List<CurseForgeModLoader>();
        if (request.Instance.Loader is not LoaderKind.Vanilla)
        {
            modLoaders.Add(new CurseForgeModLoader(
                $"{ResolveLoaderId(request.Instance.Loader)}-{request.Instance.LoaderVersion!.Trim()}",
                true));
        }

        return new CurseForgeManifest(
            new CurseForgeMinecraft(request.Instance.MinecraftVersion.Trim(), modLoaders),
            "minecraftModpack",
            1,
            request.Name.Trim(),
            request.Version.Trim(),
            request.Author.Trim(),
            files,
            "overrides");
    }

    private static string ResolveLoaderId(LoaderKind loader)
    {
        return loader switch
        {
            LoaderKind.Forge => "forge",
            LoaderKind.Fabric => "fabric",
            LoaderKind.NeoForge => "neoforge",
            LoaderKind.Quilt => "quilt",
            _ => throw new InvalidOperationException($"Unsupported CurseForge loader: {loader}")
        };
    }

    private static ModpackExportResult Failure(ModpackExportFailureReason reason) => new(false, reason);

    private sealed record CurseForgeManifest(
        [property: JsonPropertyName("minecraft")] CurseForgeMinecraft Minecraft,
        [property: JsonPropertyName("manifestType")] string ManifestType,
        [property: JsonPropertyName("manifestVersion")] int ManifestVersion,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("author")] string Author,
        [property: JsonPropertyName("files")] IReadOnlyList<CurseForgeManifestFile> Files,
        [property: JsonPropertyName("overrides")] string Overrides);

    private sealed record CurseForgeMinecraft(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("modLoaders")] IReadOnlyList<CurseForgeModLoader> ModLoaders);

    private sealed record CurseForgeModLoader(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("primary")] bool Primary);

    private sealed record CurseForgeManifestFile(
        [property: JsonPropertyName("projectID")] long ProjectId,
        [property: JsonPropertyName("fileID")] long FileId,
        [property: JsonPropertyName("required")] bool Required);

    private sealed class MissingApiKeyException : Exception;
}
