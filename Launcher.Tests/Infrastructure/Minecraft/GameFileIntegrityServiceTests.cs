/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Tests.Helpers;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class GameFileIntegrityServiceTests : TestTempDirectory
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReferencedLocalParentWithDifferentJsonNameUsesItsActualClient(int referenceKind)
    {
        const string client = "parent client";
        var root = Path.Combine(TempRoot, ".minecraft");
        var parent = Path.Combine(root, "versions", "Parent");
        var child = Path.Combine(root, "versions", "Child");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(child);
        var json = JsonNode.Parse("""{"id":"A","jar":"A","mainClass":"example.Main","libraries":[],"arguments":{"game":[],"jvm":["-cp","${classpath}"]}}""")!.AsObject();
        json["downloads"] = new JsonObject { ["client"] = new JsonObject
        {
            ["url"] = "https://example.test/client.jar", ["sha1"] = Sha1(client), ["size"] = client.Length
        } };
        await File.WriteAllTextAsync(Path.Combine(parent, "A.json"), json.ToJsonString());
        var jar = Path.Combine(parent, "A.jar");
        await File.WriteAllTextAsync(jar, referenceKind == 2 ? "broken" : client);
        var childJson = JsonNode.Parse("""{"id":"Child","inheritsFrom":"Parent","mainClass":"example.Child","libraries":[],"arguments":{"game":[],"jvm":["-cp","${classpath}"]}}""")!.AsObject();
        if (referenceKind != 0) childJson["jar"] = "Parent";
        if (referenceKind == 2) childJson.Remove("inheritsFrom");
        await File.WriteAllTextAsync(Path.Combine(child, "Child.json"), childJson.ToJsonString());
        var handler = new ContentHandler(new Dictionary<string, string> { ["https://example.test/client.jar"] = client });
        var service = new GameFileIntegrityService(new HttpClient(handler), downloadSpeedLimitState: null);
        var request = new GameFileIntegrityRequest(root, "Child", child);
        var repaired = await service.ValidateAndRepairAsync(request, new GameFileRepairOptions(AllowRepair: true));
        Assert.True(repaired.LaunchAllowed, string.Join("; ", repaired.Failures.Select(f => f.Source)));
        using var process = await VanillaLoaderProvider.CreateLauncher(root, progress: null).BuildProcessAsync("Child",
            new CmlLib.Core.ProcessBuilder.MLaunchOption { JavaPath = Environment.ProcessPath! });
        var final = await service.ValidateFinalLaunchCommandAsync(request, process.StartInfo);
        Assert.True(final.LaunchAllowed, string.Join("; ", final.Failures.Select(f => f.TargetPath + ": " + f.Source)));
        Assert.Contains(jar, process.StartInfo.Arguments);
        Assert.Equal(client, await File.ReadAllTextAsync(jar));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopiedInstanceRepairAndFinalCommandValidateTheActualClientJar(
        bool hasFolderNamedJar, bool corruptOriginalJar)
    {
        const string originalName = "26.3";
        const string copiedName = "26.3 - Copy";
        const string client = "valid client jar";
        const string clientUrl = "https://example.test/client.jar";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", copiedName);
        Directory.CreateDirectory(versionDirectory);
        var jsonPath = Path.Combine(versionDirectory, originalName + ".json");
        var json = new JsonObject
        {
            ["id"] = originalName,
            ["jar"] = originalName,
            ["mainClass"] = "example.Main",
            ["libraries"] = new JsonArray(),
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray(),
                ["jvm"] = new JsonArray("-cp", "${classpath}")
            },
            ["downloads"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["url"] = clientUrl,
                    ["sha1"] = Sha1(client),
                    ["size"] = Encoding.UTF8.GetByteCount(client)
                }
            }
        };
        await File.WriteAllTextAsync(jsonPath, json.ToJsonString());
        var originalJson = await File.ReadAllBytesAsync(jsonPath);
        var actualJar = Path.Combine(versionDirectory, originalName + ".jar");
        var folderNamedJar = Path.Combine(versionDirectory, copiedName + ".jar");
        await File.WriteAllTextAsync(actualJar, corruptOriginalJar ? "broken" : client);
        if (hasFolderNamedJar) await File.WriteAllTextAsync(folderNamedJar, client);
        var service = new GameFileIntegrityService(
            new HttpClient(new ContentHandler(new Dictionary<string, string> { [clientUrl] = client })),
            downloadSpeedLimitState: null);
        var request = new GameFileIntegrityRequest(minecraftDirectory, copiedName, versionDirectory);

        var repaired = await service.ValidateAndRepairAsync(request, new GameFileRepairOptions(AllowRepair: true));
        Assert.True(repaired.LaunchAllowed);
        Assert.Equal(client, await File.ReadAllTextAsync(actualJar));
        Assert.Equal(hasFolderNamedJar, File.Exists(folderNamedJar));

        var launcher = VanillaLoaderProvider.CreateLauncher(minecraftDirectory, progress: null);
        using var process = await launcher.BuildProcessAsync(copiedName,
            new CmlLib.Core.ProcessBuilder.MLaunchOption
            {
                JavaPath = Environment.ProcessPath!,
                Path = new InstanceMinecraftPath(versionDirectory)
                {
                    Versions = Path.Combine(minecraftDirectory, "versions"),
                    Library = Path.Combine(minecraftDirectory, "libraries"),
                    Assets = Path.Combine(minecraftDirectory, "assets")
                }
            });
        var finalValidation = await service.ValidateFinalLaunchCommandAsync(request, process.StartInfo);

        Assert.True(finalValidation.LaunchAllowed, string.Join("; ", finalValidation.Failures.Select(f => f.TargetPath + ": " + f.Source)));
        Assert.Contains(actualJar, process.StartInfo.Arguments);
        Assert.Equal(originalJson, await File.ReadAllBytesAsync(jsonPath));
    }

    [Fact]
    public async Task ManifestResolutionUsesMissingOfficialParentInMemoryWithoutWritingIt()
    {
        const string versionName = "26.2";
        const string parentVersion = "1.21.8";
        const string parentMetadataUrl = "https://piston-meta.mojang.com/v1/packages/test/1.21.8.json";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        Directory.CreateDirectory(versionDirectory);
        var childJson = """
            {
              "id": "26.2",
              "inheritsFrom": "1.21.8",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [
                { "name": "net.fabricmc:fabric-loader:0.16.14" }
              ]
            }
            """;
        var childJsonPath = Path.Combine(versionDirectory, $"{versionName}.json");
        await File.WriteAllTextAsync(childJsonPath, childJson);
        var originalBytes = await File.ReadAllBytesAsync(childJsonPath);
        var parentJson = """
            {
              "id": "1.21.8",
              "mainClass": "net.minecraft.client.main.Main",
              "libraries": [
                { "name": "com.mojang:patchy:2.2.10" }
              ]
            }
            """;
        var manifestJson = $$"""
            {
              "versions": [
                {
                  "id": "{{parentVersion}}",
                  "url": "{{parentMetadataUrl}}"
                }
              ]
            }
            """;
        var handler = new ContentHandler(new Dictionary<string, string>
        {
            ["https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"] = manifestJson,
            [parentMetadataUrl] = parentJson
        });
        var httpClient = new HttpClient(handler);
        var repairService = new ManagedVersionRepairService(httpClient);
        var builder = new RequiredGameFileManifestBuilder(repairService);

        var plan = await builder.ResolveFinalCommandAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory),
            CancellationToken.None);
        _ = await builder.ResolveFinalCommandAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory),
            CancellationToken.None);

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", plan.VersionJson["mainClass"]!.GetValue<string>());
        Assert.Contains(
            plan.VersionJson["libraries"]!.AsArray(),
            node => node?["name"]?.GetValue<string>() == "com.mojang:patchy:2.2.10");
        Assert.Contains(
            plan.VersionJson["libraries"]!.AsArray(),
            node => node?["name"]?.GetValue<string>() == "net.fabricmc:fabric-loader:0.16.14");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(childJsonPath));
        Assert.False(Directory.Exists(Path.Combine(minecraftDirectory, "versions", parentVersion)));
        Assert.DoesNotContain(
            plan.Manifest.Files,
            file => file.Category == "VersionMetadata"
                && file.TargetPath.Contains(parentVersion, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task MissingLibraryIsRecoveredFromResolvedStandardMetadata()
    {
        const string versionName = "Loader-1.18.2";
        const string relativePath = "com/example/runtime/1.0/runtime-1.0.jar";
        const string libraryContent = "runtime";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        CreateVersion(minecraftDirectory, versionName, relativePath, libraryContent, createLibrary: false);
        var service = new GameFileIntegrityService(new HttpClient(new ContentHandler(new Dictionary<string, string>
        {
            ["https://example.test/" + relativePath] = libraryContent
        })), downloadSpeedLimitState: null);
        var progressReports = new List<LauncherProgress>();

        var result = await service.ValidateAndRepairAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, Path.Combine(minecraftDirectory, "versions", versionName)),
            new GameFileRepairOptions(AllowRepair: true),
            new InlineProgress(progressReports));

        Assert.True(result.LaunchAllowed);
        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(libraryContent, await File.ReadAllTextAsync(Path.Combine(minecraftDirectory, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Contains(
            progressReports,
            report => report.Stage == LaunchProgressStages.RevalidatingFiles && report.Percent == 84);
        Assert.Contains(
            progressReports,
            report => report.Stage == LaunchProgressStages.RevalidatingFiles && report.Percent == 90);
        Assert.Equal(
            progressReports.Where(report => report.Percent is not null).Select(report => report.Percent!.Value).Order(),
            progressReports.Where(report => report.Percent is not null).Select(report => report.Percent!.Value));
    }

    [Fact]
    public async Task MalformedInstanceMetadataIsPreservedAndDoesNotStartLoaderRepair()
    {
        const string versionName = "26.2";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        Directory.CreateDirectory(versionDirectory);
        var jsonPath = Path.Combine(versionDirectory, $"{versionName}.json");
        await File.WriteAllTextAsync(jsonPath, "{ not-json");
        var originalBytes = await File.ReadAllBytesAsync(jsonPath);
        var provider = new FailingLoaderProvider(
            LoaderKind.Forge,
            new InvalidOperationException("Loader repair must not start."));
        var service = new GameFileIntegrityService(
            new HttpClient(new ContentHandler(new Dictionary<string, string>())),
            downloadSpeedLimitState: null,
            logger: null,
            loaderProviders: [provider],
            gameInstallCoordinator: new GameInstallCoordinator());

        var result = await service.ValidateAndRepairAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory)
            {
                LoaderIdentity = new GameFileLoaderIdentity(LoaderKind.Forge, versionName, "test")
            },
            new GameFileRepairOptions(AllowRepair: true));

        Assert.False(result.LaunchAllowed);
        Assert.Equal(GameFileRepairFailureReason.Corrupted, Assert.Single(result.Failures).Reason);
        Assert.Equal(0, provider.InstallCallCount);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(jsonPath));
    }

    [Fact]
    public async Task ValidationCancellationIsPropagated()
    {
        const string versionName = "Canceled Validation";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        CreateVersion(minecraftDirectory, versionName, "example/library/1.0/library-1.0.jar", "library");
        var service = new GameFileIntegrityService(
            new HttpClient(new ContentHandler(new Dictionary<string, string>())),
            downloadSpeedLimitState: null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ValidateAndRepairAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory),
            new GameFileRepairOptions(AllowRepair: true),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task PostInstallValidationCancellationIsPropagated()
    {
        const string versionName = "Canceled Post Install Validation";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        CreateVersion(minecraftDirectory, versionName, "example/library/1.0/library-1.0.jar", "library");
        using var operation = new MinecraftDownloadOperationContext(minecraftDirectory);
        var service = new GameFileIntegrityService(
            new HttpClient(new ContentHandler(new Dictionary<string, string>())),
            downloadSpeedLimitState: null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ValidateInstalledVersionAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory),
            operation,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task NestedLoaderDownloadFailureIsNotMisreportedAsCorruption()
    {
        const string versionName = "Forge Download Failure";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        Directory.CreateDirectory(versionDirectory);
        var provider = new FailingLoaderProvider(
            LoaderKind.Forge,
            new InstanceRepairException(
                "Forge sandbox repair failed.",
                new DownloadAttemptException(
                    DownloadFailureDisposition.SwitchSource,
                    DownloadFailureReason.HttpStatus,
                    "The source returned HTTP 404.",
                    statusCode: HttpStatusCode.NotFound)));
        var service = new GameFileIntegrityService(
            new HttpClient(new ContentHandler(new Dictionary<string, string>())),
            downloadSpeedLimitState: null,
            logger: null,
            loaderProviders: [provider],
            gameInstallCoordinator: new GameInstallCoordinator());

        var result = await service.ValidateAndRepairAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory)
            {
                LoaderIdentity = new GameFileLoaderIdentity(LoaderKind.Forge, "1.20.1", "47.4.20")
            },
            new GameFileRepairOptions(AllowRepair: true));

        Assert.False(result.LaunchAllowed);
        Assert.Equal(0, result.CorruptedCount);
        Assert.Equal(GameFileRepairFailureReason.DownloadFailed, Assert.Single(result.Failures).Reason);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, GameFileVerificationLevel.SizeVerified)]
    public async Task GeneratedProcessorOutputWithSameSizeDoesNotUseRecordedHashes(
        LoaderKind loaderKind,
        GameFileVerificationLevel verificationLevel)
    {
        var result = await ValidateLoaderArtifactAsync(
            loaderKind,
            LoaderArtifactKind.ProcessorOutput,
            verificationLevel,
            expectedContent: "old!",
            actualContent: "new!");

        Assert.True(result.LaunchAllowed);
        Assert.DoesNotContain(result.Failures, failure => failure.Category == "LoaderProcessorOutput");
    }

    [Theory]
    [InlineData(null, GameFileRepairFailureReason.Missing)]
    public async Task GeneratedProcessorOutputStillRequiresExistenceAndRecordedSize(
        string? actualContent,
        GameFileRepairFailureReason expectedReason)
    {
        var result = await ValidateLoaderArtifactAsync(
            LoaderKind.Forge,
            LoaderArtifactKind.ProcessorOutput,
            GameFileVerificationLevel.SizeVerified,
            expectedContent: "old!",
            actualContent);

        Assert.False(result.LaunchAllowed);
        var failure = Assert.Single(result.Failures, item => item.Category == "LoaderProcessorOutput");
        Assert.Equal(expectedReason, failure.Reason);
    }

    [Theory]
    [InlineData((int)LoaderArtifactKind.ProcessorOutput, GameFileVerificationLevel.HashVerified)]
    public async Task TrustedLoaderArtifactsRetainFullHashValidation(
        int artifactKind,
        GameFileVerificationLevel verificationLevel)
    {
        var result = await ValidateLoaderArtifactAsync(
            LoaderKind.Forge,
            (LoaderArtifactKind)artifactKind,
            verificationLevel,
            expectedContent: "old!",
            actualContent: "new!");

        Assert.False(result.LaunchAllowed);
        Assert.Equal(GameFileRepairFailureReason.Corrupted, Assert.Single(result.Failures).Reason);
    }

    [Fact]
    public async Task VerifiedFileLeaseBlocksConcurrentWrites()
    {
        const string content = "library";
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "leased.jar");
        await File.WriteAllTextAsync(path, content);
        using var operation = new MinecraftDownloadOperationContext(TempRoot);
        var expectation = DownloadIntegrityExpectation.Sha1(Sha1(content), Encoding.UTF8.GetByteCount(content));
        operation.MarkVerified(path, expectation);

        using var lease = operation.AcquireVerifiedFileLease(path, expectation);

        Assert.NotNull(lease);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "corrupt"));
    }

    [Fact]
    public async Task AllowedAgentReparsePointIsRejectedWhenSupported()
    {
        const string versionName = "Linked Agent";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        CreateVersion(minecraftDirectory, versionName, "example/library/1.0/library-1.0.jar", "library");
        var targetPath = Path.Combine(TempRoot, "agent-target.jar");
        var linkPath = Path.Combine(TempRoot, "agent-link.jar");
        await File.WriteAllTextAsync(targetPath, "agent");
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        startInfo.ArgumentList.Add($"-javaagent:{linkPath}");
        var service = new GameFileIntegrityService(
            new HttpClient(new ContentHandler(new Dictionary<string, string>())),
            downloadSpeedLimitState: null);

        var result = await service.ValidateFinalLaunchCommandAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory)
            {
                AllowedAdditionalCommandFilePaths = [linkPath]
            },
            startInfo);

        Assert.False(result.LaunchAllowed);
        Assert.Contains(result.Failures, item => item.Category == "JavaAgent"
            && item.Source == "Allowed additional path is not an ordinary file.");
    }

    private static void MarkVerified(MinecraftDownloadOperationContext operation, string path, string content)
    {
        operation.MarkVerified(
            path,
            DownloadIntegrityExpectation.Sha1(Sha1(content), Encoding.UTF8.GetByteCount(content)));
    }

    private async Task<GameFileRepairResult> ValidateLoaderArtifactAsync(
        LoaderKind loaderKind,
        LoaderArtifactKind artifactKind,
        GameFileVerificationLevel verificationLevel,
        string expectedContent,
        string? actualContent)
    {
        const string versionName = "Loader Verification";
        const string artifactRelativePath =
            "libraries/net/minecraft/client/1.16.5-test/client-1.16.5-test-srg.jar";
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        CreateVersion(
            minecraftDirectory,
            versionName,
            "com/example/runtime/1.0/runtime-1.0.jar",
            "runtime");
        var artifactPath = Path.Combine(
            minecraftDirectory,
            artifactRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var expectedBytes = CreateJarBytes(expectedContent);
        if (actualContent is not null)
            await File.WriteAllBytesAsync(artifactPath, CreateJarBytes(actualContent));

        var manifestPath = LoaderArtifactManifestStore.GetPath(versionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, "{}");
        var manifest = new LoaderArtifactManifest(
            LoaderArtifactManifestStore.CurrentSchemaVersion,
            loaderKind,
            "1.16.5",
            loaderKind == LoaderKind.Forge ? "36.2.42" : "20.4.237",
            new string('a', 64),
            [
                new LoaderArtifactManifestEntry(
                    artifactRelativePath,
                    artifactKind,
                    Source: null,
                    Sha1(expectedBytes),
                    Sha256(expectedBytes),
                    expectedBytes.LongLength,
                    verificationLevel)
            ]);
        var contributor = new StaticLoaderManifestContributor(loaderKind, manifestPath, manifest);
        var service = new GameFileIntegrityService(
            httpClient: null,
            downloadSpeedLimitState: null,
            manifestContributors: [contributor]);

        return await service.ValidateAndRepairAsync(
            new GameFileIntegrityRequest(minecraftDirectory, versionName, versionDirectory)
            {
                LoaderIdentity = new GameFileLoaderIdentity(
                    loaderKind,
                    "1.16.5",
                    manifest.LoaderVersion)
            },
            new GameFileRepairOptions(AllowRepair: false));
    }

    private static void CreateVersion(string minecraftDirectory, string versionName, string relativePath, string libraryContent, bool createLibrary = true)
    {
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionName);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(Path.Combine(versionDirectory, $"{versionName}.jar"), "client");
        var libraryPath = Path.Combine(minecraftDirectory, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        if (createLibrary)
            File.WriteAllText(libraryPath, libraryContent);
        var json = new JsonObject
        {
            ["id"] = versionName,
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "com.example:runtime:1.0",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = relativePath,
                            ["url"] = "https://example.test/" + relativePath,
                            ["sha1"] = Sha1(libraryContent),
                            ["size"] = Encoding.UTF8.GetByteCount(libraryContent)
                        }
                    }
                }
            }
        };
        File.WriteAllText(Path.Combine(versionDirectory, $"{versionName}.json"), json.ToJsonString());
    }

    private static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha1(byte[] value) => Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();
    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] CreateJarBytes(string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("test.class", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
            writer.Write(content);
        }
        return stream.ToArray();
    }

    private sealed class ContentHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri is not null && responses.TryGetValue(request.RequestUri.AbsoluteUri, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class InlineProgress(List<LauncherProgress> reports) : IProgress<LauncherProgress>
    {
        public void Report(LauncherProgress value) => reports.Add(value);
    }

    private sealed class StaticLoaderManifestContributor(
        LoaderKind kind,
        string manifestPath,
        LoaderArtifactManifest manifest)
        : ILoaderFileManifestContributor
    {
        public LoaderKind Kind { get; } = kind;

        public Task<LoaderFileManifestContribution> ResolveAsync(
            string versionDirectory,
            GameFileLoaderIdentity identity,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LoaderFileManifestContribution(
                RequiresManifest: true,
                manifestPath,
                manifest,
                Error: null));
    }

    private sealed class FailingLoaderProvider(LoaderKind kind, Exception exception) : ILoaderProvider
    {
        public LoaderKind Kind { get; } = kind;
        public bool IsImplemented => true;
        public int InstallCallCount { get; private set; }

        public Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
            string minecraftVersion,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            CancellationToken cancellationToken = default,
            int downloadSpeedLimitMbPerSecond = 0) =>
            Task.FromResult<IReadOnlyList<LoaderVersionInfo>>([new LoaderVersionInfo("test")]);

        public Task<string> InstallAsync(
            string minecraftVersion,
            string gameDirectory,
            string isolatedVersionName,
            string? loaderVersion,
            IProgress<LauncherProgress>? progress,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            CancellationToken cancellationToken = default,
            int downloadSpeedLimitMbPerSecond = 0)
        {
            InstallCallCount++;
            return Task.FromException<string>(exception);
        }
    }
}
