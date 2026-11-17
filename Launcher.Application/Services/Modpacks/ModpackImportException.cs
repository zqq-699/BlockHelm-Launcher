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

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public sealed class ModpackImportException : Exception, ISafeDiagnosticException
{
    public ModpackImportException(ModpackImportFailureReason failureReason)
    {
        FailureReason = failureReason;
    }

    public ModpackImportException(ModpackImportFailureReason failureReason, string message)
        : base(message)
    {
        FailureReason = failureReason;
    }

    public ModpackImportException(ModpackImportFailureReason failureReason, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureReason = failureReason;
    }

    public ModpackImportFailureReason FailureReason { get; }
    public string DiagnosticCode => "ModpackImport";
    public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>
    {
        ["ImportFailure"] = FailureReason.ToString()
    };
}
