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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

public sealed partial class ForgeLoaderProvider
{
/// <summary>
    /// 根据旧版 install_profile.json 手工创建版本元数据和 Forge Maven 构件。
    /// </summary>
    private async Task InstallLegacyForgeClientAsync(
        string installerJarPath,
        string gameDirectory,
        string minecraftVersion,
        string forgeVersion,
        DownloadSourcePreference downloadSourcePreference,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken,
        int downloadSpeedLimitMbPerSecond)
    {
        logger.LogDebug(
            "Legacy Forge fallback started. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion}",
            minecraftVersion,
            forgeVersion);

        var profile = await ReadLegacyForgeInstallProfileAsync(installerJarPath, minecraftVersion, forgeVersion, cancellationToken);

        await finalVersionInstaller.InstallAsync(
            new MinecraftPath(gameDirectory),
            minecraftVersion,
            downloadSourcePreference,
            progress,
            cancellationToken,
            downloadSpeedLimitMbPerSecond);

        WriteLegacyForgeVersionMetadata(gameDirectory, profile);
        ExtractLegacyForgeLibrary(installerJarPath, gameDirectory, profile);

        logger.LogDebug(
            "Legacy Forge fallback completed. MinecraftVersion={MinecraftVersion} LoaderVersion={LoaderVersion} SourceVersionName={SourceVersionName}",
            minecraftVersion,
            forgeVersion,
            profile.SourceVersionName);
    }

    private async Task<LegacyForgeInstallProfile> ReadLegacyForgeInstallProfileAsync(
        string installerJarPath,
        string minecraftVersion,
        string forgeVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(installerJarPath);
            var profileEntry = archive.GetEntry("install_profile.json")
                ?? throw SafeExceptionLog.Describe(new InvalidDataException("Legacy Forge installer is missing install_profile.json."), "DownloadValidation", ("Reason", "Legacy Forge installer is missing install_profile.json."));

            await using var profileStream = profileEntry.Open();
            var profileNode = await JsonNode.ParseAsync(profileStream, cancellationToken: cancellationToken)
                ?? throw SafeExceptionLog.Describe(new InvalidDataException("Legacy Forge install_profile.json is empty."), "DownloadValidation", ("Reason", "Legacy Forge install_profile.json is empty."));
            var profileObject = profileNode.AsObject();
            var installObject = profileObject["install"] as JsonObject
                ?? throw SafeExceptionLog.Describe(new InvalidDataException("Legacy Forge install_profile.json is missing install."), "DownloadValidation", ("Reason", "Legacy Forge install_profile.json is missing install."));
            var versionInfo = profileObject["versionInfo"] as JsonObject
                ?? throw SafeExceptionLog.Describe(new InvalidDataException("Legacy Forge install_profile.json is missing versionInfo."), "DownloadValidation", ("Reason", "Legacy Forge install_profile.json is missing versionInfo."));

            var sourceVersionName = GetRequiredString(versionInfo, "id", "versionInfo.id");
            var installMinecraftVersion = GetStringProperty(installObject, "minecraft");
            if (!string.IsNullOrWhiteSpace(installMinecraftVersion)
                && !string.Equals(installMinecraftVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Legacy Forge installer targets Minecraft {installMinecraftVersion}, not {minecraftVersion}.");
            }

            var forgeLibraryCoordinate = GetRequiredString(installObject, "path", "install.path");
            if (!forgeLibraryCoordinate.Contains($":{minecraftVersion}-{forgeVersion}", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Legacy Forge installer path does not match {minecraftVersion}-{forgeVersion}.");
            }

            var installerFilePath = GetRequiredString(installObject, "filePath", "install.filePath");
            if (archive.GetEntry(installerFilePath) is null)
                throw new FileNotFoundException($"Legacy Forge installer payload is missing: {installerFilePath}", installerFilePath);

            return new LegacyForgeInstallProfile(
                sourceVersionName,
                forgeLibraryCoordinate,
                installerFilePath,
                (JsonObject)versionInfo.DeepClone());
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not InvalidDataException and not FileNotFoundException)
        {
            throw new InvalidDataException("Legacy Forge installer profile could not be read.", exception);
        }
    }

    private void WriteLegacyForgeVersionMetadata(string gameDirectory, LegacyForgeInstallProfile profile)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", profile.SourceVersionName);
        var versionJsonPath = Path.Combine(versionDirectory, $"{profile.SourceVersionName}.json");
        if (Directory.Exists(versionDirectory))
            throw new IOException($"Legacy Forge source version directory already exists: {profile.SourceVersionName}");

        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(versionJsonPath, profile.VersionInfo.ToJsonString(JsonOptions));

        logger.LogDebug(
            "Legacy Forge version metadata written. SourceVersionName={SourceVersionName} VersionJsonPath={VersionJsonPath}",
            profile.SourceVersionName,
            versionJsonPath);
    }

