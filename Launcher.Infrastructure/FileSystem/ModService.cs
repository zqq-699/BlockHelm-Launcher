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
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

/// <summary>
/// 管理实例 mods 目录中的枚举、导入、启停和元数据识别，并兼容多种 Loader 描述格式。
/// </summary>
public sealed class ModService : IModService
{
    // 元数据优先使用各 Loader 的结构化声明，正则只解析 TOML 中少量稳定字段。
    private const string EnabledModExtension = ".jar";
    private const string DisabledModExtension = ".jar.disabled";
    private static readonly Regex TomlDisplayNameRegex = new(
        "^[\\t ]*displayName[\\t ]*=[\\t ]*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex TomlModIdRegex = new(
        "^[\\t ]*modId[\\t ]*=[\\t ]*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex TomlVersionRegex = new(
        "^[\\t ]*version[\\t ]*=[\\t ]*(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly LauncherPathProvider pathProvider;
    private readonly ILogger<ModService> logger;
    private readonly IUserFileDeletionService userFileDeletionService;
    private readonly string legacyIconCacheDirectory;
    private readonly SemaphoreSlim snapshotLock = new(1, 1);
    private readonly BoundedLocalContentSnapshotCache<LocalContentFileIdentity, LocalMod> snapshots = new();
    private readonly VersionedLocalContentCache<ModMetadataCacheValue> metadataCache;
    private int legacyIconCacheCleanupStarted;

    public ModService(
        LauncherPathProvider? pathProvider = null,
        ILogger<ModService>? logger = null,
        IUserFileDeletionService? userFileDeletionService = null)
    {
        this.pathProvider = pathProvider ?? new LauncherPathProvider();
        this.logger = logger ?? NullLogger<ModService>.Instance;
        this.userFileDeletionService = userFileDeletionService ?? new UserFileDeletionService();
        legacyIconCacheDirectory = Path.Combine(this.pathProvider.DefaultDataDirectory, "cache", "mods", "icons");
        metadataCache = new VersionedLocalContentCache<ModMetadataCacheValue>(
            Path.Combine(this.pathProvider.DefaultDataDirectory, "cache", "local-content", "mods.json"),
            this.logger);
    }

    public async Task<IReadOnlyList<LocalMod>> GetModsAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        // 文件系统枚举在线程池执行，避免大型 mods 目录阻塞 UI；取消在逐文件边界检查。
        ArgumentNullException.ThrowIfNull(instance);
        await snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modsDirectory = GetModsDirectory(instance);
            var result = await Task.Run<IReadOnlyList<LocalMod>>(
                () =>
                {
                    using var contentScope = InstanceContentOwnership.Open(instance, modsDirectory);
                    CleanupLegacyIconCacheDirectory();
                    cancellationToken.ThrowIfCancellationRequested();
                    var inventory = Directory.Exists(modsDirectory)
                        ? Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                            .Where(IsSupportedModPath)
                            .Select(CreateIdentity)
                            .ToArray()
                        : [];
                    var currentPaths = inventory
                        .Select(item => item.FullPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    metadataCache.PruneDirectory(modsDirectory, currentPaths);
                    if (inventory.Length == 0)
                    {
                        snapshots.Remove(modsDirectory);
                        logger.LogDebug(
                            "Local mod inventory checked. InstanceDirectory={InstanceDirectory} Count=0",
                            instance.InstanceDirectory);
                        return [];
                    }

                    var snapshot = snapshots.GetOrCreate(modsDirectory, contentScope.CacheIdentity);
                    var mods = snapshot.Reconcile(
                            inventory,
                            static item => item.FullPath,
                            CreateLocalMod,
                            mod => InstanceContentOwnership.CanReuse(mod, mod.FullPath))
                        .Select(mod => contentScope.Bind(mod))
                        .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    logger.LogDebug(
                        "Local mod inventory checked. InstanceDirectory={InstanceDirectory} Count={ModCount}",
                        instance.InstanceDirectory,
                        mods.Length);
                    return mods;
                },
                cancellationToken).ConfigureAwait(false);
            await metadataCache.SaveIfChangedAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    public async Task<LocalMod> ImportAsync(
        GameInstance instance,
        string sourceJarPath,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default)
    {
        // 导入完成前不修改集合；复制失败或取消时由本方法清理未完成目标。
        if (!File.Exists(sourceJarPath))
            throw new ModFileImportNotFoundException(sourceJarPath);

        var modsDirectory = GetModsDirectory(instance);
        using var contentScope = InstanceContentOwnership.Open(instance, modsDirectory, createDirectory: true);

        var destination = Path.Combine(modsDirectory, Path.GetFileName(sourceJarPath));
        if (File.Exists(destination) && !overwriteExisting)
        {
            var name = Path.GetFileNameWithoutExtension(sourceJarPath);
            destination = Path.Combine(modsDirectory, $"{name}-{DateTimeOffset.Now:yyyyMMddHHmmss}.jar");
        }

        var staging = Path.Combine(modsDirectory, $".import-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = File.OpenRead(sourceJarPath))
            await using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            contentScope.EnsureCurrent();
            File.Move(staging, destination, overwriteExisting);
        }
        finally
        {
            if (contentScope.IsCurrent && File.Exists(staging)) File.Delete(staging);
        }
        logger.LogDebug(
            "Local mod imported. InstanceDirectory={InstanceDirectory} FileName={FileName} Destination={Destination} OverwriteExisting={OverwriteExisting}",
            instance.InstanceDirectory,
            Path.GetFileName(destination),
            destination,
            overwriteExisting);
        return contentScope.Bind(ToLocalMod(destination));
    }

    public async Task SetEnabledAsync(LocalMod mod, bool enabled, CancellationToken cancellationToken = default)
    {
        // 启停通过 .disabled 后缀表达，文件内容和原始 Mod 名称均保持不变。
        if (mod.IsEnabled == enabled)
            return;

        var current = mod.FullPath;
        var targetPath = enabled
            ? GetEnabledModPath(current)
            : GetDisabledModPath(current);

        await snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var contentLease = InstanceContentOwnership.Acquire(mod, current);
            if (PathExists(targetPath))
                throw new ModEnabledStateConflictException(targetPath);

            MoveFileWithoutOverwrite(current, targetPath);
            var identity = CreateIdentity(targetPath);
            var updatedSnapshot = false;
            var modsDirectory = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(modsDirectory)
                && snapshots.TryGet(modsDirectory, out var snapshot))
            {
                updatedSnapshot = snapshot.TryMove(
                    current,
                    targetPath,
                    identity,
                    item => ApplyEnabledState(item, identity, enabled));
            }

            if (!updatedSnapshot)
                ApplyEnabledState(mod, identity, enabled);
            metadataCache.TryMove(current, identity);

            logger.LogInformation(
                "Local mod enabled state changed. FileName={FileName} Enabled={Enabled}",
                mod.FileName,
                enabled);
            logger.LogDebug("Local mod enabled state target. TargetPath={TargetPath}", targetPath);
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    internal static void MoveFileWithoutOverwrite(string sourcePath, string targetPath)
    {
        try
        {
            File.Move(sourcePath, targetPath);
        }
        catch (IOException exception) when (PathExists(targetPath))
        {
            // 目标可能在预检查后由 watcher、外部程序或另一进程创建；始终按同名冲突处理。
            throw new ModEnabledStateConflictException(targetPath, exception);
        }
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    public Task DeleteAsync(LocalMod mod, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var contentLease = InstanceContentOwnership.Acquire(mod, mod.FullPath);
        if (File.Exists(mod.FullPath))
        {
            userFileDeletionService.DeleteFile(mod.FullPath);
            logger.LogInformation("Local mod deleted. FileName={FileName}", mod.FileName);
        }

        return Task.CompletedTask;
    }

    private static bool IsSupportedModPath(string path) =>
        path.EndsWith(EnabledModExtension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(DisabledModExtension, StringComparison.OrdinalIgnoreCase);

    private static void ApplyEnabledState(
        LocalMod mod,
        LocalContentFileIdentity identity,
        bool enabled)
    {
        mod.FileName = Path.GetFileName(identity.FullPath);
        mod.FullPath = identity.FullPath;
        mod.IsEnabled = enabled;
        mod.SizeBytes = identity.Length;
    }

    private static LocalContentFileIdentity CreateIdentity(string path)
    {
        var file = new FileInfo(path);
        return new LocalContentFileIdentity(
            file.FullName,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            file.CreationTimeUtc.Ticks);
    }

    private LocalMod CreateLocalMod(LocalContentFileIdentity identity)
    {
        if (!metadataCache.TryGet(identity, validate: null, out var cached))
        {
            var metadata = TryResolveMetadata(new FileInfo(identity.FullPath));
            cached = new ModMetadataCacheValue(
                metadata.DisplayName,
                metadata.Loader,
                metadata.ModId,
                metadata.Version);
            metadataCache.Set(identity, cached);
        }

        return CreateLocalMod(identity, new ResolvedModMetadata(
            cached.DisplayName,
            cached.Loader,
            cached.ModId,
            cached.Version,
            null));
    }

    private LocalMod ToLocalMod(string path)
    {
        // 展示对象同时携带规范路径和启用状态，后续命令不再重复猜测扩展名语义。
        var identity = CreateIdentity(path);
        return CreateLocalMod(identity, TryResolveMetadata(new FileInfo(identity.FullPath)));
    }

    private static LocalMod CreateLocalMod(
        LocalContentFileIdentity identity,
        ResolvedModMetadata metadata)
    {
        var enabled = IsEnabledModPath(identity.FullPath);
        return new LocalMod
        {
            Name = string.IsNullOrWhiteSpace(metadata.DisplayName)
                ? GetDisplayFileNameWithoutModExtensions(identity.FullPath)
                : metadata.DisplayName,
            Loader = metadata.Loader,
            ModId = metadata.ModId,
            Version = metadata.Version,
            FileName = Path.GetFileName(identity.FullPath),
            FullPath = identity.FullPath,
            IconSource = metadata.IconSource,
            IsEnabled = enabled,
            SizeBytes = identity.Length,
            Source = "Local"
        };
    }

    private ResolvedModMetadata TryResolveMetadata(FileInfo jarFile)
    {
        // JAR 可能包含多个声明文件，按 Loader 专用格式到旧 mcmod.info 的顺序回退。
        try
        {
            using var archive = ZipFile.OpenRead(jarFile.FullName);
            var declaration = TryFindMetadataDeclaration(archive);
            return new ResolvedModMetadata(
                declaration?.DisplayName,
                declaration?.Loader,
                declaration?.ModId,
                declaration?.Version,
                null);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(
                exception,
                "Failed to parse mod metadata while resolving display information. FileName={FileName}",
                jarFile.Name);
            return ResolvedModMetadata.Empty;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(
                exception,
                "Failed to read mod jar while resolving display information. FileName={FileName}",
                jarFile.Name);
            return ResolvedModMetadata.Empty;
        }
        catch (NotSupportedException exception)
        {
            logger.LogWarning(
                exception,
                "Failed to resolve mod display information. FileName={FileName}",
                jarFile.Name);
            return ResolvedModMetadata.Empty;
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                exception,
                "Failed to read mod jar while resolving display information. FileName={FileName}",
                jarFile.Name);
            return ResolvedModMetadata.Empty;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unexpected error while resolving mod display information. FileName={FileName}",
                jarFile.Name);
            return ResolvedModMetadata.Empty;
        }
    }

    private static MetadataDeclaration? TryFindMetadataDeclaration(ZipArchive archive)
    {
        return TryFindFabricMetadataDeclaration(archive)
               ?? TryFindQuiltMetadataDeclaration(archive)
               ?? TryFindNeoForgeTomlMetadataDeclaration(archive)
               ?? TryFindForgeTomlMetadataDeclaration(archive)
               ?? TryFindMcmodInfoMetadataDeclaration(archive);
    }

    private static MetadataDeclaration? TryFindFabricMetadataDeclaration(ZipArchive archive)
    {
        var entry = archive.GetEntry("fabric.mod.json");
        if (entry is null)
            return null;

        var root = ParseJsonEntry(entry);
        var displayName = TryReadJsonString(root?["name"]);
        var modId = TryReadJsonString(root?["id"]);
        var version = TryReadJsonString(root?["version"]);
        return new MetadataDeclaration(entry.FullName, "fabric", displayName, modId, version);
    }

    private static MetadataDeclaration? TryFindQuiltMetadataDeclaration(ZipArchive archive)
    {
        var entry = archive.GetEntry("quilt.mod.json");
        if (entry is null)
            return null;

        var root = ParseJsonEntry(entry);
        var displayName = TryReadJsonString(root?["quilt_loader"]?["metadata"]?["name"])
                          ?? TryReadJsonString(root?["quilt_loader"]?["name"]);
        var modId = TryReadJsonString(root?["quilt_loader"]?["id"])
                    ?? TryReadJsonString(root?["id"]);
        var version = TryReadJsonString(root?["quilt_loader"]?["version"])
                      ?? TryReadJsonString(root?["version"]);
        return new MetadataDeclaration(entry.FullName, "quilt", displayName, modId, version);
    }

    private static MetadataDeclaration? TryFindNeoForgeTomlMetadataDeclaration(ZipArchive archive)
    {
        return TryFindTomlMetadataDeclaration(archive, "META-INF/neoforge.mods.toml", "neoforge");
    }

    private static MetadataDeclaration? TryFindForgeTomlMetadataDeclaration(ZipArchive archive)
    {
        return TryFindTomlMetadataDeclaration(archive, "META-INF/mods.toml", "forge");
    }

    private static MetadataDeclaration? TryFindTomlMetadataDeclaration(
        ZipArchive archive,
        string entryName,
        string loader)
    {
        // TOML 可能包含多个 [[mods]] 块，这里只提取展示所需的首个有效声明。
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return null;

        var content = ReadTextEntry(entry);
        var displayName = TryReadTomlValue(content, TomlDisplayNameRegex);
        var modId = TryReadTomlValue(content, TomlModIdRegex);
        var version = TryReadTomlValue(content, TomlVersionRegex);
        return new MetadataDeclaration(entry.FullName, loader, displayName, modId, version);
    }

    private static MetadataDeclaration? TryFindMcmodInfoMetadataDeclaration(ZipArchive archive)
    {
        var entry = archive.GetEntry("mcmod.info");
        if (entry is null)
            return null;

        var root = ParseJsonEntry(entry);
        var displayName = FindFirstJsonString(root, "name", NormalizeDisplayName);
        var modId = FindFirstJsonString(root, "modid", NormalizeDisplayName);
        var version = FindFirstJsonString(root, "version", NormalizeDisplayName);
        return new MetadataDeclaration(entry.FullName, "forge", displayName, modId, version);
    }

    private static JsonNode? ParseJsonEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream);
    }

