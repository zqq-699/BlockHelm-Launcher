/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.Application.Mods;

public sealed class ModUpdateServiceTests
{
    [Fact]
    public async Task ReleaseWinsAcrossProvidersEvenWhenPrereleaseIsNewer()
    {
        var local = CreateLocalState();
        var release = CreateCandidate(local, ResourceProjectSource.CurseForge, "release", DateTimeOffset.UtcNow.AddDays(-1));
        var beta = CreateCandidate(local, ResourceProjectSource.Modrinth, "beta", DateTimeOffset.UtcNow);
        var service = CreateService(
            new StubProvider(ResourceProjectSource.Modrinth, new ModUpdateProviderResult(true, true, beta)),
            new StubProvider(ResourceProjectSource.CurseForge, new ModUpdateProviderResult(true, true, release)));

        var result = await service.CheckAsync(CreateInstance(), CreateMod());

        Assert.Equal(ModUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Same(release, result.Candidate);
    }

    [Fact]
    public async Task PrereleaseCandidatesAreIgnoredAcrossProviders()
    {
        var local = CreateLocalState();
        var beta = CreateCandidate(local, ResourceProjectSource.Modrinth, "beta", DateTimeOffset.UtcNow);
        var alpha = CreateCandidate(local, ResourceProjectSource.CurseForge, "alpha", DateTimeOffset.UtcNow.AddDays(1));
        var service = CreateService(
            new StubProvider(ResourceProjectSource.Modrinth, new ModUpdateProviderResult(true, true, beta)),
            new StubProvider(ResourceProjectSource.CurseForge, new ModUpdateProviderResult(true, true, alpha)));

        var result = await service.CheckAsync(CreateInstance(), CreateMod());

        Assert.Equal(ModUpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task ModrinthWinsWhenPublicationTimesAreEqual()
    {
        var local = CreateLocalState();
        var publishedAt = DateTimeOffset.UtcNow;
        var modrinth = CreateCandidate(local, ResourceProjectSource.Modrinth, "release", publishedAt);
        var curseForge = CreateCandidate(local, ResourceProjectSource.CurseForge, "release", publishedAt);
        var service = CreateService(
            new StubProvider(ResourceProjectSource.CurseForge, new ModUpdateProviderResult(true, true, curseForge)),
            new StubProvider(ResourceProjectSource.Modrinth, new ModUpdateProviderResult(true, true, modrinth)));

        var result = await service.CheckAsync(CreateInstance(), CreateMod());

        Assert.Same(modrinth, result.Candidate);
    }

    [Fact]
    public async Task CandidateRemainsAvailableWhenOtherProviderFails()
    {
        var local = CreateLocalState();
        var candidate = CreateCandidate(
            local,
            ResourceProjectSource.Modrinth,
            "release",
            DateTimeOffset.UtcNow);
        var service = CreateService(
            new StubProvider(ResourceProjectSource.Modrinth, new ModUpdateProviderResult(true, true, candidate)),
            new StubProvider(ResourceProjectSource.CurseForge, new ModUpdateProviderResult(false, false)));

        var result = await service.CheckAsync(CreateInstance(), CreateMod());

        Assert.Equal(ModUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Same(candidate, result.Candidate);
        Assert.Equal(1, result.SuccessfulProviderCount);
    }

    [Theory]
    [InlineData(true, true, true, true, ModUpdateCheckStatus.UpToDate)]
    [InlineData(true, false, true, false, ModUpdateCheckStatus.NotRecognized)]
    [InlineData(true, true, false, false, ModUpdateCheckStatus.Unavailable)]
    public async Task EmptyProviderResultsDoNotMisreportPartialChecks(
        bool firstAvailable,
        bool firstRecognized,
        bool secondAvailable,
        bool secondRecognized,
        ModUpdateCheckStatus expected)
    {
        var service = CreateService(
            new StubProvider(ResourceProjectSource.Modrinth, new ModUpdateProviderResult(firstAvailable, firstRecognized)),
            new StubProvider(ResourceProjectSource.CurseForge, new ModUpdateProviderResult(secondAvailable, secondRecognized)));

        var result = await service.CheckAsync(CreateInstance(), CreateMod());

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task BatchCheckQueriesEachProviderOnceAndKeepsPerFileResults()
    {
        var provider = new RecordingProvider();
        var service = new ModUpdateService(
            [provider],
            new PerFileTransaction(),
            NullLogger<ModUpdateService>.Instance);
        var mods = new[]
        {
            CreateMod("first.jar"),
            CreateMod("second.jar"),
            CreateMod("third.jar")
        };

        var result = await service.CheckManyAsync(CreateInstance(), mods);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(3, provider.LastCount);
        Assert.All(result.Items, item => Assert.Equal(ModUpdateCheckStatus.UpToDate, item.Result.Status));
    }

    [Fact]
    public async Task BatchCheckIsolatesFingerprintCaptureFailure()
    {
        var provider = new RecordingProvider();
        var service = new ModUpdateService(
            [provider],
            new PerFileTransaction("bad.jar"),
            NullLogger<ModUpdateService>.Instance);

        var result = await service.CheckManyAsync(
            CreateInstance(),
            [CreateMod("good.jar"), CreateMod("bad.jar")]);

        Assert.Equal(ModUpdateCheckStatus.UpToDate, result.Items[0].Result.Status);
        Assert.Equal(ModUpdateCheckStatus.Unavailable, result.Items[1].Result.Status);
        Assert.Equal(1, provider.LastCount);
    }

    [Fact]
    public async Task BatchCheckProgressCountsFilesOnlyAfterEveryProviderCompletesThem()
    {
        var service = new ModUpdateService(
            [
                new ReportingProvider(ResourceProjectSource.Modrinth),
                new ReportingProvider(ResourceProjectSource.CurseForge)
            ],
            new PerFileTransaction(),
            NullLogger<ModUpdateService>.Instance);
        var progress = new RecordingProgress();

        await service.CheckManyAsync(
            CreateInstance(),
            [CreateMod("first.jar"), CreateMod("second.jar"), CreateMod("third.jar")],
            progress);

        Assert.Equal(
            [0, 20d / 3, 40d / 3, 20, 20],
            progress.Snapshot());
    }

    [Fact]
    public async Task BatchUpdateContinuesAfterAnItemFailsAndKeepsProgressMonotonic()
    {
        var transaction = new PerFileTransaction(failingUpdatePath: "first.jar");
        var service = new ModUpdateService([], transaction, NullLogger<ModUpdateService>.Instance);
        var first = CreateCandidate(CreateLocalState("first.jar"), ResourceProjectSource.Modrinth, "release", DateTimeOffset.UtcNow);
        var second = CreateCandidate(CreateLocalState("second.jar"), ResourceProjectSource.Modrinth, "release", DateTimeOffset.UtcNow);
        var progress = new RecordingProgress();

        var result = await service.UpdateManyAsync(CreateInstance(), [first, second], progress);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(ModUpdateBatchFailureReason.TargetExists, result.Items[0].FailureReason);
        Assert.NotNull(result.Items[1].Result);
        Assert.True(progress.Percentages.SequenceEqual(progress.Percentages.OrderBy(value => value)));
        Assert.All(progress.Percentages, value => Assert.InRange(value, 0, 99));
    }

    private static ModUpdateService CreateService(params IModUpdateProvider[] providers) =>
        new(providers, new StubFileTransaction(CreateLocalState()), NullLogger<ModUpdateService>.Instance);

    private static ModUpdateCandidate CreateCandidate(
        ModUpdateLocalFileState local,
        ResourceProjectSource source,
        string versionType,
        DateTimeOffset publishedAt) =>
        new(
            local,
            source,
            "project",
            "current",
            "1.0.0",
            publishedAt.AddDays(-2),
            new ResourceProjectVersion
            {
                VersionId = $"target-{source}",
                VersionNumber = "2.0.0",
                VersionType = versionType,
                FileName = "mod-2.0.0.jar",
                PublishedAt = publishedAt
            });

    private static ModUpdateLocalFileState CreateLocalState(string path = "mod.jar") =>
        new(path, $"sha1-{path}", path.GetHashCode(), 10, 20, true);

    private static LocalMod CreateMod(string path = "mod.jar") => new() { FullPath = path, IsEnabled = true };

    private static GameInstance CreateInstance() => new()
    {
        MinecraftVersion = "1.20.1",
        Loader = LoaderKind.Fabric,
        InstanceDirectory = "instance"
    };

    private sealed class StubProvider(
        ResourceProjectSource source,
        ModUpdateProviderResult result) : IModUpdateProvider
    {
        public ResourceProjectSource Source { get; } = source;

        public Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> FindUpdatesAsync(
            GameInstance instance,
            IReadOnlyList<ModUpdateLocalFileState> localFiles,
            CancellationToken cancellationToken = default,
            IProgress<string>? completedFileProgress = null)
        {
            foreach (var localFile in localFiles)
                completedFileProgress?.Report(localFile.FullPath);
            return Task.FromResult<IReadOnlyDictionary<string, ModUpdateProviderResult>>(
                localFiles.ToDictionary(
                    item => item.FullPath,
                    _ => result,
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class StubFileTransaction(ModUpdateLocalFileState state) : IModUpdateFileTransaction
    {
        public Task<ModUpdateLocalFileState> CaptureAsync(LocalMod mod, CancellationToken cancellationToken = default) =>
            Task.FromResult(state);

        public Task<ModUpdateResult> ApplyAsync(
            GameInstance instance,
            ModUpdateCandidate candidate,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingProvider : IModUpdateProvider
    {
        public ResourceProjectSource Source => ResourceProjectSource.Modrinth;

        public int Calls { get; private set; }

        public int LastCount { get; private set; }

        public Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> FindUpdatesAsync(
            GameInstance instance,
            IReadOnlyList<ModUpdateLocalFileState> localFiles,
            CancellationToken cancellationToken = default,
            IProgress<string>? completedFileProgress = null)
        {
            Calls++;
            LastCount = localFiles.Count;
            foreach (var localFile in localFiles)
                completedFileProgress?.Report(localFile.FullPath);
            return Task.FromResult<IReadOnlyDictionary<string, ModUpdateProviderResult>>(
                localFiles.ToDictionary(
                    item => item.FullPath,
                    _ => new ModUpdateProviderResult(true, true),
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class ReportingProvider(ResourceProjectSource source) : IModUpdateProvider
    {
        public ResourceProjectSource Source { get; } = source;

        public Task<IReadOnlyDictionary<string, ModUpdateProviderResult>> FindUpdatesAsync(
            GameInstance instance,
            IReadOnlyList<ModUpdateLocalFileState> localFiles,
            CancellationToken cancellationToken = default,
            IProgress<string>? completedFileProgress = null)
        {
            foreach (var localFile in localFiles)
                completedFileProgress?.Report(localFile.FullPath);
            return Task.FromResult<IReadOnlyDictionary<string, ModUpdateProviderResult>>(
                localFiles.ToDictionary(
                    item => item.FullPath,
                    _ => new ModUpdateProviderResult(true, true),
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class PerFileTransaction(
        string? failingCapturePath = null,
        string? failingUpdatePath = null) : IModUpdateFileTransaction
    {
        public Task<ModUpdateLocalFileState> CaptureAsync(
            LocalMod mod,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(mod.FullPath, failingCapturePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("capture failed");
            return Task.FromResult(CreateLocalState(mod.FullPath));
        }

        public Task<ModUpdateResult> ApplyAsync(
            GameInstance instance,
            ModUpdateCandidate candidate,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new LauncherProgress(ModUpdateProgressStages.Downloading, string.Empty, 50));
            if (string.Equals(candidate.LocalFile.FullPath, failingUpdatePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModUpdateConflictException(
                    candidate.TargetVersion.FileName,
                    ModUpdateConflictReason.TargetExists);
            }

            return Task.FromResult(new ModUpdateResult(
                candidate.TargetVersion.FileName,
                candidate.LocalFile.FullPath + ".old"));
        }
    }

    private sealed class RecordingProgress : IProgress<LauncherProgress>
    {
        private readonly object syncRoot = new();

        public List<double> Percentages { get; } = [];

        public void Report(LauncherProgress value)
        {
            if (value.Percent is { } percent)
            {
                lock (syncRoot)
                    Percentages.Add(percent);
            }
        }

        public double[] Snapshot()
        {
            lock (syncRoot)
                return Percentages.ToArray();
        }
    }
}
