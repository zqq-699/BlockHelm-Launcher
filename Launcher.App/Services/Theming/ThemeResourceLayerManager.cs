/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using Launcher.Domain.Models;

namespace Launcher.App.Services;

internal sealed class ThemeResourceLayerManager
{
    internal const string PageBackgroundOpacityResourceKey = "Opacity.Page.Background";

    private const string DarkThemeSource =
        "pack://application:,,,/BlockHelm_Launcher_x64;component/Resources/Themes/Dark.xaml";

    private const string LightThemeSource =
        "pack://application:,,,/BlockHelm_Launcher_x64;component/Resources/Themes/Light.xaml";

    private const string AccentThemeSourcePrefix =
        "pack://application:,,,/BlockHelm_Launcher_x64;component/Resources/Themes/Accents/";

    private const string ImageBackgroundSource =
        "pack://application:,,,/BlockHelm_Launcher_x64;component/Resources/Themes/Backgrounds/Image.xaml";

    private const string ImageBlurSource =
        "pack://application:,,,/BlockHelm_Launcher_x64;component/Resources/Themes/Backgrounds/ImageBlur.xaml";

    private readonly Func<string, ResourceDictionary> dictionaryFactory;
    private ResourceDictionary? themeLayer;
    private string? themeLayerSource;
    private ResourceDictionary? accentLayer;
    private string? accentLayerSource;
    private ResourceDictionary? imageLayer;
    private ResourceDictionary? imageBlurLayer;

    public ThemeResourceLayerManager()
        : this(source => new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) })
    {
    }

    internal ThemeResourceLayerManager(Func<string, ResourceDictionary> dictionaryFactory)
    {
        this.dictionaryFactory = dictionaryFactory ?? throw new ArgumentNullException(nameof(dictionaryFactory));
    }

    public void ApplyLayers(
        ResourceDictionary applicationResources,
        EffectiveTheme theme,
        string accentColor,
        bool imageBackgroundEnabled,
        bool imageControlBlurEnabled)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        var dictionaries = applicationResources.MergedDictionaries;
        var nextThemeSource = GetThemeSource(theme);
        var nextAccentLayer = GetAccentLayer(accentColor);
        var accentIndex = accentLayer is null ? -1 : dictionaries.IndexOf(accentLayer);
        if (themeLayer is not null
            && string.Equals(themeLayerSource, nextThemeSource, StringComparison.Ordinal)
            && dictionaries.Contains(themeLayer)
            && accentIndex >= 0
            && (imageLayer is not null && dictionaries.Contains(imageLayer)) == imageBackgroundEnabled
            && (imageBlurLayer is not null && dictionaries.Contains(imageBlurLayer)) == (imageBackgroundEnabled && imageControlBlurEnabled))
        {
            if (!ReferenceEquals(accentLayer, nextAccentLayer))
                dictionaries[accentIndex] = nextAccentLayer;
            accentLayer = nextAccentLayer;
            return;
        }

        RemoveLayer(dictionaries, themeLayer);
        RemoveLayer(dictionaries, accentLayer);
        RemoveLayer(dictionaries, imageLayer);
        RemoveLayer(dictionaries, imageBlurLayer);

        themeLayer = GetOrCreateLayer(themeLayer, ref themeLayerSource, nextThemeSource);
        accentLayer = nextAccentLayer;
        dictionaries.Add(themeLayer);
        dictionaries.Add(accentLayer);
        if (imageBackgroundEnabled)
        {
            imageLayer ??= dictionaryFactory(ImageBackgroundSource);
            dictionaries.Add(imageLayer);
            if (imageControlBlurEnabled)
            {
                imageBlurLayer ??= dictionaryFactory(ImageBlurSource);
                dictionaries.Add(imageBlurLayer);
            }
        }
    }

    public static void ApplyPageBackgroundOpacity(
        ResourceDictionary applicationResources,
        int opacityPercent)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);
        applicationResources[PageBackgroundOpacityResourceKey] = Math.Clamp(opacityPercent, 0, 100) / 100d;
    }

    internal static string GetThemeSource(EffectiveTheme theme) =>
        theme is EffectiveTheme.Light ? LightThemeSource : DarkThemeSource;

    internal static string GetAccentThemeSource(string accentColor) =>
        $"{AccentThemeSourcePrefix}{LauncherAccentColors.Normalize(accentColor)}.xaml";

    private ResourceDictionary GetAccentLayer(string accentColor)
    {
        if (!LauncherAccentColors.TryNormalizeCustom(accentColor, out var customColor))
            return GetOrCreateLayer(accentLayer, ref accentLayerSource, GetAccentThemeSource(accentColor));

        if (accentLayer is not null && string.Equals(accentLayerSource, customColor, StringComparison.Ordinal))
            return accentLayer;

        accentLayerSource = customColor;
        return CustomAccentResourceFactory.Create(customColor);
    }

    private ResourceDictionary GetOrCreateLayer(
        ResourceDictionary? currentLayer,
        ref string? currentSource,
        string nextSource)
    {
        if (currentLayer is not null
            && string.Equals(currentSource, nextSource, StringComparison.OrdinalIgnoreCase))
            return currentLayer;

        currentSource = nextSource;
        return dictionaryFactory(nextSource);
    }

    private static void RemoveLayer(
        ICollection<ResourceDictionary> dictionaries,
        ResourceDictionary? layer)
    {
        if (layer is null)
            return;

        while (dictionaries.Remove(layer))
        {
        }
    }
}