    private static string ReadTextEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? TryReadJsonString(JsonNode? node)
    {
        return node is JsonValue stringValue && stringValue.TryGetValue<string>(out var value)
            ? NormalizeDisplayName(value)
            : null;
    }

    private static string? TryReadTomlValue(string content, Regex regex)
    {
        var match = regex.Match(content);
        return match.Success
            ? NormalizeDisplayName(match.Groups["value"].Value)
            : null;
    }

    private static string? FindFirstJsonString(
        JsonNode? node,
        string propertyName,
        Func<string?, string?> normalize)
    {
        // 兼容数组、对象和不同历史字段名，返回首个非空标量作为展示元数据。
        switch (node)
        {
            case JsonObject objectNode:
                if (objectNode[propertyName] is JsonValue value && value.TryGetValue<string>(out var direct))
                    return normalize(direct);

                foreach (var property in objectNode)
                {
                    var nested = FindFirstJsonString(property.Value, propertyName, normalize);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }

                return null;
            case JsonArray arrayNode:
                foreach (var item in arrayNode)
                {
                    var nested = FindFirstJsonString(item, propertyName, normalize);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }

                return null;
            default:
                return null;
        }
    }

    private static string? NormalizeDisplayName(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string GetModsDirectory(GameInstance instance) => Path.Combine(instance.InstanceDirectory, "mods");

    private void CleanupLegacyIconCacheDirectory()
    {
        // 旧缓存已由新的图标服务接管；清理失败不影响 Mod 枚举和启停核心功能。
        if (Interlocked.Exchange(ref legacyIconCacheCleanupStarted, 1) != 0)
            return;

        try
        {
            if (!Directory.Exists(legacyIconCacheDirectory))
                return;

            Directory.Delete(legacyIconCacheDirectory, recursive: true);
            logger.LogDebug(
                "Legacy embedded mod icon cache directory deleted. CacheDirectory={CacheDirectory}",
                legacyIconCacheDirectory);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Failed to delete legacy embedded mod icon cache directory. CacheDirectory={CacheDirectory}",
                legacyIconCacheDirectory);
        }
    }

