/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Launcher.Infrastructure.Minecraft;

internal static class LoaderInstallerFallbackValidator
{
    private const long MaximumProfileBytes = 4 * 1024 * 1024;

    public static async Task ValidateAsync(string path, string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var profile = archive.GetEntry("install_profile.json")
                ?? throw new InvalidDataException("Installer profile is missing.");
            if (profile.Length is <= 0 or > MaximumProfileBytes)
                throw new InvalidDataException("Installer profile size is invalid.");

            await using var profileStream = profile.Open();
            using var document = await JsonDocument.ParseAsync(profileStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Installer profile is not an object.");

            // Both modern processor profiles and legacy Forge profiles are supported.
            var hasModernProfile = root.TryGetProperty("spec", out var spec)
                && spec.ValueKind == JsonValueKind.Number
                && root.TryGetProperty("processors", out var processors)
                && processors.ValueKind == JsonValueKind.Array;
            var hasLegacyProfile = root.TryGetProperty("install", out var install)
                && install.ValueKind == JsonValueKind.Object && root.TryGetProperty("versionInfo", out _);
            if (!hasModernProfile && !hasLegacyProfile)
                throw new InvalidDataException("Installer profile format is unsupported.");

            var versionContainer = hasLegacyProfile ? install : root;
            if (!versionContainer.TryGetProperty("minecraft", out var version)
                || version.ValueKind != JsonValueKind.String || version.GetString() != minecraftVersion)
                throw new InvalidDataException("Installer Minecraft version does not match the requested version.");
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new DownloadContentValidationException("Loader installer archive or profile is invalid.", exception);
        }
    }
}
