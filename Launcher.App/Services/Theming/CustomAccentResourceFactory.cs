/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Launcher.App.Services;

internal static class CustomAccentResourceFactory
{
    public static ResourceDictionary Create(string colorCode)
    {
        var primary = Color.FromRgb(
            byte.Parse(colorCode.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(colorCode.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(colorCode.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var hover = Scale(primary, 0.92);
        var pressed = Scale(primary, 0.84);
        var selection = Color.FromArgb(0x66, primary.R, primary.G, primary.B);
        var outline = Color.FromArgb(0x88, primary.R, primary.G, primary.B);
        var resources = new ResourceDictionary();
        AddColor(resources, "Color.036FC2", pressed);
        AddColor(resources, "Color.047EDC", hover);
        AddColor(resources, "Color.0891FE", primary);
        AddColor(resources, "Color.660891FE", selection);
        AddColor(resources, "Color.667ABEFF", selection);
        AddColor(resources, "Color.887ABEFF", outline);
        AddColor(resources, "Color.Accent.Primary", primary);
        AddColor(resources, "Color.Input.TextBox.BorderFocused", selection);
        AddColor(resources, "Color.List.Item.SelectedBorder", outline);
        AddBrush(resources, "Brush.036FC2", pressed);
        AddBrush(resources, "Brush.047EDC", hover);
        AddBrush(resources, "Brush.0891FE", primary);
        AddBrush(resources, "Brush.660891FE", selection);
        AddBrush(resources, "Brush.667ABEFF", selection);
        AddBrush(resources, "Brush.887ABEFF", outline);
        AddBrush(resources, "LauncherAccentBrush", primary);
        AddBrush(resources, "Brush.Accent.Primary", primary);
        AddBrush(resources, "Brush.Accent.Hover", hover);
        AddBrush(resources, "Brush.Accent.Pressed", pressed);
        AddBrush(resources, "Brush.Accent.Selection", selection);
        AddBrush(resources, "Brush.Accent.Border", selection);
        AddBrush(resources, "Brush.Accent.Outline", outline);
        AddBrush(resources, "Brush.Input.TextBox.BorderFocused", selection);
        AddBrush(resources, "Brush.List.Item.SelectedBorder", outline);
        return resources;
    }

    private static Color Scale(Color color, double factor) => Color.FromRgb(
        (byte)Math.Round(color.R * factor),
        (byte)Math.Round(color.G * factor),
        (byte)Math.Round(color.B * factor));

    private static void AddColor(ResourceDictionary resources, string key, Color color) => resources[key] = color;

    private static void AddBrush(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