    private static bool IsEnabledModPath(string path)
    {
        return path.EndsWith(EnabledModExtension, StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(DisabledModExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDisabledModPath(string path)
    {
        // 重复禁用保持幂等，避免生成 .disabled.disabled 使后续启用无法还原。
        if (path.EndsWith(DisabledModExtension, StringComparison.OrdinalIgnoreCase))
            return path;

        if (!path.EndsWith(EnabledModExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported mod path for disable: {path}");

        return path + ".disabled";
    }

    private static string GetEnabledModPath(string path)
    {
        if (path.EndsWith(DisabledModExtension, StringComparison.OrdinalIgnoreCase))
            return path[..^".disabled".Length];

        if (!path.EndsWith(EnabledModExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported mod path for enable: {path}");

        return path;
    }

    private static string GetDisplayFileNameWithoutModExtensions(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(DisabledModExtension, StringComparison.OrdinalIgnoreCase))
            return fileName[..^DisabledModExtension.Length];

        if (fileName.EndsWith(EnabledModExtension, StringComparison.OrdinalIgnoreCase))
            return fileName[..^EnabledModExtension.Length];

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private sealed record MetadataDeclaration(
        string MetadataEntryName,
        string? Loader,
        string? DisplayName,
        string? ModId,
        string? Version);

    private sealed record ModMetadataCacheValue(
        string? DisplayName,
        string? Loader,
        string? ModId,
        string? Version);

    private sealed record ResolvedModMetadata(
        string? DisplayName,
        string? Loader,
        string? ModId,
        string? Version,
        string? IconSource)
    {
        public static readonly ResolvedModMetadata Empty = new(null, null, null, null, null);
    }
}
