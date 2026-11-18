/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class LoaderInstallerArtifactServiceTests : TestTempDirectory
{
    [Fact]
    public async Task PlanIncludesProfileProcessorAndVersionRuntimeLibraries()
    {
        var installerPath = await WriteInstallerAsync(includeEmbeddedExternal: true);
        var service = new LoaderInstallerArtifactService(new HttpClient(new TestHandler("external")));

        var plan = await service.ReadPlanAsync(installerPath, CancellationToken.None);

        Assert.Equal(
            [
                "com/example/classpath/1.0/classpath-1.0.jar",
                "com/example/input/1.0/input-1.0.jar",
                "com/example/processor/1.0/processor-1.0.jar",
                "com/example/profile/1.0/profile-1.0.jar"
            ],
            plan.PrerequisiteLibraries.Select(library => library.Artifact.RelativePath));
        Assert.Equal("com/example/runtime/1.0/runtime-1.0.jar", Assert.Single(plan.RuntimeLibraries).Artifact.RelativePath);
        Assert.Equal("com/example/output/1.0/output-1.0.jar", Assert.Single(plan.ProcessorOutputs).RelativePath);
        Assert.NotNull(plan.PrerequisiteLibraries.Single(library => library.Artifact.LibraryName == "com.example:profile:1.0").EmbeddedEntryName);
    }

    [Fact]
    public async Task ManifestStoreCapturesCompleteInstallerClosureAndRejectsDifferentIdentity()
    {
        var installerPath = await WriteInstallerAsync(includeEmbeddedExternal: true);
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", "Arbitrary Pack");
        Directory.CreateDirectory(versionDirectory);
        var service = new LoaderInstallerArtifactService(new HttpClient(new TestHandler("external")));
        var plan = await service.ReadPlanAsync(installerPath, CancellationToken.None);
        await service.MaterializePrerequisitesAsync(
            installerPath,
            plan,
            minecraftDirectory,
            DownloadSourcePreference.Official,
            0,
            CancellationToken.None);
        await service.MaterializeRuntimeLibrariesAsync(
            installerPath,
            plan,
            minecraftDirectory,
            DownloadSourcePreference.Official,
            0,
            CancellationToken.None);
        var output = Assert.Single(plan.ProcessorOutputs);
        var outputPath = Path.Combine(
            minecraftDirectory,
            "libraries",
            output.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, CreateJar("generated"));
        var identity = new GameFileLoaderIdentity(LoaderKind.Forge, "9.9.9", "77.0.1");
        var inspections = await service.ValidatePublishedArtifactsAsync(minecraftDirectory, plan, CancellationToken.None);

        await LoaderArtifactManifestStore.WriteAsync(
            versionDirectory,
            minecraftDirectory,
            identity,
            installerPath,
            plan,
            inspections,
            CancellationToken.None);

        var result = await LoaderArtifactManifestStore.ReadAsync(
            versionDirectory,
            identity,
            CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal(6, result.Manifest!.Artifacts.Count);
        Assert.Contains(result.Manifest.Artifacts, artifact => artifact.Kind == LoaderArtifactKind.InstallerPrerequisite);
        Assert.Contains(result.Manifest.Artifacts, artifact => artifact.Kind == LoaderArtifactKind.RuntimeLibrary);
        Assert.Contains(result.Manifest.Artifacts, artifact => artifact.Kind == LoaderArtifactKind.ProcessorOutput);
        Assert.All(result.Manifest.Artifacts, artifact => Assert.True(MinecraftFileIntegrity.IsSha1(artifact.Sha1)));
        Assert.All(result.Manifest.Artifacts, artifact => Assert.Equal(64, artifact.Sha256.Length));
        Assert.Equal(
            GameFileVerificationLevel.SizeVerified,
            result.Manifest.Artifacts.Single(artifact => artifact.Kind == LoaderArtifactKind.ProcessorOutput)
                .VerificationLevel);

        var mismatched = await LoaderArtifactManifestStore.ReadAsync(
            versionDirectory,
            identity with { LoaderVersion = "77.0.2" },
            CancellationToken.None);
        Assert.False(mismatched.IsValid);
        Assert.Contains("identity", mismatched.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--out-jar", ModpackInstallEnvironment.Client)]
    [InlineData("--jar-out", ModpackInstallEnvironment.Server)]
    public async Task ProcessorOutputOptionsDoNotMaterializeGeneratedCoordinates(
        string outputOption,
        ModpackInstallEnvironment environment)
    {
        var installerPath = await WriteProcessorOutputInstallerAsync(outputOption);
        var handler = new RecordingHandler();
        var service = new LoaderInstallerArtifactService(new HttpClient(handler));

        var plan = await service.ReadPlanAsync(installerPath, environment, CancellationToken.None);

        var expectedRelativePath = environment is ModpackInstallEnvironment.Server
            ? "net/minecraft/server/1.0/server-1.0-srg.jar"
            : "net/minecraft/client/1.0/client-1.0-srg.jar";
        Assert.Contains(plan.ProcessorOutputs, output => output.RelativePath == expectedRelativePath);
        Assert.DoesNotContain(
            plan.PrerequisiteLibraries,
            library => library.Artifact.RelativePath == expectedRelativePath);

        await service.MaterializePrerequisitesAsync(
            installerPath,
            plan,
            Path.Combine(TempRoot, environment.ToString()),
            DownloadSourcePreference.Official,
            0,
            CancellationToken.None);

        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData(ModpackInstallEnvironment.Client, true)]
    [InlineData(ModpackInstallEnvironment.Server, false)]
    public async Task PatchedOutputUsesPostInjectionHashForFinalManifest(
        ModpackInstallEnvironment environment,
        bool usePlaceholder)
    {
        var installerPath = await WritePatchedOutputInstallerAsync(environment, usePlaceholder);
        var minecraftDirectory = Path.Combine(TempRoot, $"patched-{environment}-{usePlaceholder}");
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", "Forge Test");
        Directory.CreateDirectory(versionDirectory);
        var service = new LoaderInstallerArtifactService(new HttpClient(new TestHandler("external")));
        var plan = await service.ReadPlanAsync(installerPath, environment, CancellationToken.None);
        var output = Assert.Single(plan.ProcessorOutputs);
        Assert.Null(output.TrustedSha1);
        var outputPath = Path.Combine(
            minecraftDirectory,
            "libraries",
            output.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var finalContent = CreateJar("profile-injected-patched-output");
        await File.WriteAllBytesAsync(outputPath, finalContent);

        var inspections = await service.ValidatePublishedArtifactsAsync(
            minecraftDirectory,
            plan,
            CancellationToken.None);
        var identity = new GameFileLoaderIdentity(LoaderKind.Forge, "1.17.1", "37.1.1");
        await LoaderArtifactManifestStore.WriteAsync(
            versionDirectory,
            minecraftDirectory,
            identity,
            installerPath,
            plan,
            inspections,
            CancellationToken.None);

        var result = await LoaderArtifactManifestStore.ReadAsync(
            versionDirectory,
            identity,
            CancellationToken.None);
        var artifact = Assert.Single(result.Manifest!.Artifacts);
        Assert.Equal($"libraries/{output.RelativePath}", artifact.RelativePath);
        Assert.Equal(Sha1(finalContent), artifact.Sha1, ignoreCase: true);
        Assert.Equal(64, artifact.Sha256.Length);
        Assert.Equal(finalContent.Length, artifact.Size);
        Assert.Equal(GameFileVerificationLevel.SizeVerified, artifact.VerificationLevel);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, true)]
    [InlineData(LoaderKind.Forge, false)]
    [InlineData(LoaderKind.NeoForge, false)]
    public async Task GeneratedOutputUsesDeclaredOrStructuralVerificationThroughPublication(
        LoaderKind loaderKind, bool matchesDeclaredHash)
    {
        var expected = CreateJar("expected-srg");
        var actual = matchesDeclaredHash ? expected : CreateJar("different-srg");
        var installerPath = await WriteHashedSrgOutputInstallerAsync(expected);
        var minecraftDirectory = Path.Combine(TempRoot, "hashed-srg");
        var logger = new OutputLogger();
        var service = new LoaderInstallerArtifactService(new HttpClient(new TestHandler("external")), logger: logger);
        var plan = await service.ReadPlanAsync(installerPath, CancellationToken.None);
        var output = Assert.Single(plan.ProcessorOutputs);
        Assert.Equal(Sha1(expected), output.TrustedSha1, ignoreCase: true);
        var outputPath = Path.Combine(
            minecraftDirectory,
            "libraries",
            output.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, actual);
        var inspections = await service.ValidatePublishedArtifactsAsync(minecraftDirectory, plan, CancellationToken.None);
        Assert.Equal(!matchesDeclaredHash, logger.Warnings.Any());
        if (!matchesDeclaredHash)
        {
            var warning = Assert.Single(logger.Warnings);
            Assert.Contains(output.RelativePath, warning);
            Assert.Contains(Sha1(actual), warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Sha1(expected), warning, StringComparison.OrdinalIgnoreCase);
        }
        var sharedDirectory = Path.Combine(TempRoot, "shared");
        var sharedOutput = Path.Combine(sharedDirectory, "libraries", output.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedOutput)!);
        await File.WriteAllBytesAsync(sharedOutput, CreateJar("old-output"));
        await new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
            new LoaderInstallerWorkspaceSnapshot(minecraftDirectory, new Dictionary<string, string>()),
            sharedDirectory, CancellationToken.None,
            replaceableFileExpectations: LoaderInstallerArtifactService.CreateReplaceableProcessorOutputExpectations(inspections));
        Assert.Equal(actual, await File.ReadAllBytesAsync(sharedOutput));
        var published = await service.ValidatePublishedArtifactsAsync(sharedDirectory, plan, CancellationToken.None);
        var versionDirectory = Path.Combine(sharedDirectory, "versions", "Forge Test");
        Directory.CreateDirectory(versionDirectory);
        var identity = new GameFileLoaderIdentity(loaderKind, "1.17.1", "37.1.1");
        await LoaderArtifactManifestStore.WriteAsync(
            versionDirectory,
            sharedDirectory,
            identity,
            installerPath,
            plan,
            published,
            CancellationToken.None);

        var result = await LoaderArtifactManifestStore.ReadAsync(
            versionDirectory,
            identity,
            CancellationToken.None);
        Assert.Equal(
            matchesDeclaredHash ? GameFileVerificationLevel.HashVerified : GameFileVerificationLevel.SizeVerified,
            Assert.Single(result.Manifest!.Artifacts).VerificationLevel);
        Assert.Equal(Sha1(actual), Assert.Single(result.Manifest.Artifacts).Sha1, ignoreCase: true);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(actual)), Assert.Single(result.Manifest.Artifacts).Sha256, ignoreCase: true);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("empty", true)]
    [InlineData("text", true)]
    [InlineData("truncated", true)]
    [InlineData("crc", true)]
    [InlineData("empty-archive", true)]
    [InlineData("crc", false)]
    public async Task InvalidGeneratedArchivesAreRejectedWithoutMatchingDeclaredHash(string failure, bool hasDeclaredHash)
    {
        var bytes = CreateJar("generated");
        if (failure == "empty") bytes = [];
        if (failure == "text") bytes = "not a jar"u8.ToArray();
        if (failure == "truncated") bytes = bytes[..^12];
        if (failure == "crc") bytes[30 + "content.txt".Length] ^= 1;
        if (failure == "empty-archive")
        {
            using var empty = new MemoryStream();
            using (new ZipArchive(empty, ZipArchiveMode.Create, leaveOpen: true)) { }
            bytes = empty.ToArray();
        }
        var output = new ForgeProcessorOutput("com/example/output.jar", hasDeclaredHash ? Sha1("declared bytes") : null);
        var path = Path.Combine(TempRoot, "libraries", output.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (failure != "missing") await File.WriteAllBytesAsync(path, bytes);
        var service = new LoaderInstallerArtifactService(new HttpClient(new RecordingHandler()));
        var plan = new ForgeInstallerPlan([], [], [output], new JsonArray());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ValidatePublishedArtifactsAsync(TempRoot, plan, CancellationToken.None));

        var diagnostic = SafeExceptionLog.Format(exception);
        Assert.Contains("DiagnosticCode=ProcessorOutputInvalid", diagnostic);
        Assert.Contains(output.RelativePath, diagnostic);
        Assert.Contains(failure == "missing" ? "Missing" : failure == "empty" ? "Empty" : "InvalidArchive", diagnostic);
    }

    [Fact]
    public async Task MatchingDeclaredHashDoesNotRequireArchiveParserCompatibility()
    {
        var bytes = "opaque installer output"u8.ToArray();
        var output = new ForgeProcessorOutput("com/example/output.jar", Sha1(bytes));
        var path = Path.Combine(TempRoot, "libraries", output.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);

        var inspection = await ProcessorOutputInspector.InspectAsync(path, output, CancellationToken.None);

        Assert.Equal(GameFileVerificationLevel.HashVerified, inspection.VerificationLevel);
        Assert.Equal(Sha1(bytes), inspection.Sha1, ignoreCase: true);
    }

    [Theory]
    [InlineData("same-size")]
    [InlineData("wrong-size")]
    [InlineData("missing")]
    public async Task FinalPublicationCheckUsesInspectedSizeWithoutHashingOrOpeningArchive(string change)
    {
        var bytes = CreateJar("generated");
        var output = new ForgeProcessorOutput("com/example/output.jar", null);
        var path = Path.Combine(TempRoot, "libraries", output.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        var plan = new ForgeInstallerPlan([], [], [output], new JsonArray());
        var service = new LoaderInstallerArtifactService(new HttpClient(new RecordingHandler()));
        var inspections = await service.ValidatePublishedArtifactsAsync(TempRoot, plan, CancellationToken.None);
        if (change == "missing") File.Delete(path);
        else await File.WriteAllBytesAsync(path, new byte[bytes.Length - (change == "wrong-size" ? 1 : 0)]);

        var validation = service.ValidatePublishedArtifactsAsync(TempRoot, plan, CancellationToken.None,
            previouslyInspectedOutputs: inspections);
        if (change == "same-size")
        {
            var result = await validation;
            Assert.Equal(inspections[output.RelativePath], result[output.RelativePath]);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => validation);
            Assert.Contains(change == "missing" ? "Missing" : "SizeMismatch", SafeExceptionLog.Format(exception));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeLibraryHashMismatchRemainsFatalWhenAlsoDeclaredAsOutput(bool overlappingOutput)
    {
        const string relativePath = "com/example/library.jar";
        var expected = CreateJar("expected");
        var actual = CreateJar("different");
        var path = Path.Combine(TempRoot, "libraries", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, actual);
        var artifact = new ManagedLibraryArtifact("https://example.test/library.jar", relativePath,
            "com.example:library:1.0", "ThirdParty", Sha1(expected));
        var plan = new ForgeInstallerPlan([], [new ForgeInstallerLibrary(artifact, null)],
            overlappingOutput ? [new ForgeProcessorOutput(relativePath, Sha1(actual))] : [], new JsonArray());
        var service = new LoaderInstallerArtifactService(new HttpClient(new RecordingHandler()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ValidatePublishedArtifactsAsync(TempRoot, plan, CancellationToken.None));
        Assert.Equal(actual, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DownloadedLibraryDeclarationWinsOverConflictingProcessorHash()
    {
        const string relativePath = "com/example/library.jar";
        var actual = CreateJar("downloaded");
        var path = Path.Combine(TempRoot, "libraries", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, actual);
        var installerPath = Path.Combine(TempRoot, "installer.jar");
        await File.WriteAllBytesAsync(installerPath, CreateJar("installer"));
        var artifact = new ManagedLibraryArtifact("https://example.test/library.jar", relativePath,
            "com.example:library:1.0", "ThirdParty", Sha1(actual), actual.Length);
        var plan = new ForgeInstallerPlan([], [new ForgeInstallerLibrary(artifact, null)],
            [new ForgeProcessorOutput(relativePath, Sha1("other"))], new JsonArray());
        var service = new LoaderInstallerArtifactService(new HttpClient(new RecordingHandler()));
        var inspections = await service.ValidatePublishedArtifactsAsync(TempRoot, plan, CancellationToken.None);
        Assert.Empty(LoaderInstallerArtifactService.CreateReplaceableProcessorOutputExpectations(inspections));
        var identity = new GameFileLoaderIdentity(LoaderKind.Forge, "1.20.1", "47.4.29");
        var versionDirectory = Path.Combine(TempRoot, "versions", "test");
        await LoaderArtifactManifestStore.WriteAsync(versionDirectory, TempRoot, identity, installerPath,
            plan, inspections, CancellationToken.None);
        var manifest = await LoaderArtifactManifestStore.ReadAsync(versionDirectory, identity, CancellationToken.None);
        Assert.Equal(LoaderArtifactKind.RuntimeLibrary, Assert.Single(manifest.Manifest!.Artifacts).Kind);
        Assert.Equal(GameFileVerificationLevel.HashVerified, Assert.Single(manifest.Manifest.Artifacts).VerificationLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrCanceledAcceptedOutputDoesNotReplaceSharedFile(bool cancel)
    {
        var installerPath = await WriteHashedSrgOutputInstallerAsync(CreateJar("declared"));
        var workspace = Path.Combine(TempRoot, "workspace");
        var service = new LoaderInstallerArtifactService(new HttpClient(new RecordingHandler()));
        var plan = await service.ReadPlanAsync(installerPath, CancellationToken.None);
        var output = Assert.Single(plan.ProcessorOutputs);
        var source = Path.Combine(workspace, "libraries", output.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, CreateJar("accepted"));
        var inspections = await service.ValidatePublishedArtifactsAsync(workspace, plan, CancellationToken.None);
        var shared = Path.Combine(TempRoot, "shared");
        var destination = Path.Combine(shared, "libraries", output.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var original = CreateJar("original");
        await File.WriteAllBytesAsync(destination, original);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        else await File.WriteAllBytesAsync(source, CreateJar("changed!"));

        var publish = new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
            new LoaderInstallerWorkspaceSnapshot(workspace, new Dictionary<string, string>()), shared, cancellation.Token,
            replaceableFileExpectations: LoaderInstallerArtifactService.CreateReplaceableProcessorOutputExpectations(inspections));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish);
        else await Assert.ThrowsAsync<InvalidDataException>(() => publish);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".*.tmp"));

        var identity = new GameFileLoaderIdentity(LoaderKind.Forge, "1.20.1", "47.4.29");
        var versionDirectory = Path.Combine(workspace, "versions", "test");
        if (!cancel)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => LoaderArtifactManifestStore.WriteAsync(
                versionDirectory, workspace, identity, installerPath, plan, inspections, CancellationToken.None));
            Assert.False(File.Exists(LoaderArtifactManifestStore.GetPath(versionDirectory)));
        }
    }

    [Fact]
    public async Task LegacyRuntimeNativeLibraryDoesNotIncludeNonexistentMainArtifact()
    {
        var installerPath = await WriteLegacyNativeInstallerAsync();
        var service = new LoaderInstallerArtifactService(new HttpClient(new TestHandler("native")));

        var plan = await service.ReadPlanAsync(installerPath, CancellationToken.None);

        var library = Assert.Single(plan.RuntimeLibraries);
        Assert.Equal(
            OperatingSystem.IsWindows()
                ? "org/lwjgl/lwjgl/lwjgl-platform/2.9.0/lwjgl-platform-2.9.0-natives-windows.jar"
                : OperatingSystem.IsMacOS()
                    ? "org/lwjgl/lwjgl/lwjgl-platform/2.9.0/lwjgl-platform-2.9.0-natives-osx.jar"
                    : "org/lwjgl/lwjgl/lwjgl-platform/2.9.0/lwjgl-platform-2.9.0-natives-linux.jar",
            library.Artifact.RelativePath);
    }

    private async Task<string> WriteInstallerAsync(bool includeEmbeddedExternal)
    {
        Directory.CreateDirectory(TempRoot);
        var installerPath = Path.Combine(TempRoot, $"installer-{Guid.NewGuid():N}.jar");
        await using var stream = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "install_profile.json", $$"""
            {
              "data": {
                "INPUT": { "client": "[com.example:input:1.0]" },
                "OUTPUT": { "client": "[com.example:output:1.0]" }
              },
              "libraries": [
                {
                  "name": "com.example:profile:1.0",
                  "url": "https://example.test/",
                  "downloads": { "artifact": { "path": "com/example/profile/1.0/profile-1.0.jar", "sha1": "{{Sha1("external")}}", "size": 8 } }
                }
              ],
              "processors": [
                {
                  "sides": ["client"],
                  "jar": "com.example:processor:1.0",
                  "classpath": ["com.example:classpath:1.0"],
                  "args": ["--input", "{INPUT}", "--output", "{OUTPUT}"]
                },
                {
                  "sides": ["client"],
                  "jar": "com.example:processor:1.0",
                  "args": ["--consume-generated", "{OUTPUT}"]
                }
              ]
            }
            """);
        WriteEntry(archive, "version.json", """
            {
              "libraries": [
                { "name": "com.example:runtime:1.0", "url": "https://example.test/" }
              ]
            }
            """);
        if (includeEmbeddedExternal)
            WriteEntry(archive, "maven/com/example/profile/1.0/profile-1.0.jar", "external");
        return installerPath;
    }

    private async Task<string> WriteProcessorOutputInstallerAsync(string outputOption)
    {
        Directory.CreateDirectory(TempRoot);
        var installerPath = Path.Combine(TempRoot, $"processor-output-{Guid.NewGuid():N}.jar");
        await using var stream = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "install_profile.json", $$"""
            {
              "data": {
                "MC_SRG": {
                  "client": "[net.minecraft:client:1.0:srg]",
                  "server": "[net.minecraft:server:1.0:srg]"
                }
              },
              "processors": [
                {
                  "jar": "com.example:processor:1.0",
                  "args": ["{{outputOption}}", "{MC_SRG}"]
                },
                {
                  "jar": "com.example:processor:1.0",
                  "args": ["--clean", "{MC_SRG}"]
                }
              ]
            }
            """);
        WriteEntry(archive, "version.json", """{ "libraries": [] }""");
        WriteEntry(archive, "maven/com/example/processor/1.0/processor-1.0.jar", "processor");
        return installerPath;
    }

    private async Task<string> WritePatchedOutputInstallerAsync(
        ModpackInstallEnvironment environment,
        bool usePlaceholder)
    {
        Directory.CreateDirectory(TempRoot);
        var installerPath = Path.Combine(TempRoot, $"patched-output-{Guid.NewGuid():N}.jar");
        var coordinate = environment is ModpackInstallEnvironment.Server
            ? "net.minecraftforge:forge:1.17.1-37.1.1:server"
            : "net.minecraftforge:forge:1.17.1-37.1.1:client";
        var outputExpression = usePlaceholder ? "{PATCHED}" : $"[{coordinate}]";
        await using var stream = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "install_profile.json", $$"""
            {
              "data": {
                "PATCHED": {
                  "client": "[net.minecraftforge:forge:1.17.1-37.1.1:client]",
                  "server": "[net.minecraftforge:forge:1.17.1-37.1.1:server]"
                },
                "PATCHED_SHA": {
                  "client": "'{{Sha1("processor-stage-client")}}'",
                  "server": "'{{Sha1("processor-stage-server")}}'"
                }
              },
              "processors": [
                {
                  "outputs": {
                    "{{outputExpression}}": "{PATCHED_SHA}"
                  }
                }
              ]
            }
            """);
        WriteEntry(archive, "version.json", """{ "libraries": [] }""");
        return installerPath;
    }

    private async Task<string> WriteHashedSrgOutputInstallerAsync(byte[] expectedPayload)
    {
        Directory.CreateDirectory(TempRoot);
        var installerPath = Path.Combine(TempRoot, $"hashed-srg-{Guid.NewGuid():N}.jar");
        await using var stream = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "install_profile.json", $$"""
            {
              "data": {
                "MC_SRG": {
                  "client": "[net.minecraft:client:1.17.1:srg]"
                },
                "MC_SRG_SHA": {
                  "client": "'{{Sha1(expectedPayload)}}'"
                }
              },
              "processors": [
                {
                  "outputs": {
                    "{MC_SRG}": "{MC_SRG_SHA}"
                  }
                }
              ]
            }
            """);
        WriteEntry(archive, "version.json", """{ "libraries": [] }""");
        return installerPath;
    }

    private async Task<string> WriteLegacyNativeInstallerAsync()
    {
        Directory.CreateDirectory(TempRoot);
        var installerPath = Path.Combine(TempRoot, $"legacy-native-{Guid.NewGuid():N}.jar");
        await using var stream = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "install_profile.json", "{}");
        WriteEntry(archive, "version.json", """
            {
              "libraries": [
                {
                  "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.0",
                  "natives": {
                    "linux": "natives-linux",
                    "windows": "natives-windows",
                    "osx": "natives-osx"
                  }
                }
              ]
            }
            """);
        return installerPath;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string Sha1(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha1(byte[] value) => Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();

    private static byte[] CreateJar(string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("content.txt", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var data = entry.Open();
            data.Write(Encoding.UTF8.GetBytes(content));
        }
        return stream.ToArray();
    }

    private sealed class TestHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content)
            });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request
            });
        }
    }

    private sealed class OutputLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
