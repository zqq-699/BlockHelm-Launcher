/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Infrastructure.Persistence;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>
/// Loads installed version metadata from the instance directory without ever
/// comparing or replacing it with a same-named Mojang version.
/// </summary>
internal sealed class InstanceAuthoritativeVersionLoader(
    MinecraftPath minecraftPath,
    Func<string, CancellationToken, Task<JsonObject>> resolveMissingParentAsync,
    ILogger? logger = null)
    : IVersionLoader
{
    private const int MaximumInheritanceDepth = 10;
    private readonly ConcurrentDictionary<string, JsonObject> remoteParentCache =
        new(StringComparer.Ordinal);

    public async ValueTask<VersionMetadataCollection> GetVersionMetadatasAsync(
        CancellationToken cancellationToken = default)
    {
        var localVersions = new List<(string Name, string? Path, Exception? Error)>();
        if (Directory.Exists(minecraftPath.Versions))
        {
            foreach (var directory in Directory.EnumerateDirectories(minecraftPath.Versions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!InstanceDirectoryClassifier.IsInstanceDirectory(directory))
                    continue;
                var name = Path.GetFileName(directory);
                try
                {
                    var marker = PendingInstanceRenameMarkerFile.Read(directory);
                    if (marker.Status == PendingInstanceRenameMarkerStatus.Unreadable)
                        throw marker.Exception ?? new IOException("Unable to read the instance rename marker.");
                    if (marker.Status != PendingInstanceRenameMarkerStatus.Missing)
                        continue;
                    var path = InstanceVersionFiles.TryResolveVersionJsonPath(directory, name);
                    if (path is not null)
                        localVersions.Add((name, path, null));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(exception, "Unable to read local version metadata during launch discovery. InstanceDirectory={InstanceDirectory}", directory);
                    // Preserve the local name so an unreadable parent cannot be replaced with remote metadata.
                    localVersions.Add((name, null, exception));
                }
            }
        }
        var knownNames = localVersions
            .Select(version => version.Name)
            .ToHashSet(StringComparer.Ordinal);
        var metadata = new List<IVersionMetadata>(localVersions.Count);
        var missingParentNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, path, error) in localVersions)
        {
            if (error is not null)
            {
                metadata.Add(new UnreadableLocalVersionMetadata(name, error));
                continue;
            }
            if (path is null)
                continue;
            metadata.Add(new ReadOnlyLocalVersionMetadata(name, path));

            var parentName = await TryReadParentNameAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(parentName) && !knownNames.Contains(parentName))
                missingParentNames.Add(parentName);
        }

        foreach (var parentName in missingParentNames)
        {
            if (!knownNames.Add(parentName))
                continue;
            metadata.Add(new ReadOnlyRemoteVersionMetadata(parentName, ResolveMissingParentAsync));
        }

        return new VersionMetadataCollection(metadata, latestRelease: null, latestSnapshot: null)
        {
            MaxDepth = MaximumInheritanceDepth
        };
    }

    private async Task<JsonObject> ResolveMissingParentAsync(
        string versionName,
        CancellationToken cancellationToken)
    {
        if (remoteParentCache.TryGetValue(versionName, out var cached))
            return (JsonObject)cached.DeepClone();

        var resolved = await resolveMissingParentAsync(versionName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(GetString(resolved["id"]), versionName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resolved parent version id does not match the requested version: {versionName}.");
        }
        remoteParentCache.TryAdd(versionName, (JsonObject)resolved.DeepClone());
        return resolved;
    }

    private static async Task<string?> TryReadParentNameAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("inheritsFrom", out var inheritsFrom)
                   && inheritsFrom.ValueKind == JsonValueKind.String
                ? inheritsFrom.GetString()
                : null;
        }
        catch (JsonException)
        {
            // Keep malformed local metadata authoritative. CmlLib will report
            // the parse failure if that version is selected; never fall back
            // to a same-named remote version.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private sealed class ReadOnlyLocalVersionMetadata(string name, string path) : IVersionMetadata
    {
        public string Name => name;
        public string? Type => "local";
        public DateTimeOffset ReleaseTime => default;
        public async Task<IVersion> GetVersionAsync(CancellationToken cancellationToken = default)
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
            var originalId = GetString(node["id"]);
            if (GetString(node["jar"]) is { } jar && string.Equals(jar, originalId, StringComparison.OrdinalIgnoreCase))
                node["jar"] = name;
            node["id"] = name;
            return JsonVersionParser.ParseFromJsonString(node.ToJsonString(), new JsonVersionParserOptions());
        }
        public Task<IVersion> GetAndSaveVersionAsync(MinecraftPath path, CancellationToken cancellationToken = default) => GetVersionAsync(cancellationToken);
        public Task SaveVersionAsync(MinecraftPath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class UnreadableLocalVersionMetadata(string name, Exception error) : IVersionMetadata
    {
        public string Name => name;
        public string? Type => "local";
        public DateTimeOffset ReleaseTime => default;
        public Task<IVersion> GetVersionAsync(CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested ? Task.FromCanceled<IVersion>(cancellationToken) : Task.FromException<IVersion>(error);
        public Task<IVersion> GetAndSaveVersionAsync(MinecraftPath path, CancellationToken cancellationToken = default) => GetVersionAsync(cancellationToken);
        public Task SaveVersionAsync(MinecraftPath path, CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.FromException(error);
    }

    private sealed class ReadOnlyRemoteVersionMetadata(
        string name,
        Func<string, CancellationToken, Task<JsonObject>> resolver)
        : IVersionMetadata
    {
        public string Name { get; } = name;

        public string? Type => null;

        public DateTimeOffset ReleaseTime => default;

        public async Task<IVersion> GetVersionAsync(CancellationToken cancellationToken = default)
        {
            var versionJson = await resolver(Name, cancellationToken).ConfigureAwait(false);
            var resolvedId = GetString(versionJson["id"]);
            if (!string.Equals(resolvedId, Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Resolved parent version id does not match the requested version: {Name}.");
            }
            return JsonVersionParser.ParseFromJsonString(
                versionJson.ToJsonString(),
                new JsonVersionParserOptions());
        }

        public Task<IVersion> GetAndSaveVersionAsync(
            MinecraftPath minecraftPath,
            CancellationToken cancellationToken = default) =>
            GetVersionAsync(cancellationToken);

        public Task SaveVersionAsync(
            MinecraftPath minecraftPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
