/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Launcher.App.Logging;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modrinth;
using Launcher.Infrastructure.Resources;
using Launcher.Infrastructure.FileSystem;
using Launcher.Infrastructure.CurseForge;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Launcher.Tests.Diagnostics;

public sealed class DownloadDiagnosticsTests : TestTempDirectory
{
    [Fact]
    public async Task FabricApiMetadataFailureIsVisibleBeforeFileDownloadStarts()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(_ => throw new HttpRequestException(
            HttpRequestError.NameResolutionError, "unlabelled-metadata-secret"))))
        {
            var service = new ModrinthService(client, factory.CreateLogger<ModrinthService>());
            await Assert.ThrowsAsync<HttpRequestException>(() => service.GetFabricApiVersionsAsync("1.21.1"));
        }
        var output = ReadLogs();
        Assert.Contains("NameResolutionError", output);
        Assert.Contains("api.modrinth.com", output);
        Assert.Contains("RequestId=", output);
        Assert.DoesNotContain("unlabelled-metadata-secret", output);
    }

    [Fact]
    public async Task IconHttpRejectionIsVisibleWhenReturningAnEmptyFallback()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { RequestMessage = request, Content = new StringContent("private-response-body") })))
        {
            var provider = new RemoteModIconProviderClient(client, new DiagnosticApiKeyResolver(), factory.CreateLogger("IconMetadata"));
            Assert.Empty(await provider.ResolveModrinthAsync(
                [new ModIconLookupCandidate("unused.jar", new string('a', 40), "unused", 1, ResourceProjectKind.Mod)], CancellationToken.None));
        }
        var output = ReadLogs();
        Assert.Contains("StatusCode=403", output);
        Assert.Contains("RequestId=", output);
        Assert.DoesNotContain("private-response-body", output);
    }

    [Fact]
    public async Task IndependentMetadataParseFailureRetainsHttpRequestContext()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("<private-invalid-json>", Encoding.UTF8, "text/html") })))
        {
            var provider = new CurseForgeResourceClient(client, new DiagnosticApiKeyResolver(), factory.CreateLogger("CurseForgeParse"));
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => provider.GetProjectAsync(
                new ResourceProjectReference(ResourceProjectKind.Mod, ResourceProjectSource.CurseForge, "123"), CancellationToken.None));
        }
        var output = ReadLogs();
        Assert.Contains("Stage=MetadataReadParse", output);
        Assert.Contains("ContentType=\"text/html\"", output);
        Assert.Contains("JsonLine=", output);
        Assert.DoesNotContain("private-invalid-json", output);
        Assert.DoesNotContain("private-api-key", output);
        var lines = output.Split('\n');
        var http = lines.Single(line => line.Contains("Independent download HTTP response."));
        var failure = lines.Single(line => line.Contains("Download stage failed."));
        static string Request(string line) => System.Text.RegularExpressions.Regex.Match(line, "RequestId=\"?([a-f0-9]+)").Groups[1].Value;
        Assert.NotEmpty(Request(http));
        Assert.Equal(Request(http), Request(failure));
    }

    private sealed class DiagnosticApiKeyResolver : ICurseForgeApiKeyResolver
    {
        public Task<string?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("private-api-key");
    }

    [Fact]
    public void CleanupEnumerationDenialCannotReplaceTheOriginalInstallerFailure()
    {
        var versions = new DirectoryInfo(Path.Combine(TempRoot, "versions"));
        versions.Create();
        var security = System.IO.FileSystemAclExtensions.GetAccessControl(versions);
        var originalSecurity = security.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(versions, security);
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetDirectories(versions.FullName));
            using var sink = CreateMainLogger();
            using var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false));
            var original = new InvalidOperationException("private-original-failure");
            var actual = Record.Exception((Action)(() =>
            {
                try { throw original; }
                catch
                {
                    LoaderVersionDirectoryTransaction.CleanupCreatedVersionDirectories(TempRoot, [], null,
                        factory.CreateLogger("CleanupEnumeration"));
                    throw;
                }
            }));
            Assert.Same(original, actual);
        }
        finally
        {
            security.SetSecurityDescriptorSddlForm(originalSecurity, System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(versions, security);
        }
        var output = ReadLogs();
        Assert.Contains("cleanup enumeration failed", output);
        Assert.Contains("AccessDenied", output);
        Assert.DoesNotContain("private-original-failure", output);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(0)]
    public async Task DefaultMainLogIncludesFailureBeforeBodyProgress(int status)
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request =>
        {
            if (status == 0)
                throw new HttpRequestException(HttpRequestError.NameResolutionError, "unlabelled-secret",
                    new SocketException(11001));
            return new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Content = new StringContent("") };
        })))
        {
            var logger = factory.CreateLogger("DownloadTest");
            using var operation = DownloadDiagnostics.BeginOperation(logger, "TestInstall");
            var executor = new MinecraftDownloadRequestExecutor(client, logger, retryOptions: new DownloadRetryOptions
            { MaxFileAttemptsPerSource = 1, MaxAttemptsPerSource = 1, RetryDelay = TimeSpan.Zero },
                hostConcurrencyController: new DownloadHostConcurrencyController());
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.DownloadFileAsync("https://example.test/signed/path-secret?token=query-secret",
                    DownloadSourcePreference.Official, "ThirdParty", Path.Combine(TempRoot, "artifact.jar"),
                    null, null, CancellationToken.None));
            Assert.Equal(1, exception.NetworkAttempts);
            logger.LogError(exception, "Install failed. FileName=artifact.jar");
        }
        var output = ReadLogs();
        Assert.Contains("OperationId=", output);
        Assert.Contains("RequestId=", output);
        Assert.Contains("NetworkAttempts=1", output);
        Assert.Contains(status == 0 ? "NameResolutionError" : $"HTTP {status}", output);
        Assert.DoesNotContain("path-secret", output);
        Assert.DoesNotContain("query-secret", output);
        Assert.DoesNotContain("unlabelled-secret", output);
    }

    [Theory]
    [InlineData("tls", "SecureConnectionError")]
    [InlineData("headers", "ResponseHeadersTimeout")]
    [InlineData("json", "JsonLine=")]
    public async Task DefaultMainLogRetainsTransportAndParseFailureEvidence(string kind, string expected)
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (kind == "headers") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (kind == "tls") throw new HttpRequestException(HttpRequestError.SecureConnectionError,
                "unlabelled-secret", new System.Security.Authentication.AuthenticationException("private-certificate-detail"));
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{invalid") };
        })))
        {
            var logger = factory.CreateLogger("MetadataTest");
            var executor = new MinecraftDownloadRequestExecutor(client, logger, retryOptions: new DownloadRetryOptions
            { MaxAttemptsPerSource = 1, ResponseHeadersTimeout = TimeSpan.FromMilliseconds(30) });
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.ExecuteAsync("https://example.test/metadata", DownloadSourcePreference.Official, "ThirdParty",
                    async (context, token) => await System.Text.Json.JsonDocument.ParseAsync(
                        await context.Response.Content.ReadAsStreamAsync(token), cancellationToken: token), CancellationToken.None));
            logger.LogError(exception, "Metadata operation failed.");
        }
        var output = ReadLogs();
        Assert.Contains(expected, output);
        Assert.DoesNotContain("unlabelled-secret", output);
        Assert.DoesNotContain("private-certificate-detail", output);
    }

    [Fact]
    public async Task RecoveryCountsHeaderOnlyFailuresAndIdentifiesActualFallback()
    {
        using var sink = CreateMainLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false));
        var count = 0;
        using var client = new HttpClient(new Handler(request => new HttpResponseMessage(
            Interlocked.Increment(ref count) == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("artifact") }));
        var executor = new MinecraftDownloadRequestExecutor(client, factory.CreateLogger("RecoveryTest"),
            retryOptions: new DownloadRetryOptions { MaxFileAttemptsPerSource = 1 });
        var resolution = await executor.DownloadFileAsync(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.29/forge-1.20.1-47.4.29-installer.jar",
            DownloadSourcePreference.Official, "Forge", Path.Combine(TempRoot, "artifact.jar"), null, null, CancellationToken.None);
        Assert.Equal(2, resolution.NetworkAttempts);
        Assert.Equal(0, resolution.RetryCount);
        Assert.True(resolution.FallbackUsed);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task TerminalLogSeparatesRetriesFromRedirectRequests(bool redirect, int expectedRetries)
    {
        var sends = 0;
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request =>
        {
            if (Interlocked.Increment(ref sends) != 1)
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("good") };
            if (!redirect) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            return new HttpResponseMessage(HttpStatusCode.Found)
            { RequestMessage = request, Headers = { Location = new Uri("https://cdn.example.test/file") } };
        })))
        {
            var logger = factory.CreateLogger("Counts");
            var path = Path.Combine(TempRoot, "file.bin");
            const string url = "https://example.test/file";
            var file = new ForegroundDownloadLogScope(logger, "Test", "file.bin", path, url);
            using var context = file.RestoreContext();
            var executor = new MinecraftDownloadRequestExecutor(client, logger,
                retryOptions: new DownloadRetryOptions { MaxFileAttemptsPerSource = 2, RetryDelay = TimeSpan.Zero },
                nextRetryJitter: () => 0,
                hostConcurrencyController: new DownloadHostConcurrencyController());
            var result = await executor.DownloadFileAsync(url, DownloadSourcePreference.Official, "ThirdParty", path,
                null, null, CancellationToken.None, reportTransferredBytes: file.ReportTransferredBytes);
            file.Complete(result);
            Assert.Equal(2, result.NetworkAttempts);
            Assert.Equal(expectedRetries, result.RetryCount);
            Assert.False(result.FallbackUsed);
        }
        var terminal = ReadLogs().Split('\n').Single(line => line.Contains("Foreground file download completed."));
        Assert.Contains("HttpRequestCount=2", terminal);
        Assert.Contains($"RetryCount={expectedRetries}", terminal);
        Assert.Contains("FallbackUsed=false", terminal);
    }

    [Fact]
    public async Task WrappedDownloadFailureRetainsRealAttemptCountAndFallback()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.Forbidden)
            { RequestMessage = request, Content = new StringContent("") })))
        {
            const string url = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.29/forge-1.20.1-47.4.29-installer.jar";
            var logger = factory.CreateLogger("WrappedFailure");
            var file = new ForegroundDownloadLogScope(logger, "Test", "installer.jar", Path.Combine(TempRoot, "installer.jar"), url);
            using var context = file.RestoreContext();
            var executor = new MinecraftDownloadRequestExecutor(client, logger,
                retryOptions: new DownloadRetryOptions { MaxFileAttemptsPerSource = 1 },
                hostConcurrencyController: new DownloadHostConcurrencyController());
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.DownloadFileAsync(url, DownloadSourcePreference.Official, "Forge", Path.Combine(TempRoot, "installer.jar"), null, null, CancellationToken.None));
            file.Fail(new ModpackImportException(ModpackImportFailureReason.HashMismatch, "wrapped", exception));
        }
        var terminal = ReadLogs().Split('\n').Single(line => line.Contains("Foreground file download failed."));
        Assert.Contains("HttpRequestCount=2", terminal);
        Assert.Contains("RetryCount=0", terminal);
        Assert.Contains("FallbackUsed=true", terminal);
    }

    [Fact]
    public async Task TruncatedBodyRecordsExpectedAndActualLengthBeforeCleanup()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("body") })))
        {
            var logger = factory.CreateLogger("LengthTest");
            var executor = new MinecraftDownloadRequestExecutor(client, logger,
                retryOptions: new DownloadRetryOptions { MaxFileAttemptsPerSource = 1 });
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.DownloadFileAsync("https://example.test/file", DownloadSourcePreference.Official, "ThirdParty",
                    Path.Combine(TempRoot, "truncated.jar"), null, 999, CancellationToken.None));
            logger.LogError(exception, "Download terminal failure.");
        }
        var output = ReadLogs();
        Assert.Contains("ExpectedBytes=999", output);
        Assert.Contains("ActualBytes=4", output);
        Assert.Contains("ResponseBodyLength", output);
    }

    [Theory]
    [InlineData(false, "FirstByteTimeout")]
    [InlineData(true, "BodyIdleTimeout")]
    public async Task BodyTimeoutLogsDistinguishNoFirstByteFromStalledTransfer(bool firstByte, string expected)
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(new StalledStream(firstByte)) })))
        {
            var logger = factory.CreateLogger("BodyTimeoutTest");
            var executor = new MinecraftDownloadRequestExecutor(client, logger, retryOptions: new DownloadRetryOptions
            {
                MaxFileAttemptsPerSource = 1, FirstByteTimeout = TimeSpan.FromMilliseconds(30),
                BodyIdleTimeout = TimeSpan.FromMilliseconds(30)
            });
            var exception = await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() =>
                executor.DownloadFileAsync("https://example.test/file", DownloadSourcePreference.Official, "ThirdParty",
                    Path.Combine(TempRoot, "stalled.jar"), null, null, CancellationToken.None));
            logger.LogError(exception, "Transfer failed.");
        }
        var output = ReadLogs();
        Assert.Contains(expected, output);
        Assert.Contains(firstByte ? "DownloadedBytes=1" : "DownloadedBytes=0", output);
        Assert.Contains("StatusCode=200", output);
        Assert.Contains("ResponseHeadersDuration=", output);
        Assert.Contains("ResponseBodyDuration=", output);
    }

    [Theory]
    [InlineData(112, "DiskFull")]
    [InlineData(5, "AccessDenied")]
    [InlineData(32, "SharingViolation")]
    public void LocalFileSystemFailureHasActionableCodeWithoutRawMessage(int nativeCode, string expected)
    {
        using (var sink = CreateMainLogger())
            sink.Error(new IOException("unlabelled-filesystem-secret", unchecked((int)0x80070000) | nativeCode), "File publication failed.");
        var output = ReadLogs();
        Assert.Contains($"FileSystemFailure={expected}", output);
        Assert.DoesNotContain("unlabelled-filesystem-secret", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPublicationConflictRetainsComparisonEvidenceAndExistingFile(bool sameLength)
    {
        Directory.CreateDirectory(TempRoot);
        var source = Path.Combine(TempRoot, "source.jar");
        var destination = Path.Combine(TempRoot, "existing.jar");
        var existing = sameLength ? "two" : "longer";
        await File.WriteAllTextAsync(source, "one");
        await File.WriteAllTextAsync(destination, existing);
        using (var sink = CreateMainLogger())
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => AtomicSharedFilePublisher.PublishCopyAsync(
                source, destination, null, CancellationToken.None, TempRoot));
            sink.Error(exception, "Shared content publication failed.");
        }
        Assert.Equal(existing, await File.ReadAllTextAsync(destination));
        var output = ReadLogs();
        Assert.Contains("DiagnosticCode=SharedPublicationConflict", output);
        Assert.Contains("existing.jar", output);
        Assert.Contains("ExpectedBytes=3", output);
        Assert.Contains($"ActualBytes={existing.Length}", output);
        Assert.Contains(sameLength ? Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(existing))) : "NotChecked", output);
    }

    [Fact]
    public async Task IndependentHttpTimeoutIsNotLoggedAsCallerCancellation()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { Timeout = TimeSpan.FromMilliseconds(30) })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadHttpDiagnostics.GetBytesAsync(client,
                factory.CreateLogger("IndependentTimeout"), "https://example.test/image", CancellationToken.None));
        }
        var output = ReadLogs();
        Assert.Contains("Independent download failed.", output);
        Assert.Contains("CancellationOrigin=InternalTimeout", output);
        Assert.DoesNotContain("CancellationOrigin=CallerCancellation", output);
    }

    [Fact]
    public void LargeOrInvalidDiagnosticFieldsCannotEraseFailureIdentityAndExitCode()
    {
        using (var sink = CreateMainLogger())
        {
            sink.Error(new InstallerProcessException(7, false, new string('x', 1024 * 1024), "stderr-summary"), "Installer failed.");
            sink.Error(new FaultyDiagnosticException(), "Diagnostic projection failed.");
        }
        var output = ReadLogs();
        Assert.Contains("DiagnosticCode=LoaderInstallerExit", output);
        Assert.Contains("ExitCode=7", output);
        Assert.Contains("tail truncated", output);
        Assert.Contains("stderr-summary", output);
        Assert.Contains("DiagnosticProjectionErrorType=System.InvalidOperationException", output);
        Assert.DoesNotContain("unlabelled-projection-secret", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAndDiagnosticLevelsKeepTerminalEvidenceButFilterRoutineDetails(bool diagnostic)
    {
        using (var sink = LauncherLogConfiguration.CreateLogger(
            new LoggingLevelSwitch(diagnostic ? LogEventLevel.Verbose : LogEventLevel.Information),
            new LoggingLevelSwitch(LogEventLevel.Warning), TempRoot))
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("content") })))
        {
            var logger = factory.CreateLogger("LogLevels");
            var path = Path.Combine(TempRoot, "file.jar");
            const string url = "https://example.test/file.jar";
            var file = new ForegroundDownloadLogScope(logger, "Test", "file.jar", path, url);
            using var scope = file.RestoreContext();
            var executor = new MinecraftDownloadRequestExecutor(client, logger,
                hostConcurrencyController: new DownloadHostConcurrencyController());
            var resolution = await executor.DownloadFileAsync(url, DownloadSourcePreference.Official, "ThirdParty",
                path, null, null, CancellationToken.None, reportTransferredBytes: file.ReportTransferredBytes);
            file.Complete(resolution);
            var capture = new InstallerProcessOutput(logger, 42);
            using var stream = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("installer line\n")));
            Assert.Equal("installer line", await capture.PumpAsync(stream, "stdout"));
        }
        var output = ReadLogs();
        Assert.Contains("Foreground file download completed.", output);
        foreach (var message in new[] { "Foreground file download started.", "Download HTTP request started.",
                     "Download HTTP sending.", "Download HTTP headers received.", "Loader installer output." })
            Assert.Equal(diagnostic, output.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void InstallerTailKeepsLastEvidenceWithinSerializedLimitAfterRedaction()
    {
        var output = string.Concat(Enumerable.Repeat("escaped\"文本\\\n", 3000)) + "\npassword=private-tail-secret\nlast-evidence";
        var failure = new InstallerProcessException(7, false, output, "stderr-evidence");
        var tail = Assert.IsType<string>(failure.DiagnosticFields["StdoutTail"]);
        Assert.True(Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(tail)) <= 12 * 1024);
        Assert.Contains("tail truncated", tail);
        Assert.Contains("last-evidence", tail);
        Assert.DoesNotContain("private-tail-secret", tail);
        var formatted = SafeExceptionLog.Format(failure);
        Assert.Contains("ExitCode=7", formatted);
        Assert.Contains("last-evidence", formatted);
        Assert.Contains("stderr-evidence", formatted);
        Assert.DoesNotContain("private-tail-secret", formatted);
    }

    [Theory]
    [InlineData(false, "InstallerArchiveInvalid")]
    [InlineData(true, "InstallerLibraryUnresolvable")]
    public async Task InvalidInstallerMetadataHasSafeSpecificReasonWithoutDumpingJson(bool zip, string code)
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "invalid-installer.jar");
        if (zip)
        {
            using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("install_profile.json").Open());
            await writer.WriteAsync("""{"libraries":[{"name":null,"private":"unlabelled-payload-secret"}]}""");
        }
        else await File.WriteAllTextAsync(path, "invalid zip");
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var client = new HttpClient())
        {
            var logger = factory.CreateLogger("InstallerPlanTest");
            var service = new LoaderInstallerArtifactService(client);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                DownloadDiagnostics.RunStageAsync(logger, "InstallerPlan", () => service.ReadPlanAsync(path, CancellationToken.None)));
            Assert.Contains(code, SafeExceptionLog.Format(exception));
        }
        var output = ReadLogs();
        Assert.Contains("Stage=InstallerPlan", output);
        Assert.Contains(code, output);
        Assert.DoesNotContain("unlabelled-payload-secret", output);
    }

    [Fact]
    public async Task CancellationCallbackFailureCannotReplacePrimaryDownloadFailure()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        {
            var logger = factory.CreateLogger("CancellationTest");
            var tracker = new ParallelDownloadFailureTracker(logger, CancellationToken.None,
                () => throw new InvalidOperationException("unlabelled-callback-secret"));
            var original = new IOException("unlabelled-original-secret");
            var thrown = await Assert.ThrowsAsync<IOException>(() => tracker.RunAsync<bool>("LoaderInstall",
                () => Task.FromException<bool>(original)));
            Assert.Same(original, thrown);
        }
        var output = ReadLogs();
        Assert.Contains("cancellation callback failed", output);
        Assert.DoesNotContain("unlabelled-callback-secret", output);
        Assert.DoesNotContain("unlabelled-original-secret", output);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("size")]
    [InlineData("hash")]
    public async Task PostInstallerValidationRetainsActualEvidence(string failure)
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "processor.jar");
        if (failure != "missing") await File.WriteAllTextAsync(path, "actual");
        var expected = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("expected")));
        var result = await MinecraftFileIntegrity.InspectAsync(path, expected, failure == "size" ? 999 : null,
            MinecraftFileVerification.Full);
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        {
            var logger = factory.CreateLogger("LoaderTest");
            logger.LogInformation("Java installer process completed. ExitCode=0");
            await Assert.ThrowsAsync<InvalidDataException>(() => DownloadDiagnostics.RunStageAsync(logger,
                "ArtifactValidation", () => Task.FromException(result.CreateException(path, "ProcessorOutput"))));
        }
        var output = ReadLogs();
        Assert.Contains("ExitCode=0", output);
        Assert.Contains("Stage=ArtifactValidation", output);
        Assert.Contains("processor.jar", output);
        Assert.Contains("ExpectedHash", output);
        Assert.Contains("ActualHash", output);
        Assert.Contains(failure switch { "missing" => "Missing", "size" => "SizeMismatch", _ => "HashMismatch" }, output);
    }

    [Fact]
    public async Task ProcessOutputIsStreamingBoundedAndRedactedAcrossLines()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        {
            var logger = factory.CreateLogger("InstallerTest");
            var capture = new InstallerProcessOutput(logger, 42);
            var text = "password=\"first\nsecret-suffix\" healthy\n" +
                ForgeInstallerRunner.InstallClientUnrecognizedOptionMessage + "\n" + new string('x', 17 * 1024) + "\nignored";
            using var stream = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
            var tail = await capture.PumpAsync(stream, "stderr");
            Assert.True(capture.UnsupportedInstallClient);
            Assert.DoesNotContain("secret-suffix", tail);
        }
        var output = ReadLogs();
        Assert.Contains("Stream=stderr", output);
        Assert.Contains("output truncated", output);
        Assert.DoesNotContain("secret-suffix", output);
        Assert.DoesNotContain(new string('x', 100), output);
    }

    [Fact]
    public async Task InstallerInheritedJavaOptionsAreRedactedInMainLogAndFailureTail()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        {
            var runner = new ForgeInstallerRunner(startInfo =>
            {
                startInfo.Environment["JAVA_TOOL_OPTIONS"] = "-Dcustom=unlabelled-tool-secret";
                startInfo.Environment["_JAVA_OPTIONS"] = "-Dcustom=unlabelled-legacy-secret";
                startInfo.Environment["JDK_JAVA_OPTIONS"] = "-Dcustom=unlabelled-jdk-secret";
                return Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = "/d /c echo Picked up JAVA_TOOL_OPTIONS: -Dcustom=unlabelled-tool-secret & "
                        + "echo Picked up _JAVA_OPTIONS: -Dcustom=unlabelled-legacy-secret & "
                        + "echo NOTE: Picked up JDK_JAVA_OPTIONS: -Dcustom=unlabelled-jdk-secret & "
                        + "echo installer-diagnostic-preserved & exit /b 7",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                });
            }, factory.CreateLogger("InstallerInheritedOptions"));
            var failure = await Assert.ThrowsAsync<InstallerProcessException>(() => runner.RunInstallerAsync(
                Path.Combine(TempRoot, "java.exe"), "installer.jar", TempRoot, CancellationToken.None));
            Assert.DoesNotContain("unlabelled-", SafeExceptionLog.Format(failure));
        }
        var output = ReadLogs();
        Assert.DoesNotContain("unlabelled-", output);
        Assert.Contains("installer-diagnostic-preserved", output);
        Assert.Contains("ExitCode=7", output);
    }

    [Fact]
    public async Task ConcurrentOperationsAndQueuedWorkRetainIndependentContext()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> CaptureAsync(string branch)
        {
            using var operation = DownloadDiagnostics.BeginOperation(null, "Test");
            using var scope = DownloadDiagnostics.BeginBranch(null, branch);
            var context = DownloadDiagnostics.Capture();
            await ready.Task;
            return (string)context["OperationId"]!;
        }
        var first = CaptureAsync("one");
        var second = CaptureAsync("two");
        ready.SetResult();
        Assert.NotEqual(await first, await second);
        Assert.Empty(DownloadDiagnostics.Capture());
    }

    [Fact]
    public async Task InstallerNonzeroExitAndStartFailureRemainDistinguishable()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        {
            var logger = factory.CreateLogger("InstallerProcess");
            var runner = new ForgeInstallerRunner(_ => Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c echo controlled-installer-failure & exit /b 7",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }), logger);
            var failure = await Assert.ThrowsAsync<InstallerProcessException>(() => runner.RunInstallerAsync(
                Path.Combine(TempRoot, "java.exe"), "installer.jar", TempRoot, CancellationToken.None));
            Assert.Contains("7", SafeExceptionLog.Format(failure));
            var missing = new ForgeInstallerRunner(_ => throw new System.ComponentModel.Win32Exception(2), logger);
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.RunInstallerAsync(
                Path.Combine(TempRoot, "missing-java.exe"), "installer.jar", TempRoot, CancellationToken.None));
        }
        var output = ReadLogs();
        Assert.Contains("ExitCode=7", output);
        Assert.Contains("controlled-installer-failure", output);
        Assert.Contains("Stage=JavaProcessStart", output);
    }

    [Fact]
    public async Task PrimaryBranchFailureExplainsSiblingCancellation()
    {
        using (var sink = CreateMainLogger())
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var cancellation = new CancellationTokenSource())
        {
            var logger = factory.CreateLogger("ModpackImport");
            using var operation = DownloadDiagnostics.BeginOperation(logger, "ModpackImport");
            var tracker = new ParallelDownloadFailureTracker(logger, CancellationToken.None, cancellation.Cancel);
            var sibling = tracker.RunAsync("PackFiles", async () =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                return true;
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.RunAsync<bool>("LoaderInstall",
                () => Task.FromException<bool>(new InvalidOperationException("unlabelled-secret"))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sibling);
        }
        var output = ReadLogs();
        Assert.Contains("IsPrimaryFailure=true", output);
        Assert.Contains("CancellationOrigin=UpstreamFailure", output);
        Assert.Contains("PrimaryFailureId=", output);
        Assert.DoesNotContain("unlabelled-secret", output);
        var primary = System.Text.RegularExpressions.Regex.Match(output, @"PrimaryFailureId=([a-f0-9]+)").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(primary));
        Assert.Contains($"CauseFailureId={primary}", output);
    }

    [Fact]
    public void AggregateExceptionsAndRolledMainLogsRetainStructureWithoutRawMessages()
    {
        using (var sink = LauncherLogConfiguration.CreateLogger(new LoggingLevelSwitch(LogEventLevel.Information),
            new LoggingLevelSwitch(LogEventLevel.Warning), TempRoot, 1024))
        {
            for (var i = 0; i < 30; i++) sink.Information("Padding {Index} {Content}", i, new string('x', 100));
            sink.Error(new AggregateException(new IOException("private-first"), new InvalidOperationException("private-second")), "Terminal failure");
        }
        Assert.True(Directory.GetFiles(TempRoot, "bhl*.log").Length > 1);
        var output = ReadLogs();
        Assert.Contains("ExceptionBranch=root.0", output);
        Assert.Contains("ExceptionBranch=root.1", output);
        Assert.Contains("LogSession=", output);
        Assert.DoesNotContain("private-first", output);
        Assert.DoesNotContain("private-second", output);
    }

    private Serilog.Core.Logger CreateMainLogger() => LauncherLogConfiguration.CreateLogger(
        new LoggingLevelSwitch(LogEventLevel.Information), new LoggingLevelSwitch(LogEventLevel.Warning), TempRoot);
    private string ReadLogs() => string.Join("\n", Directory.GetFiles(TempRoot, "bhl*.log").Select(File.ReadAllText));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FaultyDiagnosticException : Exception, ISafeDiagnosticException
    {
        public string DiagnosticCode => "FaultyProjection";
        public IReadOnlyDictionary<string, object?> DiagnosticFields => throw new InvalidOperationException("unlabelled-projection-secret");
    }
    private sealed class StalledStream(bool emitFirstByte) : Stream
    {
        private bool emitted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (emitFirstByte && !emitted) { buffer.Span[0] = 1; emitted = true; return 1; }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
