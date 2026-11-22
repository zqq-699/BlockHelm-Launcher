/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
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

public sealed class LoaderInstallerMappingsTests : TestTempDirectory
{
    private const string MinecraftVersion = "1.21.1";
    private const string RelativePath = "net/minecraft/client/1.21.1/client-1.21.1-mappings.txt";
    private static readonly byte[] Mappings = "net.minecraft.Client -> abc:\n"u8.ToArray();
    private static readonly string MappingUrl = $"https://piston-data.mojang.com/v1/objects/{Hash(Mappings)}/client.txt";

    [Theory]
    [InlineData("net.minecraftforge", false)]
    [InlineData("net.neoforged.installertools", false)]
    [InlineData("net.neoforged.installertools", true)]
    public async Task ClientPreparationDownloadsMappingsAndPreservesOriginalInstallerAndOtherProcessors(string tool, bool inline)
    {
        var (installer, profile, plan) = await CreateInstallerAsync(tool, inline);
        var original = await File.ReadAllBytesAsync(installer);
        var handler = new MappingHandler();
        using var client = new HttpClient(handler);
        var logger = new PreparationLogger();
        var service = new LoaderInstallerArtifactService(client, logger: logger);
        var game = Path.Combine(TempRoot, "game");

        var prepared = await service.PrepareInstallerMappingsAsync(installer, plan, MinecraftVersion, game,
            Metadata(), DownloadSourcePreference.Official, 0, CancellationToken.None);

        Assert.Equal(original, await File.ReadAllBytesAsync(installer));
        Assert.Equal([MappingUrl], handler.Requests.Select(uri => uri.AbsoluteUri));
        Assert.Equal(Mappings, await File.ReadAllBytesAsync(Path.Combine(game, "libraries", RelativePath)));
        Assert.NotEqual(installer, prepared.ExecutionInstallerPath);
        var summary = Assert.Single(logger.Messages.Where(message => message.Level == LogLevel.Information
            && message.Text.Contains("Installer mappings prepared"))).Text;
        Assert.Contains($"OriginalInstallerSha256={Convert.ToHexString(SHA256.HashData(original))}", summary);
        Assert.Contains($"ExecutionInstallerSha256={Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(prepared.ExecutionInstallerPath)))}", summary);
        Assert.Contains("ProcessorIndices=0", summary);
        using (var archive = ZipFile.OpenRead(prepared.ExecutionInstallerPath))
        {
            using var reader = new StreamReader(archive.GetEntry("install_profile.json")!.Open());
            var patched = JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Equal(profile["processors"]![1]!.ToJsonString(), Assert.Single(patched["processors"]!.AsArray())!.ToJsonString());
            Assert.Null(archive.GetEntry("META-INF/BHL.SF"));
            Assert.Null(archive.GetEntry("META-INF/BHL.RSA"));
            Assert.NotNull(archive.GetEntry("META-INF/MANIFEST.MF"));
            using var resource = new StreamReader(archive.GetEntry("data/unchanged.txt")!.Open());
            Assert.Equal("unchanged", await resource.ReadToEndAsync());
        }

        var input = Assert.Single(prepared.Plan.PrerequisiteLibraries);
        Assert.Equal(Hash(Mappings), input.Artifact.Sha1, ignoreCase: true);
        Assert.Equal(MappingUrl, input.Artifact.Url);
        var verified = await service.ValidatePublishedArtifactsAsync(game, prepared.Plan, CancellationToken.None);
        Assert.Empty(verified);
        var shared = Path.Combine(TempRoot, "shared");
        await new LoaderInstallerPrerequisiteSeeder().PublishDeltaAsync(
            new LoaderInstallerWorkspaceSnapshot(game, new Dictionary<string, string>()), shared, CancellationToken.None,
            LoaderInstallerArtifactService.CreateTrustedSharedLibraryExpectations(prepared.Plan));
        var version = Path.Combine(TempRoot, "versions", "test");
        var identity = new GameFileLoaderIdentity(LoaderKind.NeoForge, MinecraftVersion, "21.1.250");
        await LoaderArtifactManifestStore.WriteAsync(version, shared, identity, installer, prepared.Plan, verified, CancellationToken.None);
        var manifest = await LoaderArtifactManifestStore.ReadAsync(version, identity, CancellationToken.None);
        var recorded = Assert.Single(manifest.Manifest!.Artifacts);
        Assert.Equal(LoaderArtifactKind.InstallerPrerequisite, recorded.Kind);
        Assert.Equal(GameFileVerificationLevel.HashVerified, recorded.VerificationLevel);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), manifest.Manifest.InstallerSha256, ignoreCase: true);

        await File.WriteAllBytesAsync(Path.Combine(game, "libraries", RelativePath), new byte[Mappings.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidatePublishedArtifactsAsync(game, prepared.Plan, CancellationToken.None));
    }

    [Fact]
    public async Task PreferredSourceFailureUsesMirrorAndValidLocalMappingsNeedNoNetwork()
    {
        var (installer, _, plan) = await CreateInstallerAsync();
        var handler = new MappingHandler(failOfficial: true);
        using var client = new HttpClient(handler);
        var service = new LoaderInstallerArtifactService(client);
        var game = Path.Combine(TempRoot, "game");
        await service.PrepareInstallerMappingsAsync(installer, plan, MinecraftVersion, game,
            Metadata(), DownloadSourcePreference.Official, 0, CancellationToken.None);
        Assert.Equal(new[] { "piston-data.mojang.com", "bmclapi2.bangbang93.com" }, handler.Requests.Select(uri => uri.Host).Distinct());
        handler.Requests.Clear();
        using var operationContext = new MinecraftDownloadOperationContext(game);
        await service.PrepareInstallerMappingsAsync(installer, plan, MinecraftVersion, game,
            Metadata(), DownloadSourcePreference.BmclApi, 0, CancellationToken.None, operationContext);
        Assert.Empty(handler.Requests);
        var destination = Path.Combine(game, "libraries", RelativePath);
        var integrity = DownloadIntegrityExpectation.Sha1(Hash(Mappings), Mappings.Length);
        Assert.True(operationContext.IsVerified(destination, integrity));
        // The cached verification must cease to apply after an external content change.
        await File.WriteAllBytesAsync(destination, new byte[Mappings.Length]);
        Assert.False(operationContext.IsVerified(destination, integrity));
    }

    [Theory]
    [InlineData("server-only")]
    [InlineData("server-side")]
    [InlineData("foreign-tool")]
    [InlineData("other-task")]
    [InlineData("legacy")]
    public async Task NonClientMappingTasksLeaveInstallerAndNetworkUntouched(string scenario)
    {
        var (installer, profile, plan) = await CreateInstallerAsync();
        var processor = profile["processors"]![0]!;
        if (scenario == "server-only") processor["sides"] = new JsonArray("server");
        if (scenario == "server-side") processor["args"]![5] = "server";
        if (scenario == "foreign-tool") processor["jar"] = "com.example:installertools:1.0";
        if (scenario == "other-task") processor["args"]![1] = "OTHER_TASK";
        if (scenario == "legacy") profile.Remove("processors");
        WriteInstaller(installer, profile);
        var original = await File.ReadAllBytesAsync(installer);
        var handler = new MappingHandler();
        using var client = new HttpClient(handler);
        var prepared = await new LoaderInstallerArtifactService(client).PrepareInstallerMappingsAsync(
            installer, plan, MinecraftVersion, Path.Combine(TempRoot, "game"), null,
            DownloadSourcePreference.Official, 0, CancellationToken.None);
        Assert.Equal(installer, prepared.ExecutionInstallerPath);
        Assert.Same(plan, prepared.Plan);
        Assert.Empty(handler.Requests);
        Assert.Equal(original, await File.ReadAllBytesAsync(installer));
        Assert.Empty(Directory.GetFiles(TempRoot, "*.bhl-managed-*.jar"));
    }

    [Theory]
    [InlineData("missing-sha1")]
    [InlineData("version-mismatch")]
    [InlineData("unknown-version-expression")]
    [InlineData("unknown-output-expression")]
    [InlineData("plain-output")]
    [InlineData("missing-side")]
    public async Task UnsupportedMappingPreparationFallsBackBeforeDownloadWithoutChangingPlan(string failure)
    {
        var (installer, profile, plan) = await CreateInstallerAsync();
        var metadata = Metadata();
        if (failure == "missing-sha1") metadata["downloads"]!["client_mappings"]!.AsObject().Remove("sha1");
        if (failure == "version-mismatch") profile["processors"]![0]!["args"]![3] = "1.20.1";
        if (failure == "unknown-version-expression") profile["processors"]![0]!["args"]![3] = "{FUTURE_VERSION}";
        if (failure == "unknown-output-expression") profile["data"]!["MOJMAPS"]!["client"] = "[net.minecraft:client:{FUTURE_VERSION}:mappings@txt]";
        if (failure == "plain-output") profile["data"]!["MOJMAPS"]!["client"] = "libraries/client-mappings.txt";
        if (failure == "missing-side") profile["processors"]![0]!["args"]!.AsArray().RemoveAt(5);
        WriteInstaller(installer, profile);
        var original = await File.ReadAllBytesAsync(installer);
        var handler = new MappingHandler();
        using var client = new HttpClient(handler);

        var logger = new PreparationLogger();
        var prepared = await new LoaderInstallerArtifactService(client, logger: logger).PrepareInstallerMappingsAsync(
            installer, plan, MinecraftVersion, Path.Combine(TempRoot, "game"), metadata,
            DownloadSourcePreference.Official, 0, CancellationToken.None);

        Assert.Equal(installer, prepared.ExecutionInstallerPath);
        Assert.Same(plan, prepared.Plan);
        Assert.Contains(logger.Messages, message => message.Level == LogLevel.Warning && message.Text.Contains("Side=client"));
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(TempRoot, "*.bhl-managed-*.jar"));
        Assert.Equal(original, await File.ReadAllBytesAsync(installer));
    }

    [Theory]
    [InlineData("[net.minecraft:client:../../outside:mappings@txt]")]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    public async Task UnsafeOutputStillFailsBeforeDownload(string output)
    {
        var (installer, profile, plan) = await CreateInstallerAsync();
        profile["data"]!["MOJMAPS"]!["client"] = output;
        WriteInstaller(installer, profile);
        var handler = new MappingHandler();
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LoaderInstallerArtifactService(client).PrepareInstallerMappingsAsync(installer, plan, MinecraftVersion,
                Path.Combine(TempRoot, "game"), Metadata(), DownloadSourcePreference.Official, 0, CancellationToken.None));
        Assert.Contains("UnsafeOutputPath", SafeExceptionLog.Format(exception));
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(TempRoot, "*.bhl-managed-*.jar"));
    }

    [Fact]
    public async Task MinecraftVersionPlaceholderIsExpandedAndAllCompletedTasksAreRemoved()
    {
        var (installer, profile, plan) = await CreateInstallerAsync();
        profile["processors"]![0]!["args"]![3] = "{MINECRAFT_VERSION}";
        profile["processors"]!.AsArray().Add(profile["processors"]![0]!.DeepClone());
        WriteInstaller(installer, profile);
        var handler = new MappingHandler();
        using var client = new HttpClient(handler);
        var prepared = await new LoaderInstallerArtifactService(client).PrepareInstallerMappingsAsync(installer, plan,
            MinecraftVersion, Path.Combine(TempRoot, "game"), Metadata(), DownloadSourcePreference.Official, 0, CancellationToken.None);
        Assert.NotEqual(installer, prepared.ExecutionInstallerPath);
        using var archive = ZipFile.OpenRead(prepared.ExecutionInstallerPath);
        using var reader = new StreamReader(archive.GetEntry("install_profile.json")!.Open());
        var remaining = Assert.Single(JsonNode.Parse(await reader.ReadToEndAsync())!["processors"]!.AsArray());
        Assert.Equal("com.example:processor:1.0", remaining!["jar"]!.GetValue<string>());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Manifest-Version: 1.0\r\n\r\nName: resource\r\nMain-Class: wrong.Section\r\n")]
    public async Task ExecutionCopyWithoutMainEntryPointIsDeletedAndOriginalPlanIsRestored(string? manifest)
    {
        var (installer, profile, plan) = await CreateInstallerAsync();
        WriteInstaller(installer, profile, manifest);
        var original = await File.ReadAllBytesAsync(installer);
        var handler = new MappingHandler();
        using var client = new HttpClient(handler);
        var logger = new PreparationLogger();
        var prepared = await new LoaderInstallerArtifactService(client, logger: logger).PrepareInstallerMappingsAsync(
            installer, plan, MinecraftVersion, Path.Combine(TempRoot, "game"), Metadata(),
            DownloadSourcePreference.Official, 0, CancellationToken.None);
        Assert.Equal(installer, prepared.ExecutionInstallerPath);
        Assert.Same(plan, prepared.Plan);
        Assert.Equal(original, await File.ReadAllBytesAsync(installer));
        Assert.Empty(Directory.GetFiles(TempRoot, "*.bhl-managed-*.jar"));
        Assert.Contains(logger.Messages, message => message.Level == LogLevel.Warning && message.Text.Contains("Reason=ExecutionCopyValidation"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HashFailureOrCancellationDoesNotPrepareExecutionCopyOrReplaceExistingFile(bool cancel)
    {
        var (installer, _, plan) = await CreateInstallerAsync();
        var original = await File.ReadAllBytesAsync(installer);
        using var cancellation = new CancellationTokenSource();
        var handler = new MappingHandler(corrupt: true, cancel: cancel ? cancellation : null);
        using var client = new HttpClient(handler);
        var game = Path.Combine(TempRoot, "game");
        var destination = Path.Combine(game, "libraries", RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var previous = "previous-file"u8.ToArray();
        await File.WriteAllBytesAsync(destination, previous);
        var preparation = new LoaderInstallerArtifactService(client).PrepareInstallerMappingsAsync(
            installer, plan, MinecraftVersion, game, Metadata(), DownloadSourcePreference.Official, 0, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        else await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() => preparation);
        Assert.Empty(Directory.GetFiles(TempRoot, "*.bhl-managed-*.jar"));
        Assert.Equal(original, await File.ReadAllBytesAsync(installer));
        Assert.Equal(previous, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
    }

    private Task<(string Path, JsonObject Profile, ForgeInstallerPlan Plan)> CreateInstallerAsync(
        string tool = "net.neoforged.installertools", bool inline = false)
    {
        var args = inline
            ? new JsonArray("--task=DOWNLOAD_MOJMAPS", "--version=1.21.1", "--side={SIDE}", "--output={MOJMAPS}")
            : new JsonArray("--task", "DOWNLOAD_MOJMAPS", "--version", MinecraftVersion, "--side", "{SIDE}", "--output", "{MOJMAPS}");
        var profile = new JsonObject
        {
            ["data"] = new JsonObject { ["MOJMAPS"] = new JsonObject { ["client"] = "[net.minecraft:client:1.21.1:mappings@txt]" } },
            ["processors"] = new JsonArray(
                new JsonObject { ["jar"] = $"{tool}:installertools:1.0", ["args"] = args },
                new JsonObject { ["jar"] = "com.example:processor:1.0", ["args"] = new JsonArray("--keep", "{MOJMAPS}") })
        };
        var path = Path.Combine(TempRoot, "installer.jar");
        WriteInstaller(path, profile);
        var plan = new ForgeInstallerPlan([], [], [new ForgeProcessorOutput(RelativePath, null)], new JsonArray());
        return Task.FromResult((path, profile, plan));
    }

    private static void WriteInstaller(string path, JsonObject profile,
        string? manifest = "Manifest-Version: 1.0\r\nMain-Class: example.Installer\r\n\r\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        Write("install_profile.json", profile.ToJsonString());
        if (manifest is not null) Write("META-INF/MANIFEST.MF", manifest);
        Write("META-INF/BHL.SF", "signature");
        Write("META-INF/BHL.RSA", "signature");
        Write("data/unchanged.txt", "unchanged");
        void Write(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    private static JsonObject Metadata() => new()
    {
        ["id"] = MinecraftVersion,
        ["downloads"] = new JsonObject { ["client_mappings"] = new JsonObject
        { ["url"] = MappingUrl, ["sha1"] = Hash(Mappings), ["size"] = (long)Mappings.Length } }
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));

    private sealed class PreparationLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class MappingHandler(bool failOfficial = false, bool corrupt = false, CancellationTokenSource? cancel = null) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            if (cancel is not null)
            {
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            var bytes = Mappings.ToArray();
            if (corrupt) bytes[0] ^= 1;
            return Task.FromResult(new HttpResponseMessage(failOfficial && request.RequestUri!.Host == "piston-data.mojang.com"
                ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
