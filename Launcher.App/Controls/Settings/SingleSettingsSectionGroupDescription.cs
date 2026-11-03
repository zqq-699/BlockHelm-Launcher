/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.Globalization;

namespace Launcher.App.Controls;

/// <summary>Provides a permanent group, including when the list is empty.</summary>
public sealed class SingleSettingsSectionGroupDescription : GroupDescription
{
    private const string GroupName = "SettingsSection";

    public SingleSettingsSectionGroupDescription() => GroupNames.Add(GroupName);

    public override object GroupNameFromItem(object item, int level, CultureInfo culture) => GroupName;
}
