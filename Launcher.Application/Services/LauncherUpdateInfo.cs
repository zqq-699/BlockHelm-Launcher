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

namespace Launcher.Application.Services;

public sealed record LauncherUpdateDownloadUrl(
    string Name,
    string Url,
    int Priority);

public sealed record LauncherUpdateReleaseNotesSection(
    string? Title,
    IReadOnlyList<string> Items);

public sealed record LauncherUpdateInfo(
    string Version,
    string DisplayVersion,
    string ReleasePageUrl,
    string? DownloadUrl,
    string? Changelog,
    string? DownloadFileName,
    LauncherUpdateAssetKind AssetKind,
    long SizeBytes,
    string Sha256,
    int VersionCode = 0,
    bool IsMandatory = false,
    int MinSupportedVersionCode = 0,
    DateTimeOffset? PublishedAt = null,
    IReadOnlyList<LauncherUpdateDownloadUrl>? DownloadUrls = null,
    IReadOnlyList<LauncherUpdateReleaseNotesSection>? ReleaseNotesSections = null)
{
    public bool CanAutoInstall => AssetKind is LauncherUpdateAssetKind.WindowsX64Executable
        && SizeBytes > 0
        && Sha256.Length == 64
        && Sha256.All(Uri.IsHexDigit)
        && EffectiveDownloadUrls.Count > 0;

    public IReadOnlyList<LauncherUpdateDownloadUrl> EffectiveDownloadUrls =>
        DownloadUrls is { Count: > 0 }
            ? DownloadUrls
            : string.IsNullOrWhiteSpace(DownloadUrl)
                ? []
                : [new LauncherUpdateDownloadUrl("default", DownloadUrl, 1)];

    public IReadOnlyList<LauncherUpdateReleaseNotesSection> EffectiveReleaseNotesSections =>
        ReleaseNotesSections is { Count: > 0 } ? ReleaseNotesSections : [];
}
