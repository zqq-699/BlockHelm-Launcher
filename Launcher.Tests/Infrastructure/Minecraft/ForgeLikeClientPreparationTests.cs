/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using Launcher.App.Logging;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class ForgeLikeClientPreparationTests : TestTempDirectory
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task MissingProcessorOutputRetainsSuccessfulInstallerContext()
    {
        using (var sink = LauncherLogConfiguration.CreateLogger(new LoggingLevelSwitch(LogEventLevel.Verbose),
            new LoggingLevelSwitch(LogEventLevel.Warning), TempRoot))
        using (var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, false)))
        using (var fixture = new Installation(TempRoot, LoaderKind.NeoForge, false, false, missingProcessorOutput: true)
        {
            DiagnosticLogger = factory.CreateLogger("ProcessorOutputTest")
        })
        {
            fixture.FinishInstaller.TrySetResult();
            fixture.FinishBaseContent.TrySetResult();
            fixture.FinishDependency.TrySetResult();
            using var cancellation = new CancellationTokenSource(TestTimeout);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StartAsync(cancellation.Token));
            Assert.True(fixture.JavaCompletedSignal.Task.IsCompletedSuccessfully);
        }

        var output = string.Join('\n', Directory.GetFiles(TempRoot, "bhl*.log").Select(File.ReadAllText));
        Assert.Contains("Stage=ArtifactValidation", output);
        Assert.Contains("missing-1.jar", output);
        Assert.Contains("ProcessId=42 ExitCode=0", output);
        Assert.Contains("test installer stdout", output);
        Assert.Contains("test installer stderr", output);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, false)]
    [InlineData(LoaderKind.NeoForge, false)]
    [InlineData(LoaderKind.Forge, true)]
    public async Task AssetsLibrariesAndMappingsStartWhileTheClientJarIsPending(LoaderKind loader, bool fallbackToOriginalInstaller)
    {
        using var fixture = new Installation(TempRoot, loader, false, false,
            withMappingsAndSharedDependency: true, fallbackToOriginalInstaller: fallbackToOriginalInstaller)
        {
            SupportsSplitBaseContent = true, HoldClient = true
        };
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var task = fixture.StartAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(fixture.InstallerStarted.Task, fixture.ClientStarted.Task,
                fixture.BaseContentStarted.Task, fixture.AssetsStarted.Task).WaitAsync(TestTimeout);
            fixture.FinishInstaller.TrySetResult();
            await Task.WhenAll(fixture.MappingsStarted.Task, fixture.DependencyStarted.Task).WaitAsync(TestTimeout);
            Assert.False(fixture.JavaStarted);
            fixture.FinishBaseContent.TrySetResult();
            fixture.FinishMappings.TrySetResult();
            fixture.FinishDependency.TrySetResult();
            Assert.False(fixture.JavaStarted);

            fixture.FinishClient.TrySetResult();
            await Task.WhenAll(fixture.JavaCompletedSignal.Task, fixture.RuntimeLibraryStarted.Task).WaitAsync(TestTimeout);
            Assert.False(Directory.Exists(fixture.FinalDirectory));
            fixture.FinishRuntimeLibrary.TrySetResult();
            fixture.FinishAssets.TrySetResult();
            Assert.Equal(Installation.FinalName, await task.WaitAsync(TestTimeout));
            Assert.Equal(1, fixture.ClientRequests);
            Assert.Equal(1, fixture.MappingsRequests);
        }
        finally
        {
            fixture.ReleaseGates();
        }
    }

    [Theory]
    [InlineData(LoaderKind.Forge, false)]
    [InlineData(LoaderKind.NeoForge, false)]
    [InlineData(LoaderKind.NeoForge, true)]
    public async Task JavaCanFinishBeforeAssetsButTheInstanceCannotPublish(LoaderKind loader, bool assetsFail)
    {
        using var fixture = new Installation(TempRoot, loader, false, false, useUnifiedCard: true)
        {
            SupportsSplitBaseContent = true, FailAssets = assetsFail
        };
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var task = fixture.StartAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(fixture.InstallerStarted.Task, fixture.BaseContentStarted.Task, fixture.AssetsStarted.Task)
                .WaitAsync(TestTimeout);
            fixture.FinishInstaller.TrySetResult();
            fixture.FinishBaseContent.TrySetResult();
            await fixture.DependencyStarted.Task.WaitAsync(TestTimeout);
            Assert.False(fixture.JavaStarted);
            fixture.FinishDependency.TrySetResult();
            await fixture.JavaCompletedSignal.Task.WaitAsync(TestTimeout);
            await fixture.WaitingForAssetsProgress.Task.WaitAsync(TestTimeout);
            Assert.False(task.IsCompleted);
            Assert.False(Directory.Exists(fixture.FinalDirectory));
            fixture.FinishAssets.TrySetResult();
            if (assetsFail)
            {
                var failure = await Assert.ThrowsAsync<MinecraftAssetInstallationException>(() => task.WaitAsync(TestTimeout));
                Assert.Same(fixture.BaseFailure, failure.InnerException);
                Assert.False(Directory.Exists(fixture.FinalDirectory));
                Assert.Empty(Directory.GetDirectories(fixture.InstallerRoot));
            }
            else
            {
                Assert.Equal(Installation.FinalName, await task.WaitAsync(TestTimeout));
                Assert.True(Directory.Exists(fixture.FinalDirectory));
            }
            Assert.Equal(1, fixture.ClientRequests);
            Assert.All(fixture.ProgressUpdates, progress => Assert.InRange(progress.Percent!.Value, 0, 99));
        }
        finally
        {
            fixture.ReleaseGates();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationWaitsForJavaBeforeRemovingTheSandbox(bool userCancels)
    {
        using var fixture = new Installation(TempRoot, LoaderKind.NeoForge, false, false, useUnifiedCard: true)
        {
            SupportsSplitBaseContent = true, FailAssets = !userCancels, HoldJava = true, HoldCanceledBranch = true
        };
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var task = fixture.StartAsync(cancellation.Token);
        try
        {
            fixture.FinishInstaller.TrySetResult();
            fixture.FinishBaseContent.TrySetResult();
            fixture.FinishDependency.TrySetResult();
            await Task.WhenAll(fixture.AssetsStarted.Task, fixture.JavaStartedSignal.Task).WaitAsync(TestTimeout);
            if (userCancels)
                await cancellation.CancelAsync();
            else
                fixture.FinishAssets.TrySetResult();
            await fixture.BranchCanceled.Task.WaitAsync(TestTimeout);
            Assert.Single(Directory.GetDirectories(fixture.InstallerRoot));
            Assert.False(task.IsCompleted);
            fixture.FinishCanceledBranch.TrySetResult();
            var failure = await Record.ExceptionAsync(() => task.WaitAsync(TestTimeout));
            if (userCancels)
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else
                Assert.Same(fixture.BaseFailure, Assert.IsType<MinecraftAssetInstallationException>(failure).InnerException);
            Assert.Empty(Directory.GetDirectories(fixture.InstallerRoot));
            Assert.False(Directory.Exists(fixture.FinalDirectory));
            Assert.All(fixture.ProgressUpdates, progress => Assert.InRange(progress.Percent!.Value, 0, 99));
        }
        finally
        {
            fixture.ReleaseGates();
        }
    }

    [Theory]
    [InlineData(LoaderKind.NeoForge, false, false)]
    [InlineData(LoaderKind.Forge, true, false)]
    [InlineData(LoaderKind.Forge, true, true)]
    public async Task PreparesBaseAndInstallerBeforePublishingTheDerivedInstance(LoaderKind loader, bool warmCache, bool legacyMetadata)
    {
        using var fixture = new Installation(TempRoot, loader, warmCache, legacyMetadata);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var task = fixture.StartAsync(cancellation.Token);
        try
        {
            await fixture.InstallerStarted.Task.WaitAsync(TestTimeout);
            if (!legacyMetadata)
            {
                // Neither branch can get here if the other is awaited serially.
                await fixture.BaseContentStarted.Task.WaitAsync(TestTimeout);
                fixture.FinishInstaller.TrySetResult();
                Assert.False(fixture.JavaStarted);
                Assert.False(fixture.DependencyStarted.Task.IsCompleted);
                fixture.FinishBaseContent.TrySetResult();
            }
            else
            {
                fixture.FinishInstaller.TrySetResult();
            }

            await fixture.DependencyStarted.Task.WaitAsync(TestTimeout);
            Assert.False(fixture.JavaStarted);
            Assert.False(Directory.Exists(fixture.FinalDirectory));
            fixture.FinishDependency.TrySetResult();
            Assert.Equal(Installation.FinalName, await task.WaitAsync(TestTimeout));

            Assert.Equal(warmCache ? 0 : 1, fixture.ClientRequests);
            Assert.Equal(!legacyMetadata, fixture.BaseContentStarted.Task.IsCompletedSuccessfully);
            Assert.True(fixture.JavaStarted);
            Assert.Equal("verified vanilla jar", File.ReadAllText(Path.Combine(fixture.FinalDirectory, Installation.FinalName + ".jar")));
            Assert.True(File.Exists(fixture.DerivedLibrary));
            Assert.Empty(Directory.GetDirectories(fixture.InstallerRoot));
        }
        finally
        {
            fixture.ReleaseGates();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WaitsForBothBranchesBeforeCleaningUpAfterFailureOrCancellation(bool installerFails, bool userCancels)
    {
        using var fixture = new Installation(TempRoot, LoaderKind.NeoForge, false, false)
        {
            FailInstaller = installerFails,
            HoldCanceledBranch = true,
            FailBase = !installerFails && !userCancels
        };
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var task = fixture.StartAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(fixture.InstallerStarted.Task, fixture.BaseContentStarted.Task).WaitAsync(TestTimeout);
            if (userCancels)
                await cancellation.CancelAsync();
            else if (installerFails)
                fixture.FinishInstaller.TrySetResult();
            else
                fixture.FinishBaseContent.TrySetResult();

            await fixture.BranchCanceled.Task.WaitAsync(TestTimeout);
            Assert.False(task.IsCompleted);
            Assert.Single(Directory.GetDirectories(fixture.InstallerRoot));
            Assert.False(fixture.JavaStarted);
            Assert.False(Directory.Exists(fixture.FinalDirectory));
            fixture.FinishCanceledBranch.TrySetResult();

            var failure = await Record.ExceptionAsync(() => task.WaitAsync(TestTimeout));
            if (userCancels)
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else if (installerFails)
                Assert.IsType<InvalidDataException>(failure);
            else
                Assert.Same(fixture.BaseFailure, failure);
            Assert.Empty(Directory.GetDirectories(fixture.InstallerRoot));
            Assert.True(File.Exists(fixture.SharedLibrary));
        }
        finally
        {
            fixture.ReleaseGates();
        }
    }

    private sealed class Installation : HttpMessageHandler, IFinalVersionInstaller, IForgeInstallerRunner, ILoaderInstallerJavaRuntimeResolver
    {
        public const string FinalName = "Prepared instance";
        private const string ClientJar = "verified vanilla jar";
        private const string Dependency = "installer library";
        private const string DependencyPath = "example/preinstall/1/preinstall-1.jar";
        private const string SharedDependency = "shared vanilla library";
        private const string SharedDependencyPath = "example/shared/1/shared-1.jar";
        private const string ClientUrl = "https://piston-data.mojang.com/v1/objects/test/client.jar";
        private const string DependencyUrl = "https://libraries.minecraft.net/" + DependencyPath;
        private const string SharedDependencyUrl = "https://libraries.minecraft.net/" + SharedDependencyPath;
        private const string Mappings = "verified client mappings";
        private const string MappingsUrl = "https://piston-data.mojang.com/v1/objects/test/client.txt";
        private const string RuntimeLibrary = "loader runtime library";
        private const string RuntimeLibraryPath = "example/runtime/1/runtime-1.jar";
        private const string RuntimeLibraryUrl = "https://libraries.minecraft.net/" + RuntimeLibraryPath;
        private readonly LoaderKind loader;
        private readonly string version;
        private readonly string loaderVersion;
        private readonly string shared;
        private readonly string output;
        private readonly string sandbox;
        private readonly string metadata;
        private readonly byte[] installerBytes;
        private readonly HttpClient client;
        private readonly bool legacyMetadata;
        private readonly bool withMappingsAndSharedDependency;
        private readonly bool fallbackToOriginalInstaller;
        private readonly ProgressCapture progressCapture = new();
        private readonly IProgress<LauncherProgress> progress;
        private int clientRequests;
        private int mappingsRequests;
        public int ClientRequests => Volatile.Read(ref clientRequests);
        public int MappingsRequests => Volatile.Read(ref mappingsRequests);
        public IEnumerable<LauncherProgress> ProgressUpdates => progressCapture.Updates;
        public TaskCompletionSource WaitingForAssetsProgress => progressCapture.WaitingForAssets;
        public bool JavaStarted { get; private set; }
        public bool FailBase { get; init; }
        public bool FailInstaller { get; init; }
        public bool FailAssets { get; init; }
        public bool HoldJava { get; init; }
        public bool HoldClient { get; init; }
        public bool SupportsSplitBaseContent { get; init; }
        public bool HoldCanceledBranch { get; init; }
        public ILogger? DiagnosticLogger { get; init; }
        public Exception BaseFailure { get; } = new InvalidOperationException("Base branch failed.");
        public string InstallerRoot => Path.Combine(sandbox, loader == LoaderKind.Forge ? "launcher-forge" : "launcher-neoforge");
        public string FinalDirectory => Path.Combine(output, "versions", FinalName);
        public string SharedLibrary => Path.Combine(shared, "libraries", "prepared.jar");
        public string DerivedLibrary => Path.Combine(shared, "libraries", "example", "derived", "1", "derived-1.jar");
        public TaskCompletionSource InstallerStarted { get; } = NewGate();
        public TaskCompletionSource BaseContentStarted { get; } = NewGate();
        public TaskCompletionSource AssetsStarted { get; } = NewGate();
        public TaskCompletionSource FinishAssets { get; } = NewGate();
        public TaskCompletionSource JavaStartedSignal { get; } = NewGate();
        public TaskCompletionSource JavaCompletedSignal { get; } = NewGate();
        public TaskCompletionSource FinishJava { get; } = NewGate();
        public TaskCompletionSource DependencyStarted { get; } = NewGate();
        public TaskCompletionSource ClientStarted { get; } = NewGate();
        public TaskCompletionSource MappingsStarted { get; } = NewGate();
        public TaskCompletionSource RuntimeLibraryStarted { get; } = NewGate();
        public TaskCompletionSource BranchCanceled { get; } = NewGate();
        public TaskCompletionSource FinishInstaller { get; } = NewGate();
        public TaskCompletionSource FinishBaseContent { get; } = NewGate();
        public TaskCompletionSource FinishDependency { get; } = NewGate();
        public TaskCompletionSource FinishClient { get; } = NewGate();
        public TaskCompletionSource FinishMappings { get; } = NewGate();
        public TaskCompletionSource FinishRuntimeLibrary { get; } = NewGate();
        public TaskCompletionSource FinishCanceledBranch { get; } = NewGate();
        private string MetadataUrl => $"https://piston-meta.mojang.com/v1/packages/test/{version}.json";
        private string MappingsPath => $"net/minecraft/client/{version}/client-{version}-mappings.txt";
        private string InstallerUrl => loader == LoaderKind.Forge
            ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}-{loaderVersion}/forge-{version}-{loaderVersion}-installer.jar"
            : NeoForgeArtifactResolver.ResolveInstaller(version, loaderVersion).Url;

        public Installation(string root, LoaderKind loader, bool warmCache, bool legacyMetadata,
            bool withMappingsAndSharedDependency = false, bool fallbackToOriginalInstaller = false, bool useUnifiedCard = false,
            bool missingProcessorOutput = false)
        {
            this.loader = loader;
            this.legacyMetadata = legacyMetadata;
            this.withMappingsAndSharedDependency = withMappingsAndSharedDependency;
            this.fallbackToOriginalInstaller = fallbackToOriginalInstaller;
            version = loader == LoaderKind.Forge ? "1.20.1" : "1.21.1";
            loaderVersion = loader == LoaderKind.Forge ? "47.2.0" : "21.1.250";
            shared = Path.Combine(root, "shared");
            output = Path.Combine(root, "output");
            sandbox = Path.Combine(root, "sandbox");
            var download = new JsonObject { ["sha1"] = Hash(ClientJar), ["size"] = ClientJar.Length };
            if (!legacyMetadata)
                download["url"] = ClientUrl;
            var downloads = new JsonObject { ["client"] = download };
            var libraries = new JsonArray();
            if (withMappingsAndSharedDependency)
            {
                downloads["client_mappings"] = DownloadMetadata(MappingsUrl, Mappings);
                libraries.Add(SharedLibraryMetadata());
            }
            metadata = new JsonObject
            {
                ["id"] = version, ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main",
                ["downloads"] = downloads, ["libraries"] = libraries
            }.ToJsonString();
            if (warmCache)
                WriteFile(Path.Combine(shared, "versions", version, version + ".jar"), ClientJar);
            installerBytes = CreateInstaller(missingProcessorOutput);
            client = new HttpClient(this, disposeHandler: false);
            progress = new InstallCardProgressMapper(progressCapture, loader, hasOptionalContent: false, useUnifiedCard);
        }

        public Task<string> StartAsync(CancellationToken token) => loader == LoaderKind.Forge
            ? new ForgeLoaderProvider(client, this, this, sandbox, logger: DiagnosticLogger, javaRuntimeResolver: this)
                .InstallStagedAsync(version, output, shared, FinalName, loaderVersion, progress, DownloadSourcePreference.Official, token, 0)
            : new NeoForgeLoaderProvider(client, this, this, sandbox, logger: DiagnosticLogger, javaRuntimeResolver: this)
                .InstallStagedAsync(version, output, shared, FinalName, loaderVersion, progress, DownloadSourcePreference.Official, token, 0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var payload = FailInstaller ? "invalid installer archive"u8.ToArray() : installerBytes;
            if (url == InstallerUrl + ".sha1")
                return Reply(Hash(payload));
            if (url == InstallerUrl)
            {
                InstallerStarted.TrySetResult();
                await WaitForBranchAsync(FinishInstaller.Task, token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            }
            if (request.RequestUri.Host == "files.minecraftforge.net")
                return Reply($"<html>{InstallerUrl}</html>");
            if (request.RequestUri.AbsolutePath == "/mc/game/version_manifest_v2.json")
                return Reply(JsonSerializer.Serialize(new { versions = new[] { new { id = version, url = MetadataUrl, sha1 = Hash(metadata) } } }));
            if (url == MetadataUrl)
                return Reply(metadata);
            if (url == ClientUrl)
            {
                Interlocked.Increment(ref clientRequests);
                ClientStarted.TrySetResult();
                if (HoldClient)
                    await FinishClient.Task.WaitAsync(token);
                return Reply(ClientJar);
            }
            if (url == MappingsUrl && withMappingsAndSharedDependency)
            {
                Interlocked.Increment(ref mappingsRequests);
                MappingsStarted.TrySetResult();
                await FinishMappings.Task.WaitAsync(token);
                return Reply(Mappings);
            }
            if (url == RuntimeLibraryUrl && withMappingsAndSharedDependency)
            {
                Assert.True(JavaCompletedSignal.Task.IsCompletedSuccessfully);
                RuntimeLibraryStarted.TrySetResult();
                await FinishRuntimeLibrary.Task.WaitAsync(token);
                return Reply(RuntimeLibrary);
            }
            if (url == DependencyUrl)
            {
                DependencyStarted.TrySetResult();
                await FinishDependency.Task.WaitAsync(token);
                return Reply(Dependency);
            }
            throw new InvalidOperationException($"Unexpected test request {url}");
        }

        public Task InstallAsync(string gameDirectory, string versionName, DownloadSourcePreference preference,
            IProgress<LauncherProgress>? progress, CancellationToken token, int downloadSpeedLimitMbPerSecond)
            => throw new NotSupportedException();

        public async Task InstallAsync(MinecraftPath path, string versionName, MinecraftDownloadOperationContext operation,
            DownloadSourcePreference preference, IProgress<LauncherProgress>? progress, CancellationToken token,
            int downloadSpeedLimitMbPerSecond, SpeedMeter? speedMeter)
        {
            if (versionName == version)
            {
                Assert.False(legacyMetadata);
                if (!SupportsSplitBaseContent)
                    Assert.Equal(ClientJar, File.ReadAllText(Path.Combine(path.Versions, version, version + ".jar")));
                Assert.True(File.Exists(Path.Combine(path.Versions, version, version + ".json")));
                WriteFile(SharedLibrary, "verified library");
                BaseContentStarted.TrySetResult();
                await WaitForBranchAsync(FinishBaseContent.Task, token);
                if (FailBase)
                    throw BaseFailure;
                if (withMappingsAndSharedDependency)
                    WriteFile(Path.Combine(path.Library, SharedDependencyPath), SharedDependency);
                if (!SupportsSplitBaseContent)
                    WriteFile(Path.Combine(path.Assets, "prepared.asset"), "vanilla asset");
                progress?.Report(new LauncherProgress(LaunchProgressStages.DownloadingFiles, string.Empty, 100));
                return;
            }

            Assert.Equal(ClientJar, File.ReadAllText(Path.Combine(path.Versions, versionName, versionName + ".jar")));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path.Versions, versionName, versionName + ".json")));
            Assert.Contains(document.RootElement.GetProperty("libraries").EnumerateArray(),
                library => library.GetProperty("name").GetString() == "example:derived:1");
            WriteFile(DerivedLibrary, "derived library");
        }

        public async Task InstallBasePartAsync(MinecraftPath path, string versionName, MinecraftDownloadOperationContext operation,
            DownloadSourcePreference preference, IProgress<LauncherProgress>? progress, CancellationToken token,
            int speedLimit, SpeedMeter? speedMeter, LoaderBaseContentPart part)
        {
            if (part is LoaderBaseContentPart.WithoutClientOrAssets)
            {
                await InstallAsync(path, versionName, operation, preference, progress, token, speedLimit, speedMeter);
                return;
            }
            Assert.Equal(LoaderBaseContentPart.Assets, part);
            AssetsStarted.TrySetResult();
            await FinishAssets.Task.WaitAsync(token);
            if (FailAssets)
                throw BaseFailure;
            WriteFile(Path.Combine(path.Assets, "prepared.asset"), "vanilla asset");
        }

        public async Task<InstallerProcessResult?> RunInstallerAsync(string javaExecutablePath, string installerJarPath, string minecraftDirectory, CancellationToken token)
        {
            JavaStarted = true;
            Assert.Equal(ClientJar, File.ReadAllText(Path.Combine(minecraftDirectory, "versions", version, version + ".jar")));
            Assert.Equal(Dependency, File.ReadAllText(Path.Combine(minecraftDirectory, "libraries", DependencyPath)));
            if (withMappingsAndSharedDependency)
            {
                Assert.Equal(!fallbackToOriginalInstaller, Path.GetFileName(installerJarPath).Contains(".bhl-managed-", StringComparison.Ordinal));
                Assert.Equal(SharedDependency, File.ReadAllText(Path.Combine(minecraftDirectory, "libraries", SharedDependencyPath)));
                Assert.Equal(Mappings, File.ReadAllText(Path.Combine(minecraftDirectory, "libraries", MappingsPath)));
            }
            if (!legacyMetadata)
            {
                if (!SupportsSplitBaseContent)
                    Assert.Equal("vanilla asset", File.ReadAllText(Path.Combine(shared, "assets", "prepared.asset")));
                var unified = progress is IParallelInstallProgress { TracksParallelInstall: true };
                Assert.Contains(progressCapture.Updates, value => unified
                    ? value.Stage == InstallProgressStages.DownloadingGameFiles && value.Percent == 20
                    : value.Stage == ImportProgressStages.InstallingMinecraftBase && value.Percent == 26);
            }
            Assert.All(progressCapture.Updates, value => Assert.InRange(value.Percent!.Value, 0, 30));
            JavaStartedSignal.TrySetResult();
            if (HoldJava)
                await WaitForBranchAsync(FinishJava.Task, token);
            var id = loader == LoaderKind.Forge
                ? $"{version}-forge-{loaderVersion}"
                : NeoForgeArtifactResolver.ResolveInstaller(version, loaderVersion).ExpectedVersionId;
            WriteFile(Path.Combine(minecraftDirectory, "versions", id, id + ".json"), JsonSerializer.Serialize(new
            {
                id, inheritsFrom = version, libraries = new[] { new { name = "example:derived:1" } }
            }));
            JavaCompletedSignal.TrySetResult();
            return new InstallerProcessResult(42, "test installer stdout", "test installer stderr", null);
        }

        public Task<JavaRuntimeInfo> ResolveAsync(LoaderInstallerJavaRuntimeRequest request, CancellationToken token)
            => Task.FromResult(new JavaRuntimeInfo("Test Java", "21", 21, "x64", "java.exe", ".", "Test"));

        public void ReleaseGates()
        {
            FinishBaseContent.TrySetResult();
            FinishInstaller.TrySetResult();
            FinishDependency.TrySetResult();
            FinishClient.TrySetResult();
            FinishMappings.TrySetResult();
            FinishRuntimeLibrary.TrySetResult();
            FinishAssets.TrySetResult();
            FinishJava.TrySetResult();
            FinishCanceledBranch.TrySetResult();
            client.Dispose();
        }

        private async Task WaitForBranchAsync(Task signal, CancellationToken token)
        {
            try { await signal.WaitAsync(token); }
            catch (OperationCanceledException) when (HoldCanceledBranch)
            {
                BranchCanceled.TrySetResult();
                await FinishCanceledBranch.Task.WaitAsync(TestTimeout);
                throw;
            }
        }

        private byte[] CreateInstaller(bool missingProcessorOutput)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var library = DownloadMetadata(DependencyUrl, Dependency);
                library["path"] = DependencyPath;
                var libraries = new JsonArray(new JsonObject
                {
                    ["name"] = "example:preinstall:1", ["downloads"] = new JsonObject { ["artifact"] = library }
                });
                var profile = new JsonObject { ["minecraft"] = version, ["libraries"] = libraries };
                if (missingProcessorOutput)
                {
                    profile["processors"] = new JsonArray(new JsonObject
                    {
                        ["jar"] = "example:preinstall:1",
                        ["outputs"] = new JsonObject { ["[example:missing:1]"] = Hash("expected output") }
                    });
                }
                if (withMappingsAndSharedDependency)
                {
                    libraries.Add(SharedLibraryMetadata());
                    var mappingsArtifact = DownloadMetadata(MappingsUrl, Mappings);
                    mappingsArtifact["path"] = MappingsPath;
                    libraries.Add(new JsonObject
                    {
                        ["name"] = $"net.minecraft:client:{version}:mappings@txt",
                        ["downloads"] = new JsonObject { ["artifact"] = mappingsArtifact }
                    });
                    profile["processors"] = new JsonArray(new JsonObject
                    {
                        ["jar"] = "net.minecraftforge:installertools:1",
                        ["args"] = new JsonArray("--task", "DOWNLOAD_MOJMAPS", "--version", version, "--side", "client",
                            "--output", $"[net.minecraft:client:{version}:mappings@txt]"),
                        ["outputs"] = new JsonObject { ["[example:runtime:1]"] = Hash(RuntimeLibrary) }
                    });
                    var runtimeArtifact = DownloadMetadata(RuntimeLibraryUrl, RuntimeLibrary);
                    runtimeArtifact["path"] = RuntimeLibraryPath;
                    WriteEntry("version.json", new JsonObject
                    {
                        ["libraries"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "example:runtime:1",
                            ["downloads"] = new JsonObject { ["artifact"] = runtimeArtifact }
                        })
                    }.ToJsonString());
                    WriteEntry("maven/net/minecraftforge/installertools/1/installertools-1.jar", "test tool");
                    if (!fallbackToOriginalInstaller)
                        WriteEntry("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nMain-Class: example.Installer\r\n\r\n");
                }
                WriteEntry("install_profile.json", profile.ToJsonString());

                void WriteEntry(string path, string contents)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                    writer.Write(contents);
                }
            }
            return stream.ToArray();
        }

        private static JsonObject SharedLibraryMetadata()
        {
            var artifact = DownloadMetadata(SharedDependencyUrl, SharedDependency);
            artifact["path"] = SharedDependencyPath;
            return new JsonObject
            {
                ["name"] = "example:shared:1", ["downloads"] = new JsonObject { ["artifact"] = artifact }
            };
        }

        private static JsonObject DownloadMetadata(string url, string contents) => new()
        {
            ["url"] = url, ["sha1"] = Hash(contents), ["size"] = Encoding.UTF8.GetByteCount(contents)
        };

        private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
        private sealed class ProgressCapture : IProgress<LauncherProgress>
        {
            public ConcurrentQueue<LauncherProgress> Updates { get; } = new();
            public TaskCompletionSource WaitingForAssets { get; } = NewGate();
            public void Report(LauncherProgress value)
            {
                Updates.Enqueue(value);
                if (value.Stage == InstallProgressStages.DownloadingGameFiles && value.Percent == 25)
                    WaitingForAssets.TrySetResult();
            }
        }
        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
