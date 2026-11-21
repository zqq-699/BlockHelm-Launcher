/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record PreparedLoaderInstaller(string ExecutionInstallerPath, ForgeInstallerPlan Plan);

internal sealed partial class LoaderInstallerArtifactService
{
    public async Task<PreparedLoaderInstaller> PrepareInstallerMappingsAsync(
        string installerPath,
        ForgeInstallerPlan plan,
        string minecraftVersion,
        string minecraftDirectory,
        JsonObject? verifiedVersionMetadata,
        DownloadSourcePreference preference,
        int speedLimit,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext = null,
        SpeedMeter? speedMeter = null,
        ModpackInstallEnvironment environment = ModpackInstallEnvironment.Client)
    {
        var mappings = await ReadInstallerMappingsPlanAsync(installerPath, plan, minecraftVersion, minecraftDirectory,
            verifiedVersionMetadata, environment, cancellationToken).ConfigureAwait(false);
        return await PrepareInstallerMappingsAsync(installerPath, plan, mappings, minecraftVersion, minecraftDirectory,
            preference, speedLimit, cancellationToken, operationContext, speedMeter).ConfigureAwait(false);
    }

    private async Task<InstallerMappingsPlan?> ReadInstallerMappingsPlanAsync(
        string installerPath,
        ForgeInstallerPlan plan,
        string minecraftVersion,
        string minecraftDirectory,
        JsonObject? verifiedVersionMetadata,
        ModpackInstallEnvironment environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous);
        using var archive = OpenInstallerArchive(source, installerPath);
        var profile = await ReadObjectAsync(archive, "install_profile.json", required: true, cancellationToken).ConfigureAwait(false);
        if (profile["processors"] is not JsonArray processors)
            return null;

        var side = environment is ModpackInstallEnvironment.Server ? "server" : "client";
        var oppositeSide = environment is ModpackInstallEnvironment.Server ? "client" : "server";
        var values = ReadSideData(profile["data"] as JsonObject, environment);
        values["SIDE"] = side;
        values["MINECRAFT_VERSION"] = minecraftVersion;
        var tasks = new List<(int Index, string RelativePath)>();
        for (var index = 0; index < processors.Count; index++)
        {
            if (processors[index] is not JsonObject processor
                || !RunsOnSide(processor, environment))
                continue;
            var options = ReadMappingsOptions(processor["args"] as JsonArray, values);
            if (options.GetValueOrDefault("task") != "DOWNLOAD_MOJMAPS")
                continue;
            if (options.GetValueOrDefault("side") == oppositeSide)
                continue;
            if (!IsMappingsTool(processor["jar"]))
            {
                logger.LogDebug("Mappings processor uses an unsupported tool. ProcessorIndex={ProcessorIndex} Tool={Tool} Side={Side}",
                    index, MappingString(processor["jar"]), side);
                return Fallback("UnsupportedTool", index);
            }
            if (options.GetValueOrDefault("side") != side
                || options.GetValueOrDefault("version") != minecraftVersion)
                return Fallback("ProcessorParameters", index);
            var output = options.GetValueOrDefault("output") ?? string.Empty;
            var outputValue = output.Trim('[', ']');
            if (Path.IsPathRooted(outputValue) || outputValue.Split('/', '\\', ':').Any(segment => segment == ".."))
                throw InvalidMappings("UnsafeOutputPath", index, side);
            if (output.Length < 3 || output[0] != '[' || output[^1] != ']'
                || output.Contains('{') || output.Contains('}')
                || !ManagedLibraryArtifactResolver.TryBuildMavenPath(output[1..^1], null, out var relativePath))
                return Fallback("OutputPath", index);
            if (!IsSafeRelativePath(relativePath))
                throw InvalidMappings("UnsafeOutputPath", index, side);
            _ = ResolveLibraryPath(minecraftDirectory, relativePath);
            tasks.Add((index, relativePath));
        }
        if (tasks.Count == 0)
            return null;

