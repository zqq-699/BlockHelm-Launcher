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

using System.Diagnostics;
using System.IO;
using System.Text;
using Launcher.Application;
using Launcher.Application.Services;

namespace Launcher.Infrastructure.Minecraft;

internal static partial class LaunchDiagnosticsWriter
{
private static void AppendAnalysisSection(
        StringBuilder builder,
        Launcher.Application.Services.LaunchFailureAnalysis? analysis,
        IReadOnlyList<string> sensitiveValues)
    {
        builder.AppendLine();
        builder.AppendLine("[Analysis]");
        if (analysis is null)
        {
            builder.AppendLine("(none)");
            return;
        }

        builder.AppendLine($"Category: {analysis.Category}");
        builder.AppendLine($"ReasonTitle: {analysis.ReasonTitle}");
        builder.AppendLine($"ReasonDetail: {analysis.ReasonDetail}");
        builder.AppendLine($"Recommendation: {analysis.Recommendation}");
        if (analysis.RequiredJavaMajorVersion is int requiredJava)
            builder.AppendLine($"RequiredJavaMajorVersion: {requiredJava}");
        if (analysis.CurrentJavaMajorVersion is int currentJava)
            builder.AppendLine($"CurrentJavaMajorVersion: {currentJava}");
        if (!string.IsNullOrWhiteSpace(analysis.ModName))
            builder.AppendLine($"ModName: {analysis.ModName}");
        if (!string.IsNullOrWhiteSpace(analysis.DependencyName))
            builder.AppendLine($"DependencyName: {analysis.DependencyName}");
        if (!string.IsNullOrWhiteSpace(analysis.MissingPath))
            builder.AppendLine($"MissingPath: {analysis.MissingPath}");
        if (analysis.GameFileFailureReason is { } gameFileFailureReason)
            builder.AppendLine($"GameFileFailureReason: {gameFileFailureReason}");
        if (!string.IsNullOrWhiteSpace(analysis.AffectedPath))
            builder.AppendLine($"AffectedPath: {analysis.AffectedPath}");
        if (analysis.AutoRepairEnabled is bool autoRepairEnabled)
            builder.AppendLine($"AutoRepairEnabled: {autoRepairEnabled}");

        for (var index = 0; index < analysis.Details.Count; index++)
        {
            var detail = analysis.Details[index];
            builder.AppendLine();
            builder.AppendLine($"[Analysis.Detail.{index + 1}]");
            builder.AppendLine($"Kind: {detail.Kind}");
            builder.AppendLine($"ModName: {detail.ModName ?? string.Empty}");
            builder.AppendLine($"ModVersion: {detail.ModVersion ?? string.Empty}");
            builder.AppendLine($"DependencyName: {detail.DependencyName ?? string.Empty}");
            builder.AppendLine($"RequiredVersion: {detail.RequiredVersion ?? string.Empty}");
            builder.AppendLine($"CurrentVersion: {detail.CurrentVersion ?? string.Empty}");
            builder.AppendLine($"OriginalReason: {RedactSensitiveText(detail.OriginalReason ?? string.Empty, sensitiveValues)}");
            builder.AppendLine($"OriginalSuggestion: {RedactSensitiveText(detail.OriginalSuggestion ?? string.Empty, sensitiveValues)}");
        }

        for (var index = 0; index < analysis.Evidence.Count; index++)
        {
            var evidence = analysis.Evidence[index];
            builder.AppendLine(
                $"Evidence.{index + 1}: {evidence.Kind}: {RedactSensitiveText(evidence.Text, sensitiveValues)}");
        }
    }

    private static List<string> FindMatchedErrorLines(
        string stdout,
        string stderr,
        string latestLogTail,
        IReadOnlyList<CrashPreview> crashPreviews)
    {
        // 从尾部提取少量高信号错误行，完整日志路径仍写入诊断供进一步查看。
        var candidates = new List<string>();
        AddMatches(stderr);
        AddMatches(stdout);
        AddMatches(latestLogTail);

        foreach (var crashPreview in crashPreviews.Take(2))
            AddMatches(crashPreview.Text);

        return candidates
            .OrderByDescending(GetFailureLineScore)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        void AddMatches(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (LooksLikeFailureLine(line))
                {
                    candidates.Add(line.Length <= MaxMatchedErrorLineLength
                        ? line
                        : line[..MaxMatchedErrorLineLength] + "…");
                }
            }
        }
    }

    private static int GetFailureLineScore(string line)
    {
        if (line.Contains("Invalid paths argument, contained no existing paths", StringComparison.OrdinalIgnoreCase))
            return 600;
        if (line.Contains("caused by", StringComparison.OrdinalIgnoreCase))
            return 550;
        if (line.Contains("exception", StringComparison.OrdinalIgnoreCase))
            return 500;
        if (line.Contains("fatal", StringComparison.OrdinalIgnoreCase))
            return 450;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
            return 400;
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return 300;
        return 100;
    }

    private static bool LooksLikeFailureLine(string line)
    {
        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
               || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
               || line.Contains("caused by", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Invalid paths argument, contained no existing paths", StringComparison.OrdinalIgnoreCase)
               || line.Contains("missing", StringComparison.OrdinalIgnoreCase)
               || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
               || line.Contains("fatal", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatExceptionChain(Exception exception)
    {
        var builder = new StringBuilder();
        var depth = 0;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (depth > 0)
                builder.AppendLine();

            builder.AppendLine($"[{depth}] {current.GetType().FullName}");
            builder.AppendLine($"HResult: {current.HResult}");
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
                builder.AppendLine(current.StackTrace.Trim());
            depth++;
        }

        return builder.ToString();
    }

    private static LaunchDownloadDiagnostic? FindDownloadDiagnostic(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is InstanceRepairException { DownloadDiagnostic: { } diagnostic })
                return diagnostic;
        }

        return null;
    }

    private static void AppendDownloadSection(
        StringBuilder builder,
        LaunchDownloadDiagnostic? diagnostic,
        IReadOnlyList<string> sensitiveValues)
    {
        builder.AppendLine();
        builder.AppendLine("[Download]");
        if (diagnostic is null)
        {
            builder.AppendLine("(none)");
            return;
        }

        builder.AppendLine($"OriginalUrl: {RedactSensitiveText(diagnostic.OriginalUrl, sensitiveValues)}");
        builder.AppendLine($"ActualUrl: {RedactSensitiveText(diagnostic.ActualUrl, sensitiveValues)}");
        builder.AppendLine($"DestinationPath: {diagnostic.DestinationPath}");
        builder.AppendLine($"HttpStatusCode: {diagnostic.HttpStatusCode?.ToString() ?? string.Empty}");
        builder.AppendLine($"LibraryName: {diagnostic.LibraryName ?? string.Empty}");
        builder.AppendLine($"ArtifactPath: {diagnostic.ArtifactPath ?? string.Empty}");
        builder.AppendLine($"RequestedSourcePreference: {diagnostic.RequestedSourcePreference}");
        builder.AppendLine($"ResolvedSourceKind: {diagnostic.ResolvedSourceKind}");
        builder.AppendLine($"ResourceCategory: {diagnostic.ResourceCategory}");
    }
}
