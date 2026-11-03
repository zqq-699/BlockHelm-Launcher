/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Modpacks;

public sealed class ServerDeploymentTests : TestTempDirectory
{
    [Fact]
    public async Task TransactionCommitsAtomicallyAndRejectsExistingDirectory()
    {
        var parent = Path.Combine(TempRoot, "parent");
        Directory.CreateDirectory(parent);
        var service = new ServerDeploymentTransactionService();
        await using (var transaction = await service.BeginAsync(parent, "Pack"))
        {
            await File.WriteAllTextAsync(Path.Combine(transaction.StagingDirectory, "server.jar"), "server");
            await transaction.CommitAsync();
        }

        Assert.True(File.Exists(Path.Combine(parent, "Pack", "server.jar")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(parent, "Pack")),
            path => Path.GetFileName(path).StartsWith(
                ServerDeploymentTransactionService.MarkerFileNamePrefix,
                StringComparison.Ordinal));
        await Assert.ThrowsAsync<ServerDeploymentDirectoryExistsException>(() => service.BeginAsync(parent, "Pack"));
    }

    [Fact]
    public async Task TransactionCommitRacePreservesExistingFinalDirectory()
    {
        var parent = Path.Combine(TempRoot, "parent");
        Directory.CreateDirectory(parent);
        await using var transaction = await new ServerDeploymentTransactionService().BeginAsync(parent, "Pack");
        var finalDirectory = Path.Combine(parent, "Pack");
        Directory.CreateDirectory(finalDirectory);
        await File.WriteAllTextAsync(Path.Combine(finalDirectory, "owned-by-user.txt"), "keep");

        await Assert.ThrowsAsync<ServerDeploymentDirectoryExistsException>(() => transaction.CommitAsync());
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(finalDirectory, "owned-by-user.txt")));
        Assert.True(Directory.Exists(transaction.StagingDirectory));

        await transaction.AbortAsync();
        Assert.False(Directory.Exists(transaction.StagingDirectory));
        Assert.True(Directory.Exists(finalDirectory));
    }

    [Fact]
    public async Task CurseForgeExtractorRejectsPathTraversal()
    {
        Directory.CreateDirectory(TempRoot);
        var archivePath = Path.Combine(TempRoot, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            AddEntry(archive, "../outside.txt", "bad");
        var target = Path.Combine(TempRoot, "target");
        Directory.CreateDirectory(target);

        await Assert.ThrowsAsync<ModpackImportException>(() =>
            new CurseForgeServerPackExtractor().ExtractAsync(archivePath, target));

        Assert.False(File.Exists(Path.Combine(TempRoot, "outside.txt")));
    }

    [Fact]
    public async Task ForgeLikeRuntimeRejectsSuccessfulInstallerWithMissingServerProcessorOutput()
    {
        var target = Path.Combine(TempRoot, "forge-missing-output");
        var installerBytes = CreateForgeServerInstaller();
        var handler = new ForgeServerRuntimeHandler(installerBytes);
        var runner = new ScriptedForgeServerInstallerRunner(directory =>
            WriteModernForgeServerEntrypoint(directory, writeProcessorOutput: false));
        var installer = new ServerRuntimeInstaller(
            new HttpClient(handler),
            new FixedSettingsService(),
            new FixedLoaderJavaRuntimeResolver(),
            runner);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(
            CreateForgeServerModpack(),
            target));

        Assert.Contains("processor output", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(runner.WasRun);
        Assert.Contains(handler.RequestedUrls, url => url.EndsWith("-installer.jar.sha1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForgeLikeRuntimeInstallsValidArchiveWithoutChecksumMetadata()
    {
        var target = Path.Combine(TempRoot, "forge-missing-checksum");
        var installerBytes = CreateForgeServerInstaller();
        var handler = new ForgeServerRuntimeHandler(installerBytes, checksumUnavailable: true);
        var runner = new ScriptedForgeServerInstallerRunner(directory =>
            WriteModernForgeServerEntrypoint(directory, writeProcessorOutput: true));
        var installer = new ServerRuntimeInstaller(
            new HttpClient(handler),
            new FixedSettingsService(),
            new FixedLoaderJavaRuntimeResolver(),
            runner);

        await installer.InstallAsync(CreateForgeServerModpack(), target);

        Assert.True(runner.WasRun);
        Assert.Contains(
            handler.RequestedUrls,
            url => url.EndsWith("-installer.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForgeLikeRuntimeDoesNotExecuteInstallerWhenChecksumDoesNotMatch()
    {
        var target = Path.Combine(TempRoot, "forge-mismatched-checksum");
        var installerBytes = CreateForgeServerInstaller();
        var handler = new ForgeServerRuntimeHandler(
            installerBytes,
            checksumOverride: "0000000000000000000000000000000000000000");
        var runner = new ScriptedForgeServerInstallerRunner(directory =>
            WriteModernForgeServerEntrypoint(directory, writeProcessorOutput: true));
        var installer = new ServerRuntimeInstaller(
            new HttpClient(handler),
            new FixedSettingsService(),
            new FixedLoaderJavaRuntimeResolver(),
            runner);

        await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(
            CreateForgeServerModpack(),
            target));

        Assert.False(runner.WasRun);
        Assert.Contains(
            handler.RequestedUrls,
            url => url.EndsWith("-installer.jar", StringComparison.Ordinal));
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static PreparedModpack CreateForgeServerModpack() => new()
    {
        Environment = ModpackInstallEnvironment.Server,
        MinecraftVersion = "1.20.1",
        Loader = LoaderKind.Forge,
        LoaderVersion = "1.20.1-47.2.0"
    };

    private static byte[] CreateForgeServerInstaller()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "install_profile.json", """
                {
                  "spec": 1,
                  "minecraft": "1.20.1",
                  "data": {
                    "OUTPUT": {
                      "client": "[com.example:client-output:1.0]",
                      "server": "[com.example:server-output:1.0]"
                    }
                  },
                  "processors": [
                    {
                      "sides": ["server"],
                      "jar": "com.example:processor:1.0",
                      "args": ["--output", "{OUTPUT}"]
                    }
                  ]
                }
                """);
            AddEntry(archive, "version.json", """{ "libraries": [] }""");
            AddEntry(archive, "maven/com/example/processor/1.0/processor-1.0.jar", "processor");
        }
        return stream.ToArray();
    }

    private static void WriteModernForgeServerEntrypoint(
        string targetDirectory,
        bool writeProcessorOutput,
        bool writeBundledLibrary = true)
    {
        var argsDirectory = Path.Combine(
            targetDirectory,
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            "1.20.1-47.2.0");
        Directory.CreateDirectory(argsDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "user_jvm_args.txt"), string.Empty);
        const string bundledLibrary = "libraries/com/example/bootstrap/1.0/bootstrap-1.0.jar";
        File.WriteAllText(Path.Combine(argsDirectory, "win_args.txt"), $"-p {bundledLibrary}");
        File.WriteAllText(Path.Combine(argsDirectory, "unix_args.txt"), $"-p {bundledLibrary}");
        File.WriteAllText(
            Path.Combine(targetDirectory, "run.bat"),
            "@echo off\r\njava @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.2.0/win_args.txt %*");
        File.WriteAllText(
            Path.Combine(targetDirectory, "run.sh"),
            "java @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.2.0/unix_args.txt \"$@\"");

        if (writeBundledLibrary)
            WriteTestJar(Path.Combine(targetDirectory, bundledLibrary.Replace('/', Path.DirectorySeparatorChar)));

        if (!writeProcessorOutput)
            return;
        var output = Path.Combine(
            targetDirectory,
            "libraries",
            "com",
            "example",
            "server-output",
            "1.0",
            "server-output-1.0.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "generated");
    }

    private static void WriteTestJar(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n");
    }

    private sealed class FixedSettingsService : ISettingsService
    {
        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherSettings
            {
                DownloadSourcePreference = DownloadSourcePreference.Official,
                DownloadSpeedLimitMbPerSecond = 0
            });

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ServerRuntimeHandler(
        byte[] serverBytes,
        string sha1,
        string version = "1.20.1",
        string releaseTime = "2023-06-12T13:25:51+00:00") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            HttpContent content = url switch
            {
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json" => new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        versions = new[]
                        {
                            new
                            {
                                id = version,
                                url = $"https://piston-meta.mojang.com/v1/packages/test/{version}.json",
                                releaseTime
                            }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json"),
                var metadataUrl when metadataUrl == $"https://piston-meta.mojang.com/v1/packages/test/{version}.json" => new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        releaseTime,
                        downloads = new
                        {
                            server = new
                            {
                                url = "https://piston-data.mojang.com/v1/objects/test/server.jar",
                                sha1,
                                size = serverBytes.Length
                            }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json"),
                "https://piston-data.mojang.com/v1/objects/test/server.jar" => new ByteArrayContent(serverBytes),
                _ => throw new InvalidOperationException($"Unexpected request: {url}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }

    private sealed class FixedLoaderJavaRuntimeResolver : ILoaderInstallerJavaRuntimeResolver
    {
        public Task<JavaRuntimeInfo> ResolveAsync(
            LoaderInstallerJavaRuntimeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new JavaRuntimeInfo(
                "Test Java",
                "17.0.1",
                17,
                "x64",
                "C:\\Java\\bin\\java.exe",
                "C:\\Java",
                "Test"));
    }

    private sealed class ScriptedForgeServerInstallerRunner(Action<string> install) : IForgeInstallerRunner
    {
        public bool WasRun { get; private set; }

        public Task RunInstallerAsync(
            string javaExecutablePath,
            string installerJarPath,
            string minecraftDirectory,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RunServerInstallerAsync(
            string javaExecutablePath,
            string installerJarPath,
            string serverDirectory,
            CancellationToken cancellationToken)
        {
            WasRun = true;
            install(serverDirectory);
            return Task.CompletedTask;
        }
    }

    private sealed class ForgeServerRuntimeHandler(
        byte[] installerBytes,
        string? checksumOverride = null,
        bool checksumUnavailable = false) : HttpMessageHandler
    {
        private static readonly byte[] ServerBytes = Encoding.UTF8.GetBytes("server-jar");
        private readonly string installerSha1 = Convert.ToHexString(SHA1.HashData(installerBytes)).ToLowerInvariant();
        private readonly string serverSha1 = Convert.ToHexString(SHA1.HashData(ServerBytes)).ToLowerInvariant();

        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            RequestedUrls.Add(url);
            if (checksumUnavailable
                && url.EndsWith("-installer.jar.sha1", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            HttpContent content = url switch
            {
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json" => new StringContent(
                    """{"versions":[{"id":"1.20.1","url":"https://piston-meta.mojang.com/v1/packages/test/1.20.1.json"}]}""",
                    Encoding.UTF8,
                    "application/json"),
                "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json" => new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        releaseTime = "2023-06-12T13:25:51+00:00",
                        downloads = new
                        {
                            server = new
                            {
                                url = "https://piston-data.mojang.com/v1/objects/test/server.jar",
                                sha1 = serverSha1,
                                size = ServerBytes.Length
                            }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json"),
                "https://piston-data.mojang.com/v1/objects/test/server.jar" => new ByteArrayContent(ServerBytes),
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar.sha1" => new StringContent(checksumOverride ?? installerSha1),
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar" => new ByteArrayContent(installerBytes),
                _ => throw new InvalidOperationException($"Unexpected request: {url}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }
}
