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

using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Resources;

internal static class ResourceProjectCategoryMapping
{
    public static IReadOnlyList<ResourceProjectCategory> MapModrinth(
        ResourceProjectKind kind,
        IEnumerable<string?> values) => MapDistinct(
        kind,
        values,
        (category, value) => string.Equals(
            Normalize(GetModrinthId(category)),
            Normalize(value),
            StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ResourceProjectCategory> MapCurseForge(
        ResourceProjectKind kind,
        IEnumerable<string?> values) => MapDistinct(
        kind,
        values,
        (category, value) => MatchesCurseForge(category, value));

    public static string GetModrinthId(ResourceProjectCategory category) => category switch
    {
        ResourceProjectCategory.WorldGeneration => "worldgen",
        ResourceProjectCategory.VanillaLike => "vanilla-like",
        ResourceProjectCategory.SemiRealistic => "semi-realistic",
        ResourceProjectCategory.GameMap => "game-map",
        ResourceProjectCategory.KitchenSink => "kitchen-sink",
        _ => category.ToString().ToLowerInvariant()
    };

    public static bool MatchesCurseForge(ResourceProjectCategory category, params string?[] values)
    {
        var aliases = GetCurseForgeAliases(category)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => new[]
            {
                value!,
                value!.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value
            })
            .Select(Normalize)
            .Any(aliases.Contains);
    }

    private static IReadOnlyList<ResourceProjectCategory> MapDistinct(
        ResourceProjectKind kind,
        IEnumerable<string?> values,
        Func<ResourceProjectCategory, string, bool> matches)
    {
        var supported = GetSupportedCategories(kind);
        var result = new List<ResourceProjectCategory>();
        var seen = new HashSet<ResourceProjectCategory>();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()))
        {
            foreach (var category in supported)
            {
                if (!seen.Contains(category) && matches(category, value))
                {
                    seen.Add(category);
                    result.Add(category);
                    break;
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<ResourceProjectCategory> GetSupportedCategories(ResourceProjectKind kind) => kind switch
    {
        ResourceProjectKind.Mod =>
        [
            ResourceProjectCategory.Optimization,
            ResourceProjectCategory.Utility,
            ResourceProjectCategory.Adventure,
            ResourceProjectCategory.Decoration,
            ResourceProjectCategory.Equipment,
            ResourceProjectCategory.Technology,
            ResourceProjectCategory.Magic,
            ResourceProjectCategory.Mobs,
            ResourceProjectCategory.WorldGeneration,
            ResourceProjectCategory.Storage,
            ResourceProjectCategory.Library
        ],
        ResourceProjectKind.ResourcePack =>
        [
            ResourceProjectCategory.Simplistic,
            ResourceProjectCategory.Themed,
            ResourceProjectCategory.Realistic,
            ResourceProjectCategory.VanillaLike,
            ResourceProjectCategory.Audio
        ],
        ResourceProjectKind.DataPack =>
        [
            ResourceProjectCategory.Optimization,
            ResourceProjectCategory.Utility,
            ResourceProjectCategory.Adventure,
            ResourceProjectCategory.Decoration,
            ResourceProjectCategory.Equipment,
            ResourceProjectCategory.Technology,
            ResourceProjectCategory.Magic,
            ResourceProjectCategory.Mobs,
            ResourceProjectCategory.WorldGeneration,
            ResourceProjectCategory.Storage,
            ResourceProjectCategory.Library
        ],
        ResourceProjectKind.ShaderPack =>
        [
            ResourceProjectCategory.Cartoon,
            ResourceProjectCategory.Cursed,
            ResourceProjectCategory.Fantasy,
            ResourceProjectCategory.Realistic,
            ResourceProjectCategory.SemiRealistic,
            ResourceProjectCategory.VanillaLike
        ],
        ResourceProjectKind.World =>
        [
            ResourceProjectCategory.Adventure,
            ResourceProjectCategory.Creation,
            ResourceProjectCategory.GameMap,
            ResourceProjectCategory.Parkour,
            ResourceProjectCategory.Puzzle,
            ResourceProjectCategory.Survival
        ],
        ResourceProjectKind.Modpack =>
        [
            ResourceProjectCategory.Adventure,
            ResourceProjectCategory.Technology,
            ResourceProjectCategory.Magic,
            ResourceProjectCategory.Optimization,
            ResourceProjectCategory.Quests,
            ResourceProjectCategory.KitchenSink,
            ResourceProjectCategory.Lightweight,
            ResourceProjectCategory.Multiplayer,
            ResourceProjectCategory.Exploration
        ],
        _ => []
    };

    private static IReadOnlyList<string> GetCurseForgeAliases(ResourceProjectCategory category) => category switch
    {
        ResourceProjectCategory.Optimization => ["optimization", "performance"],
        ResourceProjectCategory.Utility => ["utility", "utilities", "qol", "quality of life", "miscellaneous"],
        ResourceProjectCategory.Adventure => ["adventure", "rpg", "adventure rpg", "adventure and rpg"],
        ResourceProjectCategory.Decoration => ["decoration", "decorative", "cosmetic"],
        ResourceProjectCategory.Equipment => ["equipment", "armor weapons tools", "armor", "weapons", "tools"],
        ResourceProjectCategory.Technology => ["technology", "tech"],
        ResourceProjectCategory.Magic => ["magic"],
        ResourceProjectCategory.Mobs => ["mobs", "creatures"],
        ResourceProjectCategory.WorldGeneration => ["worldgen", "world gen", "world generation", "biomes", "dimensions"],
        ResourceProjectCategory.Storage => ["storage"],
        ResourceProjectCategory.Library => ["library", "api", "library api", "api and library"],
        ResourceProjectCategory.Simplistic => ["simplistic", "simple", "16x"],
        ResourceProjectCategory.Themed => ["themed", "theme", "medieval", "modern", "fantasy"],
        ResourceProjectCategory.Realistic => ["realistic", "realism", "photorealistic", "128x", "256x", "512x"],
        ResourceProjectCategory.VanillaLike => ["vanilla like", "vanilla-like", "vanilla"],
        ResourceProjectCategory.Audio => ["audio", "sound", "music"],
        ResourceProjectCategory.Cartoon => ["cartoon"],
        ResourceProjectCategory.Cursed => ["cursed"],
        ResourceProjectCategory.Fantasy => ["fantasy"],
        ResourceProjectCategory.SemiRealistic => ["semi realistic", "semi-realistic", "semirealistic"],
        ResourceProjectCategory.Creation => ["creation", "creations", "building", "buildings", "creative"],
        ResourceProjectCategory.GameMap => ["game map", "game maps", "minigame", "mini game", "mini games"],
        ResourceProjectCategory.Parkour => ["parkour"],
        ResourceProjectCategory.Puzzle => ["puzzle", "puzzles"],
        ResourceProjectCategory.Survival => ["survival"],
        ResourceProjectCategory.Quests => ["quests", "questing", "quest"],
        ResourceProjectCategory.KitchenSink => ["kitchen sink", "kitchen-sink", "kitchensink"],
        ResourceProjectCategory.Lightweight => ["lightweight", "small", "light"],
        ResourceProjectCategory.Multiplayer => ["multiplayer", "server", "servers"],
        ResourceProjectCategory.Exploration => ["exploration", "explore"],
        _ => []
    };

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
