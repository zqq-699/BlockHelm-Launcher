/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Services;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Resources;

namespace Launcher.Tests.Infrastructure.Resources;

public sealed class ResourceCatalogServiceTests : TestTempDirectory
{
    [Fact]
    public async Task SearchStartsAllSelectedProvidersConcurrently()
    {
        var handler = new ConcurrentProviderSearchHandler();
        var service = CreateService(handler, "key");

        var searchTask = service.SearchProjectsAsync(new ResourceCatalogSearchRequest
        {
            Kind = ResourceProjectKind.Mod
        });

        try
        {
            await handler.AllProvidersStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, handler.StartedCount);
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        var result = await searchTask;
        Assert.Equal(2, result.Projects.Count);
    }

    [Fact]
    public async Task ModrinthVersionsPreserveFileIntegrityMetadata()
    {
        const string sha512 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sha1 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var handler = new StubHandler(_ => Json(
            $$$"""[{"id":"v1","name":"Main","version_number":"1","version_type":"release","files":[{"filename":"main.jar","url":"https://download.test/main.jar","primary":true,"size":123,"hashes":{"sha512":"{{{sha512}}}","sha1":"{{{sha1}}}"}}]}]"""));
        var service = CreateService(handler);

        var result = await service.GetProjectVersionsAsync(new ResourceProjectVersionsRequest
        {
            Kind = ResourceProjectKind.Mod,
            Source = ResourceProjectSource.Modrinth,
            ProjectId = "main",
            MinecraftVersion = "1.20.1",
            Loader = LoaderKind.Fabric
        });

        var version = Assert.Single(result.Versions);
        Assert.Equal(123, version.ExpectedFileSize);
        Assert.Contains(version.FileHashes, hash => hash.Algorithm == ResourceFileHashAlgorithm.Sha512 && hash.Value == sha512);
        Assert.Contains(version.FileHashes, hash => hash.Algorithm == ResourceFileHashAlgorithm.Sha1 && hash.Value == sha1);
    }

