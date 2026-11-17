/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Globalization;
using System.IO;
using Launcher.Application.Services;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Launcher.App.Logging;

internal sealed class RedactingLogFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter formatter = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}",
        CultureInfo.InvariantCulture);

    internal static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static long sequence;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, buffer);
        foreach (var key in new[] { "OperationId", "BranchId", "FileId", "RequestId", "ParentRequestId", "Stage", "FailureId", "PrimaryFailureId", "Attempt", "SourceKind", "ResourceCategory", "MinecraftVersion", "LoaderVersion", "DownloadSourcePreference", "DownloadSpeedLimitMbPerSecond" })
        {
            if (!logEvent.MessageTemplate.Text.Contains("{" + key, StringComparison.Ordinal)
                && logEvent.Properties.TryGetValue(key, out var value))
                buffer.Write($" {key}={value}");
        }
        buffer.Write($" LogSession={SessionId} LogSequence={Interlocked.Increment(ref sequence)}");
        var safeMessage = SensitiveLogText.Redact(buffer.ToString());
        output.Write(safeMessage);
        if (logEvent.Exception is { } exception)
        {
            // Keep structured failure evidence even if redacting a very large message hits its safety limit.
            output.Write($" LauncherBuild={typeof(RedactingLogFormatter).Assembly.GetName().Version} ProcessArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            output.Write(SafeExceptionLog.Format(exception));
        }
        output.WriteLine();
    }
}
