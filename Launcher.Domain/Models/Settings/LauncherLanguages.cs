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

using System.Globalization;

namespace Launcher.Domain.Models;

public static class LauncherLanguages
{
    public const string SimplifiedChinese = "zh-Hans";
    public const string TraditionalChinese = "zh-Hant";
    public const string Japanese = "ja-JP";
    public const string English = "en";

    public static bool IsChineseCulture(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? language)
    {
        if (string.Equals(language, English, StringComparison.OrdinalIgnoreCase))
            return English;

        if (string.Equals(language, TraditionalChinese, StringComparison.OrdinalIgnoreCase))
            return TraditionalChinese;

        if (string.Equals(language, Japanese, StringComparison.OrdinalIgnoreCase))
            return Japanese;

        if (string.Equals(language, SimplifiedChinese, StringComparison.OrdinalIgnoreCase))
            return SimplifiedChinese;

        return LauncherDefaults.DefaultLauncherLanguage;
    }
}
