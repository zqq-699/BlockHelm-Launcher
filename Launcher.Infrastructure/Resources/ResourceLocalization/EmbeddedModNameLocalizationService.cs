/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Resources.ResourceLocalization;

/// <summary>
/// 从随程序发布的 JSONL 数据中按平台 slug 查找中文名。
/// </summary>
public sealed class EmbeddedModNameLocalizationService : IModNameLocalizationService
{
    private const string EmbeddedResourceName =
        "Launcher.Infrastructure.Resources.ResourceLocalization.mods.v1.jsonl";

    private readonly ILogger<EmbeddedModNameLocalizationService> logger;
    private readonly Lazy<LocalizationIndexes> indexes;

    public EmbeddedModNameLocalizationService(ILogger<EmbeddedModNameLocalizationService>? logger = null)
    {
        this.logger = logger ?? NullLogger<EmbeddedModNameLocalizationService>.Instance;
        indexes = new Lazy<LocalizationIndexes>(LoadIndexes, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string? FindByProjectSlug(ResourceProjectSource source, string? slug, string? originalTitle)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var titlesBySlug = source switch
        {
            ResourceProjectSource.Modrinth => indexes.Value.ModrinthTitlesBySlug,
            ResourceProjectSource.CurseForge => indexes.Value.CurseForgeTitlesBySlug,
            _ => null
        };
        return titlesBySlug is not null && titlesBySlug.TryGetValue(slug.Trim(), out var localizedTitle)
            ? NormalizeMatch(localizedTitle, originalTitle)
            : null;
    }

    public int? FindMcmodIdByProjectSlug(ResourceProjectSource source, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var idsBySlug = source switch
        {
            ResourceProjectSource.Modrinth => indexes.Value.ModrinthMcmodIdsBySlug,
            ResourceProjectSource.CurseForge => indexes.Value.CurseForgeMcmodIdsBySlug,
            _ => null
        };
        return idsBySlug is not null && idsBySlug.TryGetValue(slug.Trim(), out var id)
            ? id
            : null;
    }

    private LocalizationIndexes LoadIndexes()
    {
        var stopwatch = Stopwatch.StartNew();
        var modrinthTitles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var curseForgeTitles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var modrinthMcmodIds = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var curseForgeMcmodIds = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var invalidLineCount = 0;

        try
        {
            using var stream = typeof(EmbeddedModNameLocalizationService).Assembly
                .GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                logger.LogWarning(
                    "Embedded mod localization catalog was not found. ResourceName={ResourceName}",
                    EmbeddedResourceName);
                return LocalizationIndexes.Empty;
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ModLocalizationRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<ModLocalizationRecord>(line);
                }
                catch (JsonException)
                {
                    invalidLineCount++;
                    continue;
                }

                if (record is null)
                    continue;

                if (record.McmodId > 0)
                {
                    AddIds(modrinthMcmodIds, record.ModrinthSlugs, record.McmodId);
                    AddIds(curseForgeMcmodIds, record.CurseForgeSlugs, record.McmodId);
                }

                if (string.IsNullOrWhiteSpace(record.ChineseName))
                    continue;

                AddKeys(modrinthTitles, record.ModrinthSlugs, record.ChineseName);
                AddKeys(curseForgeTitles, record.CurseForgeSlugs, record.ChineseName);
            }

            var result = new LocalizationIndexes(
                RemoveAmbiguousEntries(modrinthTitles),
                RemoveAmbiguousEntries(curseForgeTitles),
                RemoveAmbiguousIds(modrinthMcmodIds),
                RemoveAmbiguousIds(curseForgeMcmodIds));
            if (invalidLineCount > 0)
            {
                logger.LogWarning(
                    "Mod localization catalog contained invalid JSONL records. InvalidRecordCount={InvalidRecordCount}",
                    invalidLineCount);
            }

            logger.LogInformation(
                "Mod localization catalog loaded. ModrinthSlugCount={ModrinthSlugCount} CurseForgeSlugCount={CurseForgeSlugCount} ModrinthMcmodIdCount={ModrinthMcmodIdCount} CurseForgeMcmodIdCount={CurseForgeMcmodIdCount} ElapsedMilliseconds={ElapsedMilliseconds}",
                result.ModrinthTitlesBySlug.Count,
                result.CurseForgeTitlesBySlug.Count,
                result.ModrinthMcmodIdsBySlug.Count,
                result.CurseForgeMcmodIdsBySlug.Count,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load embedded mod localization catalog.");
            return LocalizationIndexes.Empty;
        }
    }

    private static void AddKeys(
        IDictionary<string, string?> titlesByKey,
        IReadOnlyList<string>? keys,
        string chineseName)
    {
        if (keys is null)
            return;

        var normalizedTitle = chineseName.Trim();
        foreach (var value in keys)
        {
            var key = value?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!titlesByKey.TryGetValue(key, out var existingTitle))
            {
                titlesByKey[key] = normalizedTitle;
                continue;
            }

            if (existingTitle is not null
                && !string.Equals(existingTitle, normalizedTitle, StringComparison.Ordinal))
            {
                // 同一标识指向多个不同条目时宁可不显示，也不猜测错误的中文名。
                titlesByKey[key] = null;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> RemoveAmbiguousEntries(
        IReadOnlyDictionary<string, string?> titlesByKey) =>
        titlesByKey
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);

    private static void AddIds(IDictionary<string, int?> idsBySlug, IReadOnlyList<string>? slugs, int mcmodId)
    {
        if (slugs is null)
            return;

        foreach (var value in slugs)
        {
            var slug = value?.Trim();
            if (string.IsNullOrWhiteSpace(slug))
                continue;

            if (!idsBySlug.TryGetValue(slug, out var existingId))
                idsBySlug[slug] = mcmodId;
            else if (existingId != mcmodId)
                idsBySlug[slug] = null;
        }
    }

    private static IReadOnlyDictionary<string, int> RemoveAmbiguousIds(
        IReadOnlyDictionary<string, int?> idsBySlug) =>
        idsBySlug
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.Value, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeMatch(string localizedTitle, string? originalTitle)
    {
        localizedTitle = localizedTitle.Trim();
        return ContainsHanCharacter(localizedTitle)
            && !string.Equals(localizedTitle, originalTitle?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? localizedTitle
                : null;
    }

    private static bool ContainsHanCharacter(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF');

    private sealed record LocalizationIndexes(
        IReadOnlyDictionary<string, string> ModrinthTitlesBySlug,
        IReadOnlyDictionary<string, string> CurseForgeTitlesBySlug,
        IReadOnlyDictionary<string, int> ModrinthMcmodIdsBySlug,
        IReadOnlyDictionary<string, int> CurseForgeMcmodIdsBySlug)
    {
        public static LocalizationIndexes Empty { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class ModLocalizationRecord
    {
        [JsonPropertyName("mcmodId")]
        public int McmodId { get; init; }

        [JsonPropertyName("chineseName")]
        public string ChineseName { get; init; } = string.Empty;

        [JsonPropertyName("englishName")]
        public string? EnglishName { get; init; }

        [JsonPropertyName("curseForgeSlugs")]
        public IReadOnlyList<string> CurseForgeSlugs { get; init; } = [];

        [JsonPropertyName("modrinthSlugs")]
        public IReadOnlyList<string> ModrinthSlugs { get; init; } = [];

        [JsonPropertyName("modIds")]
        public IReadOnlyList<string> ModIds { get; init; } = [];
    }
}
