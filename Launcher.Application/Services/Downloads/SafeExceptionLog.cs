/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Runtime.CompilerServices;

namespace Launcher.Application.Services;

/// <summary>Renders exception structure and approved fields, never arbitrary Message/Data/ToString().</summary>
public static class SafeExceptionLog
{
    private sealed record FailureIdentity(string Id);
    private static readonly ConditionalWeakTable<Exception, FailureIdentity> Identities = new();
    public static string FailureId(Exception exception) => Identities.GetValue(exception, _ => new(Guid.NewGuid().ToString("N"))).Id;
    private sealed record Detail(string Code, IReadOnlyDictionary<string, object?> Fields);
    private sealed class DetailSlot { public Detail? Value; }
    private static readonly ConditionalWeakTable<Exception, DetailSlot> Details = new();
    private const int MaximumDiagnosticCharacters = 64 * 1024;
    private const int MaximumFieldCharacters = 16 * 1024;
    public static T Describe<T>(T exception, string code, params (string Key, object? Value)[] fields) where T : Exception
    {
        var detail = new Detail(code, fields.ToDictionary(field => field.Key, field => field.Value));
        Volatile.Write(ref Details.GetValue(exception, _ => new DetailSlot()).Value, detail);
        return exception;
    }

    public static string Format(Exception exception, bool includeStack = true)
    {
        var text = new StringBuilder($" FailureId={FailureId(exception)}");
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        Append(exception, "root", 0);
        if (text.Length >= MaximumDiagnosticCharacters) text.Append(" DiagnosticTruncated=true");
        return SensitiveLogText.Redact(text.ToString());

        void AppendFields(IReadOnlyDictionary<string, object?> fields)
        {
            foreach (var field in fields.Take(128))
            {
                if (text.Length >= MaximumDiagnosticCharacters) break;
                var safe = field.Value switch
                {
                    null => "null",
                    string value => JsonSerializer.Serialize(BoundedText(value)),
                    bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => JsonSerializer.Serialize(field.Value),
                    double value => double.IsFinite(value) ? JsonSerializer.Serialize(value) : JsonSerializer.Serialize(value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    float value => float.IsFinite(value) ? JsonSerializer.Serialize(value) : JsonSerializer.Serialize(value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    IEnumerable<string> values => JsonSerializer.Serialize(values.Take(128).Select(BoundedText).ToArray()),
                    _ => "\"<unsupported-diagnostic-type>\""
                };
                if (safe.Length > MaximumFieldCharacters) safe = "\"<diagnostic-field-truncated>\"";
                text.Append($" {field.Key}={safe}");
            }
        }

        static string BoundedText(string value)
        {
            // Do not slice a potentially secret value before redaction. Large fields are omitted as a whole;
            // process output has already been recorded incrementally in its own safe events.
            if (value.Length > MaximumFieldCharacters) return "<diagnostic-field-truncated>";
            return SensitiveLogText.Redact(value);
        }

        void Append(Exception current, string branch, int depth)
        {
            if (text.Length >= MaximumDiagnosticCharacters || !visited.Add(current)) return;
            if (depth >= 32 || visited.Count > 128) { text.Append(" ExceptionTreeTruncated=true"); return; }
            text.Append($" ExceptionBranch={branch} ExceptionType={current.GetType().FullName} CauseFailureId={FailureId(current)} HResult={current.HResult}");
            if (current is IOException or UnauthorizedAccessException
                && ((uint)current.HResult & 0xffff0000) == 0x80070000)
            {
                var code = current.HResult & 0xffff;
                var reason = code switch
                {
                    2 => "FileNotFound", 3 => "DirectoryNotFound", 5 => "AccessDenied",
                    32 or 33 => "SharingViolation", 39 or 112 => "DiskFull",
                    80 or 183 => "AlreadyExists", 206 => "PathTooLong", _ => "OtherWin32FileError"
                };
                text.Append($" FileSystemFailure={reason} NativeErrorCode={code}");
            }
            if (current is HttpRequestException http)
                text.Append($" HttpStatus={(int?)http.StatusCode} HttpRequestError={http.HttpRequestError}");
            if (current is WebException web)
            {
                text.Append($" WebExceptionStatus={web.Status}");
                if (web.Response is HttpWebResponse response) text.Append($" HttpStatus={(int)response.StatusCode}");
            }
            if (current is SocketException socket)
                text.Append($" SocketError={socket.SocketErrorCode} NativeErrorCode={socket.NativeErrorCode}");
            else if (current is Win32Exception win32)
                text.Append($" NativeErrorCode={win32.NativeErrorCode}");
            if (current is JsonException json)
                text.Append($" JsonLine={json.LineNumber} JsonBytePosition={json.BytePositionInLine}");
            if (current is XmlException xml)
                text.Append($" XmlLine={xml.LineNumber} XmlPosition={xml.LinePosition}");
            try
            {
                if (current is ISafeDiagnosticException diagnostic)
                {
                    text.Append($" DiagnosticCode={diagnostic.DiagnosticCode}");
                    AppendFields(diagnostic.DiagnosticFields);
                }
                if (Details.TryGetValue(current, out var slot) && Volatile.Read(ref slot.Value) is { } detail)
                {
                    text.Append($" DiagnosticCode={detail.Code}");
                    AppendFields(detail.Fields);
                }
            }
            catch (Exception projectionException)
            {
                text.Append($" DiagnosticProjectionErrorType={projectionException.GetType().FullName}");
            }
            foreach (var frame in includeStack ? new StackTrace(current, true).GetFrames() ?? [] : [])
            {
                if (text.Length >= MaximumDiagnosticCharacters) break;
                var method = frame.GetMethod();
                text.Append($"\n  at {method?.DeclaringType?.FullName}.{method?.Name} Line={frame.GetFileLineNumber()}");
            }
            if (current is AggregateException aggregate)
            {
                for (var i = 0; i < aggregate.InnerExceptions.Count; i++)
                    Append(aggregate.InnerExceptions[i], $"{branch}.{i}", depth + 1);
            }
            else if (current.InnerException is { } inner) Append(inner, $"{branch}.inner", depth + 1);
        }
    }
}
