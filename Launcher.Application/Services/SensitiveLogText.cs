/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.RegularExpressions;

namespace Launcher.Application.Services;

/// <summary>Last-resort redaction for free-form text at log and diagnostic file boundaries.</summary>
public static class SensitiveLogText
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const string Name = @"(?:access[_-]?token|refresh[_-]?token|client[_-]?token|token|session|password|passwd|secret|api[_-]?key|authorization|auth[_-]?code)";
    private static readonly Regex Assignment = Create($@"(?i)(?<![\w-])((?:-D|--?)?(?:""|')?{Name}(?:""|')?\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s&,;}}]+)");
    private static readonly Regex Argument = Create($@"(?i)(?<![\w-])((?:--?{Name})\s+)(?:""[^""\r\n]*""|'[^'\r\n]*'|\S+)");
    private static readonly Regex Bearer = Create(@"(?i)\b((?:proxy-)?authorization\s*:\s*(?:bearer|basic)\s+|\bbearer\s+)(\S+)");
    private static readonly Regex Url = Create(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s<>""']+");

    public static string Redact(string text, IReadOnlyList<string>? sensitiveValues = null)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        try
        {
            var redacted = Bearer.Replace(text, "$1<redacted>");
            redacted = Assignment.Replace(redacted, "$1<redacted>");
            redacted = Argument.Replace(redacted, "$1<redacted>");
            redacted = Url.Replace(redacted, match => RedactUrl(match.Value));
            if (sensitiveValues is not null)
            {
                foreach (var value in sensitiveValues)
                {
                    if (!string.IsNullOrWhiteSpace(value) && value.Length >= 8)
                        redacted = redacted.Replace(value, "<redacted>", StringComparison.Ordinal);
                }
            }

            return redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            return "<redacted: log text exceeded safe processing limits>";
        }
    }

    private static string RedactUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return "<redacted-url>";

        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}/<redacted-path>";
    }

    private static Regex Create(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);
}