    private void ExtractLegacyForgeLibrary(string installerJarPath, string gameDirectory, LegacyForgeInstallProfile profile)
    {
        if (!TryBuildMavenArtifactPath(profile.ForgeLibraryCoordinate, out var relativeArtifactPath))
            throw new InvalidDataException($"Legacy Forge library coordinate is invalid: {profile.ForgeLibraryCoordinate}");

        var libraryPath = Path.Combine(
            gameDirectory,
            "libraries",
            relativeArtifactPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);

        using var archive = ZipFile.OpenRead(installerJarPath);
        var payloadEntry = archive.GetEntry(profile.InstallerFilePath)
            ?? throw new FileNotFoundException($"Legacy Forge installer payload is missing: {profile.InstallerFilePath}", profile.InstallerFilePath);

        using var source = payloadEntry.Open();
        using var destination = File.Create(libraryPath);
        source.CopyTo(destination);

        logger.LogDebug(
            "Legacy Forge library extracted. Coordinate={Coordinate} LibraryPath={LibraryPath}",
            profile.ForgeLibraryCoordinate,
            libraryPath);
    }

    private static bool TryBuildMavenArtifactPath(string mavenName, out string relativePath)
    {
        relativePath = string.Empty;
        var parts = mavenName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || parts.Length > 4)
            return false;

        var extension = "jar";
        var versionAndExtension = parts[2].Split('@', 2, StringSplitOptions.TrimEntries);
        var version = versionAndExtension[0];
        if (string.IsNullOrWhiteSpace(version))
            return false;

        if (versionAndExtension.Length == 2 && !string.IsNullOrWhiteSpace(versionAndExtension[1]))
            extension = versionAndExtension[1];

        string? classifier = null;
        if (parts.Length == 4)
        {
            var classifierAndExtension = parts[3].Split('@', 2, StringSplitOptions.TrimEntries);
            classifier = classifierAndExtension[0];
            if (classifierAndExtension.Length == 2 && !string.IsNullOrWhiteSpace(classifierAndExtension[1]))
                extension = classifierAndExtension[1];
        }

        var groupPath = parts[0].Replace('.', '/');
        var artifact = parts[1];
        if (string.IsNullOrWhiteSpace(groupPath) || string.IsNullOrWhiteSpace(artifact))
            return false;

        var fileName = string.IsNullOrWhiteSpace(classifier)
            ? $"{artifact}-{version}.{extension}"
            : $"{artifact}-{version}-{classifier}.{extension}";
        relativePath = $"{groupPath}/{artifact}/{version}/{fileName}";
        return true;
    }

    private static string GetRequiredString(JsonObject node, string propertyName, string displayName)
    {
        var value = GetStringProperty(node, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Legacy Forge install_profile.json is missing {displayName}.");

        return value;
    }

    private static string GetStringProperty(JsonObject node, string name)
    {
        return node[name]?.GetValue<string>() ?? string.Empty;
    }

    private static VersionJsonMetadata? TryReadVersionMetadata(string versionDirectory, string versionName, ILogger? diagnosticLogger = null)
    {
        var versionJsonPath = Path.Combine(versionDirectory, $"{versionName}.json");
        if (!File.Exists(versionJsonPath))
            return null;

        try
        {
            using var stream = File.OpenRead(versionJsonPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return new VersionJsonMetadata(
                GetStringProperty(root, "id"),
                GetStringProperty(root, "inheritsFrom"),
                GetStringProperty(root, "jar"),
                ReadLibraryNames(root));
        }
        catch (JsonException exception)
        {
            diagnosticLogger?.LogWarning(exception, "Installer version metadata could not be read. VersionName={VersionName} Stage=InstalledVersionIdentification", versionName);
            return null;
        }
        catch (IOException exception)
        {
            diagnosticLogger?.LogWarning(exception, "Installer version metadata could not be read. VersionName={VersionName} Stage=InstalledVersionIdentification", versionName);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnosticLogger?.LogWarning(exception, "Installer version metadata could not be read. VersionName={VersionName} Stage=InstalledVersionIdentification", versionName);
            return null;
        }
    }

    private static IReadOnlyList<string> ReadLibraryNames(JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var library in libraries.EnumerateArray())
        {
            var name = GetStringProperty(library, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    private static string GetStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
               && property.ValueKind is JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed record ForgeCatalogEntry(
        string MinecraftVersion,
        string ForgeVersion,
        Uri InstallerUrl);

    private sealed record ForgeSourceMatch(
        string VersionName,
        VersionJsonMetadata Metadata);

    private sealed record LegacyForgeInstallProfile(
        string SourceVersionName,
        string ForgeLibraryCoordinate,
        string InstallerFilePath,
        JsonObject VersionInfo);

    private sealed record VersionJsonMetadata(
        string Id,
        string InheritsFrom,
        string Jar,
        IReadOnlyList<string> LibraryNames);
}
