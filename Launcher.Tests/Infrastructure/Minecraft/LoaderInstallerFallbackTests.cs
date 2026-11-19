/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class LoaderInstallerFallbackTests
{
    private const string Url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.250/neoforge-21.1.250-installer.jar";
    private const string ValidSha1 = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task ChecksumBudgetIncludesPendingOfficialRequestAfterMirror404()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "maven.neoforged.net")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var logger = new RecordingLogger();
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, logger,
            Url, DownloadSourcePreference.Auto, "NeoForge", 0, CancellationToken.None,
            new DownloadHostConcurrencyController()).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Null(checksum);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(LogLevel.Warning, logger.Levels);
        Assert.DoesNotContain(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task MissingMirrorChecksumDoesNotDiscardTheStillPendingOfficialLookup()
    {
        var requests = new ConcurrentQueue<Uri>();
        var mirrorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            requests.Enqueue(request.RequestUri!);
            if (request.RequestUri!.Host == "bmclapi2.bangbang93.com")
            {
                mirrorStarted.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            await mirrorStarted.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidSha1) };
        }));
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, NullLogger.Instance,
            Url, DownloadSourcePreference.BmclApi, "NeoForge", 0, CancellationToken.None,
            new DownloadHostConcurrencyController()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ValidSha1, checksum);
        Assert.Equal(new[] { "maven.neoforged.net", "bmclapi2.bangbang93.com" }, requests.Select(uri => uri.Host));
    }

    [Fact]
    public async Task ValidMirrorChecksumWinsWithoutWaitingForOfficialTimeoutAndCancelsIt()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "maven.neoforged.net")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidSha1) };
        }));
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, NullLogger.Instance,
            Url, DownloadSourcePreference.Official, "NeoForge", 0, CancellationToken.None,
            new DownloadHostConcurrencyController()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ValidSha1, checksum);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MissingOfficialChecksumTriesMirrorWithoutReportingAnError()
    {
        var requests = new List<Uri>();
        var logger = new RecordingLogger();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.Host == "maven.neoforged.net"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidSha1) };
        }));
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, logger,
            Url, DownloadSourcePreference.BmclApi, "NeoForge", 0, CancellationToken.None);
        Assert.Equal(ValidSha1, checksum);
        Assert.Equal(new[] { "maven.neoforged.net", "bmclapi2.bangbang93.com" }, requests.Select(uri => uri.Host));
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ChecksumsAbsentFromBothSourcesAreAnOptionalMetadataMiss()
    {
        var logger = new RecordingLogger();
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, logger,
            Url, DownloadSourcePreference.Official, "NeoForge", 0, CancellationToken.None);
        Assert.Null(checksum);
        Assert.Contains(LogLevel.Information, logger.Levels);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("invalid checksum", null)]
    public async Task ChecksumIsUsedOnlyWhenValid(string response, string? expected)
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        }));
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, NullLogger.Instance,
            Url, DownloadSourcePreference.Official, "NeoForge", 0, CancellationToken.None);
        Assert.Equal(expected, checksum);
    }

    [Fact]
    public async Task MissingChecksumFallsBackAndRejectsBadArchiveBeforeTryingNextSource()
    {
        var goodArchive = CreateArchive("1.21.1");
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha1"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri.Host == "maven.neoforged.net" ? [1, 2, 3] : goodArchive)
            };
        }));
        var checksum = await LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(client, null, NullLogger.Instance,
            Url, DownloadSourcePreference.Official, "NeoForge", 0, CancellationToken.None);
        Assert.Null(checksum);
        var directory = Path.Combine(Path.GetTempPath(), "bhl-loader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "installer.jar");
            var executor = new MinecraftDownloadRequestExecutor(client, retryOptions: new DownloadRetryOptions { MaxAttemptsPerSource = 1, MaxFileAttemptsPerSource = 1 });
            var result = await executor.DownloadFileAsync(Url, DownloadSourcePreference.Official, "NeoForge", destination,
                checksum, null, CancellationToken.None,
                options: new DownloadFileOptions(ValidateUnverifiedFile: (path, token) => LoaderInstallerFallbackValidator.ValidateAsync(path, "1.21.1", token)));
            Assert.Contains("bmclapi", result.ActualUrl);
            Assert.Equal(goodArchive, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UserCancellationDoesNotBecomeChecksumFallback()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LoaderInstallerChecksumResolver.ResolveOptionalSha1Async(
            client, null, NullLogger.Instance, Url, DownloadSourcePreference.Official, "NeoForge", 0, canceled.Token));
    }

    [Fact]
    public async Task WrongMinecraftVersionIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, CreateArchive("1.20.1"));
            await Assert.ThrowsAsync<DownloadContentValidationException>(() => LoaderInstallerFallbackValidator.ValidateAsync(path, "1.21.1", CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    private static byte[] CreateArchive(string minecraftVersion)
        => CreateProfileArchive($"{{\"spec\":1,\"minecraft\":\"{minecraftVersion}\",\"processors\":[]}}");

    [Theory]
    [InlineData("{\"install\":{\"path\":\"net.minecraftforge:forge:1.7.10-10.13.4.1614\"},\"versionInfo\":{}}", true)]
    [InlineData("{\"install\":{\"path\":\"net.minecraftforge:forge:1.8-11.14.0\"},\"versionInfo\":{}}", false)]
    [InlineData("{\"install\":{\"minecraft\":\"1.7.10\"},\"versionInfo\":null}", false)]
    public async Task LegacyProfileUsesCoordinateWhenMinecraftFieldIsMissing(string profile, bool valid)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, CreateProfileArchive(profile));
            if (valid)
                await LoaderInstallerFallbackValidator.ValidateAsync(path, "1.7.10", CancellationToken.None);
            else
                await Assert.ThrowsAsync<DownloadContentValidationException>(() =>
                    LoaderInstallerFallbackValidator.ValidateAsync(path, "1.7.10", CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancellationDuringValidationPreservesDestinationAndCleansTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bhl-loader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "installer.jar");
            await File.WriteAllTextAsync(path, "existing");
            using var cancellation = new CancellationTokenSource();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("replacement") };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MinecraftDownloadFileWriter.WriteAsync(
                response, path, null, null, 1, null, cancellation.Token,
                validateFile: (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }));
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] CreateProfileArchive(string profile)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("install_profile.json").Open()))
            writer.Write(profile);
        return stream.ToArray();
    }

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Enqueue(logLevel);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await response(request, cancellationToken);
            result.RequestMessage = request;
            return result;
        }
    }
}
