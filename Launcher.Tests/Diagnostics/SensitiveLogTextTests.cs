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
    [InlineData("-Dpassword=synthetic-jvm-secret", "synthetic-jvm-secret")]
    [InlineData("--accessToken synthetic-access-secret", "synthetic-access-secret")]
    [InlineData("{\"refresh_token\":\"synthetic-json-secret\"}", "synthetic-json-secret")]
    [InlineData("Authorization: Bearer synthetic-bearer-secret", "synthetic-bearer-secret")]
    [InlineData("https://example.test/signed/synthetic-path-secret?token=synthetic-query-secret", "synthetic-path-secret")]
    public void RedactRemovesKnownSecretsFromFreeFormText(string input, string secret)
    {
        var output = SensitiveLogText.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains("<redacted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FileFormatterDoesNotRenderRawExceptionMessages()
    {
        var exception = new InvalidOperationException("unlabelled-synthetic-secret");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Request failed at {Url}"),
            [new LogEventProperty("Url", new ScalarValue("https://example.test/signed/synthetic-path-secret?token=synthetic-query-secret"))]);
        using var writer = new StringWriter();

        new RedactingLogFormatter().Format(logEvent, writer);
        var output = writer.ToString();

        Assert.Contains("ExceptionType=System.InvalidOperationException", output, StringComparison.Ordinal);
        Assert.Contains("example.test", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unlabelled-synthetic-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-path-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-query-secret", output, StringComparison.Ordinal);
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
            "Account session failed.",
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
    }
}