    [Fact]
    public async Task DownloadFallsBackAfterIntegrityMismatch()
    {
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("fallback", StringComparison.Ordinal)
                ? "expected"
                : "modified")
        });
        var service = CreateService(handler);

        var path = await service.DownloadProjectVersionAsync(new ResourceProjectVersion
        {
            VersionId = "v1",
            FileName = "mod.jar",
            PrimaryDownloadUrl = "https://download.test/primary.jar",
            FallbackDownloadUrls = ["https://download.test/fallback.jar"],
            ExpectedFileSize = Encoding.UTF8.GetByteCount("expected"),
            FileHashes = [CreateHash(ResourceFileHashAlgorithm.Sha512, "expected")]
        }, TempRoot);

        Assert.Equal("expected", await File.ReadAllTextAsync(path));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HashMismatchPreservesExistingFileAndCleansTemporaryFile()
    {
        Directory.CreateDirectory(TempRoot);
        var target = Path.Combine(TempRoot, "mod.jar");
        await File.WriteAllTextAsync(target, "existing");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("modified")
        });
        var service = CreateService(handler);
        var destinationWriter = Assert.IsAssignableFrom<IResourceCatalogDestinationWriter>(service);
        var version = new ResourceProjectVersion
        {
            VersionId = "v1",
            FileName = "mod.jar",
            PrimaryDownloadUrl = "https://download.test/mod.jar",
            ExpectedFileSize = Encoding.UTF8.GetByteCount("modified"),
            FileHashes = [CreateHash(ResourceFileHashAlgorithm.Sha512, "expected")]
        };
        var destinationState = await destinationWriter.CaptureDownloadDestinationAsync(
            version,
            target,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ResourceProjectIntegrityException>(() =>
            destinationWriter.DownloadProjectVersionToDestinationAsync(
                version,
                target,
                destinationState,
                progress: null,
                CancellationToken.None));

        Assert.Equal(ResourceProjectIntegrityFailureReason.HashMismatch, exception.Reason);
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(TempRoot, "*.download"));
    }

    [Fact]
    public async Task InstanceInstallRejectsSameNameDifferentContentWithoutDownloadingOrOverwriting()
    {
        var instanceDirectory = Path.Combine(TempRoot, "instance");
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        var target = Path.Combine(modsDirectory, "dependency.jar");
        await File.WriteAllTextAsync(target, "user-owned");
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("dependency") });
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ResourceProjectDestinationConflictException>(() =>
            service.InstallProjectVersionAsync(
                new ResourceProjectVersion
                {
                    Kind = ResourceProjectKind.Mod,
                    VersionId = "dependency",
                    FileName = "dependency.jar",
                    PrimaryDownloadUrl = "https://download.test/dependency.jar",
                    ExpectedFileSize = Encoding.UTF8.GetByteCount("dependency"),
                    FileHashes = [CreateHash(ResourceFileHashAlgorithm.Sha512, "dependency")]
                },
                new GameInstance { Id = "instance", InstanceDirectory = instanceDirectory }));

        Assert.Equal(ResourceProjectDestinationConflictReason.ExistingDifferentContent, exception.Reason);
        Assert.Equal("user-owned", await File.ReadAllTextAsync(target));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExplicitDownloadStopsWhenMissingTargetAppearsAfterConfirmation()
    {
        Directory.CreateDirectory(TempRoot);
        var target = Path.Combine(TempRoot, "resource.zip");
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("downloaded") });
        var service = CreateService(handler);
        var destinationWriter = Assert.IsAssignableFrom<IResourceCatalogDestinationWriter>(service);
        var version = new ResourceProjectVersion
        {
            Kind = ResourceProjectKind.ResourcePack,
            VersionId = "resource",
            FileName = "resource.zip",
            PrimaryDownloadUrl = "https://download.test/resource.zip",
            ExpectedFileSize = Encoding.UTF8.GetByteCount("downloaded"),
            FileHashes = [CreateHash(ResourceFileHashAlgorithm.Sha512, "downloaded")]
        };
        var expectedState = await destinationWriter.CaptureDownloadDestinationAsync(
            version,
            target,
            CancellationToken.None);
        await File.WriteAllTextAsync(target, "created-after-confirmation");

        var exception = await Assert.ThrowsAsync<ResourceProjectDestinationConflictException>(() =>
            destinationWriter.DownloadProjectVersionToDestinationAsync(
                version,
                target,
                expectedState,
                progress: null,
                CancellationToken.None));

        Assert.Equal(ResourceProjectDestinationConflictReason.ChangedAfterConfirmation, exception.Reason);
        Assert.Equal("created-after-confirmation", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(TempRoot, "*.download"));
    }

    [Fact]
    public async Task ExplicitInstanceDestinationOutsideContentDirectoryIsRejected()
    {
        var instanceDirectory = Path.Combine(TempRoot, "instance");
        Directory.CreateDirectory(Path.Combine(instanceDirectory, "mods"));
        var outside = Path.Combine(TempRoot, "outside.jar");
        var handler = new StubHandler(_ => throw new InvalidOperationException("Download must not start."));
        var service = CreateService(handler);
        var destinationWriter = Assert.IsAssignableFrom<IResourceCatalogDestinationWriter>(service);

        var exception = await Assert.ThrowsAsync<ResourceProjectDestinationConflictException>(() =>
            destinationWriter.CaptureInstallDestinationAsync(
                new ResourceProjectVersion
                {
                    Kind = ResourceProjectKind.Mod,
                    VersionId = "mod",
                    FileName = "mod.jar"
                },
                new GameInstance { Id = "instance", InstanceDirectory = instanceDirectory },
                outside,
                CancellationToken.None));

        Assert.Equal(ResourceProjectDestinationConflictReason.OutsideInstanceContentDirectory, exception.Reason);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(outside));
    }

    [Theory]
    [InlineData(false)]
    public async Task ExecutableResourceWithoutTrustedHashIsRejectedBeforeDownload(bool md5Only)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Download must not start."));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<ResourceProjectIntegrityException>(() =>
            service.DownloadProjectVersionAsync(new ResourceProjectVersion
            {
                VersionId = "v1",
                FileName = "mod.jar",
                PrimaryDownloadUrl = "https://download.test/mod.jar",
                ExpectedFileSize = 3,
                FileHashes = md5Only ? [CreateHash(ResourceFileHashAlgorithm.Md5, "jar")] : []
            }, TempRoot));

        Assert.Equal(ResourceProjectIntegrityFailureReason.MissingTrustedHash, exception.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationCleansTemporaryFileWithoutPublishingTarget()
    {
        var stream = new CancelableDownloadStream();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        });
        var service = CreateService(handler);
        using var cancellation = new CancellationTokenSource();
        var download = service.DownloadProjectVersionAsync(new ResourceProjectVersion
        {
            Kind = ResourceProjectKind.ResourcePack,
            VersionId = "v1",
            FileName = "pack.zip",
            PrimaryDownloadUrl = "https://download.test/pack.zip",
            FallbackDownloadUrls = ["https://download.test/fallback.zip"]
        }, TempRoot, cancellation.Token);
        await stream.BlockingReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Single(handler.Requests);
        Assert.False(File.Exists(Path.Combine(TempRoot, "pack.zip")));
        Assert.Empty(Directory.GetFiles(TempRoot, "*.download"));
    }

    [Fact]
    public async Task ModInstallRejectsModsReparsePointBeforeNetworkOrExternalWriteWhenSupported()
    {
        var instanceDirectory = Path.Combine(TempRoot, "instance-with-linked-mods");
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        var externalDirectory = Path.Combine(TempRoot, "external-mods");
        Directory.CreateDirectory(instanceDirectory);
        Directory.CreateDirectory(externalDirectory);
        var externalFile = Path.Combine(externalDirectory, "mod.jar");
        await File.WriteAllTextAsync(externalFile, "external-original");
        try
        {
            Directory.CreateSymbolicLink(modsDirectory, externalDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("replacement") });
        var service = CreateService(handler);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallProjectVersionAsync(
                new ResourceProjectVersion
                {
                    Kind = ResourceProjectKind.Mod,
                    VersionId = "v1",
                    FileName = "mod.jar",
                    PrimaryDownloadUrl = "https://download.test/mod.jar",
                    ExpectedFileSize = Encoding.UTF8.GetByteCount("replacement"),
                    FileHashes = [CreateHash(ResourceFileHashAlgorithm.Sha512, "replacement")]
                },
                new GameInstance { Id = "instance", InstanceDirectory = instanceDirectory }));

            Assert.Empty(handler.Requests);
            Assert.Equal("external-original", await File.ReadAllTextAsync(externalFile));
        }
        finally
        {
            if (Directory.Exists(modsDirectory))
                Directory.Delete(modsDirectory, recursive: false);
        }
    }

    private static ResourceFileHash CreateHash(ResourceFileHashAlgorithm algorithm, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = algorithm switch
        {
            ResourceFileHashAlgorithm.Sha512 => SHA512.HashData(bytes),
            ResourceFileHashAlgorithm.Sha1 => SHA1.HashData(bytes),
            ResourceFileHashAlgorithm.Md5 => MD5.HashData(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
        return new ResourceFileHash(algorithm, Convert.ToHexString(hash));
    }

    private static ResourceCatalogService CreateService(HttpMessageHandler handler, string? key = null) =>
        new(new HttpClient(handler), curseForgeApiKeyResolver: new StubKeyResolver(key));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(response(request));
        }
    }

    private sealed class StubKeyResolver(string? key) : ICurseForgeApiKeyResolver
    {
        public Task<string?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(key);
    }

    private sealed class CancelableDownloadStream : Stream
    {
        private int readCount;

        public TaskCompletionSource BlockingReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                "partial"u8.CopyTo(buffer.Span);
                return 7;
            }
            BlockingReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ConcurrentProviderSearchHandler : HttpMessageHandler
    {
        private int startedCount;

        public TaskCompletionSource AllProvidersStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartedCount => Volatile.Read(ref startedCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startedCount) == 2)
                AllProvidersStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return request.RequestUri!.Host == "api.modrinth.com"
                ? Json("""{"hits":[{"project_id":"m","slug":"modrinth","title":"Modrinth","description":"","downloads":50}]}""")
                : Json("""{"data":[{"id":9,"name":"CurseForge","slug":"curseforge","summary":"","downloadCount":120,"links":null,"logo":null}]}""");
        }
    }

}
