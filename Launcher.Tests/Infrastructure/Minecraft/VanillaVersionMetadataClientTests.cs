/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure;
using Launcher.Tests.Helpers;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class VanillaVersionMetadataClientTests : TestTempDirectory
{
    private const string Version = "1.21.1";
    private const string VersionUrl = "https://piston-meta.mojang.com/v1/packages/example/1.21.1.json";
    private const string Json = "{\"id\":\"1.21.1\",\"javaVersion\":{\"majorVersion\":21}}";
    private static readonly string Sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Json))).ToLowerInvariant();
    private static readonly string Manifest = new JsonObject
    {
        ["versions"] = new JsonArray(new JsonObject { ["id"] = Version, ["url"] = VersionUrl, ["sha1"] = Sha1 })
    }.ToJsonString();

    [Theory]
    [InlineData("manifest")]
    [InlineData("version")]
    public async Task SlowPreferredSourceStartsMirrorWithoutWaitingForHeadersTimeout(string stage)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            var manifest = IsManifest(request);
            if (request.RequestUri!.Host == "piston-meta.mojang.com" && (stage == "manifest" ? manifest : !manifest))
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            }
            return Response(manifest ? Manifest : Json);
        });
        using var client = new HttpClient(handler);
        var result = await Load(client).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Version, result["id"]!.GetValue<string>());
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(handler.Requests, uri => uri.Host == "bmclapi2.bangbang93.com");
    }

    [Fact]
    public async Task ConcurrentCallsShareValidatedBytesAndReturnIndependentObjects()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (IsManifest(request)) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return Response(IsManifest(request) ? Manifest : Json);
        });
        using var client = new HttpClient(handler);
        var first = Load(client);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = Load(client);
        release.TrySetResult();
        var values = await Task.WhenAll(first, second);
        values[0]["id"] = "mutated";
        Assert.Equal(Version, values[1]["id"]!.GetValue<string>());
        Assert.Equal(Version, (await Load(client))["id"]!.GetValue<string>());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DiskCacheWorksWithANewClientAndVersionCorruptionIsRepaired()
    {
        var handler = ValidHandler();
        using (var client = new HttpClient(handler))
            await Load(client, new VanillaVersionMetadataCache(TempRoot));
        var offline = new Handler((_, _) => throw new InvalidOperationException("Valid disk cache must not request the network."));
        using (var client = new HttpClient(offline))
            Assert.Equal(Version, (await Load(client, new VanillaVersionMetadataCache(TempRoot)))["id"]!.GetValue<string>());
        Assert.Empty(offline.Requests);

        await File.WriteAllTextAsync(Path.Combine(TempRoot, $"version-{Sha1}.json"), "corrupt");
        var repair = ValidHandler();
        using (var client = new HttpClient(repair))
            await Load(client, new VanillaVersionMetadataCache(TempRoot));
        Assert.False(IsManifest(Assert.Single(repair.Requests)));
        Assert.Equal(Json, await File.ReadAllTextAsync(Path.Combine(TempRoot, $"version-{Sha1}.json")));
        Assert.Empty(Directory.GetFiles(TempRoot, "*.tmp"));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("missing-version")]
    [InlineData("invalid-url")]
    [InlineData("corrupt")]
    public async Task StaleOrUnusableManifestIsRefreshedButVerifiedVersionIsReused(string scenario)
    {
        using (var client = new HttpClient(ValidHandler()))
            await Load(client, new VanillaVersionMetadataCache(TempRoot));
        var path = Path.Combine(TempRoot, "manifest-Official.json");
        if (scenario == "expired") File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(1));
        else await File.WriteAllTextAsync(path, scenario switch
        {
            "missing-version" => "{\"versions\":[]}",
            "invalid-url" => Manifest.Replace(VersionUrl, "file:///private.json", StringComparison.Ordinal),
            _ => "corrupt"
        });
        var handler = ValidHandler();
        using var next = new HttpClient(handler);
        await Load(next, new VanillaVersionMetadataCache(TempRoot));
        Assert.True(IsManifest(Assert.Single(handler.Requests)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPreferredMetadataCannotWinOrEnterTheCache(bool wrongIdentity)
    {
        var invalidJson = wrongIdentity ? Json.Replace(Version, "1.20.1", StringComparison.Ordinal) : "{\"id\":\"1.21.1\",\"bad\":true}";
        var handler = new Handler((request, _) => Task.FromResult(Response(IsManifest(request) ? Manifest
            : request.RequestUri!.Host == "piston-meta.mojang.com" ? invalidJson : Json)));
        using var client = new HttpClient(handler);
        var result = await Load(client, new VanillaVersionMetadataCache(TempRoot));
        Assert.Equal(Version, result["id"]!.GetValue<string>());
        Assert.Equal(Json, await File.ReadAllTextAsync(Path.Combine(TempRoot, $"version-{Sha1}.json")));
        Assert.Contains(handler.Requests, uri => uri.Host == "bmclapi2.bangbang93.com");
    }

    [Fact]
    public async Task CancelingCacheWaiterDoesNotCancelTheActiveDownloadOrPoisonCache()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (IsManifest(request)) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return Response(IsManifest(request) ? Manifest : Json);
        });
        using var client = new HttpClient(handler);
        var active = Load(client);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var waiting = Load(client, token: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.TrySetResult();
        Assert.Equal(Version, (await active)["id"]!.GetValue<string>());
        await Load(client);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CancelingActiveDownloadAllowsTheNextCallerToRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new Handler(async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response(IsManifest(request) ? Manifest : Json);
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var active = Load(client, token: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal(Version, (await Load(client).WaitAsync(TimeSpan.FromSeconds(5)))["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task MatchingHashWithWrongVersionIdentityIsRejectedAndNotCached()
    {
        var wrongJson = Json.Replace(Version, "1.20.1", StringComparison.Ordinal);
        var wrongHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(wrongJson))).ToLowerInvariant();
        var handler = new Handler((request, _) => Task.FromResult(Response(IsManifest(request)
            ? Manifest.Replace(Sha1, wrongHash, StringComparison.Ordinal) : wrongJson)));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<MinecraftDownloadRequestExecutor.DownloadSourceRequestException>(() => Load(client, new VanillaVersionMetadataCache(TempRoot)));
        Assert.Empty(Directory.GetFiles(TempRoot, "version-*.json"));
    }

    [Fact]
    public async Task CacheWriteFailureDoesNotFailValidMetadataOrOverwriteExistingFile()
    {
        Directory.CreateDirectory(TempRoot);
        var occupied = Path.Combine(TempRoot, "occupied");
        await File.WriteAllTextAsync(occupied, "keep");
        var cache = new VanillaVersionMetadataCache(occupied);
        var handler = ValidHandler();
        using var client = new HttpClient(handler);
        await Load(client, cache);
        await Load(client, cache);
        Assert.Equal("keep", await File.ReadAllTextAsync(occupied));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HashlessResultCannotSatisfyASubsequentVerifiedRequest()
    {
        var provideHash = false;
        var handler = new Handler((request, _) => Task.FromResult(Response(IsManifest(request)
            ? provideHash ? Manifest : new JsonObject { ["versions"] = new JsonArray(new JsonObject { ["id"] = Version, ["url"] = VersionUrl }) }.ToJsonString()
            : provideHash ? Json : "{\"id\":\"1.21.1\",\"unverified\":true}")));
        using var client = new HttpClient(handler);
        var weak = await VanillaVersionMetadataClient.DownloadVersionJsonAsync(client, Version, DownloadSourcePreference.Official,
            hostConcurrencyController: new DownloadHostConcurrencyController(maximumJitter: TimeSpan.Zero));
        Assert.True(weak["unverified"]!.GetValue<bool>());
        provideHash = true;
        Assert.Null((await Load(client))["unverified"]);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task DifferentGameDirectoriesShareBhlCacheIncludingAJunctionGameRoot()
    {
        var paths = new LauncherPathProvider(applicationBaseDirectory: Path.Combine(TempRoot, "launcher"));
        var gameTarget = Path.Combine(TempRoot, "game-target");
        var gameLink = Path.Combine(TempRoot, "game-link");
        var otherGame = Path.Combine(TempRoot, "other-game");
        Directory.CreateDirectory(gameTarget);
        CreateDirectoryJunction(gameLink, gameTarget);
        try
        {
            using (var first = new HttpClient(ValidHandler()))
            {
                VanillaVersionMetadataCache.ForLauncher(first, paths);
                await VanillaVersionComposer.CreateFinalVersionAsync(first, Version, "first", gameLink, DownloadSourcePreference.Official);
            }
            var offline = new Handler((_, _) => throw new InvalidOperationException("All game directories should share BHL's cache."));
            using (var second = new HttpClient(offline))
            {
                VanillaVersionMetadataCache.ForLauncher(second, paths);
                await VanillaVersionComposer.CreateFinalVersionAsync(second, Version, "second", otherGame, DownloadSourcePreference.Official);
            }
            Assert.Empty(offline.Requests);
            Assert.True(File.Exists(Path.Combine(paths.DefaultDataDirectory, "cache", "minecraft-metadata", "manifest-Official.json")));
            Assert.True(File.Exists(Path.Combine(paths.MinecraftMetadataCacheDirectory, $"version-{Sha1}.json")));
            Assert.False(Directory.Exists(Path.Combine(gameTarget, ".bhl-cache")));
            Assert.False(Directory.Exists(Path.Combine(otherGame, ".bhl-cache")));
            using var factoryClient = MinecraftHttpClientFactory.CreateTransportClient(paths);
            var factoryCache = VanillaVersionMetadataCache.ForClient(factoryClient);
            Assert.Equal(Json, Encoding.UTF8.GetString((await factoryCache.ReadAsync($"version-{Sha1}.json", null, null, CancellationToken.None))!));
        }
        finally { Directory.Delete(gameLink, recursive: false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachePathGuardFailureFallsBackWithoutChangingExistingContent(bool junctionRoot)
    {
        var paths = new LauncherPathProvider(applicationBaseDirectory: Path.Combine(TempRoot, "launcher"));
        Directory.CreateDirectory(paths.ApplicationBaseDirectory);
        var protectedDirectory = junctionRoot ? Path.Combine(TempRoot, "external")
            : Path.Combine(paths.MinecraftMetadataCacheDirectory, "manifest-Official.json");
        Directory.CreateDirectory(protectedDirectory);
        var sentinel = Path.Combine(protectedDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        if (junctionRoot) CreateDirectoryJunction(paths.DefaultDataDirectory, protectedDirectory);
        try
        {
            var handler = ValidHandler();
            using var client = new HttpClient(handler);
            VanillaVersionMetadataCache.ForLauncher(client, paths);
            Assert.Equal(Version, (await Load(client))["id"]!.GetValue<string>());
            await Load(client);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            if (junctionRoot) Assert.False(Directory.Exists(Path.Combine(protectedDirectory, "cache")));
        }
        finally
        {
            if (junctionRoot) Directory.Delete(paths.DefaultDataDirectory, recursive: false);
        }
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        }) ?? throw new InvalidOperationException("Junction creation did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private static Task<JsonObject> Load(HttpClient client, VanillaVersionMetadataCache? cache = null, CancellationToken token = default) =>
        VanillaVersionMetadataClient.DownloadVerifiedVersionJsonAsync(client, Version, DownloadSourcePreference.Official,
            cancellationToken: token, cache: cache,
            hostConcurrencyController: new DownloadHostConcurrencyController(maximumJitter: TimeSpan.Zero));
    private static bool IsManifest(HttpRequestMessage request) => IsManifest(request.RequestUri!);
    private static bool IsManifest(Uri uri) => uri.AbsolutePath == "/mc/game/version_manifest_v2.json";
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static Handler ValidHandler() => new((request, _) => Task.FromResult(Response(IsManifest(request) ? Manifest : Json)));

    [Fact]
    public async Task CleanupDeletesOnlyOldOwnedFilesAndDoesNotFollowJunctions()
    {
        var versionName = $"version-{new string('a', 40)}.json";
        var expired = Path.Combine(TempRoot, versionName);
        var temporary = Path.Combine(TempRoot, $".{versionName}.{Guid.NewGuid():N}.tmp");
        var activeTemporary = Path.Combine(TempRoot, $".{versionName}.{Guid.NewGuid():N}.tmp");
        var unknown = Path.Combine(TempRoot, "keep.json");
        var external = Path.Combine(TempRoot, "external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, $"version-{new string('b', 40)}.json");
        await File.WriteAllTextAsync(sentinel, "keep");
        var link = Path.Combine(TempRoot, "cache-link");
        CreateDirectoryJunction(link, external);
        try
        {
            foreach (var path in new[] { expired, temporary, unknown })
            {
                await File.WriteAllTextAsync(path, "old");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-100));
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-100));
            }
            await File.WriteAllTextAsync(activeTemporary, "active");
            var cache = new VanillaVersionMetadataCache(TempRoot);
            await cache.WriteAsync("manifest-Official.json", "valid"u8.ToArray(), null, CancellationToken.None);
            Assert.False(File.Exists(expired));
            Assert.False(File.Exists(temporary));
            Assert.True(File.Exists(activeTemporary));
            Assert.True(File.Exists(unknown));
            Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
            Assert.Equal("valid", await File.ReadAllTextAsync(Path.Combine(TempRoot, "manifest-Official.json")));
        }
        finally { Directory.Delete(link, recursive: false); }
    }

    [Fact]
    public async Task CleanupTrimsOldestEntriesToBudgetAndRunsAtMostDaily()
    {
        Directory.CreateDirectory(TempRoot);
        var clock = new CacheTimeProvider();
        var oldKey = $"version-{new string('a', 40)}.json";
        var recentKey = $"version-{new string('b', 40)}.json";
        foreach (var (key, days) in new[] { (oldKey, 10), (recentKey, 1) })
        {
            var path = Path.Combine(TempRoot, key);
            await File.WriteAllTextAsync(path, "123456");
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddDays(-days));
            File.SetLastAccessTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddDays(-days));
        }
        var cache = new VanillaVersionMetadataCache(TempRoot, clock, maximumDiskBytes: 12);
        await cache.WriteAsync("manifest-Official.json", "valid"u8.ToArray(), null, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(TempRoot, oldKey)));
        Assert.True(File.Exists(Path.Combine(TempRoot, recentKey)));
        await File.WriteAllTextAsync(Path.Combine(TempRoot, oldKey), "123456");
        await cache.ReadAsync("manifest-Official.json", null, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(TempRoot, oldKey)));
        clock.Advance(TimeSpan.FromDays(91));
        await cache.ReadAsync("manifest-Official.json", null, null, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(TempRoot, oldKey)));
        Assert.False(File.Exists(Path.Combine(TempRoot, recentKey)));
    }

    [Fact]
    public async Task ReadingVersionRefreshesRetentionWithoutExtendingManifestFreshness()
    {
        var clock = new CacheTimeProvider();
        var cache = new VanillaVersionMetadataCache(TempRoot, clock);
        var key = $"version-{Sha1}.json";
        await cache.WriteAsync(key, Encoding.UTF8.GetBytes(Json), null, CancellationToken.None);
        await cache.WriteAsync("manifest-Official.json", Encoding.UTF8.GetBytes(Manifest), null, CancellationToken.None);
        var written = File.GetLastWriteTimeUtc(Path.Combine(TempRoot, "manifest-Official.json"));
        clock.Advance(TimeSpan.FromDays(80));
        Assert.NotNull(await cache.ReadAsync(key, null, null, CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(20));
        await cache.ReadAsync("manifest-Official.json", VanillaVersionMetadataCache.ManifestLifetime, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(TempRoot, key)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(TempRoot, "manifest-Official.json")));
    }

    private sealed class CacheTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            var result = await response(request, cancellationToken);
            result.RequestMessage = request;
            return result;
        }
    }
}
