/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Collections.Concurrent;
using Launcher.Application.Services;
using Launcher.Infrastructure;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;

namespace Launcher.Tests.Infrastructure.Modpacks;

public sealed class LocalModpackPackageServiceTests : TestTempDirectory
{
    [Theory]
    [InlineData("pack.mrpack", "modrinth.index.json", ModpackPackageKind.Modrinth, LoaderKind.Fabric)]
    [InlineData("pack.zip", "manifest.json", ModpackPackageKind.CurseForge, LoaderKind.NeoForge)]
    public async Task PrepareParsesSupportedPackage(
        string archiveFileName,
        string manifestName,
        ModpackPackageKind kind,
        LoaderKind loader)
    {
        var path = Path.Combine(TempRoot, archiveFileName);
        CreateArchive(path, archive => AddEntry(archive, manifestName, kind == ModpackPackageKind.Modrinth
            ? """{"name":"Demo","dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.10"},"files":[]}"""
            : """{"name":"Demo","minecraft":{"version":"1.20.4","modLoaders":[{"id":"neoforge-20.4.237","primary":true}]},"files":[]}"""));

        var prepared = await CreateService().PrepareAsync(path);

        Assert.Equal(kind, prepared.PackageKind);
        Assert.Equal(loader, prepared.Loader);
        Assert.Equal("Demo", prepared.PackageName);
    }

    [Fact]
    public async Task PrepareRejectsPathTraversal()
    {
        var path = Path.Combine(TempRoot, "unsafe.mrpack");
        CreateArchive(path, archive =>
        {
            AddEntry(archive, "modrinth.index.json", """{"name":"Unsafe","dependencies":{"minecraft":"1.20.1"},"files":[]}""");
            AddEntry(archive, "overrides/../evil.txt", "bad");
        });

        var exception = await Assert.ThrowsAsync<ModpackImportException>(() => CreateService().PrepareAsync(path));

        Assert.Equal(ModpackImportFailureReason.InvalidManifest, exception.FailureReason);
        Assert.False(File.Exists(Path.Combine(TempRoot, "evil.txt")));
    }

    [Fact]
    public async Task PrepareRejectsCaseInsensitiveDuplicateOverrideTargets()
    {
        var path = Path.Combine(TempRoot, "duplicates.mrpack");
        CreateArchive(path, archive =>
        {
            AddEntry(archive, "modrinth.index.json", """{"name":"Duplicates","dependencies":{"minecraft":"1.20.1"},"files":[]}""");
            AddEntry(archive, "overrides/config/Settings.txt", "first");
            AddEntry(archive, "overrides/CONFIG/settings.txt", "second");
        });

        var exception = await Assert.ThrowsAsync<ModpackImportException>(() => CreateService().PrepareAsync(path));

        Assert.Equal(ModpackImportFailureReason.InvalidManifest, exception.FailureReason);
    }

