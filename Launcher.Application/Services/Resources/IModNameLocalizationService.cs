/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

/// <summary>
/// 根据资源平台 slug 查找随启动器提供的中文名称和 MC 百科编号。
/// </summary>
public interface IModNameLocalizationService
{
    string? FindByProjectSlug(ResourceProjectSource source, string? slug, string? originalTitle);

    int? FindMcmodIdByProjectSlug(ResourceProjectSource source, string? slug);

    string? FindSearchTerm(ResourceProjectSource source, string query);
}
