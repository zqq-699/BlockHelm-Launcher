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

using Launcher.Infrastructure.Persistence;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed partial class ManagedVersionRepairService
{
private static async Task<JsonObject> ReadVersionJsonAsync(
        string versionDirectory,
        string versionName,
        CancellationToken cancellationToken)
    {
        var jsonPath = (InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) ?? Path.Combine(versionDirectory, $"{versionName}.json"));
        MinecraftPathGuard.EnsureSafeFileDestination(
            jsonPath,
            versionDirectory,
            "Managed version metadata");
        if (!File.Exists(jsonPath))
            throw new InstanceRepairException($"Version metadata is missing for {versionName}.");

        try
        {
            await using var stream = File.OpenRead(jsonPath);
            var jsonNode = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
                ?? throw new InstanceRepairException($"Version metadata is empty for {versionName}.");
            return jsonNode as JsonObject
                ?? throw new InstanceRepairException($"Version metadata is not a JSON object for {versionName}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InstanceRepairException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InstanceRepairException(
                $"Version metadata is invalid or unreadable for {versionName}.",
                exception);
        }
    }

    private static Task WriteVersionJsonAsync(
        string versionDirectory,
        string versionName,
        JsonObject versionJson,
        CancellationToken cancellationToken)
    {
        var jsonPath = (InstanceVersionFiles.TryResolveVersionJsonPath(versionDirectory, versionName) ?? Path.Combine(versionDirectory, $"{versionName}.json"));
        MinecraftPathGuard.EnsureSafeFileDestination(
            jsonPath,
            versionDirectory,
            "Managed version metadata");
        return File.WriteAllTextAsync(
            jsonPath,
            versionJson.ToJsonString(JsonOptions),
            cancellationToken);
    }

    private static string ResolveVersionDirectory(string minecraftDirectory, string versionName, string instanceDirectory)
    {
        var expectedDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return expectedDirectory;

        var normalizedExpected = Path.GetFullPath(expectedDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedInstance = Path.GetFullPath(instanceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return PathComparer.Equals(normalizedExpected, normalizedInstance)
            ? expectedDirectory
            : normalizedInstance;
    }

    private static string GetStringProperty(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<string>() ?? string.Empty;
    }

    private static long? GetLongProperty(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<long?>();
    }

    private static void ReportProgress(
        IProgress<LauncherProgress>? progress,
        string stage,
        string message,
        double percent)
    {
        progress?.Report(new LauncherProgress(stage, message, percent));
    }

    internal sealed record ResolvedVersionMetadata(
        string VersionName,
        JsonObject VersionJson,
        string? LocalJarPath,
        string? ClientJarUrl,
        bool WasModified,
        IReadOnlyList<string> LocalMetadataPaths,
        string? ClientJarSha1 = null,
        long? ClientJarSize = null);
}
