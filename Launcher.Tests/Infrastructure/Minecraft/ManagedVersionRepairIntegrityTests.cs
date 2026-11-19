/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class ManagedVersionRepairIntegrityTests : TestTempDirectory
{
    [Fact]
    public async Task RepairReplacesInvalidJarLibraryIndexLoggingAndWrongSizedAsset()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var expected = CreateExpectedFiles();
        var versionDirectory = CreateVersion(
            minecraftDirectory,
            expected,
            jarContent: SameLengthWrongContent(expected.Jar),
            libraryContent: SameLengthWrongContent(expected.Library),
            indexContent: SameLengthWrongContent(expected.Index),
            loggingContent: SameLengthWrongContent(expected.Logging),
            assetContent: "short");
        var handler = new ContentHandler(expected.Downloads);
        var service = new ManagedVersionRepairService(new HttpClient(handler));

        await service.RepairAsync(
            minecraftDirectory,
            "Test",
            versionDirectory,
            progress: null,
            allowRepair: true,
            CancellationToken.None,
            DownloadSourcePreference.Official);

        Assert.Equal(expected.Jar, await File.ReadAllTextAsync(Path.Combine(versionDirectory, "Test.jar")));
        Assert.Equal(expected.Library, await File.ReadAllTextAsync(Path.Combine(minecraftDirectory, "libraries", "example", "library.jar")));
        Assert.Equal(expected.Index, await File.ReadAllTextAsync(Path.Combine(minecraftDirectory, "assets", "indexes", "5.json")));
        Assert.Equal(expected.Logging, await File.ReadAllTextAsync(Path.Combine(minecraftDirectory, "assets", "log_configs", "client.xml")));
        Assert.Equal(expected.Asset, await File.ReadAllTextAsync(expected.AssetPath(minecraftDirectory)));
        Assert.Equal(expected.Downloads.Keys.Order(StringComparer.Ordinal), handler.Requests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DisabledRepairRejectsInvalidFileWithoutChangingIt()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var expected = CreateExpectedFiles();
        var invalidJar = SameLengthWrongContent(expected.Jar);
        var versionDirectory = CreateVersion(
            minecraftDirectory,
            expected,
            invalidJar,
            expected.Library,
            expected.Index,
            expected.Logging,
            expected.Asset);
        var handler = new ContentHandler(expected.Downloads);
        var service = new ManagedVersionRepairService(new HttpClient(handler));

        await Assert.ThrowsAsync<InstanceRepairException>(() => service.RepairAsync(
            minecraftDirectory,
            "Test",
            versionDirectory,
            progress: null,
            allowRepair: false,
            CancellationToken.None,
            DownloadSourcePreference.Official));

        Assert.Equal(invalidJar, await File.ReadAllTextAsync(Path.Combine(versionDirectory, "Test.jar")));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RepairResolvesLocalInheritanceWithoutRewritingChildJson()
    {
        const string jarContent = "inherited-client";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var parentDirectory = Path.Combine(minecraftDirectory, "versions", "26.2");
        var childDirectory = Path.Combine(minecraftDirectory, "versions", "fabric-child");
        WriteFile(Path.Combine(parentDirectory, "26.2.json"), new JsonObject
        {
            ["id"] = "26.2",
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["downloads"] = new JsonObject
            {
                ["client"] = DownloadMetadata("https://example.test/client.jar", jarContent)
            },
            ["libraries"] = new JsonArray()
        }.ToJsonString());
        WriteFile(Path.Combine(parentDirectory, "26.2.jar"), jarContent);
        WriteFile(Path.Combine(childDirectory, "fabric-child.jar"), jarContent);
        var childJsonPath = Path.Combine(childDirectory, "fabric-child.json");
        var childJson = """
            {
              "id": "fabric-child",
              "inheritsFrom": "26.2",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": []
            }
            """;
        WriteFile(childJsonPath, childJson);
        var originalBytes = await File.ReadAllBytesAsync(childJsonPath);
        var handler = new ContentHandler(new Dictionary<string, string>());
        var service = new ManagedVersionRepairService(new HttpClient(handler));

        await service.RepairAsync(
            minecraftDirectory,
            "fabric-child",
            childDirectory,
            progress: null,
            allowRepair: false,
            CancellationToken.None,
            DownloadSourcePreference.Official);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(childJsonPath));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ResolveRejectsLocalInheritanceCycleWithoutChangingMetadata()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var firstDirectory = Path.Combine(minecraftDirectory, "versions", "cycle-a");
        var secondDirectory = Path.Combine(minecraftDirectory, "versions", "cycle-b");
        var firstPath = Path.Combine(firstDirectory, "cycle-a.json");
        var secondPath = Path.Combine(secondDirectory, "cycle-b.json");
        WriteFile(firstPath, """{"id":"cycle-a","inheritsFrom":"cycle-b"}""");
        WriteFile(secondPath, """{"id":"cycle-b","inheritsFrom":"cycle-a"}""");
        var firstBytes = await File.ReadAllBytesAsync(firstPath);
        var secondBytes = await File.ReadAllBytesAsync(secondPath);
        var handler = new ContentHandler(new Dictionary<string, string>());
        var service = new ManagedVersionRepairService(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InstanceRepairException>(() =>
            service.ResolveVersionForRepairAsync(
                minecraftDirectory,
                "cycle-a",
                firstDirectory,
                DownloadSourcePreference.Official,
                CancellationToken.None,
                allowRemoteParentResolution: true));

        Assert.Contains("inheritance cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(secondPath));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ResolveRejectsInheritedMetadataPathTraversalWithoutReadingOutsideVersions()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", "unsafe-child");
        var jsonPath = Path.Combine(versionDirectory, "unsafe-child.json");
        WriteFile(jsonPath, """{"id":"unsafe-child","inheritsFrom":"..\\..\\outside"}""");
        var originalBytes = await File.ReadAllBytesAsync(jsonPath);
        var outsidePath = Path.Combine(minecraftDirectory, "outside", "..", "outside.json");
        WriteFile(Path.GetFullPath(outsidePath), """{"id":"outside"}""");
        var handler = new ContentHandler(new Dictionary<string, string>());
        var service = new ManagedVersionRepairService(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InstanceRepairException>(() =>
            service.ResolveVersionForRepairAsync(
                minecraftDirectory,
                "unsafe-child",
                versionDirectory,
                DownloadSourcePreference.Official,
                CancellationToken.None,
                allowRemoteParentResolution: true));

        Assert.Contains("path is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(jsonPath));
        Assert.Equal(0, handler.RequestCount);
    }

    private string CreateVersion(
        string minecraftDirectory,
        ExpectedFiles expected,
        string jarContent,
        string libraryContent,
        string indexContent,
        string loggingContent,
        string assetContent)
    {
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", "Test");
        WriteFile(Path.Combine(versionDirectory, "Test.jar"), jarContent);
        WriteFile(Path.Combine(minecraftDirectory, "libraries", "example", "library.jar"), libraryContent);
        WriteFile(Path.Combine(minecraftDirectory, "assets", "indexes", "5.json"), indexContent);
        WriteFile(Path.Combine(minecraftDirectory, "assets", "log_configs", "client.xml"), loggingContent);
        WriteFile(expected.AssetPath(minecraftDirectory), assetContent);

        var versionJson = new JsonObject
        {
            ["id"] = "Test",
            ["downloads"] = new JsonObject
            {
                ["client"] = DownloadMetadata("https://example.test/client.jar", expected.Jar)
            },
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "example:library:1.0",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = "example/library.jar",
                            ["url"] = "https://example.test/library.jar",
                            ["sha1"] = ComputeSha1(expected.Library),
                            ["size"] = Encoding.UTF8.GetByteCount(expected.Library)
                        }
                    }
                }
            },
            ["assetIndex"] = new JsonObject
            {
                ["id"] = "5",
                ["url"] = "https://example.test/5.json",
                ["sha1"] = ComputeSha1(expected.Index),
                ["size"] = Encoding.UTF8.GetByteCount(expected.Index)
            },
            ["logging"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["id"] = "client.xml",
                        ["url"] = "https://example.test/client.xml",
                        ["sha1"] = ComputeSha1(expected.Logging),
                        ["size"] = Encoding.UTF8.GetByteCount(expected.Logging)
                    }
                }
            }
        };
        WriteFile(Path.Combine(versionDirectory, "Test.json"), versionJson.ToJsonString());
        return versionDirectory;
    }

    private static JsonObject DownloadMetadata(string url, string content)
    {
        return new JsonObject
        {
            ["url"] = url,
            ["sha1"] = ComputeSha1(content),
            ["size"] = Encoding.UTF8.GetByteCount(content)
        };
    }

    private static ExpectedFiles CreateExpectedFiles()
    {
        const string jar = "client-new";
        const string library = "library-new";
        const string logging = "logging-new";
        const string asset = "asset-new";
        var assetSha1 = ComputeSha1(asset);
        var index = $"{{\"objects\":{{\"minecraft/lang/en_us.json\":{{\"hash\":\"{assetSha1}\",\"size\":{Encoding.UTF8.GetByteCount(asset)}}}}}}}";
        return new ExpectedFiles(
            jar,
            library,
            index,
            logging,
            asset,
            assetSha1,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://example.test/client.jar"] = jar,
                ["https://example.test/library.jar"] = library,
                ["https://example.test/5.json"] = index,
                ["https://example.test/client.xml"] = logging,
                [$"https://resources.download.minecraft.net/{assetSha1[..2]}/{assetSha1}"] = asset
            });
    }

    private static string SameLengthWrongContent(string content)
    {
        var replacement = new string('x', content.Length);
        return string.Equals(replacement, content, StringComparison.Ordinal)
            ? new string('y', content.Length)
            : replacement;
    }

    private static string ComputeSha1(string content)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed record ExpectedFiles(
        string Jar,
        string Library,
        string Index,
        string Logging,
        string Asset,
        string AssetSha1,
        IReadOnlyDictionary<string, string> Downloads)
    {
        public string AssetPath(string minecraftDirectory)
        {
            return Path.Combine(minecraftDirectory, "assets", "objects", AssetSha1[..2], AssetSha1);
        }
    }

    private sealed class ContentHandler(IReadOnlyDictionary<string, string> content) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public int RequestCount => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Requests.Enqueue(url);
            if (!content.TryGetValue(url, out var responseContent))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(responseContent))
            });
        }
    }

}
