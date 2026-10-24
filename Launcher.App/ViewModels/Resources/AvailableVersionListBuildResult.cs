/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Resources;

internal sealed record AvailableVersionListBuildResult(
    IReadOnlyList<object> Items,
    int VisibleVersionCount,
    IReadOnlyDictionary<string, IReadOnlyList<ResourceProjectVersion>> GroupVersions);
