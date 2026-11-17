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

namespace Launcher.Application.Services;

public sealed class JavaRuntimeSelectionException : Exception, ISafeDiagnosticException
{
    public JavaRuntimeSelectionException(
        string message,
        JavaRuntimeSelectionFailureReason reason = JavaRuntimeSelectionFailureReason.Unknown,
        int? requiredMajorVersion = null,
        int? currentMajorVersion = null,
        string? currentVersion = null,
        int? recommendedMajorVersion = null)
        : base(message)
    {
        Reason = reason;
        RequiredMajorVersion = requiredMajorVersion;
        CurrentMajorVersion = currentMajorVersion;
        CurrentVersion = currentVersion;
        RecommendedMajorVersion = recommendedMajorVersion ?? requiredMajorVersion;
    }

    public JavaRuntimeSelectionException(
        string message,
        Exception innerException,
        JavaRuntimeSelectionFailureReason reason = JavaRuntimeSelectionFailureReason.Unknown,
        int? requiredMajorVersion = null,
        int? currentMajorVersion = null,
        string? currentVersion = null,
        int? recommendedMajorVersion = null)
        : base(message, innerException)
    {
        Reason = reason;
        RequiredMajorVersion = requiredMajorVersion;
        CurrentMajorVersion = currentMajorVersion;
        CurrentVersion = currentVersion;
        RecommendedMajorVersion = recommendedMajorVersion ?? requiredMajorVersion;
    }

    public string DiagnosticCode => "JavaRuntimeSelection";
    public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>
    {
        ["SelectionFailure"] = Reason.ToString(), ["RequiredJavaMajor"] = RequiredMajorVersion,
        ["ActualJavaMajor"] = CurrentMajorVersion, ["RecommendedJavaMajor"] = RecommendedMajorVersion
    };
    public JavaRuntimeSelectionFailureReason Reason { get; }

    public int? RequiredMajorVersion { get; }

    public int? CurrentMajorVersion { get; }

    public string? CurrentVersion { get; }

    public int? RecommendedMajorVersion { get; }
}

public enum JavaRuntimeSelectionFailureReason
{
    Unknown,
    AutomaticRuntimeMissing,
    AutomaticRuntimeNotFound,
    ManualRuntimeMissing,
    ManualRuntimeUnavailable,
    ManualRuntimeVersionTooLow,
    ManualRuntimeIncompatible
}
