/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Infrastructure.Minecraft;

internal enum LoaderArtifactKind
{
    InstallerPrerequisite,
    RuntimeLibrary,
    ProcessorOutput
}

internal sealed record LoaderArtifactManifestEntry(
    string RelativePath,
    LoaderArtifactKind Kind,
    string? Source,
    string Sha1,
    string Sha256,
    long Size,
    GameFileVerificationLevel VerificationLevel);

internal sealed record LoaderArtifactManifest(
    int SchemaVersion,
    LoaderKind LoaderKind,
    string MinecraftVersion,
    string? LoaderVersion,
    string InstallerSha256,
    IReadOnlyList<LoaderArtifactManifestEntry> Artifacts);

internal sealed record LoaderArtifactManifestReadResult(
    LoaderArtifactManifest? Manifest,
    string? Error)
{
    public bool IsValid => Manifest is not null && Error is null;
}

internal static class LoaderArtifactVerificationPolicy
{
    public static GameFileVerificationLevel ResolveManifestLevel(
        LoaderArtifactKind kind,
        string? authoritativeSha1)
    {
        if (MinecraftFileIntegrity.IsSha1(authoritativeSha1))
            return GameFileVerificationLevel.HashVerified;

        return kind == LoaderArtifactKind.ProcessorOutput
            ? GameFileVerificationLevel.SizeVerified
            : GameFileVerificationLevel.TrustedAcquisitionHash;
    }

    public static bool UsesRecordedHashes(LoaderArtifactManifestEntry artifact) =>
        artifact.Kind != LoaderArtifactKind.ProcessorOutput
        || artifact.VerificationLevel == GameFileVerificationLevel.HashVerified;
}

