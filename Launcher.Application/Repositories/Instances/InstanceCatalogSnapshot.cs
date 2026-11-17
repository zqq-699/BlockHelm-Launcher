/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Repositories;

public sealed record InstanceCatalogSnapshot(
    IReadOnlyList<GameInstance> Instances,
    IReadOnlyList<InstalledGameVersion> InstalledVersions,
    string? DefaultInstanceDirectory);
