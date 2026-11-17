/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.Json;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Persistence;
using Launcher.Tests.Fakes;

namespace Launcher.Tests.Infrastructure.Persistence;

public sealed class InstanceInstallTransactionTests : TestTempDirectory
{
    [Fact]
    public async Task CommitMovesPreparedLogicalVersionIntoFinalDirectory()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var service = new InstanceInstallTransactionService(
            logger: null,
            guidFactory: () => Guid.ParseExact("a83f21c4000000000000000000000000", "N"));
        await using var transaction = await service.BeginAsync(
            minecraftDirectory, "Test", "game", true);
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        Assert.False(File.Exists(Path.Combine(versionsDirectory, ".bhl-install-transaction.lock")));
        Assert.False(File.Exists(Path.Combine(versionsDirectory, ".bhl-version-mutation.lock")));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(minecraftDirectory), "BHL", "locks", "versions", "install.lock"),
            CrossProcessVersionLock.GetInstallCoordinationPath(minecraftDirectory));
        Directory.CreateDirectory(Path.Combine(transaction.PendingDirectory, "mods"));
        await File.WriteAllTextAsync(
            Path.Combine(transaction.PendingDirectory, "Test.json"),
            """{"id":"Test","jar":"Test"}""");
        await File.WriteAllTextAsync(Path.Combine(transaction.PendingDirectory, "Test.jar"), "jar");
        var instance = CreateInstance("instance", "Test", transaction.PendingDirectory);

        await transaction.CommitAsync(instance);

        Assert.True(transaction.IsCommitted);
        Assert.False(Directory.Exists(transaction.PendingDirectory));
        Assert.True(File.Exists(Path.Combine(transaction.FinalDirectory, "Test.json")));
        Assert.True(File.Exists(Path.Combine(transaction.FinalDirectory, "Test.jar")));
        Assert.True(Directory.Exists(Path.Combine(transaction.FinalDirectory, "mods")));
        Assert.True(File.Exists(Path.Combine(transaction.FinalDirectory, ".bhl-install-pending.json")));
        await transaction.CompleteLogicalCommitAsync();
        Assert.False(File.Exists(Path.Combine(transaction.FinalDirectory, ".bhl-install-pending.json")));
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(transaction.FinalDirectory, "BHL", "instance-settings.json")));
        Assert.Equal("Test", settings.RootElement.GetProperty("VersionName").GetString());
        Assert.Equal(transaction.FinalDirectory, settings.RootElement.GetProperty("InstanceDirectory").GetString());
    }

    [Fact]
    public void CrossProcessLocksAreScopedToTheMinecraftDirectory()
    {
        var firstMinecraftDirectory = Path.Combine(TempRoot, "first", ".minecraft");
        var secondMinecraftDirectory = Path.Combine(TempRoot, "second", ".minecraft");

        var firstInstallPath = CrossProcessVersionLock.GetInstallCoordinationPath(firstMinecraftDirectory);
        var firstMutationPath = CrossProcessVersionLock.GetMutationPath(firstMinecraftDirectory);
        var secondInstallPath = CrossProcessVersionLock.GetInstallCoordinationPath(secondMinecraftDirectory);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(firstMinecraftDirectory), "BHL", "locks", "versions", "install.lock"),
            firstInstallPath);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(firstMinecraftDirectory), "BHL", "locks", "versions", "mutation.lock"),
            firstMutationPath);
        Assert.NotEqual(firstInstallPath, secondInstallPath);
    }

    [Fact]
    public async Task InstallFailsSafelyWhenSharedLockDirectoryCannotBeCreated()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        Directory.CreateDirectory(minecraftDirectory);
        await File.WriteAllTextAsync(Path.Combine(minecraftDirectory, "BHL"), "occupied");
        var service = new InstanceInstallTransactionService();

        await Assert.ThrowsAnyAsync<IOException>(() => service.BeginAsync(
            minecraftDirectory, "Test", "game", false));

        Assert.False(Directory.Exists(Path.Combine(minecraftDirectory, "versions", "Test")));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(minecraftDirectory, "versions"),
            ".bhl-install-pending-*"));
    }

    [Fact]
    public async Task BeginPublishesPendingDirectoryOnlyAfterMarkerExists()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var service = new InstanceInstallTransactionService();

        await using var transaction = await service.BeginAsync(
            minecraftDirectory, "Test", "game", false);

        Assert.True(File.Exists(Path.Combine(
            transaction.PendingDirectory,
            PendingInstanceInstallDirectory.MarkerFileName)));
        Assert.True(PendingInstanceInstallDirectory.TryReadValidPendingMarker(
            transaction.PendingDirectory,
            out var marker));
        Assert.True(InstanceDirectoryOwnership.Matches(transaction.PendingDirectory, marker.OwnerProof));
        Assert.False(Directory.Exists(PendingInstanceInstallDirectory.GetPreparationRoot(
            minecraftDirectory)));
    }

    [Fact]
    public async Task CommitFailsWithoutOverwritingOrMergingWhenFinalDirectoryAppearsDuringInstall()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var service = new InstanceInstallTransactionService();
        await using var transaction = await service.BeginAsync(
            minecraftDirectory, "Test", "game", false);
        await File.WriteAllTextAsync(
            Path.Combine(transaction.PendingDirectory, "Test.json"),
            """{"id":"Test"}""");
        await File.WriteAllTextAsync(Path.Combine(transaction.PendingDirectory, "downloaded.txt"), "pending");
        Directory.CreateDirectory(transaction.FinalDirectory);
        await File.WriteAllTextAsync(Path.Combine(transaction.FinalDirectory, "user-created.txt"), "keep");
        var instance = CreateInstance("instance", "Test", transaction.PendingDirectory);

        await Assert.ThrowsAnyAsync<IOException>(() => transaction.CommitAsync(instance));

        Assert.False(transaction.IsCommitted);
        Assert.True(Directory.Exists(transaction.PendingDirectory));
        Assert.Equal("keep", await File.ReadAllTextAsync(
            Path.Combine(transaction.FinalDirectory, "user-created.txt")));
        Assert.False(File.Exists(Path.Combine(transaction.FinalDirectory, "downloaded.txt")));
    }

    [Fact]
    public async Task LoaderUsesLogicalIdentityButCopiesToExplicitPhysicalDirectory()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var transactionService = new InstanceInstallTransactionService();
        await using var transaction = await transactionService.BeginAsync(
            minecraftDirectory, "Test", "game", false);
        var provider = new FakeLoaderProvider();
        var installer = new ModpackGameInstaller([provider]);
        var target = new LoaderInstallTarget(minecraftDirectory, "Test", transaction.PendingDirectory);

        var versionName = await installer.InstallInstanceAsync(
            "1.20.1", LoaderKind.Vanilla, null, target, progress: null);

        Assert.Equal("Test", versionName);
        Assert.Equal("Test", provider.LastIsolatedVersionName);
        Assert.NotEqual(Path.GetFullPath(minecraftDirectory), Path.GetFullPath(provider.LastGameDirectory!));
        Assert.False(Directory.Exists(Path.Combine(minecraftDirectory, "versions", "Test")));
        Assert.True(File.Exists(Path.Combine(transaction.PendingDirectory, "Test.json")));
        Assert.True(File.Exists(Path.Combine(transaction.PendingDirectory, "Test.jar")));
    }

    [Fact]
    public async Task CanceledLoaderInstallDefersSandboxCleanupWithoutWaitingForDeletion()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var transactionService = new InstanceInstallTransactionService();
        await using var transaction = await transactionService.BeginAsync(
            minecraftDirectory, "Test", "game", false);
        var provider = new FakeLoaderProvider { WaitBeforeInstall = Task.Delay(Timeout.InfiniteTimeSpan) };
        var cleanupService = new RecordingSandboxCleanupService(TempRoot);
        var installer = new ModpackGameInstaller(
            [provider],
            new FinalVersionInstaller(),
            sandboxCleanupService: cleanupService);
        using var cancellation = new CancellationTokenSource();

        var installation = installer.InstallInstanceAsync(
            "1.20.1",
            LoaderKind.Vanilla,
            null,
            new LoaderInstallTarget(minecraftDirectory, "Test", transaction.PendingDirectory),
            progress: null,
            cancellation.Token);
        await provider.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installation);

        Assert.True(cleanupService.Session.CleanupCalled);
        Assert.True(cleanupService.Session.DeferCleanup);
    }

    [Fact]
    public async Task ConcurrentInstallersProceedWhileCleanupSkipsTheirActivePendingDirectories()
    {
        var minecraftDirectory = Path.Combine(TempRoot, ".minecraft");
        var settingsService = new TestSettingsService(new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = minecraftDirectory
        });
        var first = new InstanceInstallTransactionService();
        var second = new InstanceInstallTransactionService();
        await using var transaction = await first.BeginAsync(
            minecraftDirectory, "Test", "game", false);
        await using var otherTransaction = await second.BeginAsync(
            minecraftDirectory, "Other", "game", false);
        var cleanup = new InstanceInstallCleanupService(settingsService);
        await cleanup.CleanupPendingAsync();

        Assert.True(Directory.Exists(transaction.PendingDirectory));
        Assert.True(Directory.Exists(otherTransaction.PendingDirectory));
    }

    [Fact]
    public async Task ModpackStagingSuffixesSameNameWhileFirstImportOrDownloadIsActive()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        var transactionService = new InstanceInstallTransactionService();
        var instanceService = new GameInstanceService(
            settingsService,
            repository,
            Array.Empty<ILoaderProvider>(),
            installTransactionService: transactionService);
        var staging = new ModpackInstanceStagingService(
            settingsService,
            repository,
            instanceService,
            transactionService);
        var prepared = new PreparedModpack
        {
            PackageKind = ModpackPackageKind.Modrinth,
            SourceArchivePath = Path.Combine(TempRoot, "pack.mrpack"),
            WorkingDirectory = Path.Combine(TempRoot, "work"),
            PackageName = "Same Pack",
            MinecraftVersion = "1.20.1",
            Loader = LoaderKind.Fabric,
            LoaderVersion = "0.16.10"
        };

        var first = await staging.StageAsync(prepared, "Same Pack");
        var second = await staging.StageAsync(prepared, "Same Pack");

        Assert.Equal("Same Pack", first.ResolvedInstanceName);
        Assert.Equal("Same Pack (1)", second.ResolvedInstanceName);
        Assert.Contains(".bhl-install-pending-Same Pack-", first.InstanceDirectory);
        Assert.Contains(".bhl-install-pending-Same Pack (1)-", second.InstanceDirectory);

        await staging.CleanupFailedImportAsync(first, null);
        await staging.CleanupFailedImportAsync(second, null);
    }

    [Fact]
    public async Task ActiveInstallationDoesNotBlockDeletingAnotherInstance()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        var oldDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "Old");
        Directory.CreateDirectory(oldDirectory);
        await File.WriteAllTextAsync(Path.Combine(oldDirectory, "Old.json"), """{"id":"Old"}""");
        await repository.SaveAllAsync(
        [
            new GameInstance
            {

                Name = "Old",
                MinecraftVersion = "1.20.1",
                VersionName = "Old",
                InstanceDirectory = oldDirectory
            }
        ]);
        var installService = new InstanceInstallTransactionService();
        await using var transaction = await installService.BeginAsync(
            settings.MinecraftDirectory, "Downloading", "game", false);

        var stagedDelete = await repository.StageVersionForDeletionAsync(settings.MinecraftDirectory, "Old", oldDirectory);

        Assert.False(Directory.Exists(oldDirectory));
        Assert.True(Directory.Exists(stagedDelete));
        Assert.True(Directory.Exists(transaction.PendingDirectory));
    }

    [Fact]
    public async Task RenameCannotClaimLogicalNameReservedByActiveInstallation()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        var oldDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "Old");
        Directory.CreateDirectory(oldDirectory);
        await File.WriteAllTextAsync(Path.Combine(oldDirectory, "Old.json"), """{"id":"Old"}""");
        var oldInstance = CreateInstance("old", "Old", oldDirectory);
        await repository.SaveAllAsync([oldInstance]);
        var installService = new InstanceInstallTransactionService();
        var instanceService = new GameInstanceService(
            settingsService,
            repository,
            Array.Empty<ILoaderProvider>(),
            installTransactionService: installService);
        await using var transaction = await installService.BeginAsync(
            settings.MinecraftDirectory, "Test", "game", false);

        await Assert.ThrowsAsync<DuplicateGameInstanceNameException>(() => instanceService.RenameInstanceAsync(
            oldInstance.InstanceDirectory,
            "Test",
            newIconSource: null));

        Assert.True(Directory.Exists(oldDirectory));
        Assert.False(Directory.Exists(Path.Combine(settings.MinecraftDirectory, "versions", "Test")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupCleanupOnlyDeletesInterruptedInstallPreparationWithValidOwnershipMarker(
        bool markerWasPublished)
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var versionsDirectory = Path.Combine(settings.MinecraftDirectory, "versions");
        var preparationRoot = PendingInstanceInstallDirectory.GetPreparationRoot(settings.MinecraftDirectory);
        var preparationDirectory = Path.Combine(
            preparationRoot,
            "a83f21c4000000000000000000000000");
        Directory.CreateDirectory(preparationDirectory);
        await File.WriteAllTextAsync(Path.Combine(preparationDirectory, "partial.tmp"), "partial");
        if (markerWasPublished)
        {
            await File.WriteAllTextAsync(
                Path.Combine(preparationDirectory, PendingInstanceInstallDirectory.MarkerFileName),
                JsonSerializer.Serialize(new { schemaVersion = 2, transactionId = "a83f21c4000000000000000000000000", ownerProof = InstanceDirectoryOwnership.Capture(preparationDirectory), logicalVersionName = "Test", installKind = "game", initializeDefaultIfEmpty = false, createdAtUtc = DateTimeOffset.UtcNow }));
        }

        await new InstanceInstallCleanupService(new TestSettingsService(settings)).CleanupPendingAsync();

        Assert.Equal(markerWasPublished, !Directory.Exists(preparationDirectory));
        Assert.Equal(markerWasPublished, !Directory.Exists(preparationRoot));
        Assert.False(Directory.Exists(Path.Combine(
            versionsDirectory,
            ".bhl-install-pending-Test-a83f21c4")));
    }

    [Fact]
    public async Task CleanupReconcilesCommittedMarkerWithoutDeletingFinalDirectory()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var finalDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "Test");
        await WriteCommittedInstallAsync(finalDirectory);

        await new InstanceInstallCleanupService(settingsService).CleanupPendingAsync();

        Assert.True(Directory.Exists(finalDirectory));
        Assert.True(File.Exists(Path.Combine(finalDirectory, "Test.json")));
        Assert.False(File.Exists(Path.Combine(finalDirectory, ".bhl-install-pending.json")));
        Assert.Equal(finalDirectory, (await settingsService.LoadAsync()).DefaultInstanceDirectory);
    }

    [Fact]
    public async Task CleanupPreservesCommittedMarkerWhenLatestSettingsSaveFails()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new FailingSaveSettingsService(settings);
        var finalDirectory = Path.Combine(settings.MinecraftDirectory, "versions", "Test");
        await WriteCommittedInstallAsync(finalDirectory);
        var markerPath = Path.Combine(finalDirectory, ".bhl-install-pending.json");

        await new InstanceInstallCleanupService(settingsService).CleanupPendingAsync();

        Assert.True(Directory.Exists(finalDirectory));
        Assert.True(File.Exists(markerPath));
    }

    private static async Task WriteCommittedInstallAsync(
        string finalDirectory,
        int schemaVersion = 1,
        string transactionId = "a83f21c4000000000000000000000000",
        string markerInstanceId = "instance",
        string logicalVersionName = "Test",
        string versionJsonId = "Test",
        string settingsInstanceId = "instance",
        string settingsVersionName = "Test",
        string? settingsDirectory = null)
    {
        Directory.CreateDirectory(Path.Combine(finalDirectory, "BHL"));
        await File.WriteAllTextAsync(
            Path.Combine(finalDirectory, "Test.json"),
            JsonSerializer.Serialize(new { id = versionJsonId }));
        await File.WriteAllTextAsync(
            Path.Combine(finalDirectory, ".bhl-install-pending.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion,
                transactionId,
                instanceId = markerInstanceId,
                logicalVersionName,
                installKind = "game",
                initializeDefaultIfEmpty = true,
                createdAtUtc = DateTimeOffset.Parse("2026-07-13T00:00:00Z")
            }));
        await File.WriteAllTextAsync(
            Path.Combine(finalDirectory, "BHL", "instance-settings.json"),
            JsonSerializer.Serialize(new { Id = settingsInstanceId, VersionName = settingsVersionName, InstanceDirectory = settingsDirectory ?? finalDirectory }));
    }

    private static GameInstance CreateInstance(string id, string name, string directory) => new()
    {

        Name = name,
        MinecraftVersion = "1.20.1",
        VersionName = name,
        InstanceDirectory = directory
    };

    private (
        LauncherSettings Settings,
        ModpackInstanceStagingService Staging,
        PreparedModpack PreparedModpack) CreateModpackStagingContext()
    {
        var settings = new LauncherSettings
        {
            DataDirectory = TempRoot,
            MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
        };
        var settingsService = new TestSettingsService(settings);
        var repository = new JsonGameInstanceRepository(settingsService);
        var transactionService = new InstanceInstallTransactionService();
        var instanceService = new GameInstanceService(
            settingsService,
            repository,
            Array.Empty<ILoaderProvider>(),
            installTransactionService: transactionService);
        var staging = new ModpackInstanceStagingService(
            settingsService,
            repository,
            instanceService,
            transactionService);
        return (settings, staging, CreatePreparedModpack());
    }

    private PreparedModpack CreatePreparedModpack() => new()
    {
        PackageKind = ModpackPackageKind.Modrinth,
        SourceArchivePath = Path.Combine(TempRoot, "pack.mrpack"),
        WorkingDirectory = Path.Combine(TempRoot, "work"),
        PackageName = "Same Pack",
        MinecraftVersion = "1.20.1",
        Loader = LoaderKind.Fabric,
        LoaderVersion = "0.16.10"
    };

    private sealed class FailingSaveSettingsService(LauncherSettings settings) : ISettingsService
    {
        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveAsync(LauncherSettings value, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("settings are locked"));
    }

    private sealed class RecordingSandboxCleanupService : IModpackSandboxCleanupService
    {
        public RecordingSandboxCleanupService(string rootDirectory)
        {
            Session = new RecordingSandboxSession(Path.Combine(rootDirectory, "recording-sandbox"));
            Directory.CreateDirectory(Session.DirectoryPath);
        }

        public RecordingSandboxSession Session { get; }

        public IModpackSandboxSession CreateSession(ModpackSandboxKind kind) => Session;

        public Task CleanupStaleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForPendingCleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSandboxSession(string directoryPath) : IModpackSandboxSession
    {
        public string DirectoryPath { get; } = directoryPath;
        public bool CleanupCalled { get; private set; }
        public bool DeferCleanup { get; private set; }

        public Task CleanupAsync(bool deferCleanup)
        {
            CleanupCalled = true;
            DeferCleanup = deferCleanup;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
