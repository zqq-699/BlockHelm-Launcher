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

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Application;
using Launcher.Application.Repositories;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Persistence;

internal sealed class InstalledVersionMetadataReader(ILogger logger)
{
    private const string LauncherDirectoryName = LauncherApplicationIdentity.StorageDirectoryName;
    private const string InstanceSettingsFileName = "instance-settings.json";
    private static readonly Regex NeoForgeVersionArgumentRegex = new(
        "--fml\\.(?:neoForgeVersion|forgeVersion)\\s+(?<version>[^\\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MinecraftVersionRegex = new(
        "(?<version>(?:[1-9][0-9]w[0-9]{2}[a-z])|(?:(?:1|[2-9][0-9])\\.[0-9]+(?:\\.[0-9]+)?(?:-(?:pre|rc|snapshot-?)[0-9]+| Pre-Release [0-9]+)?)|(?:[ab][0-9]\\.[0-9]+(?:\\.[0-9]+)?))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 枚举 versions 目录，容错读取每个版本并转换为统一的已安装版本模型。
    /// </summary>
    public Task<IReadOnlyList<InstalledGameVersion>> DiscoverAsync(
        string minecraftDirectory,
        CancellationToken cancellationToken)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        var installedVersions = new List<InstalledGameVersion>();
        foreach (var versionDirectory in EnumerateDirectories(versionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldIgnoreDirectory(versionDirectory))
                continue;
            var versionName = Path.GetFileName(versionDirectory);
            if (string.IsNullOrWhiteSpace(versionName))
                continue;
            var metadata = TryReadVersionMetadata(versionDirectory, versionName, logger);
            if (metadata is null)
                continue;
            var loader = ResolveLoader(metadata);
            var minecraftVersion = ResolveMinecraftVersion(
                metadata,
                minecraftDirectory,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            installedVersions.Add(new InstalledGameVersion(
                metadata.VersionName,
                minecraftVersion,
                ResolveVersionType(metadata, minecraftDirectory, minecraftVersion),
                loader.Kind,
                loader.Version,
                versionDirectory,
                ResolveDiscoveredAt(versionDirectory)));
        }

        logger.LogDebug(
            "Installed game versions discovered. Count={VersionCount} MinecraftDirectory={MinecraftDirectory}",
            installedVersions.Count,
            minecraftDirectory);
        return Task.FromResult<IReadOnlyList<InstalledGameVersion>>(installedVersions);
    }

    public bool Exists(string versionDirectory, string versionName) =>
        !ShouldIgnoreDirectory(versionDirectory)
        && TryReadVersionMetadata(versionDirectory, versionName, logger) is not null;

    private static bool ShouldIgnoreDirectory(string versionDirectory)
    {
        if (!InstanceDirectoryClassifier.IsInstanceDirectory(versionDirectory))
        {
            return true;
        }

        var markerStatus = PendingInstanceRenameMarkerFile.Read(versionDirectory).Status;
        return markerStatus is PendingInstanceRenameMarkerStatus.Valid
            or PendingInstanceRenameMarkerStatus.Unreadable;
    }

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try { return Directory.EnumerateDirectories(directory).ToArray(); }
        catch (DirectoryNotFoundException) { return []; }
    }

    /// <summary>
    /// 读取一个候选版本 JSON，并提取后续兼容识别所需的全部信号；损坏文件返回空。
    /// </summary>
    private static VersionJsonMetadata? TryReadVersionMetadata(
        string versionDirectory,
        string versionName,
        ILogger? logger = null)
    {
        var versionJsonPath = InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName, logger);
        if (string.IsNullOrWhiteSpace(versionJsonPath))
            return null;