    [Fact]
    public void OverrideExtractionBudgetRejectsMoreThanEightGiBWithSpecificReason()
    {
        var budget = new ZipExtractionBudget(ModpackArchiveUtility.MaxOverrideTotalBytes);
        budget.Reserve(ModpackArchiveUtility.MaxOverrideTotalBytes);

        var exception = Assert.Throws<ModpackImportException>(() => budget.Reserve(1));

        Assert.Equal(ModpackImportFailureReason.ArchiveTooLarge, exception.FailureReason);
        Assert.Contains(
            $"LimitBytes={ModpackArchiveUtility.MaxOverrideTotalBytes}",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyOverridesValidatesCapacityBeforeExtractingContent()
    {
        var path = Path.Combine(TempRoot, "overrides.zip");
        CreateArchive(path, archive =>
        {
            AddEntry(
                archive,
                "manifest.json",
                """{"name":"Overrides","minecraft":{"version":"1.20.4","modLoaders":[]},"files":[]}""");
            AddEntry(archive, "overrides/config/example.txt", "content");
        });
        var service = CreateService();
        var prepared = await service.PrepareAsync(path);
        var instance = new GameInstance
        {
            Name = "Overrides",
            InstanceDirectory = Path.Combine(TempRoot, "instance")
        };

        await service.CopyOverridesAsync(prepared, instance, progress: null);

        Assert.Equal(
            "content",
            await File.ReadAllTextAsync(Path.Combine(instance.InstanceDirectory, "config", "example.txt")));
    }

    [Fact]
    public async Task InstallRejectsHashMismatchAndRemovesPartialFile()
    {
        var service = CreateService(new HttpClient(new FixedHandler("actual-bytes")));
        var instance = new GameInstance { Name = "Pack", InstanceDirectory = Path.Combine(TempRoot, "instance") };
        Directory.CreateDirectory(instance.InstanceDirectory);
        var prepared = new PreparedModpack
        {
            PackageKind = ModpackPackageKind.Modrinth,
            SourceArchivePath = Path.Combine(TempRoot, "pack.mrpack"),
            WorkingDirectory = Path.Combine(TempRoot, "work"),
            PackageName = "Pack",
            MinecraftVersion = "1.20.1",
            Loader = LoaderKind.Vanilla,
            Files = [new PreparedModpackDownload
            {
                FileName = "mod.jar",
                RelativePath = "mods/mod.jar",
                SourceUrl = "https://download.test/mod.jar",
                Sha1 = new string('0', 40)
            }]
        };
        Directory.CreateDirectory(prepared.WorkingDirectory);

        var exception = await Assert.ThrowsAsync<ModpackImportException>(() =>
            service.InstallContentAsync(prepared, instance, null));

        Assert.Equal(ModpackImportFailureReason.HashMismatch, exception.FailureReason);
        Assert.Empty(Directory.EnumerateFiles(instance.InstanceDirectory, "*.download", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CurseForgeMetadataFailureStopsInstallationInsteadOfCreatingManualDownloads()
    {
        var service = CreateService(new HttpClient(new FailingCurseForgeResolutionHandler()), apiKey: "test-key");
        var instance = new GameInstance { Name = "Pack", InstanceDirectory = Path.Combine(TempRoot, "instance") };
        var prepared = CreateCurseForgePreparedModpack();
        var progress = new ThreadSafeProgress();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            service.DownloadFilesAsync(prepared, instance, progress));

        Assert.DoesNotContain(
            progress.Snapshot(),
            report => report.Stage == ImportProgressStages.DownloadingPackFiles);
        Assert.False(File.Exists(Path.Combine(instance.InstanceDirectory, "mods", "first.jar")));
    }

    private LocalModpackPackageService CreateService(
        HttpClient? client = null,
        string? apiKey = null,
        ILogger<LocalModpackPackageService>? logger = null)
    {
        var paths = new LauncherPathProvider(TempRoot);
        return new LocalModpackPackageService(paths, httpClient: client,
            logger: logger,
            curseForgeApiKeyResolver: new CurseForgeApiKeyResolver(
                paths,
                embeddedApiKeyProvider: _ => Task.FromResult(apiKey)));
    }

    private static PreparedModpack CreateCurseForgePreparedModpack() => new()
    {
        PackageKind = ModpackPackageKind.CurseForge,
        PackageName = "Pack",
        MinecraftVersion = "1.20.1",
        Files =
        [
            new PreparedModpackDownload { ProjectId = 1, FileId = 101, TargetDirectory = "mods" },
            new PreparedModpackDownload { ProjectId = 2, FileId = 202, TargetDirectory = "mods" }
        ]
    };

    private static PreparedModpack CreateSingleCurseForgePreparedModpack() => new()
    {
        PackageKind = ModpackPackageKind.CurseForge,
        PackageName = "Pack",
        MinecraftVersion = "1.20.1",
        Files = [new PreparedModpackDownload { ProjectId = 1, FileId = 101, TargetDirectory = "mods" }]
    };

    private static void CreateArchive(string path, Action<ZipArchive> configure)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        configure(archive);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class FixedHandler(string content) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(CreateResponse());

        private HttpResponseMessage CreateResponse()
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class FailingCurseForgeResolutionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }

    private sealed class DirectCurseForgeFallbackHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> downloadHosts = [];

        public string[] DownloadHosts => downloadHosts.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (string.Equals(uri.Host, "api.curseforge.com", StringComparison.OrdinalIgnoreCase))
            {
                var json = uri.AbsolutePath.EndsWith("/download-url", StringComparison.Ordinal)
                    ? """{"data":"https://download.example/direct.jar"}"""
                    : """{"data":{"displayName":"Direct","fileName":"direct.jar","downloadUrl":"https://download.example/direct.jar","hashes":[]}}""";
                return Task.FromResult(JsonResponse(json));
            }

            downloadHosts.Enqueue(uri.Host);
            return Task.FromResult(string.Equals(uri.Host, "edge.forgecdn.net", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("downloaded") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class BodyGateStream(byte[] content, Task release) : Stream
    {
        private readonly MemoryStream inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            release.GetAwaiter().GetResult();
            return inner.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken);
            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ThreadSafeProgress : IProgress<LauncherProgress>
    {
        private readonly object syncRoot = new();
        private readonly List<LauncherProgress> reports = [];

        public void Report(LauncherProgress value)
        {
            lock (syncRoot)
                reports.Add(value);
        }

        public LauncherProgress[] Snapshot()
        {
            lock (syncRoot)
                return reports.ToArray();
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