/// <summary>
/// Persists the complete client-side closure declared by a Forge-like installer.
/// File names and coordinates are taken exclusively from the installer plan.
/// </summary>
internal static class LoaderArtifactManifestStore
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "loader-artifact-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string GetPath(string versionDirectory) =>
        Path.Combine(Path.GetFullPath(versionDirectory), "BHL", FileName);

    public static async Task WriteAsync(
        string versionDirectory,
        string minecraftDirectory,
        GameFileLoaderIdentity identity,
        string installerJarPath,
        ForgeInstallerPlan plan,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var artifacts = new Dictionary<string, LoaderArtifactManifestEntry>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var library in plan.PrerequisiteLibraries)
        {
            await AddLibraryAsync(
                artifacts,
                minecraftDirectory,
                library,
                LoaderArtifactKind.InstallerPrerequisite,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var library in plan.RuntimeLibraries)
        {
            await AddLibraryAsync(
                artifacts,
                minecraftDirectory,
                library,
                LoaderArtifactKind.RuntimeLibrary,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var output in plan.ProcessorOutputs)
        {
            var relativePath = NormalizeLibraryRelativePath($"libraries/{output.RelativePath}");
            var fullPath = ResolveManagedPath(minecraftDirectory, relativePath);
            var entry = await CreateEntryAsync(
                relativePath,
                LoaderArtifactKind.ProcessorOutput,
                source: null,
                output.TrustedSha1,
                expectedSize: null,
                fullPath,
                cancellationToken).ConfigureAwait(false);
            AddOrMerge(artifacts, entry);
        }

        if (artifacts.Count == 0)
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader installer did not declare any client artifacts."), "DownloadValidation", ("Reason", "Loader installer did not declare any client artifacts."));

        var installerSha256 = await ComputeHashAsync(installerJarPath, HashAlgorithmName.SHA256, cancellationToken)
            .ConfigureAwait(false);
        var manifest = new LoaderArtifactManifest(
            CurrentSchemaVersion,
            identity.LoaderKind,
            identity.MinecraftVersion.Trim(),
            identity.LoaderVersion?.Trim(),
            installerSha256,
            artifacts.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
        await AtomicJsonFileWriter.WriteAsync(GetPath(versionDirectory), manifest, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<LoaderArtifactManifestReadResult> ReadAsync(
        string versionDirectory,
        GameFileLoaderIdentity identity,
        CancellationToken cancellationToken)
    {
        var path = GetPath(versionDirectory);
        if (!File.Exists(path))
            return new LoaderArtifactManifestReadResult(null, "Loader artifact manifest is missing.");

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<LoaderArtifactManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            Validate(manifest, identity);
            return new LoaderArtifactManifestReadResult(manifest, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return new LoaderArtifactManifestReadResult(null, exception.Message);
        }
    }

    public static string ResolveManagedPath(string minecraftDirectory, string relativePath)
    {
        var normalized = NormalizeLibraryRelativePath(relativePath);
        var root = Path.GetFullPath(minecraftDirectory);
        return MinecraftPathGuard.EnsureWithin(
            Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)),
            root,
            "Loader managed artifact");
    }

    private static async Task AddLibraryAsync(
        IDictionary<string, LoaderArtifactManifestEntry> artifacts,
        string minecraftDirectory,
        ForgeInstallerLibrary library,
        LoaderArtifactKind kind,
        CancellationToken cancellationToken)
    {
        var relativePath = NormalizeLibraryRelativePath($"libraries/{library.Artifact.RelativePath}");
        var fullPath = ResolveManagedPath(minecraftDirectory, relativePath);
        var entry = await CreateEntryAsync(
            relativePath,
            kind,
            library.Artifact.Url,
            library.Artifact.Sha1,
            library.Artifact.Size,
            fullPath,
            cancellationToken).ConfigureAwait(false);
        AddOrMerge(artifacts, entry);
    }

    private static async Task<LoaderArtifactManifestEntry> CreateEntryAsync(
        string relativePath,
        LoaderArtifactKind kind,
        string? source,
        string? authoritativeSha1,
        long? expectedSize,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
            throw new InvalidDataException($"Loader installer artifact is missing after installation: {relativePath}");
        var file = new FileInfo(fullPath);
        if (expectedSize is not null && file.Length != expectedSize.Value)
            throw new InvalidDataException($"Loader installer artifact size does not match: {relativePath}");

        var actualSha1 = await ComputeHashAsync(fullPath, HashAlgorithmName.SHA1, cancellationToken).ConfigureAwait(false);
        var actualSha256 = await ComputeHashAsync(fullPath, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false);
        if (MinecraftFileIntegrity.IsSha1(authoritativeSha1)
            && !string.Equals(actualSha1, authoritativeSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Loader installer artifact hash does not match: {relativePath}");
        }

        return new LoaderArtifactManifestEntry(
            relativePath,
            kind,
            string.IsNullOrWhiteSpace(source) ? null : source,
            actualSha1,
            actualSha256,
            file.Length,
            LoaderArtifactVerificationPolicy.ResolveManifestLevel(kind, authoritativeSha1));
    }

    private static void AddOrMerge(
        IDictionary<string, LoaderArtifactManifestEntry> artifacts,
        LoaderArtifactManifestEntry candidate)
    {
        if (!artifacts.TryGetValue(candidate.RelativePath, out var existing))
        {
            artifacts[candidate.RelativePath] = candidate;
            return;
        }

        if (!string.Equals(existing.Sha1, candidate.Sha1, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase)
            || existing.Size != candidate.Size)
        {
            throw new InvalidDataException(
                $"Loader installer declared conflicting artifacts for {candidate.RelativePath}.");
        }

        if (candidate.Kind == LoaderArtifactKind.RuntimeLibrary
            || existing.Kind == LoaderArtifactKind.InstallerPrerequisite && candidate.Kind == LoaderArtifactKind.ProcessorOutput)
        {
            artifacts[candidate.RelativePath] = candidate;
        }
    }

    private static void Validate(LoaderArtifactManifest? manifest, GameFileLoaderIdentity identity)
    {
        ValidateIdentity(identity);
        if (manifest is null)
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact manifest is empty."), "DownloadValidation", ("Reason", "Loader artifact manifest is empty."));
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact manifest schema is unsupported."), "DownloadValidation", ("Reason", "Loader artifact manifest schema is unsupported."));
        if (manifest.LoaderKind != identity.LoaderKind
            || !string.Equals(manifest.MinecraftVersion, identity.MinecraftVersion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.LoaderVersion ?? string.Empty, identity.LoaderVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact manifest identity does not match the instance."), "DownloadValidation", ("Reason", "Loader artifact manifest identity does not match the instance."));
        }
        if (manifest.InstallerSha256.Length != 64 || !manifest.InstallerSha256.All(Uri.IsHexDigit))
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact manifest source fingerprint is invalid."), "DownloadValidation", ("Reason", "Loader artifact manifest source fingerprint is invalid."));
        if (manifest.Artifacts.Count == 0)
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact manifest contains no artifacts."), "DownloadValidation", ("Reason", "Loader artifact manifest contains no artifacts."));

        var paths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            var normalized = NormalizeLibraryRelativePath(artifact.RelativePath);
            if (!string.Equals(normalized, artifact.RelativePath, StringComparison.Ordinal)
                || !paths.Add(normalized))
                throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact manifest contains duplicate or non-normalized paths."), "DownloadValidation", ("Reason", "Loader artifact manifest contains duplicate or non-normalized paths."));
            if (!MinecraftFileIntegrity.IsSha1(artifact.Sha1)
                || artifact.Sha256.Length != 64
                || !artifact.Sha256.All(Uri.IsHexDigit)
                || artifact.Size < 0)
                throw new InvalidDataException($"Loader artifact manifest has invalid verification data: {normalized}");
        }
    }

    private static void ValidateIdentity(GameFileLoaderIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.MinecraftVersion))
            throw SafeExceptionLog.Describe(new InvalidDataException("Minecraft version is missing from loader identity."), "DownloadValidation", ("Reason", "Minecraft version is missing from loader identity."));
        if (identity.LoaderKind is LoaderKind.Forge or LoaderKind.NeoForge
            && string.IsNullOrWhiteSpace(identity.LoaderVersion))
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader version is missing from Forge-like loader identity."), "DownloadValidation", ("Reason", "Loader version is missing from Forge-like loader identity."));
    }

    private static string NormalizeLibraryRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw SafeExceptionLog.Describe(new InvalidDataException("Loader artifact path must be relative."), "DownloadValidation", ("Reason", "Loader artifact path must be relative."));
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Loader artifact path is unsafe: {relativePath}");
        }
        return normalized;
    }

    private static async Task<string> ComputeHashAsync(
        string path,
        HashAlgorithmName algorithm,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = algorithm == HashAlgorithmName.SHA1
            ? await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)
            : await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
