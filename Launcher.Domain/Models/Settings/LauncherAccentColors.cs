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

namespace Launcher.Domain.Models;

public static class LauncherAccentColors
{
    public const string Blue = "Blue";
    public const string Cyan = "Cyan";
    public const string Green = "Green";
    public const string Emerald = "Emerald";
    public const string Purple = "Purple";
    public const string Pink = "Pink";
    public const string Orange = "Orange";
    public const string Amber = "Amber";

    public static IReadOnlyList<string> All { get; } =
    [
        Blue,
        Cyan,
        Green,
        Emerald,
        Purple,
        Pink,
        Orange,
        Amber
    ];

    public static string Normalize(string? accentColor)
    {
        if (TryNormalizeCustom(accentColor, out var customColor))
            return customColor;

        foreach (var knownAccentColor in All)
        {
            if (string.Equals(knownAccentColor, accentColor, StringComparison.OrdinalIgnoreCase))
                return knownAccentColor;
        }

        return Blue;
    }

    public static bool TryNormalizeCustom(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 7 || value[0] != '#')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                return false;
        }

        normalized = value.ToUpperInvariant();
        return true;
    }
}