        try
        {
            using var stream = File.OpenRead(versionJsonPath);
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var libraryNames = ReadLibraryNames(root);
            var argumentText = ReadArgumentText(root);
            return new VersionJsonMetadata(
                versionName,
                GetStringProperty(root, "id"),
                GetStringProperty(root, "inheritsFrom"),
                GetStringProperty(root, "jar"),
                GetStringProperty(root, "type"),
                ReadMainClassText(root),
                LauncherVersionMetadata.ReadMinecraftVersion(root),
                GetStringProperty(root, "clientVersion"),
                ReadPatchMinecraftVersion(root),
                TryReadFmlMinecraftVersion(argumentText),
                ReadAssetIndexMinecraftVersion(root),
                libraryNames,
                argumentText,
                TryResolveMinecraftVersionFromLibraries(libraryNames),
                TryReadJarMinecraftVersion(versionDirectory, root, versionName));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 以同名文件、唯一候选、唯一 id 匹配、唯一文件名匹配的顺序选择版本 JSON。
    /// </summary>
    /// <summary>
    /// 按可靠性顺序从显式元数据、继承链、库坐标、名称和 JAR 中解析 Minecraft 版本。
    /// </summary>
    private static string ResolveMinecraftVersion(
        VersionJsonMetadata metadata,
        string minecraftDirectory,
        HashSet<string> visitedVersions)
    {
        // 显式 Launcher 元数据最可靠，其次递归继承和协议字段，文件名/JAR 扫描仅作为兼容兜底。
        return FirstNonEmpty(
            metadata.LauncherMinecraftVersion,
            metadata.ClientMinecraftVersion,
            metadata.PatchMinecraftVersion,
            ResolveInheritedMinecraftVersion(metadata, minecraftDirectory, visitedVersions),
            metadata.FmlMinecraftVersion,
            metadata.AssetIndexId,
            metadata.LibraryMinecraftVersion,
            GetVersionLikeValueOrEmpty(metadata.Jar),
            GetVersionLikeValueOrEmpty(metadata.Id),
            GetVersionLikeValueOrEmpty(metadata.VersionName),
            metadata.JarMinecraftVersion);
    }

    /// <summary>
    /// 递归解析 inheritsFrom 链，并用访问集合防止损坏元数据形成循环。
    /// </summary>
    private static string ResolveInheritedMinecraftVersion(
        VersionJsonMetadata metadata,
        string minecraftDirectory,
        HashSet<string> visitedVersions)
    {
        if (string.IsNullOrWhiteSpace(metadata.InheritsFrom))
            return string.Empty;

        var directVersion = GetVersionLikeValueOrEmpty(metadata.InheritsFrom, requireFullValue: true);
        if (!string.IsNullOrWhiteSpace(directVersion))
            return directVersion;

        if (!visitedVersions.Add(metadata.InheritsFrom))
            return string.Empty;

        var inheritedDirectory = Path.Combine(minecraftDirectory, "versions", metadata.InheritsFrom);
        var inheritedMetadata = TryReadVersionMetadata(inheritedDirectory, metadata.InheritsFrom);
        if (inheritedMetadata is null)
            return GetVersionLikeValueOrEmpty(metadata.InheritsFrom);

        return ResolveMinecraftVersion(inheritedMetadata, minecraftDirectory, visitedVersions);
    }

    private static string ResolveVersionType(
        VersionJsonMetadata metadata,
        string minecraftDirectory,
        string minecraftVersion)
    {
        var versionType = NormalizeVersionType(metadata.Type);
        if (!string.IsNullOrWhiteSpace(versionType))
            return versionType;

        if (!string.IsNullOrWhiteSpace(metadata.InheritsFrom))
        {
            versionType = TryReadInheritedVersionType(
                minecraftDirectory,
                metadata.InheritsFrom,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(versionType))
                return versionType;
        }

        return IsSnapshotVersionName(metadata.VersionName)
               || IsSnapshotVersionName(metadata.Id)
               || IsSnapshotVersionName(minecraftVersion)
            ? "snapshot"
            : "release";
    }

    private static string TryReadInheritedVersionType(
        string minecraftDirectory,
        string versionName,
        HashSet<string> visitedVersions)
    {
        if (!visitedVersions.Add(versionName))
            return string.Empty;

        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        var metadata = TryReadVersionMetadata(versionDirectory, versionName);
        if (metadata is null)
            return string.Empty;

        var versionType = NormalizeVersionType(metadata.Type);
        if (!string.IsNullOrWhiteSpace(versionType))
            return versionType;

        return string.IsNullOrWhiteSpace(metadata.InheritsFrom)
            ? string.Empty
            : TryReadInheritedVersionType(minecraftDirectory, metadata.InheritsFrom, visitedVersions);
    }

    /// <summary>
    /// 综合 Maven 坐标、主类、参数和版本名称识别 Loader 类型及版本。
    /// </summary>
    private static LoaderInfo ResolveLoader(VersionJsonMetadata metadata)
    {
        // Maven 坐标能提供最准确的 Loader 与版本；文本特征只补偿缺失或旧格式元数据。
        LoaderInfo? hintedLoader = null;
        foreach (var libraryName in metadata.LibraryNames)
        {
            var loader = ResolveLoaderFromLibraryName(libraryName);
            if (loader.Kind is not LoaderKind.Vanilla)
            {
                if (!string.IsNullOrWhiteSpace(loader.Version))
                    return loader;

                hintedLoader ??= loader;
            }
        }

        var resolvedLoader = ResolveLoaderFromText(
            $"{metadata.Id} {metadata.VersionName} {metadata.InheritsFrom} {metadata.Jar} {metadata.MainClass} {metadata.ArgumentText}",
            allowLooseMatch: true);

        if (resolvedLoader.Kind is LoaderKind.Vanilla && hintedLoader is not null)
            resolvedLoader = hintedLoader;
        else if (resolvedLoader.Kind is LoaderKind.Forge && hintedLoader?.Kind is LoaderKind.NeoForge)
            resolvedLoader = hintedLoader;

        if (resolvedLoader.Kind is LoaderKind.NeoForge && string.IsNullOrWhiteSpace(resolvedLoader.Version))
            return resolvedLoader with { Version = TryReadNeoForgeVersion(metadata.ArgumentText) };

        if (resolvedLoader.Kind is LoaderKind.Forge && string.IsNullOrWhiteSpace(resolvedLoader.Version))
            return resolvedLoader with { Version = TryReadArgumentValue(metadata.ArgumentText, "--fml.forgeVersion") };

        return resolvedLoader;
    }

    private static LoaderInfo ResolveLoaderFromLibraryName(string value)
    {
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return ResolveLoaderFromText(value, allowLooseMatch: false);

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];

        if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase)
            && artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return new LoaderInfo(LoaderKind.NeoForge, version);
        }

        if (group.Equals("net.neoforged.fancymodloader", StringComparison.OrdinalIgnoreCase)
            && artifact.Equals("loader", StringComparison.OrdinalIgnoreCase))
        {
            return new LoaderInfo(LoaderKind.NeoForge, null);
        }

        if (group.Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase)
            && artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
        {
            return new LoaderInfo(LoaderKind.Quilt, version);
        }

        if (group.Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase)
            && artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
        {
            return new LoaderInfo(LoaderKind.Fabric, version);
        }

        if (group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
            && (artifact.Equals("forge", StringComparison.OrdinalIgnoreCase)
                || artifact.Equals("fmlloader", StringComparison.OrdinalIgnoreCase)))
        {
            return new LoaderInfo(LoaderKind.Forge, TryReadForgeVersion(version));
        }

        return new LoaderInfo(LoaderKind.Vanilla, null);
    }

    private static LoaderInfo ResolveLoaderFromText(string value, bool allowLooseMatch)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("net.neoforged:neoforge", StringComparison.Ordinal)
            || normalized.Contains(" neoforge-", StringComparison.Ordinal)
            || normalized.StartsWith("neoforge-", StringComparison.Ordinal)
            || (allowLooseMatch && normalized.Contains("neoforge", StringComparison.Ordinal)))
        {
            return new LoaderInfo(LoaderKind.NeoForge, TryReadMavenVersion(value));
        }

        if (normalized.Contains("org.quiltmc:quilt-loader", StringComparison.Ordinal)
            || normalized.Contains("quilt-loader", StringComparison.Ordinal)
            || (allowLooseMatch && normalized.Contains("quilt", StringComparison.Ordinal)))
        {
            return new LoaderInfo(LoaderKind.Quilt, TryReadMavenVersion(value));
        }

        if (normalized.Contains("net.fabricmc:fabric-loader", StringComparison.Ordinal)
            || normalized.Contains("fabric-loader", StringComparison.Ordinal)
            || (allowLooseMatch && normalized.Contains("fabric", StringComparison.Ordinal)))
        {
            return new LoaderInfo(LoaderKind.Fabric, TryReadMavenVersion(value));
        }

        if (normalized.Contains("net.minecraftforge:forge", StringComparison.Ordinal)
            || normalized.Contains("net.minecraftforge:fmlloader", StringComparison.Ordinal)
            || (allowLooseMatch && normalized.Contains("forge", StringComparison.Ordinal)))
        {
            return new LoaderInfo(LoaderKind.Forge, TryReadMavenVersion(value));
        }

        return new LoaderInfo(LoaderKind.Vanilla, null);
    }

    private static string? TryReadMavenVersion(string value)
    {
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return null;

        if (parts[0].Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
            && (parts[1].Equals("forge", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("fmlloader", StringComparison.OrdinalIgnoreCase)))
        {
            return TryReadForgeVersion(parts[2]);
        }

        return parts[2];
    }

    private static string? TryReadNeoForgeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = NeoForgeVersionArgumentRegex.Match(value);
        if (!match.Success)
            return null;

        var version = match.Groups["version"].Value.Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string? TryReadArgumentValue(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var inlinePattern = $@"(?:^|\s){Regex.Escape(option)}=(?<value>[^\s]+)";
        var inlineMatch = Regex.Match(
            value,
            inlinePattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (inlineMatch.Success)
            return inlineMatch.Groups["value"].Value.Trim();

        var spacedPattern = $@"(?:^|\s){Regex.Escape(option)}\s+(?<value>[^\s]+)";
        var spacedMatch = Regex.Match(
            value,
            spacedPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return spacedMatch.Success
            ? spacedMatch.Groups["value"].Value.Trim()
            : null;
    }

    private static string TryReadForgeVersion(string combinedVersion)
    {
        var separatorIndex = combinedVersion.IndexOf('-');
        return separatorIndex >= 0 && separatorIndex < combinedVersion.Length - 1
            ? combinedVersion[(separatorIndex + 1)..]
            : combinedVersion;
    }

    private static IReadOnlyList<string> ReadLibraryNames(JsonElement root)
    {
        var names = new List<string>();
        AppendLibraryNames(root, names);

        if (root.TryGetProperty("patches", out var patches)
            && patches.ValueKind is JsonValueKind.Array)
        {
            foreach (var patch in patches.EnumerateArray())
                AppendLibraryNames(patch, names);
        }

        return names;
    }

    private static void AppendLibraryNames(JsonElement root, List<string> names)
    {
        if (!root.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var library in libraries.EnumerateArray())
        {
            var name = GetStringProperty(library, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
    }

    private static string ReadArgumentText(JsonElement root)
    {
        var values = new List<string>();
        AppendArgumentText(root, values);

        if (root.TryGetProperty("patches", out var patches)
            && patches.ValueKind is JsonValueKind.Array)
        {
            foreach (var patch in patches.EnumerateArray())
                AppendArgumentText(patch, values);
        }

        return values.Count == 0 ? string.Empty : string.Join(' ', values);
    }

    private static void AppendArgumentText(JsonElement root, List<string> values)
    {
        if (!root.TryGetProperty("arguments", out var arguments)
            || arguments.ValueKind is not JsonValueKind.Object
            || !arguments.TryGetProperty("game", out var gameArguments))
        {
            var minecraftArguments = GetStringProperty(root, "minecraftArguments");
            if (!string.IsNullOrWhiteSpace(minecraftArguments))
                values.Add(minecraftArguments);
            return;
        }

        AppendArgumentValues(gameArguments, values);
    }

    private static void AppendArgumentValues(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AppendArgumentValues(item, values);
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("value", out var nestedValue))
                    AppendArgumentValues(nestedValue, values);
                break;
        }
    }

    private static string ReadMainClassText(JsonElement root)
    {
        var values = new List<string>();
        AppendMainClass(root, values);

        if (root.TryGetProperty("patches", out var patches)
            && patches.ValueKind is JsonValueKind.Array)
        {
            foreach (var patch in patches.EnumerateArray())
                AppendMainClass(patch, values);
        }

        return values.Count == 0 ? string.Empty : string.Join(' ', values);
    }

    private static void AppendMainClass(JsonElement root, List<string> values)
    {
        var mainClass = GetStringProperty(root, "mainClass");
        if (!string.IsNullOrWhiteSpace(mainClass))
            values.Add(mainClass);
    }

    private static string ReadPatchMinecraftVersion(JsonElement root)
    {
        if (!root.TryGetProperty("patches", out var patches)
            || patches.ValueKind is not JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var patch in patches.EnumerateArray())
        {
            if (string.Equals(GetStringProperty(patch, "id"), "game", StringComparison.OrdinalIgnoreCase))
            {
                var version = GetStringProperty(patch, "version");
                if (!string.IsNullOrWhiteSpace(version))
                    return version;
            }
        }

        return string.Empty;
    }

    private static string? TryReadFmlMinecraftVersion(string argumentText)
    {
        return TryReadArgumentValue(argumentText, "--fml.mcVersion");
    }

    private static string ReadAssetIndexMinecraftVersion(JsonElement root)
    {
        if (!root.TryGetProperty("assetIndex", out var assetIndex)
            || assetIndex.ValueKind is not JsonValueKind.Object)
        {
            return string.Empty;
        }

        var assetIndexId = GetStringProperty(assetIndex, "id");
        return LooksLikeMinecraftVersion(assetIndexId) ? assetIndexId : string.Empty;
    }

    private static string? TryResolveMinecraftVersionFromLibraries(IReadOnlyList<string> libraryNames)
    {
        foreach (var libraryName in libraryNames)
        {
            var minecraftVersion = TryResolveMinecraftVersionFromLibrary(libraryName);
            if (!string.IsNullOrWhiteSpace(minecraftVersion))
                return minecraftVersion;
        }

        return null;
    }

    private static string? TryResolveMinecraftVersionFromLibrary(string libraryName)
    {
        var parts = libraryName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return null;

        if (parts[0].Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
            && (parts[1].Equals("forge", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("fmlloader", StringComparison.OrdinalIgnoreCase)))
        {
            var separatorIndex = parts[2].IndexOf('-');
            return separatorIndex > 0 ? parts[2][..separatorIndex] : parts[2];
        }

        if ((parts[0].Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase))
            && parts[1].Equals("intermediary", StringComparison.OrdinalIgnoreCase)
            && LooksLikeMinecraftVersion(parts[2]))
        {
            return parts[2];
        }

        return null;
    }

    private static string TryReadJarMinecraftVersion(
        string versionDirectory,
        JsonElement root,
        string versionName)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddJarCandidate(GetStringProperty(root, "jar"));
        AddJarCandidate(GetStringProperty(root, "id"));
        AddJarCandidate(versionName);

        foreach (var jarPath in candidates)
        {
            var minecraftVersion = TryReadMinecraftVersionFromJar(jarPath);
            if (!string.IsNullOrWhiteSpace(minecraftVersion))
                return minecraftVersion;
        }

        return string.Empty;

        void AddJarCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            candidates.Add(Path.Combine(versionDirectory, $"{candidate}.jar"));
        }
    }

    private static string TryReadMinecraftVersionFromJar(string jarPath)
    {
        if (!File.Exists(jarPath))
            return string.Empty;

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entry = archive.GetEntry("version.json");
            if (entry is null)
                return string.Empty;

            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var name = GetStringProperty(json.RootElement, "name");
            return name.Length < 32 ? GetVersionLikeValueOrEmpty(name) : string.Empty;
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string GetStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
               && property.ValueKind is JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool HasStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
               && property.ValueKind is JsonValueKind.String
               && !string.IsNullOrWhiteSpace(property.GetString());
    }

    private static bool LooksLikeMinecraftVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return MinecraftVersionRegex.Match(trimmed) is { Success: true } match
               && string.Equals(match.Value, trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetVersionLikeValueOrEmpty(string? value, bool requireFullValue = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (LooksLikeMinecraftVersion(trimmed))
            return trimmed;

        if (requireFullValue)
            return string.Empty;

        var match = MinecraftVersionRegex.Match(trimmed);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    private static string NormalizeVersionType(string? type)
    {
        return type?.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal) switch
        {
            "release" => "release",
            "snapshot" => "snapshot",
            "old_beta" or "oldbeta" or "beta" => "old_beta",
            "old_alpha" or "oldalpha" or "alpha" => "old_alpha",
            _ => string.Empty
        };
    }

    private static bool IsSnapshotVersionName(string? version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && version.Length >= 5
            && char.IsDigit(version[0])
            && char.IsDigit(version[1])
            && version[2] == 'w'
            && char.IsDigit(version[3])
            && char.IsDigit(version[4]);
    }

    private static DateTimeOffset ResolveDiscoveredAt(string versionDirectory)
    {
        try
        {
            var info = new DirectoryInfo(versionDirectory);
            return new DateTimeOffset(info.CreationTimeUtc);
        }
        catch (IOException)
        {
            return DateTimeOffset.UtcNow;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private sealed record VersionJsonMetadata(
        string VersionName,
        string Id,
        string InheritsFrom,
        string Jar,
        string Type,
        string MainClass,
        string LauncherMinecraftVersion,
        string ClientMinecraftVersion,
        string PatchMinecraftVersion,
        string? FmlMinecraftVersion,
        string AssetIndexId,
        IReadOnlyList<string> LibraryNames,
        string ArgumentText,
        string? LibraryMinecraftVersion,
        string JarMinecraftVersion);

    private sealed record LoaderInfo(LoaderKind Kind, string? Version);
}
