/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class MinecraftInstallPathLayoutTests : TestTempDirectory
{
    [Fact]
    public void SplitLayoutKeepsVersionsPrivateAndSharedRuntimeOutsideSandbox()
    {
        var sandbox = Path.Combine(TempRoot, "sandbox", ".minecraft");
        var realMinecraft = Path.Combine(TempRoot, "real", ".minecraft");

        var layout = MinecraftInstallPathLayout.Create(sandbox, realMinecraft);

        Assert.Equal(Path.GetFullPath(Path.Combine(sandbox, "versions")), layout.Path.Versions);
        Assert.Equal(Path.GetFullPath(Path.Combine(realMinecraft, "libraries")), layout.Path.Library);
        Assert.Equal(Path.GetFullPath(Path.Combine(realMinecraft, "assets")), layout.Path.Assets);
        Assert.Equal(Path.GetFullPath(Path.Combine(realMinecraft, "resources")), layout.Path.Resource);
        Assert.Equal(Path.GetFullPath(Path.Combine(realMinecraft, "runtime")), layout.Path.Runtime);
        Assert.False(Directory.Exists(Path.Combine(sandbox, "libraries")));
        Assert.False(Directory.Exists(Path.Combine(sandbox, "assets", "objects")));
        Assert.False(Directory.Exists(Path.Combine(realMinecraft, "versions", "Test")));
    }

    [Fact]
    public async Task CmlLibDownloadCannotEscapeConfiguredInstallRoots()
    {
        var sandbox = Path.Combine(TempRoot, "sandbox", ".minecraft");
        var realMinecraft = Path.Combine(TempRoot, "real", ".minecraft");
        var layout = MinecraftInstallPathLayout.Create(sandbox, realMinecraft);
        var launcher = VanillaLoaderProvider.CreateLauncher(layout.Path, progress: null);
        var installer = Assert.IsType<DownloadSpeedTrackingGameInstaller>(launcher.GameInstaller);
        var escapedPath = Path.Combine(TempRoot, "escaped.jar");

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadGameFileAsync(
            new GameFile("escaped")
            {
                Path = escapedPath,
                Url = "https://example.invalid/escaped.jar"
            },
            progress: null,
            CancellationToken.None));

        Assert.False(File.Exists(escapedPath));
    }

    [Fact]
    public async Task AtomicPublisherReplacesDifferentContentOnlyAfterSourceHashIsVerified()
    {
        var source = CreateFile("source-index.json", "current");
        var destination = CreateFile("destination-index.json", "stale");
        var expectedSha1 = AtomicSharedFilePublisher.ComputeSha1(source);

        var result = await AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
            source,
            destination,
            expectedSha1,
            CancellationToken.None);

        Assert.Equal(SharedFilePublishDisposition.Replaced, result.Disposition);
        Assert.Equal("current", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".destination-index.json.*.tmp"));
    }

    [Fact]
    public async Task AtomicPublisherPreservesDestinationWhenReplacementSourceHashIsInvalid()
    {
        var source = CreateFile("source-index.json", "untrusted");
        var destination = CreateFile("destination-index.json", "stale");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AtomicSharedFilePublisher.PublishVerifiedReplacementAsync(
                source,
                destination,
                ComputeSha1("expected"),
                CancellationToken.None));

        Assert.Equal("stale", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".destination-index.json.*.tmp"));
    }

    [Fact]
    public async Task LoaderDeltaDoesNotTrustDerivedResourcesFromTamperedAssetIndex()
    {
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        const string assetContent = "current";
        var assetSha1 = ComputeSha1(assetContent);
        var tamperedIndex = CreateFile(
            Path.Combine("installer", ".minecraft", "assets", "indexes", "5.json"),
            $"{{\"map_to_resources\":true,\"objects\":{{\"minecraft/lang/en_us.json\":{{\"hash\":\"{assetSha1}\",\"size\":{assetContent.Length}}}}}}}");
        CreateFile(
            Path.Combine("installer", ".minecraft", "resources", "minecraft", "lang", "en_us.json"),
            assetContent);
        CreateFile(
            Path.Combine("installer", ".minecraft", "versions", "Test", "Test.json"),
            $"{{\"assetIndex\":{{\"id\":\"5\",\"sha1\":\"{ComputeSha1("trusted-index")}\",\"size\":13}}}}");
        var destination = Path.Combine(TempRoot, "published");
        var resourceDestination = CreateFile(
            Path.Combine("published", "resources", "minecraft", "lang", "en_us.json"),
            "stale!!");
        var snapshot = new LoaderInstallerWorkspaceSnapshot(
            workspace,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["assets/indexes/5.json"] = AtomicSharedFilePublisher.ComputeSha1(tamperedIndex)
            });

        await Assert.ThrowsAsync<IOException>(() =>
            new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
                snapshot,
                destination,
                CancellationToken.None));

        Assert.Equal("stale!!", await File.ReadAllTextAsync(resourceDestination));
    }

    [Fact]
    public async Task LoaderDeltaKeepsLibraryConflictsStrict()
    {
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        CreateFile(Path.Combine("installer", ".minecraft", "libraries", "example", "library.jar"), "new-library");
        var destination = Path.Combine(TempRoot, "published");
        var destinationLibrary = CreateFile(
            Path.Combine("published", "libraries", "example", "library.jar"),
            "old-library");

        await Assert.ThrowsAsync<IOException>(() =>
            new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
                EmptySnapshot(workspace),
                destination,
                CancellationToken.None));

        Assert.Equal("old-library", await File.ReadAllTextAsync(destinationLibrary));
    }

    [Theory]
    [InlineData("client")]
    public async Task LoaderDeltaAtomicallyReplacesDeclaredProcessorOutputEvenWhenSeeded(string side)
    {
        var relativePath = $"libraries/net/minecraft/{side}/1.16.5/{side}-1.16.5-srg.jar";
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        CreateFile(
            Path.Combine("installer", ".minecraft", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            "new-processor-output");
        var destination = Path.Combine(TempRoot, "published");
        var destinationOutput = CreateFile(
            Path.Combine("published", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            "old-processor-output");
        var snapshot = new LoaderInstallerWorkspaceSnapshot(
            workspace,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [relativePath] = ComputeSha1("old-processor-output")
            });

        await new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
            snapshot,
            destination,
            CancellationToken.None,
            replaceableFileExpectations: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [relativePath] = null
            });

        Assert.Equal("new-processor-output", await File.ReadAllTextAsync(destinationOutput));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(destinationOutput)!,
            $".{Path.GetFileName(destinationOutput)}.*.tmp"));
    }

    [Fact]
    public async Task LoaderDeltaPreservesExistingProcessorOutputWhenReplacementHashIsInvalid()
    {
        const string relativePath = "libraries/net/minecraft/client/1.16.5/client-1.16.5-srg.jar";
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        CreateFile(
            Path.Combine("installer", ".minecraft", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            "untrusted-processor-output");
        var destination = Path.Combine(TempRoot, "published");
        var destinationOutput = CreateFile(
            Path.Combine("published", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            "old-processor-output");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
                EmptySnapshot(workspace),
                destination,
                CancellationToken.None,
                replaceableFileExpectations: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [relativePath] = ComputeSha1("expected-processor-output")
                }));

        Assert.Equal("old-processor-output", await File.ReadAllTextAsync(destinationOutput));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(destinationOutput)!,
            $".{Path.GetFileName(destinationOutput)}.*.tmp"));
    }

    [Fact]
    public async Task LoaderDeltaPublicationRejectsJunctionWithoutReadingExternalFile()
    {
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var libraries = Path.Combine(workspace, "libraries");
        var external = Path.Combine(TempRoot, "external");
        Directory.CreateDirectory(libraries);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "outside.jar"), "outside");
        var junction = Path.Combine(libraries, "linked");
        CreateDirectoryJunction(junction, external);
        var snapshot = new LoaderInstallerWorkspaceSnapshot(
            workspace,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var destination = Path.Combine(TempRoot, "published");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
                    snapshot,
                    destination,
                    CancellationToken.None));

            Assert.False(File.Exists(Path.Combine(destination, "libraries", "linked", "outside.jar")));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction, recursive: false);
        }
    }

    [Fact]
    public async Task ForgePrerequisitePathTraversalIsIgnored()
    {
        const string minecraftVersion = "1.20.1";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json";
        var officialJson = $$"""{"id":"{{minecraftVersion}}","type":"release"}""";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var escapedSource = CreateFile(Path.Combine("shared", "escaped.jar"), "escaped");
        var installerJar = Path.Combine(TempRoot, "installer.jar");
        CreateInstallerArchive(installerJar, "../escaped.jar", AtomicSharedFilePublisher.ComputeSha1(escapedSource));
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"{{ComputeSha1(officialJson)}}"}]}""",
            metadataUrl,
            officialJson);

        await new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null).SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            installerJar,
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(workspace, "escaped.jar")));
        Assert.False(File.Exists(Path.Combine(TempRoot, "installer", "escaped.jar")));
    }

    [Fact]
    public async Task LoaderInstallerSeederDownloadsVerifiedOfficialMetadataWhenSharedVersionIsMissing()
    {
        const string minecraftVersion = "1.20.1";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var officialJson = $$"""{"id":"{{minecraftVersion}}","type":"release"}""";
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"{{ComputeSha1(officialJson)}}"}]}""",
            metadataUrl,
            officialJson);
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        var snapshot = await seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None);

        Assert.Equal(minecraftVersion, snapshot.VerifiedVersionMetadata!["id"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(shared, "versions", minecraftVersion)));
        Assert.Equal(
            minecraftVersion,
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
                workspace,
                "versions",
                minecraftVersion,
                $"{minecraftVersion}.json")))!["id"]!.GetValue<string>());
        Assert.Contains(
            handler.RequestUris,
            uri => uri.AbsolutePath == "/mc/game/version_manifest_v2.json");
        Assert.Contains(handler.RequestUris, uri => uri.AbsoluteUri == metadataUrl);
    }

    [Fact]
    public async Task LoaderInstallerSeederUsesVerifiedOfficialMetadataInsteadOfSameNameCustomInstance()
    {
        const string minecraftVersion = "26.2";
        const string officialMetadataUrl = "https://piston-meta.mojang.com/v1/packages/test/26.2.json";
        const string officialJarContent = "official jar";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var sharedVersionDirectory = Path.Combine(shared, "versions", minecraftVersion);
        Directory.CreateDirectory(sharedVersionDirectory);
        var sharedJsonPath = Path.Combine(sharedVersionDirectory, $"{minecraftVersion}.json");
        var customJson = """
            {
              "id": "26.2",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [
                { "name": "net.fabricmc:fabric-loader:0.16.14" }
              ]
            }
            """;
        await File.WriteAllTextAsync(sharedJsonPath, customJson);
        await File.WriteAllTextAsync(Path.Combine(sharedVersionDirectory, $"{minecraftVersion}.jar"), "custom jar");
        var originalBytes = await File.ReadAllBytesAsync(sharedJsonPath);
        var officialJson = $$"""
            {
              "id": "{{minecraftVersion}}",
              "mainClass": "net.minecraft.client.main.Main",
              "downloads": {
                "client": {
                  "sha1": "{{ComputeSha1(officialJarContent)}}",
                  "size": {{Encoding.UTF8.GetByteCount(officialJarContent)}}
                }
              }
            }
            """;
        var officialJsonSha1 = ComputeSha1(officialJson);
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{officialMetadataUrl}}","sha1":"{{officialJsonSha1}}"}]}""",
            officialMetadataUrl,
            officialJson);
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sharedJsonPath));
        var sandboxJson = await File.ReadAllTextAsync(
            Path.Combine(workspace, "versions", minecraftVersion, $"{minecraftVersion}.json"));
        Assert.Contains("net.minecraft.client.main.Main", sandboxJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fabric-loader", sandboxJson, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(workspace, "versions", minecraftVersion, $"{minecraftVersion}.jar")));
        Assert.Contains(handler.RequestUris, uri => uri.AbsoluteUri == officialMetadataUrl);
    }

    [Fact]
    public async Task LoaderInstallerSeederRejectsMetadataThatDoesNotMatchManifestSha1()
    {
        const string minecraftVersion = "26.2";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/26.2.json";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var sharedVersionDirectory = Path.Combine(shared, "versions", minecraftVersion);
        Directory.CreateDirectory(sharedVersionDirectory);
        var sharedJsonPath = Path.Combine(sharedVersionDirectory, $"{minecraftVersion}.json");
        await File.WriteAllTextAsync(
            sharedJsonPath,
            """{"id":"26.2","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient"}""");
        var originalBytes = await File.ReadAllBytesAsync(sharedJsonPath);
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"0000000000000000000000000000000000000000"}]}""",
            metadataUrl,
            """{"id":"26.2","mainClass":"net.minecraft.client.main.Main"}""");
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await Assert.ThrowsAnyAsync<Exception>(() => seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sharedJsonPath));
        Assert.False(File.Exists(Path.Combine(
            workspace,
            "versions",
            minecraftVersion,
            $"{minecraftVersion}.json")));
    }

    [Fact]
    public async Task LoaderInstallerSeederRejectsManifestWithoutMetadataSha1()
    {
        const string minecraftVersion = "26.2";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/26.2.json";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}"}]}""",
            metadataUrl,
            $$"""{"id":"{{minecraftVersion}}"}""");
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await Assert.ThrowsAnyAsync<Exception>(() => seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None));

        Assert.DoesNotContain(handler.RequestUris, uri => uri.AbsoluteUri == metadataUrl);
        Assert.False(Directory.Exists(Path.Combine(workspace, "versions", minecraftVersion)));
    }

    [Fact]
    public async Task LoaderInstallerSeederRejectsMetadataWithDifferentVersionId()
    {
        const string minecraftVersion = "26.2";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/26.2.json";
        const string wrongMetadata = """{"id":"fabric-loader-26.2"}""";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"{{ComputeSha1(wrongMetadata)}}"}]}""",
            metadataUrl,
            wrongMetadata);
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await Assert.ThrowsAnyAsync<Exception>(() => seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(workspace, "versions", minecraftVersion)));
    }

    [Fact]
    public async Task LoaderInstallerSeederDoesNotReuseClientJarWithoutOfficialHashAndSize()
    {
        const string minecraftVersion = "1.20.1";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        CreateFile(
            Path.Combine("shared", "versions", minecraftVersion, $"{minecraftVersion}.jar"),
            "unverified shared jar");
        var officialJson = $"{{\"id\":\"{minecraftVersion}\",\"downloads\":{{\"client\":{{}}}}}}";
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"{{ComputeSha1(officialJson)}}"}]}""",
            metadataUrl,
            officialJson);
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(
            workspace,
            "versions",
            minecraftVersion,
            $"{minecraftVersion}.jar")));
    }

    [Fact]
    public async Task LoaderInstallerSeederReusesClientJarOnlyWhenOfficialHashAndSizeMatch()
    {
        const string minecraftVersion = "1.20.1";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json";
        const string jarContent = "verified shared jar";
        var shared = Path.Combine(TempRoot, "shared");
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        CreateFile(
            Path.Combine("shared", "versions", minecraftVersion, $"{minecraftVersion}.jar"),
            jarContent);
        var officialJson = $$"""
            {
              "id":"{{minecraftVersion}}",
              "downloads":{
                "client":{
                  "sha1":"{{ComputeSha1(jarContent)}}",
                  "size":{{Encoding.UTF8.GetByteCount(jarContent)}}
                }
              }
            }
            """;
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"{{ComputeSha1(officialJson)}}"}]}""",
            metadataUrl,
            officialJson);
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await seeder.SeedAsync(
            shared,
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None);

        Assert.Equal(
            jarContent,
            await File.ReadAllTextAsync(Path.Combine(
                workspace,
                "versions",
                minecraftVersion,
                $"{minecraftVersion}.jar")));
    }

    [Fact]
    public async Task LoaderInstallerSeederPropagatesCancellationBeforeMetadataDownload()
    {
        const string minecraftVersion = "1.20.1";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json";
        var handler = new OfficialMetadataHandler(
            """{"versions":[]}""",
            metadataUrl,
            """{"id":"1.20.1"}""");
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seeder.SeedAsync(
            Path.Combine(TempRoot, "shared"),
            Path.Combine(TempRoot, "installer", ".minecraft"),
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            cancellation.Token));

        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task LoaderInstallerSeederFailsClosedWhenMetadataRequestFails()
    {
        const string minecraftVersion = "1.20.1";
        const string metadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.20.1.json";
        var officialJson = $$"""{"id":"{{minecraftVersion}}"}""";
        var handler = new OfficialMetadataHandler(
            $$"""{"versions":[{"id":"{{minecraftVersion}}","url":"{{metadataUrl}}","sha1":"{{ComputeSha1(officialJson)}}"}]}""",
            metadataUrl,
            officialJson,
            metadataStatusCode: HttpStatusCode.BadRequest);
        var workspace = Path.Combine(TempRoot, "installer", ".minecraft");
        var seeder = new LoaderInstallerPrerequisiteSeeder(new HttpClient(handler), null);

        await Assert.ThrowsAnyAsync<Exception>(() => seeder.SeedAsync(
            Path.Combine(TempRoot, "shared"),
            workspace,
            minecraftVersion,
            Path.Combine(TempRoot, "missing-installer.jar"),
            DownloadSourcePreference.Official,
            downloadSpeedLimitMbPerSecond: 0,
            CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(workspace, "versions", minecraftVersion)));
    }

    private string CreateFile(string relativePath, string content)
    {
        var path = Path.Combine(TempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static LoaderInstallerWorkspaceSnapshot EmptySnapshot(string workspace)
    {
        return new LoaderInstallerWorkspaceSnapshot(
            workspace,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath }
        }) ?? throw new InvalidOperationException("Failed to start junction creation process.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private static void CreateInstallerArchive(string path, string libraryPath, string sha1)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("install_profile.json");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write($$"""
        {
          "libraries": [
            {
              "name": "com.example:needed:1.0",
              "downloads": {
                "artifact": {
                  "path": "{{libraryPath}}",
                  "sha1": "{{sha1}}"
                }
              }
            }
          ]
        }
        """);
    }

    private static string ComputeSha1(string content)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private sealed class OfficialMetadataHandler(
        string manifestJson,
        string metadataUrl,
        string metadataJson,
        HttpStatusCode metadataStatusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            var content = request.RequestUri!.AbsolutePath switch
            {
                "/mc/game/version_manifest_v2.json" => manifestJson,
                var path when path == new Uri(metadataUrl).AbsolutePath => metadataJson,
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
            var statusCode = request.RequestUri.AbsolutePath == new Uri(metadataUrl).AbsolutePath
                ? metadataStatusCode
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(content)
            });
        }
    }

    private static object GetPrivateField(object instance, string fieldName) =>
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
        ?? throw new InvalidOperationException($"Expected private field '{fieldName}'.");

    private static void RaisePrivateEvent<T>(object instance, string fieldName, T args)
    {
        var handler = Assert.IsType<EventHandler<T>>(GetPrivateField(instance, fieldName));
        handler.Invoke(instance, args);
    }

}
