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
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.250/neoforge-21.1.250-installer.jar.sha1")]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.29/forge-1.20.1-47.4.29-installer.jar")]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/client.txt")]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://launchermeta.mojang.com/v1/packages/abc/1.20.1.json")]
    [InlineData("https://launcher.mojang.com/v1/objects/abc/client.jar")]
    [InlineData("https://libraries.minecraft.net/org/example/1.0/example-1.0.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef")]
    [InlineData("https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.250/installer.jar.sha1")]
    [InlineData("https://bmclapi2.bangbang93.com/v1/objects/abc/client.txt")]
    [InlineData("https://meta.fabricmc.net/v2/versions/loader/1.21.1")]
    [InlineData("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar")]
    [InlineData("https://cdn.modrinth.com/data/project/versions/version/mod%20name.jar")]
    [InlineData("https://edge.forgecdn.net/files/123/456/mod.jar")]
    [InlineData("https://api.curseforge.com/v1/mods/123/files/456")]
    [InlineData("https://api.modrinth.com/v2/project/example/version")]
    [InlineData("https://api.modrinth.com/v2/version_files")]
    [InlineData("https://mirrors.cernet.edu.cn/bmclapi/v1/objects/abc/client.txt")]
    [InlineData("https://mirrors.ustc.edu.cn/bmclapi/maven/net/neoforged/example.jar")]
    public void PublicDownloadPathsSurviveBothRedactionLayersWithoutCredentials(string publicUrl)
    {
        var input = publicUrl.Replace("https://", "https://synthetic-user:synthetic-password@", StringComparison.Ordinal)
            + "?token=synthetic-query-secret&other=private-value#synthetic-fragment";
        var downloadLog = DownloadUriLogSanitizer.Sanitize(input);

        Assert.Equal(publicUrl, downloadLog);
        Assert.Equal(publicUrl, SensitiveLogText.Redact(input));
        Assert.Equal(publicUrl, SensitiveLogText.Redact(downloadLog));
        Assert.Equal(publicUrl, SensitiveLogText.Redact(SensitiveLogText.Redact(input)));
    }

    [Theory]
    [InlineData("https://example.test/private/synthetic-path-secret", "https://example.test")]
    [InlineData("https://api.telegram.org/file/botsynthetic-path-secret/file", "https://api.telegram.org")]
    [InlineData("https://maven.neoforged.net.evil.test/releases/synthetic-path-secret", "https://maven.neoforged.net.evil.test")]
    [InlineData("https://maven.neoforged.net@evil.test/releases/synthetic-path-secret", "https://evil.test")]
    [InlineData("https://bmclapi2.bangbang93.com/private/synthetic-path-secret", "https://bmclapi2.bangbang93.com")]
    [InlineData("https://maven.neoforged.net/private/synthetic-path-secret", "https://maven.neoforged.net")]
    [InlineData("https://maven.minecraftforge.net:8443/private/synthetic-path-secret", "https://maven.minecraftforge.net:8443")]
    [InlineData("https://api.curseforge.com/private/synthetic-path-secret", "https://api.curseforge.com")]
    [InlineData("https://api.modrinth.com/v2/user/synthetic-path-secret", "https://api.modrinth.com")]
    [InlineData("https://mirrors.ustc.edu.cn/private/synthetic-path-secret", "https://mirrors.ustc.edu.cn")]
    public void UnknownOrPrivateRoutesKeepPathProtectionAndAreIdempotent(string url, string origin)
    {
        var input = url + "?token=synthetic-query-secret#synthetic-fragment";
        var expected = origin + "/<redacted-path>";
        Assert.Equal(expected, DownloadUriLogSanitizer.Sanitize(input));
        Assert.Equal(expected, SensitiveLogText.Redact(input));
        Assert.Equal(expected, SensitiveLogText.Redact(expected));
        Assert.Equal(expected, DownloadUriLogSanitizer.Sanitize(expected));
        Assert.Equal($"OriginalUrl={expected} Status=404", SensitiveLogText.Redact($"OriginalUrl={expected} Status=404"));
    }

    [Fact]
    public void KnownSecretsInPublicPathsAreStillRedactedAndOldMarkersAreStable()
    {
        const string url = "https://libraries.minecraft.net/org/example/synthetic-secret/library.jar";
        var output = SensitiveLogText.Redact(url, ["synthetic-secret"]);
        Assert.DoesNotContain("synthetic-secret", output, StringComparison.Ordinal);
        Assert.Contains("org/example/", output, StringComparison.Ordinal);
        Assert.Equal(output, SensitiveLogText.Redact(output));
        const string oldLog = "https://maven.neoforged.net/<redacted-path>";
        Assert.Equal(oldLog, SensitiveLogText.Redact(oldLog));
    }

    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://libraries.minecraft.net/org/example/1.0/example-1.0.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/abcdef")]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.29/forge-1.20.1-47.4.29-installer.jar")]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.250/neoforge-21.1.250-installer.jar")]
    [InlineData("https://meta.fabricmc.net/v2/versions/loader/1.21.1")]
    public void ResolvedOfficialAndMirrorRoutesKeepTheirDiagnosticPaths(string url)
    {
        foreach (var preference in new[] { DownloadSourcePreference.Official, DownloadSourcePreference.BmclApi })
        foreach (var request in MinecraftDownloadSourceResolver.EnumerateRequests(url, preference))
        {
            var uri = new Uri(request.ActualUrl);
            var expected = uri.GetLeftPart(UriPartial.Path);
            Assert.Equal(expected, SensitiveLogText.Redact(DownloadUriLogSanitizer.Sanitize(uri)));
        }
    }

    [Theory]
    [InlineData("not a URI")]
    [InlineData("https://[invalid-host/path")]
    [InlineData("ftp://example.test/synthetic-secret")]
    public void InvalidUrisUseOnePlaceholderAndDoNotInterruptOtherLogFields(string value)
    {
        const string placeholder = "<redacted-url>";
        Assert.Equal(placeholder, LogUriSanitizer.Sanitize(value));
        Assert.Equal(placeholder, DownloadUriLogSanitizer.Sanitize(value));
        Assert.Equal("Url=<redacted-url> Status=404", SensitiveLogText.Redact(
            $"Url={DownloadUriLogSanitizer.Sanitize(value)} Status=404"));
        Assert.Equal("Url=<redacted-url> Status=404", SensitiveLogText.Redact("Url=https://[invalid-host/path Status=404"));
        Assert.Equal(placeholder, LogUriSanitizer.Sanitize(new Uri("relative/path", UriKind.Relative)));
    }

    [Fact]
    public void FileFormatterPreservesPublicPathsAndRemovesCredentialsOnFinalOutput()
    {
        const string path = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.250/installer.jar.sha1";
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null,
            new MessageTemplateParser().Parse("Download failed. OriginalUrl={OriginalUrl} ActualUrl={ActualUrl} Status={Status}"),
            [
                new LogEventProperty("OriginalUrl", new ScalarValue(path + "?signature=synthetic-signature#private-fragment")),
                new LogEventProperty("ActualUrl", new ScalarValue(DownloadUriLogSanitizer.Sanitize(path + "?token=synthetic-token"))),
                new LogEventProperty("Status", new ScalarValue(404))
            ]);
        using var writer = new StringWriter();
        new RedactingLogFormatter().Format(logEvent, writer);
        var output = writer.ToString();
        Assert.Contains($"OriginalUrl={path}", output, StringComparison.Ordinal);
        Assert.Contains($"ActualUrl={path}", output, StringComparison.Ordinal);
        Assert.Contains("Status=404", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fragment", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<redacted-path>", output, StringComparison.Ordinal);
    }

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
    [InlineData("Authorization: Negotiate synthetic-negotiate-secret", "synthetic-negotiate-secret")]
    [InlineData("Proxy-Authorization: Digest username=synthetic-user, response=synthetic-digest-secret", "synthetic-digest-secret")]
    [InlineData("""{"Authorization":"Basic synthetic-basic-secret"}""", "synthetic-basic-secret")]
    [InlineData("id_token=synthetic-id-secret", "synthetic-id-secret")]
    [InlineData("--idToken synthetic-id-secret", "synthetic-id-secret")]
    [InlineData("client_secret=synthetic-client-secret", "synthetic-client-secret")]
    [InlineData("-Dhttp.proxyPassword=synthetic-proxy-secret", "synthetic-proxy-secret")]
    [InlineData("-Djavax.net.ssl.keyStorePassword=synthetic-keystore-secret", "synthetic-keystore-secret")]
    [InlineData("-Daws.secretAccessKey=synthetic-aws-secret", "synthetic-aws-secret")]
    [InlineData("customAccessToken=synthetic-custom-secret", "synthetic-custom-secret")]
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

    [Theory]
    [InlineData("password=", "synthetic-secret status=ok", "<redacted> status=ok")]
    [InlineData("--accessToken", "synthetic-secret --mode safe", "<redacted> --mode safe")]
    [InlineData("Authorization: Bearer", "synthetic-secret", "<redacted>")]
    [InlineData("proxyPassword:", "\"first synthetic-secret", "<redacted>")]
    public void StreamingRedactionProtectsValuesStartingOnTheNextLine(string key, string value, string expected)
    {
        var redactor = new SensitiveLogText.LineRedactor([]);
        redactor.RedactLine(key);
        redactor.RedactLine(" ");
        Assert.Equal(expected, redactor.RedactLine(value));
        if (value.StartsWith('"'))
            Assert.Equal("<redacted> status=ok", redactor.RedactLine("last synthetic-secret\" status=ok"));
        Assert.Equal("healthy", redactor.RedactLine("healthy"));
    }

    [Theory]
    [InlineData("Negotiate")]
    [InlineData("Digest")]
    public void StreamingAuthorizationRedactsAllSchemeFields(string scheme)
    {
        var redactor = new SensitiveLogText.LineRedactor([]);
        redactor.RedactLine("Authorization:");
        redactor.RedactLine(scheme);
        Assert.Equal("<redacted>", redactor.RedactLine("opaque-secret username=private-user"));
        Assert.Equal("healthy", redactor.RedactLine("healthy"));
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
            TempRoot, Path.Combine(TempRoot, "instance"), "Instance", "1.21.1",
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
