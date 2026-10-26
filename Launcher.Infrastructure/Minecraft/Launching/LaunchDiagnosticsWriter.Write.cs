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
private static async Task<DiagnosticWriteResult> WriteDiagnosticAsync(
        LaunchDiagnosticContext context,
        string failureKind,
        string failureSummary,
        Launcher.Application.Services.LaunchFailureAnalysis? analysis,
        DateTimeOffset createdAt,
        IReadOnlyList<LaunchDiagnosticReference> diagnosticCandidates,
        CancellationToken cancellationToken,
        Action<StringBuilder> appendSections)
    {
        // 写入使用单一入口以统一目录创建、命名、保留数量和失败降级行为。
        var logsDirectory = Path.Combine(
            context.InstanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName,
            "logs");
        Directory.CreateDirectory(logsDirectory);
        var diagnosticPath = Path.Combine(
            logsDirectory,
            $"launch-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
        var allCandidates = diagnosticCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .Concat([new LaunchDiagnosticReference(LaunchDiagnosticType.LauncherDiagnostic, diagnosticPath)])
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"CreatedAtUtc: {createdAt:O}");
        builder.AppendLine($"FailureKind: {failureKind}");
        builder.AppendLine($"FailureSummary: {LaunchDiagnosticRedactor.Redact(failureSummary, context.SensitiveValues)}");
        builder.AppendLine($"InstanceName: {context.InstanceName}");
        builder.AppendLine($"VersionName: {context.VersionName}");
        builder.AppendLine($"MinecraftVersion: {context.MinecraftVersion}");
        builder.AppendLine($"Loader: {context.Loader}");
        builder.AppendLine($"LoaderVersion: {context.LoaderVersion ?? string.Empty}");
        builder.AppendLine($"JavaPath: {context.JavaPath ?? string.Empty}");
        builder.AppendLine($"JavaVersion: {context.JavaVersion ?? string.Empty}");
        builder.AppendLine($"JavaSource: {context.JavaSource ?? string.Empty}");
        builder.AppendLine($"MemoryMb: {context.MemoryMb}");
        builder.AppendLine($"InstanceDirectory: {Path.GetFullPath(context.InstanceDirectory)}");
        builder.AppendLine($"MinecraftDirectory: {Path.GetFullPath(context.MinecraftDirectory)}");

        AppendDiagnosticReferences(builder, allCandidates);
        AppendAnalysisSection(builder, analysis, context.SensitiveValues);
        appendSections(builder);

        await File.WriteAllTextAsync(
            diagnosticPath,
            LaunchDiagnosticRedactor.Redact(builder.ToString(), context.SensitiveValues),
            cancellationToken);
        PruneOldDiagnostics(logsDirectory);
        return new DiagnosticWriteResult(diagnosticPath, allCandidates);
    }

    private static void AppendDiagnosticReferences(
        StringBuilder builder,
        IReadOnlyList<LaunchDiagnosticReference> candidates)
    {
        var primary = candidates.FirstOrDefault();
        builder.AppendLine();
        builder.AppendLine("[PrimaryDiagnostic]");
        builder.AppendLine($"Type: {primary?.Type.ToString() ?? "none"}");
        builder.AppendLine($"Path: {primary?.Path ?? "none"}");

        builder.AppendLine();
        builder.AppendLine("[RelatedDiagnostics]");
        foreach (var type in Enum.GetValues<LaunchDiagnosticType>())
        {
            var matches = candidates.Where(candidate => candidate.Type == type).ToArray();
            if (matches.Length == 0)
            {
                builder.AppendLine($"{type}: none");
                continue;
            }

            foreach (var match in matches)
                builder.AppendLine($"{type}: {match.Path}");
        }
    }

    private static void PruneOldDiagnostics(string logsDirectory)
    {
        PruneOldFiles(logsDirectory, DiagnosticFilePattern);
        PruneOldFiles(logsDirectory, CapturedOutputFilePattern);
    }

    private static void PruneOldFiles(string logsDirectory, string pattern)
    {
        // 只清理启动器自己命名的诊断文件，绝不触碰 Minecraft 或 Mod 生成的其他日志。
        try
        {
            var files = Directory.GetFiles(logsDirectory, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxDiagnosticLogFiles)
                .ToList();

            foreach (var file in files)
                DeleteDiagnosticSafely(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDiagnosticSafely(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