        var mappings = verifiedVersionMetadata?["downloads"]?[$"{side}_mappings"] as JsonObject;
        var url = MappingString(mappings?["url"]);
        var sha1 = MappingString(mappings?["sha1"]);
        if (MappingString(verifiedVersionMetadata?["id"]) != minecraftVersion
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !MinecraftFileIntegrity.IsSha1(sha1))
            return Fallback("VersionMetadata");
        long? size = mappings?["size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var bytes) && bytes > 0
            ? bytes : null;
        var prerequisites = plan.PrerequisiteLibraries.ToDictionary(
            library => library.Artifact.RelativePath, StringComparer.OrdinalIgnoreCase);
        var mappingLibraries = new Dictionary<string, ForgeInstallerLibrary>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            var artifact = new ManagedLibraryArtifact(url!, task.RelativePath, null, "Mojang", sha1, size);
            AddLibrary(artifact, new Dictionary<string, string>(), prerequisites);
            prerequisites[task.RelativePath] = new ForgeInstallerLibrary(artifact, null);
            mappingLibraries[task.RelativePath] = new ForgeInstallerLibrary(artifact, null);
        }

        return new InstallerMappingsPlan(side, profile, tasks.Select(task => task.Index).ToArray(), mappingLibraries,
            plan with { PrerequisiteLibraries = prerequisites.Values.OrderBy(library => library.Artifact.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray() });

        InstallerMappingsPlan? Fallback(string reason, int? processorIndex = null)
        {
            logger.LogWarning("Mappings preparation is unsupported; using the original installer. Reason={Reason} ProcessorIndex={ProcessorIndex} Side={Side}",
                reason, processorIndex, side);
            return null;
        }
    }

