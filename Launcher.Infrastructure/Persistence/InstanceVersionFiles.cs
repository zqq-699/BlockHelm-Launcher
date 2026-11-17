/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Text.Json;
using Launcher.Application;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Persistence;

internal static class InstanceVersionFiles
{
    private const string LauncherDirectoryName = LauncherApplicationIdentity.StorageDirectoryName;
    private const string InstanceSettingsFileName = "instance-settings.json";
    public static string? TryResolveVersionJsonPath(
        string versionDirectory,
        string versionName,
        ILogger? logger = null)
    {
        // 标准同名 JSON 优先；非标准目录只有在候选可唯一确定时才接受，避免误认辅助配置文件。
        var versionJsonPath = Path.Combine(versionDirectory, $"{versionName}.json");
        if (File.Exists(versionJsonPath))
            return versionJsonPath;

        var candidates = EnumerateVersionJsonCandidates(versionDirectory).ToList();
        if (candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
        {
            logger?.LogDebug(
                "Using non-matching version json file during instance discovery. VersionName={VersionName} VersionJsonPath={VersionJsonPath}",
                versionName,
                candidates[0]);
            return candidates[0];
        }

        var idMatches = candidates
            .Where(candidate => VersionJsonIdMatches(candidate, versionName))
            .ToList();
        if (idMatches.Count == 1)
            return idMatches[0];

        var fileNameMatches = candidates
            .Where(candidate => string.Equals(Path.GetFileNameWithoutExtension(candidate), versionName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fileNameMatches.Count == 1)
            return fileNameMatches[0];

        logger?.LogWarning(
            "Skipping version directory with ambiguous json candidates. VersionName={VersionName} CandidateCount={CandidateCount} VersionDirectory={VersionDirectory}",
            versionName,
            candidates.Count,
            versionDirectory);
        return null;
    }

    private static IEnumerable<string> EnumerateVersionJsonCandidates(string versionDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(versionDirectory, "*.json", SearchOption.AllDirectories).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        return files.Where(path => IsVersionJsonCandidate(versionDirectory, path));
    }

    private static bool IsVersionJsonCandidate(string versionDirectory, string path)
    {
        if (string.Equals(Path.GetFileName(path), InstanceSettingsFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        var relativeSegments = Path.GetRelativePath(versionDirectory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relativeSegments.Any(segment => string.Equals(segment, LauncherDirectoryName, StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
                return false;

            return HasStringProperty(root, "mainClass")
                   || HasStringProperty(root, "inheritsFrom")
                   || HasStringProperty(root, "minecraftArguments")
                   || HasStringProperty(root, "clientVersion")
                   || root.TryGetProperty("arguments", out _)
                   || root.TryGetProperty("libraries", out _)
                   || root.TryGetProperty("downloads", out _)
                   || root.TryGetProperty("patches", out _)
                   || (HasStringProperty(root, "id") && HasStringProperty(root, "type"));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static bool VersionJsonIdMatches(string path, string versionName)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            return string.Equals(GetStringProperty(json.RootElement, "id"), versionName, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }


    internal static string ResolveJarPath(string directory, string versionName)
        => ResolveJarPath(directory, versionName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static string ResolveJarPath(string directory, string versionName, HashSet<string> visited)
    {
        if (!visited.Add(Path.GetFullPath(directory)) || visited.Count > 10)
            throw new InvalidDataException("Cyclic client jar reference.");
        var jsonPath = TryResolveVersionJsonPath(directory, versionName);
        if (jsonPath is null) return Path.Combine(directory, $"{versionName}.jar");
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var jar = GetStringProperty(document.RootElement, "jar");
        var id = GetStringProperty(document.RootElement, "id");
        var reference = string.IsNullOrWhiteSpace(jar)
            ? GetStringProperty(document.RootElement, "inheritsFrom")
            : !string.Equals(jar, id, StringComparison.OrdinalIgnoreCase) && !string.Equals(jar, versionName, StringComparison.OrdinalIgnoreCase)
                ? jar : null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (!VersionDirectoryName.IsSafeDirectoryName(reference)) throw new InvalidDataException("Invalid client jar reference.");
            return ResolveJarPath(Path.Combine(Path.GetDirectoryName(directory)!, reference), reference, visited);
        }
        return Path.ChangeExtension(jsonPath, ".jar");
    }

    private static string? GetStringProperty(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool HasStringProperty(JsonElement root, string name) => !string.IsNullOrWhiteSpace(GetStringProperty(root, name));

    public static string GetJsonPath(string directory, string name) => TryResolveVersionJsonPath(directory, name)
        ?? throw new FileNotFoundException("Instance manifest is missing or ambiguous.", directory);

    public static string GetOwnJarPath(string directory, string name)
        => Path.ChangeExtension(GetJsonPath(directory, name), ".jar");
}
