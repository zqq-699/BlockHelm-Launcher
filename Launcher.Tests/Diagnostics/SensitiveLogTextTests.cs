/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using Launcher.App.Logging;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Serilog.Events;
using Serilog.Parsing;

namespace Launcher.Tests.Diagnostics;

public sealed class SensitiveLogTextTests : TestTempDirectory
{
    [Theory]
    [InlineData("""{"password":"prefix\"synthetic-secret-suffix","status":"ok"}""", """{"password":<redacted>,"status":"ok"}""")]
    [InlineData("""--accessToken "prefix\"synthetic-secret-suffix" --mode safe""", """--accessToken <redacted> --mode safe""")]
    [InlineData("""-Dpassword='prefix\'synthetic-secret-suffix' mode=safe""", """-Dpassword=<redacted> mode=safe""")]
    [InlineData("""password="prefix\\" status=ok""", "password=<redacted> status=ok")]
    [InlineData("password=\"first line\nsynthetic-secret-suffix\" status=ok", "password=<redacted> status=ok")]
    [InlineData("""password="unterminated\"synthetic-secret-suffix""", "password=<redacted>")]
    [InlineData("--password 'unterminated\\", "--password <redacted>")]
    [InlineData("""password="Bearer prefix\"synthetic-secret-suffix" status=ok""", "password=<redacted> status=ok")]
    [InlineData("""--token "password=prefix\"synthetic-secret-suffix" --mode safe""", "--token <redacted> --mode safe")]
    public void QuotedCredentialsAreFullyRedacted(string input, string expected)
    {
        Assert.Equal(expected, SensitiveLogText.Redact(input));
    }

    [Theory]
    [InlineData("-Dpassword=synthetic-jvm-secret", "synthetic-jvm-secret")]
    [InlineData("--accessToken synthetic-access-secret", "synthetic-access-secret")]
    [InlineData("{\"refresh_token\":\"synthetic-json-secret\"}", "synthetic-json-secret")]
    [InlineData("Authorization: Bearer synthetic-bearer-secret", "synthetic-bearer-secret")]
    [InlineData("Authorization=Basic synthetic-basic-secret", "synthetic-basic-secret")]
    [InlineData("""{"Authorization":"Basic synthetic-basic-secret"}""", "synthetic-basic-secret")]
    [InlineData("id_token=synthetic-id-secret", "synthetic-id-secret")]
    [InlineData("--idToken synthetic-id-secret", "synthetic-id-secret")]
    [InlineData("client_secret=synthetic-client-secret", "synthetic-client-secret")]
    [InlineData("https://example.test/signed/synthetic-path-secret?token=synthetic-query-secret", "synthetic-path-secret")]
    public void RedactRemovesKnownSecretsFromFreeFormText(string input, string secret)
    {
        var output = SensitiveLogText.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains("<redacted", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prefix password=inner secret-suffix")]
    [InlineData("short")]
    public void KnownValuesAreRemovedBeforePatternReplacement(string secret)
    {
        Assert.Equal("<redacted>", SensitiveLogText.Redact(secret, [secret]));
    }

    [Fact]
    public void KnownValueOverlappingAnEscapeCannotExposeCredentialSuffix()
    {
        var input = """password="prefix\"synthetic-secret-suffix" status=ok""";
        Assert.Equal("password=<redacted> status=ok", SensitiveLogText.Redact(input, ["prefix\\"]));
        Assert.Equal("<redacted>", SensitiveLogText.Redact("abcd", ["abc", "bcd"]));
    }

    [Fact]
    public void StreamingRedactionResumesOnlyAfterUnescapedClosingQuote()
    {
        var redactor = new SensitiveLogText.LineRedactor([]);
        Assert.Equal("password=<redacted>", redactor.RedactLine("password=\"first"));
        Assert.Equal("<redacted>", redactor.RedactLine("still\\\"secret"));
        Assert.Equal("<redacted> status=ok", redactor.RedactLine("last\" status=ok"));
        Assert.Equal("healthy", redactor.RedactLine("healthy"));
    }

    [Fact]
    public void CredentialFragmentsInsideSecretCannotChangeStreamingQuoteState()
    {
        var redactor = new SensitiveLogText.LineRedactor([]);
        Assert.Equal("password=<redacted>", redactor.RedactLine("password=\"first --token 'nested"));
        Assert.Equal("<redacted>", redactor.RedactLine("nested' synthetic-secret-suffix"));
        Assert.Equal("<redacted> status=ok", redactor.RedactLine("last\" status=ok"));
    }

    [Fact]
    public void StreamingRedactionProtectsKnownMultilineValueFragments()
    {
        var redactor = new SensitiveLogText.LineRedactor(["first-secret\r\nshort"]);
        Assert.Equal("value=<redacted>", redactor.RedactLine("value=first-secret"));
        Assert.Equal("<redacted>", redactor.RedactLine("short"));
    }

    [Fact]
    public void FileFormatterDoesNotRenderRawExceptionMessages()
    {
        var exception = new InvalidOperationException("unlabelled-synthetic-secret");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Request failed at {Url} Payload={Payload}"),
            [
                new LogEventProperty("Url", new ScalarValue("https://example.test/signed/synthetic-path-secret?token=synthetic-query-secret")),
                new LogEventProperty("Payload", new ScalarValue("""{"password":"Bearer prefix\"synthetic-secret-suffix", "id_token":"synthetic-id-secret", "Authorization":"Basic synthetic-basic-secret"}"""))
            ]);
        using var writer = new StringWriter();

        new RedactingLogFormatter().Format(logEvent, writer);
        var output = writer.ToString();

        Assert.Contains("ExceptionType=System.InvalidOperationException", output, StringComparison.Ordinal);
        Assert.Contains("example.test", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unlabelled-synthetic-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-path-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-suffix", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-id-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-basic-secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EarlyLaunchFailureDiagnosticOmitsRawExceptionAndProcessArguments()
    {
        var context = new LaunchDiagnosticContext(
            TempRoot, Path.Combine(TempRoot, "instance"), "instance-id", "Instance", "1.21.1",
            "1.21.1", LoaderKind.Vanilla, null, null, null, null, 2048, []);
        var startInfo = new ProcessStartInfo("java") { Arguments = "-Dcustom=unlabelled-argument-secret" };

        var result = await LaunchDiagnosticsWriter.WriteExceptionDiagnosticAsync(
            context,
            "account_session_failed",
            "Account session failed. id_token=synthetic-id-secret Authorization=Basic synthetic-basic-secret",
            new InvalidOperationException("unlabelled-exception-secret"),
            startInfo,
            DateTimeOffset.UtcNow,
            [],
            CancellationToken.None);
        var diagnosticPath = Assert.IsType<string>(result.DiagnosticPath);
        var output = await File.ReadAllTextAsync(diagnosticPath);

        Assert.Contains("InvalidOperationException", output, StringComparison.Ordinal);
        Assert.Contains("Arguments: <redacted>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unlabelled-exception-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unlabelled-argument-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-id-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-basic-secret", output, StringComparison.Ordinal);
    }
}
