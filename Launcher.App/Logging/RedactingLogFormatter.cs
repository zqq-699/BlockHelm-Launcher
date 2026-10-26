/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
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

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, buffer);
        if (logEvent.Exception is { } exception)
        {
            var depth = 0;
            for (Exception? current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
            {
                buffer.Write($" ExceptionType={current.GetType().FullName} HResult={current.HResult}");
                if (current is HttpRequestException httpException)
                    buffer.Write($" HttpStatus={(int?)httpException.StatusCode} HttpRequestError={httpException.HttpRequestError}");
                if (current is SocketException socketException)
                    buffer.Write($" SocketError={socketException.SocketErrorCode} NativeErrorCode={socketException.NativeErrorCode}");
                else if (current is Win32Exception win32Exception)
                    buffer.Write($" NativeErrorCode={win32Exception.NativeErrorCode}");
            }
        }
        output.WriteLine(SensitiveLogText.Redact(buffer.ToString()));
    }
}
