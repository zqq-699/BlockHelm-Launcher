/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.GameSettings;

internal readonly record struct InstanceCatalogEntrySnapshot(
    string Name,
    string MinecraftVersion,
    string VersionName,
    string VersionType,
    LoaderKind Loader,
    string? LoaderVersion,
    string? IconSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string InstanceDirectory)
{
    public static InstanceCatalogEntrySnapshot Create(GameInstance instance)
    {
        return new InstanceCatalogEntrySnapshot(
            instance.Name,
            instance.MinecraftVersion,
            instance.VersionName,
            instance.VersionType,
            instance.Loader,
            instance.LoaderVersion,
            instance.IconSource,
            instance.CreatedAt,
            instance.UpdatedAt,
            instance.InstanceDirectory);
    }
}
