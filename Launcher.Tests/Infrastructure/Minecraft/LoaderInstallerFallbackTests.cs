/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO.Compression;
using System.Net;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class LoaderInstallerFallbackTests
{
    private const string Url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.250/neoforge-21.1.250-installer.jar";

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
            var executor = new MinecraftDownloadRequestExecutor(client, retryOptions: new DownloadRetryOptions { MaxAttemptsPerSource = 1, MaxFileSourceRounds = 1 });
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
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("install_profile.json").Open()))
            writer.Write($"{{\"spec\":1,\"minecraft\":\"{minecraftVersion}\",\"processors\":[]}}");
        return stream.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
