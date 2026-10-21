/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Globalization;

namespace Launcher.Tests.Helpers;

internal sealed class TestUiCultureScope : IDisposable
{
    private readonly CultureInfo previousCulture = CultureInfo.CurrentUICulture;

    public TestUiCultureScope(string cultureName) =>
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

    public void Dispose() => CultureInfo.CurrentUICulture = previousCulture;
}
