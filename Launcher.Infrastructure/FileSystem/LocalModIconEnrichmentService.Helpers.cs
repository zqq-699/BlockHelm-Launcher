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
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.CurseForge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.FileSystem;

public sealed partial class LocalModIconEnrichmentService
{
    internal static string? TryCreateFileAlias(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
                return null;

            var stablePath = fileInfo.FullName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                ? fileInfo.FullName[..^".disabled".Length]
                : fileInfo.FullName;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"file:{Path.GetFullPath(stablePath)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

}
