/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Launcher.Infrastructure.Minecraft;

using Launcher.Application.Services;

namespace Launcher.Infrastructure.Modpacks;

internal static partial class FabricServerLauncherJarBuilder
{
    private const long MaximumEntryBytes = 256L * 1024 * 1024;
    private const long MaximumTotalBytes = 1024L * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLinkType = 0xA000;
    private static readonly Version ClassPathCompatibleVersion = new(0, 12, 5);

    public static bool ShouldShadeLibraries(string? loaderVersion)
    {
        var match = LeadingVersionRegex().Match(loaderVersion?.Trim() ?? string.Empty);
        return !match.Success
            || !Version.TryParse(match.Value, out var parsed)
            || parsed.CompareTo(ClassPathCompatibleVersion) <= 0;
    }

    public static async Task CreateAsync(
        string destinationPath,
        string launcherMainClass,
        string? launchMainClass,
        IReadOnlyList<ManagedLibraryArtifact> artifacts,
        string librariesRoot,
        string loaderVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherMainClass);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentException.ThrowIfNullOrWhiteSpace(librariesRoot);

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw SafeExceptionLog.Describe(new InvalidDataException("Fabric server launcher destination has no parent directory."), "DownloadValidation", ("Reason", "Fabric server launcher destination has no parent directory."));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        var shadeLibraries = ShouldShadeLibraries(loaderVersion);
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                WriteLauncherMetadata(
                    archive,
                    launcherMainClass,
                    launchMainClass,
                    shadeLibraries
                        ? []
                        : artifacts.Select(artifact => $"libraries/{artifact.RelativePath.Replace('\\', '/')}").ToArray());

                if (shadeLibraries)
                {
                    await ShadeLibrariesAsync(
                        archive,
                        artifacts,
                        librariesRoot,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            MinecraftPathGuard.EnsureSafeFileDestination(destinationPath, destinationDirectory, "Fabric server launcher");
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void WriteLauncherMetadata(
        ZipArchive archive,
        string launcherMainClass,
        string? launchMainClass,
        IReadOnlyList<string> classPath)
    {
        var manifestEntry = archive.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false), leaveOpen: false))
        {
            writer.NewLine = "\r\n";
            writer.WriteLine("Manifest-Version: 1.0");
            WriteManifestAttribute(writer, "Main-Class", launcherMainClass);
            if (classPath.Count > 0)
                WriteManifestAttribute(writer, "Class-Path", string.Join(' ', classPath));
            writer.WriteLine();
        }

        if (!string.IsNullOrWhiteSpace(launchMainClass))
        {
            var propertiesEntry = archive.CreateEntry("fabric-server-launch.properties", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(propertiesEntry.Open(), new UTF8Encoding(false));
            writer.WriteLine($"launch.mainClass={launchMainClass}");
        }
    }

    private static async Task ShadeLibrariesAsync(
        ZipArchive destination,
        IReadOnlyList<ManagedLibraryArtifact> artifacts,
        string librariesRoot,
        CancellationToken cancellationToken)
    {
        var addedEntries = new HashSet<string>(StringComparer.Ordinal)
        {
            "META-INF/MANIFEST.MF",
            "fabric-server-launch.properties"
        };
        long totalBytes = 0;
        long copiedBytes = 0;
        var entryCount = 0;

        foreach (var artifact in artifacts
                     .DistinctBy(artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryPath = MinecraftPathGuard.EnsureWithin(
                Path.Combine(librariesRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                librariesRoot,
                "Fabric loader library");
            MinecraftPathGuard.EnsureNoReparsePoints(librariesRoot, libraryPath, "Fabric loader library");
            await using var libraryStream = await OpenVerifiedLibraryAsync(
                libraryPath,
                artifact,
                cancellationToken).ConfigureAwait(false);
            MinecraftPathGuard.EnsureNoReparsePoints(librariesRoot, libraryPath, "Fabric loader library");
            using var library = new ZipArchive(libraryStream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in library.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEntry(entry);
                if (++entryCount > MaximumEntryCount)
                    throw SafeExceptionLog.Describe(new InvalidDataException("Fabric loader libraries contain too many archive entries."), "DownloadValidation", ("Reason", "Fabric loader libraries contain too many archive entries."));
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;
                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > MaximumTotalBytes)
                    throw SafeExceptionLog.Describe(new InvalidDataException("Fabric loader libraries exceed the permitted expanded size."), "DownloadValidation", ("Reason", "Fabric loader libraries exceed the permitted expanded size."));

                var entryName = entry.FullName;
                if (IsDiscardedMetadata(entryName))
                    continue;
                // Match the class-path lookup order and ATLauncher shading behavior:
                // the first library wins when multiple JARs contain the same resource.
                if (!addedEntries.Add(entryName))
                    continue;

                var outputEntry = destination.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var source = entry.Open();
                await using var output = outputEntry.Open();
                copiedBytes += await CopyEntryAsync(
                    source,
                    output,
                    Math.Min(MaximumEntryBytes, MaximumTotalBytes - copiedBytes),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<FileStream> OpenVerifiedLibraryAsync(
        string path,
        ManagedLibraryArtifact artifact,
        CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (artifact.Size is > 0 && stream.Length != artifact.Size.Value)
                throw new InvalidDataException($"Fabric loader library has an unexpected size: {artifact.RelativePath}");
            if (MinecraftFileIntegrity.IsSha1(artifact.Sha1))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        hash.AppendData(buffer, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var actualSha1 = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualSha1, artifact.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Fabric loader library has an unexpected checksum: {artifact.RelativePath}");
                stream.Position = 0;
            }
            return stream;
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> CopyEntryAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return copied;
                copied = checked(copied + read);
                if (copied > maximumBytes)
                    throw SafeExceptionLog.Describe(new InvalidDataException("Fabric loader libraries exceed the permitted expanded size."), "DownloadValidation", ("Reason", "Fabric loader libraries exceed the permitted expanded size."));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumEntryBytes)
            throw new InvalidDataException($"Fabric loader library entry is too large: {entry.FullName}");
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixMode == UnixSymbolicLinkType)
            throw new InvalidDataException($"Fabric loader library symbolic links are not allowed: {entry.FullName}");

        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\')
            || name.StartsWith("/", StringComparison.Ordinal)
            || DrivePathRegex().IsMatch(name))
            throw new InvalidDataException($"Fabric loader library contains an unsafe entry path: {name}");
        var pathForValidation = name.EndsWith("/", StringComparison.Ordinal) ? name[..^1] : name;
        var segments = pathForValidation.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Fabric loader library contains an unsafe entry path: {name}");
    }

    private static bool IsDiscardedMetadata(string entryName) =>
        string.Equals(entryName, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)
        || string.Equals(entryName, "META-INF/INDEX.LIST", StringComparison.OrdinalIgnoreCase)
        || SignatureEntryRegex().IsMatch(entryName);

    private static void WriteManifestAttribute(TextWriter writer, string name, string value)
    {
        var line = $"{name}: {value}";
        const int maxLength = 70;
        writer.WriteLine(line[..Math.Min(maxLength, line.Length)]);
        for (var index = maxLength; index < line.Length; index += maxLength - 1)
            writer.WriteLine(" " + line.Substring(index, Math.Min(maxLength - 1, line.Length - index)));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^\d+(?:\.\d+){0,3}", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersionRegex();

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();

    [GeneratedRegex(@"^META-INF/[^/]+\.(?:SF|DSA|RSA|EC)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignatureEntryRegex();
}