    private async Task<PreparedLoaderInstaller> PrepareInstallerMappingsAsync(
        string installerPath,
        ForgeInstallerPlan originalPlan,
        InstallerMappingsPlan? mappings,
        string minecraftVersion,
        string minecraftDirectory,
        DownloadSourcePreference preference,
        int speedLimit,
        CancellationToken cancellationToken,
        MinecraftDownloadOperationContext? operationContext,
        SpeedMeter? speedMeter,
        bool parallelDownloads = false)
    {
        if (mappings is null)
            return new PreparedLoaderInstaller(installerPath, originalPlan);

        // Use the same downloader and verified-file handling as installer libraries.
        // Do not remove the Java task until every mapping file has been accepted.
        await MaterializeLibrariesAsync(installerPath, mappings.Libraries.Values, minecraftDirectory,
            preference, speedLimit, cancellationToken, operationContext, speedMeter, parallelDownloads).ConfigureAwait(false);
        var profile = mappings.Profile;
        var processors = (JsonArray)profile["processors"]!;
        foreach (var index in mappings.ProcessorIndices.OrderDescending())
            processors.RemoveAt(index);

        var executionPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(installerPath))!,
            $"{Path.GetFileNameWithoutExtension(installerPath)}.bhl-managed-{Guid.NewGuid():N}.jar");
        await using var source = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous);
        using var archive = OpenInstallerArchive(source, installerPath);
        string originalSha256;
        string executionSha256;
        try
        {
            var originalManifest = await ReadExecutableManifestAsync(archive, cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(executionPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            using (var prepared = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsJarSignature(entry.FullName))
                        continue;
                    var copy = prepared.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                    copy.LastWriteTime = entry.LastWriteTime;
                    copy.ExternalAttributes = entry.ExternalAttributes;
                    await using var output = copy.Open();
                    if (entry.FullName == "install_profile.json")
                        await System.Text.Json.JsonSerializer.SerializeAsync(output, profile, JsonOptions, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        await using var input = entry.Open();
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Inspect only the edited profile and executable manifest, not every embedded resource.
            if (!await ValidateExecutionCopyAsync(executionPath, profile, originalManifest, cancellationToken).ConfigureAwait(false))
            {
                TryDeleteFile(executionPath);
                logger.LogWarning("Mappings preparation is unsupported; using the original installer. Reason={Reason} Side={Side}",
                    "ExecutionCopyValidation", mappings.Side);
                return new PreparedLoaderInstaller(installerPath, originalPlan);
            }
            source.Position = 0;
            originalSha256 = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            await using var execution = File.OpenRead(executionPath);
            executionSha256 = Convert.ToHexString(await SHA256.HashDataAsync(execution, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            TryDeleteFile(executionPath);
            throw;
        }
        logger.LogInformation(
            "Installer mappings prepared by launcher. Side={Side} MinecraftVersion={MinecraftVersion} MappingFiles={MappingFiles} SkippedProcessors={SkippedProcessors} ProcessorIndices={ProcessorIndices} OriginalInstaller={OriginalInstaller} ExecutionInstaller={ExecutionInstaller} OriginalInstallerSha256={OriginalInstallerSha256} ExecutionInstallerSha256={ExecutionInstallerSha256}",
            mappings.Side, minecraftVersion, mappings.Libraries.Count, mappings.ProcessorIndices.Count, string.Join(",", mappings.ProcessorIndices),
            Path.GetFileName(installerPath), Path.GetFileName(executionPath), originalSha256, executionSha256);
        return new PreparedLoaderInstaller(executionPath, mappings.PreparedPlan);
    }

    private sealed record InstallerMappingsPlan(
        string Side,
        JsonObject Profile,
        IReadOnlyList<int> ProcessorIndices,
        IReadOnlyDictionary<string, ForgeInstallerLibrary> Libraries,
        ForgeInstallerPlan PreparedPlan);

    private static async Task<bool> ValidateExecutionCopyAsync(
        string path, JsonObject expectedProfile, string? originalManifest, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var actualProfile = await ReadObjectAsync(archive, "install_profile.json", required: true, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadExecutableManifestAsync(archive, cancellationToken).ConfigureAwait(false);
            return JsonNode.DeepEquals(expectedProfile, actualProfile)
                && manifest is not null && manifest == originalManifest
                && manifest.Split('\n').TakeWhile(line => line.TrimEnd('\r').Length > 0)
                    .Any(line => line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(line["Main-Class:".Length..]));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadExecutableManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsMappingsTool(JsonNode? node)
    {
        var coordinate = MappingString(node)?.Split(':');
        return coordinate is { Length: >= 3 } && coordinate[1] == "installertools"
            && coordinate[0] is "net.minecraftforge" or "net.neoforged.installertools";
    }

    private static Dictionary<string, string> ReadMappingsOptions(JsonArray? args, IReadOnlyDictionary<string, string> values)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args is null) return options;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = ResolveExpression(MappingString(args[index]), values).Trim();
            if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
            var equals = argument.IndexOf('=');
            if (equals >= 0)
                options[argument[2..equals]] = TrimLiteral(argument[(equals + 1)..]) ?? string.Empty;
            else if (index + 1 < args.Count
                && !(MappingString(args[index + 1]) ?? string.Empty).StartsWith("--", StringComparison.Ordinal))
                options[argument[2..]] = TrimLiteral(ResolveExpression(MappingString(args[++index]), values)) ?? string.Empty;
        }
        return options;
    }

    private static bool IsJarSignature(string name)
    {
        if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;
        var file = name["META-INF/".Length..];
        return !file.Contains('/') && (file.StartsWith("SIG-", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(file).ToUpperInvariant() is ".SF" or ".RSA" or ".DSA" or ".EC");
    }

    private static string? MappingString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static InvalidDataException InvalidMappings(string reason, int processor, string side) =>
        SafeExceptionLog.Describe(new InvalidDataException("Installer client mappings cannot be prepared."),
            "InstallerMappingsInvalid", ("Reason", reason), ("ProcessorIndex", processor), ("Side", side));
}
