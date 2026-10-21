/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.App.Resources;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Resources;

internal static class ResourceProjectCategoryTitleFormatter
{
    public static IReadOnlyList<string> Format(
        ResourceProjectKind kind,
        IEnumerable<ResourceProjectCategory> categories) => categories
        .Distinct()
        .Select(category => Resolve(kind, category))
        .Where(title => !string.IsNullOrWhiteSpace(title))
        .Select(title => title!)
        .ToArray();

    private static string? Resolve(ResourceProjectKind kind, ResourceProjectCategory category)
    {
        if (kind is ResourceProjectKind.DataPack)
            return Resolve(ResourceProjectKind.Mod, category);

        return (kind, category) switch
        {
            (ResourceProjectKind.Mod, ResourceProjectCategory.Optimization) => Strings.Resources_ModFilterTypeOptimization,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Utility) => Strings.Resources_ModFilterTypeUtility,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Adventure) => Strings.Resources_ModFilterTypeAdventure,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Decoration) => Strings.Resources_ModFilterTypeDecoration,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Equipment) => Strings.Resources_ModFilterTypeEquipment,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Technology) => Strings.Resources_ModFilterTypeTechnology,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Magic) => Strings.Resources_ModFilterTypeMagic,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Mobs) => Strings.Resources_ModFilterTypeMobs,
            (ResourceProjectKind.Mod, ResourceProjectCategory.WorldGeneration) => Strings.Resources_ModFilterTypeWorldGeneration,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Storage) => Strings.Resources_ModFilterTypeStorage,
            (ResourceProjectKind.Mod, ResourceProjectCategory.Library) => Strings.Resources_ModFilterTypeLibrary,
            (ResourceProjectKind.ResourcePack, ResourceProjectCategory.Simplistic) => Strings.Resources_ResourcePackFilterTypeSimplistic,
            (ResourceProjectKind.ResourcePack, ResourceProjectCategory.Themed) => Strings.Resources_ResourcePackFilterTypeThemed,
            (ResourceProjectKind.ResourcePack, ResourceProjectCategory.Realistic) => Strings.Resources_ResourcePackFilterTypeRealistic,
            (ResourceProjectKind.ResourcePack, ResourceProjectCategory.VanillaLike) => Strings.Resources_ResourcePackFilterTypeVanillaLike,
            (ResourceProjectKind.ResourcePack, ResourceProjectCategory.Audio) => Strings.Resources_ResourcePackFilterTypeAudio,
            (ResourceProjectKind.ShaderPack, ResourceProjectCategory.Cartoon) => Strings.Resources_ShaderPackFilterTypeCartoon,
            (ResourceProjectKind.ShaderPack, ResourceProjectCategory.Cursed) => Strings.Resources_ShaderPackFilterTypeCursed,
            (ResourceProjectKind.ShaderPack, ResourceProjectCategory.Fantasy) => Strings.Resources_ShaderPackFilterTypeFantasy,
            (ResourceProjectKind.ShaderPack, ResourceProjectCategory.Realistic) => Strings.Resources_ShaderPackFilterTypeRealistic,
            (ResourceProjectKind.ShaderPack, ResourceProjectCategory.SemiRealistic) => Strings.Resources_ShaderPackFilterTypeSemiRealistic,
            (ResourceProjectKind.ShaderPack, ResourceProjectCategory.VanillaLike) => Strings.Resources_ShaderPackFilterTypeVanillaLike,
            _ => null
        };
    }
}
