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
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// 把实例文件解析为 Modrinth mrpack 清单，并将无法映射的文件安全写入 overrides。
/// </summary>
internal sealed class ModrinthModpackExporter
{
    // 可由 Modrinth 识别的文件写入 downloads 清单，其余本地内容作为 override 保证导出完整性。
    private readonly ModpackExportCandidateCollector candidateCollector;
    private readonly ModpackExportArchiveWriter archiveWriter;
    private readonly ModrinthApiClient apiClient;
    private readonly ILogger logger;

    public ModrinthModpackExporter(
        ModpackExportCandidateCollector candidateCollector,
        ModpackExportArchiveWriter archiveWriter,
        ModrinthApiClient apiClient,
        ILogger logger)
    {
        this.candidateCollector = candidateCollector;
        this.archiveWriter = archiveWriter;
        this.apiClient = apiClient;
        this.logger = logger;
    }

    public async Task<ModpackExportResult> ExportAsync(
        ModpackExportRequest request,
        CancellationToken cancellationToken,
        Func<bool>? isCurrent = null)
    {
        // 先冻结候选与哈希，再创建目标压缩包；任何失败都清理未完成输出。
        logger.LogInformation(
            "Modrinth modpack export started. InstanceDirectory={InstanceDirectory} IncludeMods={IncludeMods} IncludeDisabledMods={IncludeDisabledMods} IncludeResourcePacks={IncludeResourcePacks} IncludeShaderPacks={IncludeShaderPacks} IncludeConfig={IncludeConfig}",
            request.Instance.InstanceDirectory,
            request.IncludeMods,
            request.IncludeDisabledMods,
            request.IncludeResourcePacks,
            request.IncludeShaderPacks,
            request.IncludeConfig);

        try
        {
            // 清单和 overrides 写入同一 ZipArchive 会话，关闭归档后才报告成功。
            var candidates = await candidateCollector.CollectAsync(request, cancellationToken).ConfigureAwait(false);
            var resolvedCandidates = await CreateCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
            var matches = await ResolveMatchesAsync(resolvedCandidates, cancellationToken).ConfigureAwait(false);

            var manifestFiles = new List<ModrinthManifestFile>();
            var manifestFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var overrideFiles = new List<ModpackExportArchiveFile>();
            foreach (var candidate in resolvedCandidates)
            {
                if (!candidate.IsOverrideOnly
                    && candidate.Sha1 is { } sha1
                    && matches.TryGetValue(sha1, out var match)
                    && !string.IsNullOrWhiteSpace(match.Url)
                    && manifestFileKeys.Add(candidate.OverridePath))
                {
                    manifestFiles.Add(new ModrinthManifestFile(
                        candidate.OverridePath,
                        new ModrinthManifestHashes(
                            candidate.Sha1,
                            string.IsNullOrWhiteSpace(candidate.Sha512) ? match.Sha512 : candidate.Sha512),
                        new ModrinthManifestEnvironment("required", "unsupported"),
                        [match.Url],
                        candidate.SizeBytes ?? match.Size));
                    continue;
                }

                overrideFiles.Add(new ModpackExportArchiveFile(candidate.SourcePath, candidate.OverridePath));
            }

            if (request.IncludeConfig)
                overrideFiles.AddRange(ModpackExportCandidateCollector.CollectConfigFiles(request.Instance.InstanceDirectory));

            var outputPath = await archiveWriter.WriteAsync(
                    request.OutputArchivePath,
                    "modrinth.index.json",
                    CreateManifest(request, manifestFiles),
                    overrideFiles,
                    cancellationToken,
                    isCurrent)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Modrinth modpack export completed. InstanceDirectory={InstanceDirectory} ManifestFileCount={ManifestFileCount} OverrideFileCount={OverrideFileCount}",
                request.Instance.InstanceDirectory,
                manifestFiles.Count,
                overrideFiles.Count);
            logger.LogDebug("Modrinth modpack export path resolved. OutputArchivePath={OutputArchivePath}", outputPath);

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
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Modrinth modpack export API request failed. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.ModrinthApiFailed);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Modrinth modpack export API response was invalid. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.ModrinthApiFailed);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Modrinth modpack export file operation failed. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.FileSystemError);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "Modrinth modpack export file access failed. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.FileSystemError);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Modrinth modpack export failed unexpectedly. InstanceDirectory={InstanceDirectory}", request.Instance.InstanceDirectory);
            return Failure(ModpackExportFailureReason.UnexpectedError);
        }
    }

    private async Task<IReadOnlyList<ResolvedCandidate>> CreateCandidatesAsync(
        IReadOnlyList<ModpackExportFileCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // 对普通文件计算双哈希，目录或不支持条目直接进入 override，不丢弃用户内容。
        var result = new List<ResolvedCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.IsOverrideOnly)
            {
                result.Add(ResolvedCandidate.OverrideOnly(candidate));
                continue;
            }

            try
            {
                var hashes = await ComputeHashesAsync(candidate.SourcePath, cancellationToken).ConfigureAwait(false);
                result.Add(new ResolvedCandidate(
                    candidate.SourcePath,
                    candidate.OverridePath,
                    IsOverrideOnly: false,
                    hashes.Sha1,
                    hashes.Sha512,
                    hashes.SizeBytes));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or CryptographicException
                or ArgumentException
                or NotSupportedException)
            {
                logger.LogWarning(
                    exception,
                    "Failed to hash export file for Modrinth lookup; it will be written to overrides. FilePath={FilePath}",
                    candidate.SourcePath);
                result.Add(ResolvedCandidate.OverrideOnly(candidate));
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, ModrinthApiClient.ModrinthVersionFileMatch>> ResolveMatchesAsync(
        IReadOnlyList<ResolvedCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // 哈希查询批量执行，结果以规范路径关联，防止同名不同目录文件错配。
        var hashes = candidates
            .Where(candidate => !candidate.IsOverrideOnly)
            .Select(candidate => candidate.Sha1)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hashes.Length == 0)
            return new Dictionary<string, ModrinthApiClient.ModrinthVersionFileMatch>(StringComparer.OrdinalIgnoreCase);

        return await apiClient.GetVersionFileMatchesAsync(hashes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Sha1, string Sha512, long SizeBytes)> ComputeHashesAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true);
        using var sha1 = SHA1.Create();
        using var sha512 = SHA512.Create();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            sha1.TransformBlock(buffer, 0, read, null, 0);
            sha512.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock([], 0, 0);
        sha512.TransformFinalBlock([], 0, 0);
        return (
            Convert.ToHexString(sha1.Hash!).ToLowerInvariant(),
            Convert.ToHexString(sha512.Hash!).ToLowerInvariant(),
            stream.Length);
    }

    private static ModrinthManifest CreateManifest(
        ModpackExportRequest request,
        IReadOnlyList<ModrinthManifestFile> files)
    {
        // Loader 依赖与 Minecraft 版本写入 dependencies，客户端据此重建游戏基底。
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft"] = request.Instance.MinecraftVersion.Trim()
        };
        if (request.Instance.Loader is not LoaderKind.Vanilla)
            dependencies[ResolveLoaderId(request.Instance.Loader)] = request.Instance.LoaderVersion!.Trim();

        return new ModrinthManifest(
            1,
            "minecraft",
            request.Version.Trim(),
            request.Name.Trim(),
            string.Empty,
            files,
            dependencies);
    }

    private static string ResolveLoaderId(LoaderKind loader)
    {
        return loader switch
        {
            LoaderKind.Forge => "forge",
            LoaderKind.Fabric => "fabric-loader",
            LoaderKind.NeoForge => "neoforge",
            LoaderKind.Quilt => "quilt-loader",
            _ => throw new InvalidOperationException($"Unsupported Modrinth loader: {loader}")
        };
    }

    private static ModpackExportResult Failure(ModpackExportFailureReason reason) => new(false, reason);

    private sealed record ResolvedCandidate(
        string SourcePath,
        string OverridePath,
        bool IsOverrideOnly,
        string? Sha1,
        string? Sha512,
        long? SizeBytes)
    {
        public static ResolvedCandidate OverrideOnly(ModpackExportFileCandidate candidate) => new(
            candidate.SourcePath,
            candidate.OverridePath,
            IsOverrideOnly: true,
            Sha1: null,
            Sha512: null,
            SizeBytes: null);
    }

    private sealed record ModrinthManifest(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("game")] string Game,
        [property: JsonPropertyName("versionId")] string VersionId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("files")] IReadOnlyList<ModrinthManifestFile> Files,
        [property: JsonPropertyName("dependencies")] IReadOnlyDictionary<string, string> Dependencies);

    private sealed record ModrinthManifestFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("hashes")] ModrinthManifestHashes Hashes,
        [property: JsonPropertyName("env")] ModrinthManifestEnvironment Environment,
        [property: JsonPropertyName("downloads")] IReadOnlyList<string> Downloads,
        [property: JsonPropertyName("fileSize")] long FileSize);

    private sealed record ModrinthManifestHashes(
        [property: JsonPropertyName("sha1")] string Sha1,
        [property: JsonPropertyName("sha512")] string? Sha512);

    private sealed record ModrinthManifestEnvironment(
        [property: JsonPropertyName("client")] string Client,
        [property: JsonPropertyName("server")] string Server);
}
