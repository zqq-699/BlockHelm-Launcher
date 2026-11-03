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
    private const string Name = @"(?:(?:access|refresh|client|id)[_-]?token|token|session|password|passwd|(?:client[_-]?)?secret|(?:x[_-]?)?api[_-]?key|authorization|auth(?:orization)?[_-]?code)";
    // Consume escaped characters as pairs so escaped quotes cannot terminate a secret.
    // An unfinished quoted value (including a trailing backslash) is redacted through EOF.
    private const string QuotedValue = """(?:"(?:\\[\s\S]|[^"\\])*(?:"|\\?\z)|'(?:\\[\s\S]|[^'\\])*(?:'|\\?\z))""";
    private static readonly Regex Assignment = Create($@"(?i)(?<![\w-])((?:-D|--?)?(?:""|')?{Name}(?:""|')?\s*[:=]\s*)(?<value>{QuotedValue}|[^\s&,;}}]+)");
    private static readonly Regex Argument = Create($@"(?i)(?<![\w-])((?:--?{Name})\s+)(?<value>{QuotedValue}|\S+)");
    private static readonly Regex Credentials = Create($"(?:{Assignment})|(?:{Argument})");
    private static readonly Regex Bearer = Create(@"(?i)\b((?:proxy-)?authorization[""']?\s*[:=]\s*[""']?(?:bearer|basic)\s+|\bbearer\s+)([^\s""',;}]+)");
    private static readonly Regex Url = Create(@"(?i)\b[a-z][a-z0-9+.-]*://[^\s<>""']+");

    public static string Redact(string text, IReadOnlyList<string>? sensitiveValues = null)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        try
        {
            var redacted = text;
            if (sensitiveValues is not null)
            {
                foreach (var value in sensitiveValues.Where(value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(value => value.Length))
                {
                    redacted = redacted.Replace(value, "<redacted>", StringComparison.Ordinal);
                }
            }

            redacted = Bearer.Replace(redacted, "$1<redacted>");
            redacted = Assignment.Replace(redacted, "$1<redacted>");
            redacted = Argument.Replace(redacted, "$1<redacted>");
            redacted = Url.Replace(redacted, match => RedactUrl(match.Value));

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

    /// <summary>One instance per text stream; never share between stdout and stderr.</summary>
    public sealed class LineRedactor
    {
        private readonly IReadOnlyList<string> sensitiveValues;
        private char? openQuote;
        private bool suppressRemainder;

        public LineRedactor(IReadOnlyList<string> sensitiveValues)
        {
            // Known credentials may themselves span lines. Protect each emitted fragment too.
            this.sensitiveValues = sensitiveValues.SelectMany(value => value.Split(['\r', '\n']))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        }

        public void SuppressRemainder() => suppressRemainder = true;

        public string RedactLine(string line)
        {
            if (suppressRemainder)
                return "<redacted>";

            var prefix = string.Empty;
            if (openQuote is { } quote)
            {
                var close = FindClosingQuote(line, quote, 0);
                if (close < 0)
                    return "<redacted>";
                line = line[(close + 1)..];
                openQuote = null;
                prefix = "<redacted>";
            }

            try
            {
                // Scan in text order. A credential-like fragment inside a quoted secret
                // must not replace the quote state of the enclosing value.
                foreach (Match match in Credentials.Matches(line))
                {
                    var value = match.Groups["value"].Value;
                    if (value.Length > 0 && value[0] is '\'' or '"'
                        && FindClosingQuote(value, value[0], 1) < 0)
                        openQuote = value[0];
                }
                return prefix + Redact(line, sensitiveValues);
            }
            catch (RegexMatchTimeoutException)
            {
                suppressRemainder = true;
                return "<redacted: log text exceeded safe processing limits>";
            }
        }

        private static int FindClosingQuote(string text, char quote, int start)
        {
            for (var index = start; index < text.Length; index++)
            {
                if (text[index] == '\\')
                    index++;
                else if (text[index] == quote)
                    return index;
            }
            return -1;
        }
    }
}
