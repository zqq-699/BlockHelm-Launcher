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
private static void AppendProcessSection(
        StringBuilder builder,
        ProcessStartInfo? startInfo,
        IReadOnlyList<string> sensitiveValues)
    {
        builder.AppendLine();
        builder.AppendLine("[Process]");
        if (startInfo is null)
        {
            builder.AppendLine("(none)");
            return;
        }

        builder.AppendLine($"FileName: {startInfo.FileName}");
        builder.AppendLine($"Arguments: <redacted>");
        builder.AppendLine($"WorkingDirectory: {startInfo.WorkingDirectory}");
    }

    private static string RedactSensitiveText(string text, IReadOnlyList<string> sensitiveValues) =>
        LaunchDiagnosticRedactor.Redact(text, sensitiveValues);

    private static IEnumerable<string> RedactSensitiveLines(
        IEnumerable<string> lines,
        IReadOnlyList<string> sensitiveValues)
    {
        foreach (var line in lines)
            yield return RedactSensitiveText(line, sensitiveValues);
    }

    private static string LimitTail(string content, int maxLines)
    {
        // 保留尾部是因为 Java/Minecraft 通常在退出前写出最终异常和根因。
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
    }

    private static void AppendCrashPreviewSection(
        StringBuilder builder,
        IReadOnlyList<CrashPreview> crashPreviews,
        IReadOnlyList<string> sensitiveValues)
    {
        builder.AppendLine();
        builder.AppendLine("[CrashPreview]");
        if (crashPreviews.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var crashPreview in crashPreviews.Take(2))
        {
            builder.AppendLine($"> {crashPreview.Path}");
            var preview = RedactSensitiveText(crashPreview.Text, sensitiveValues);
            builder.AppendLine(string.IsNullOrWhiteSpace(preview) ? "(empty)" : preview);
        }
    }

    private static async Task<IReadOnlyList<CrashPreview>> ReadCrashPreviewsAsync(
        IReadOnlyList<string> crashFiles,
        CancellationToken cancellationToken)
    {
        var previews = new List<CrashPreview>();
        foreach (var path in crashFiles.Take(2))
        {
            var text = await BoundedDiagnosticFileReader
                .ReadHeadAsync(path, cancellationToken)
                .ConfigureAwait(false);
            previews.Add(new CrashPreview(path, text));
        }

        return previews;
    }

    private static void AppendFileSection(StringBuilder builder, string title, IEnumerable<string> paths)
    {
        builder.AppendLine();
        builder.AppendLine($"[{title}]");
        var any = false;
        foreach (var path in paths)
        {
            builder.AppendLine(path);
            any = true;
        }

        if (!any)
            builder.AppendLine("(none)");
    }

    private static void AppendTextSection(StringBuilder builder, string title, IEnumerable<string> lines)
    {
        builder.AppendLine();
        builder.AppendLine($"[{title}]");
        var any = false;
        foreach (var line in lines)
        {
            builder.AppendLine(line);
            any = true;
        }

        if (!any)
            builder.AppendLine("(none)");
    }

    private static void AppendTextSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine();
        builder.AppendLine($"[{title}]");
        builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "(empty)" : content.Trim());
    }

    private sealed record DiagnosticWriteResult(
        string Path,
        IReadOnlyList<LaunchDiagnosticReference> Candidates);

    private sealed record CrashPreview(string Path, string Text);
}
