/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using CmlLib.Core.ProcessBuilder;
using Launcher.Application;
using Launcher.Application.Accounts;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class LaunchServiceTests : TestTempDirectory
{
    [Fact]
    public async Task LaunchRepairsBeforeBuildingProcess()
    {
        var repair = new FakeRepairService();
        var launcher = new FakeLauncherFactory();
        var service = CreateService(repair, launcher);
        var settings = CreateSettings();
        var instance = CreateInstance(settings.MinecraftDirectory, "Forge Pack");

        await service.LaunchAsync(instance, CreateAccount(), settings, null);

        Assert.Equal("Forge Pack", repair.LastVersionName);
        Assert.Equal(instance.InstanceDirectory, repair.LastInstanceDirectory);
        Assert.True(repair.LastAllowRepair);
        Assert.Equal("Forge Pack", launcher.Launcher.LastVersionName);
    }

    [Fact]
    public async Task ManualJavaFailureDoesNotProvisionRuntime()
    {
        var selection = new FakeJavaSelection(
            new JavaRuntimeSelectionException("manual missing", JavaRuntimeSelectionFailureReason.ManualRuntimeUnavailable));
        var provisioning = new FakeJavaProvisioning();
        var launcher = new FakeLauncherFactory();
        var service = CreateService(javaSelection: selection, javaProvisioning: provisioning, launcher: launcher);
        var settings = CreateSettings();
        settings.JavaSelectionMode = JavaSelectionMode.Manual;
        settings.SelectedJavaExecutablePath = @"C:\Missing\java.exe";

        var exception = await Assert.ThrowsAsync<LaunchFailedException>(() => service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "1.21.4"), CreateAccount(), settings, null));

        Assert.IsType<JavaRuntimeSelectionException>(exception.InnerException);
        Assert.Equal(0, provisioning.CallCount);
        Assert.Null(launcher.Launcher.LastVersionName);
    }

    [Fact]
    public async Task ReauthenticationRequiredIsPropagatedForAccountDialogHandling()
    {
        var expected = new LaunchAccountSessionException(
            LaunchAccountSessionFailureReason.ReauthenticationRequired,
            "Interactive Microsoft authentication is required.");
        var service = CreateService(accountSession: new FailingAccountSession(expected));
        var settings = CreateSettings();
        settings.DefaultCheckFilesBeforeLaunch = false;

        var actual = await Assert.ThrowsAsync<LaunchAccountSessionException>(() =>
            service.LaunchAsync(
                CreateInstance(settings.MinecraftDirectory, "Microsoft Reauthentication"),
                CreateAccount(),
                settings,
                progress: null));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task QuickExitWritesRedactedDiagnostic()
    {
        var settings = CreateSettings();
        var instance = CreateInstance(settings.MinecraftDirectory, "Broken Pack");
        Directory.CreateDirectory(Path.Combine(instance.InstanceDirectory, "logs"));
        await File.WriteAllTextAsync(Path.Combine(instance.InstanceDirectory, "logs", "latest.log"), "[ERROR]: Missing launch target");
        var launcher = new FakeLauncherFactory
        {
            BuildProcess = (_, _) => CreateCommandProcess(
                "/c echo ERROR Missing launch target --accessToken super-secret-access-token 1>&2 & exit 1")
        };
        var service = CreateService(
            launcher: launcher,
            crashMonitor: new LaunchCrashMonitor(),
            startupReadinessWaiter: new GameStartupReadinessWaiter());
        var reports = new List<LauncherProgress>();

        var exception = await Assert.ThrowsAsync<LaunchProcessExitedException>(() =>
            service.LaunchAsync(instance, CreateAccount(), settings, new InlineProgress(reports)));

        Assert.Equal(LaunchFailureKind.StartupAbnormalExit, exception.Report.Kind);
        Assert.DoesNotContain(reports, report => report.Percent == 100);
        Assert.Contains("super-secret-access-token", exception.Report.ExportSensitiveValues);
        Assert.True(File.Exists(exception.DiagnosticPath));
        var expectedDiagnosticDirectory = Path.Combine(
            instance.InstanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName,
            "logs");
        Assert.Equal(expectedDiagnosticDirectory, exception.Report.DiagnosticDirectory);
        Assert.Equal(expectedDiagnosticDirectory, Path.GetDirectoryName(exception.DiagnosticPath));
        Assert.Equal(LaunchDiagnosticType.CapturedOutput, exception.Report.PrimaryDiagnostic?.Type);
        Assert.Equal(expectedDiagnosticDirectory, Path.GetDirectoryName(exception.Report.PrimaryDiagnostic?.Path));
        Assert.Equal(LaunchDiagnosticType.LauncherDiagnostic, exception.Report.DiagnosticCandidates[^1].Type);
        var diagnostic = await File.ReadAllTextAsync(exception.DiagnosticPath!);
        Assert.Contains("Missing launch target", diagnostic);
        Assert.Contains("[PrimaryDiagnostic]", diagnostic);
        Assert.Contains("Type: CapturedOutput", diagnostic);
        Assert.Contains("[RelatedDiagnostics]", diagnostic);
        Assert.Contains("LauncherDiagnostic:", diagnostic);
        Assert.Contains("Evidence.1: Reason:", diagnostic);
        Assert.DoesNotContain("super-secret-access-token", diagnostic);
        var capturedOutput = await File.ReadAllTextAsync(exception.Report.PrimaryDiagnostic!.Path);
        Assert.Contains("[stderr]", capturedOutput);
        Assert.Contains("<redacted>", capturedOutput);
        Assert.DoesNotContain("super-secret-access-token", capturedOutput);
    }

    [Theory]
    [InlineData("[Render thread/INFO]: OpenAL initialized on device Test")]
    [InlineData("[Render thread/INFO]: Sound engine started")]
    [InlineData("Created 1024x1024 minecraft:textures/atlas/blocks-atlas")]
    public void StartupReadinessDetectorRecognizesStableGameOutput(string line)
    {
        Assert.True(GameStartupLogReadinessDetector.IsReadyLine(line));
        Assert.False(GameStartupLogReadinessDetector.IsReadyLine("Preparing spawn area: 75%"));
    }

    [Fact]
    public async Task IntegrityCancellationDoesNotWriteRepairFailureDiagnostic()
    {
        using var cancellation = new CancellationTokenSource();
        var integrity = new RecordingIntegrityService
        {
            OnValidate = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<GameFileRepairResult>(token);
            }
        };
        var launcher = new FakeLauncherFactory();
        var service = CreateService(integrity: integrity, launcher: launcher);
        var settings = CreateSettings();
        var instance = CreateInstance(settings.MinecraftDirectory, "Canceled Integrity");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LaunchAsync(
            instance,
            CreateAccount(),
            settings,
            progress: null,
            cancellationToken: cancellation.Token));

        Assert.Null(launcher.Launcher.LastVersionName);
        Assert.False(Directory.Exists(Path.Combine(
            instance.InstanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName,
            "logs")));
    }

    [Fact]
    public async Task IntegrityCanceledResultUsesCancellationFlowWithoutFailureDialogReport()
    {
        using var cancellation = new CancellationTokenSource();
        var integrity = new RecordingIntegrityService
        {
            OnValidate = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(CreateIntegrityFailureResult(
                    new GameFileRepairFailure(
                        "canceled",
                        "integrity",
                        GameFileRepairFailureReason.Canceled,
                        "none",
                        null)));
            }
        };
        var service = CreateService(integrity: integrity);
        var settings = CreateSettings();
        var instance = CreateInstance(settings.MinecraftDirectory, "Canceled Integrity Result");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LaunchAsync(
            instance,
            CreateAccount(),
            settings,
            progress: null,
            cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(
            instance.InstanceDirectory,
            LauncherApplicationIdentity.StorageDirectoryName,
            "logs")));
    }

    public static TheoryData<GameFileRepairFailureReason, string> PreLaunchIntegrityFailures => new()
    {
        { GameFileRepairFailureReason.Missing, "assets/indexes/32.json" },
        { GameFileRepairFailureReason.Corrupted, "assets/indexes/32.json" },
        { GameFileRepairFailureReason.MetadataIncomplete, "versions/1.20.1/1.20.1.json" },
        { GameFileRepairFailureReason.DownloadFailed, "libraries/example/library.jar" },
        { GameFileRepairFailureReason.ProcessorRegenerationFailed, "versions/Forge Pack/Forge Pack.jar" },
        { GameFileRepairFailureReason.PublicationFailed, "assets/objects/ab/example" }
    };

    [Theory]
    [MemberData(nameof(PreLaunchIntegrityFailures))]
    public async Task PreLaunchIntegrityFailureWritesStructuredAnalysis(
        GameFileRepairFailureReason reason,
        string relativeTargetPath)
    {
        var settings = CreateSettings();
        settings.DefaultAutoRepairMissingFiles = false;
        var targetPath = Path.Combine(
            settings.MinecraftDirectory,
            Path.Combine(relativeTargetPath.Split('/')));
        var integrity = new RecordingIntegrityService
        {
            OnValidate = _ => Task.FromResult(CreateIntegrityFailureResult(
                new GameFileRepairFailure(targetPath, "test", reason, "test", null),
                new GameFileRepairFailure("ignored", "test", GameFileRepairFailureReason.DownloadFailed, "test", null)))
        };
        var service = CreateService(integrity: integrity);

        var exception = await Assert.ThrowsAsync<LaunchFailedException>(() => service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "Integrity Failure"),
            CreateAccount(),
            settings,
            progress: null));

        var analysis = Assert.IsType<LaunchFailureAnalysis>(exception.Report.Analysis);
        Assert.Equal(LaunchFailureCategory.GameFileIntegrity, analysis.Category);
        Assert.Equal(reason, analysis.GameFileFailureReason);
        Assert.False(analysis.AutoRepairEnabled);
        Assert.False(integrity.LastOptions?.AllowRepair);
        Assert.Equal(Path.Combine(relativeTargetPath.Split('/')), analysis.AffectedPath);
        Assert.Contains(targetPath, exception.Report.FailureSummary);
    }

    [Fact]
    public async Task FinalLaunchPlanFailureWritesStructuredAnalysis()
    {
        var settings = CreateSettings();
        var targetPath = Path.Combine(settings.MinecraftDirectory, "versions", "Final Plan", "missing.jar");
        var integrity = new RecordingIntegrityService
        {
            OnFinalValidate = _ => Task.FromResult(CreateIntegrityFailureResult(
                new GameFileRepairFailure(
                    targetPath,
                    "launch-command",
                    GameFileRepairFailureReason.FinalLaunchPlanInvalid,
                    "none",
                    null)))
        };
        var service = CreateService(integrity: integrity);

        var exception = await Assert.ThrowsAsync<LaunchFailedException>(() => service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "Final Plan"),
            CreateAccount(),
            settings,
            progress: null));

        var analysis = Assert.IsType<LaunchFailureAnalysis>(exception.Report.Analysis);
        Assert.Equal(LaunchFailureCategory.GameFileIntegrity, analysis.Category);
        Assert.Equal(GameFileRepairFailureReason.FinalLaunchPlanInvalid, analysis.GameFileFailureReason);
        Assert.True(analysis.AutoRepairEnabled);
        Assert.Equal(Path.Combine("versions", "Final Plan", "missing.jar"), analysis.AffectedPath);
        Assert.Equal(1, integrity.FinalValidationCallCount);
    }

    [Fact]
    public async Task IntegrityFailureOutsideMinecraftDirectoryOnlyReportsFileName()
    {
        var settings = CreateSettings();
        var targetPath = Path.Combine(TempRoot, "outside", "private-path", "locked.jar");
        var integrity = new RecordingIntegrityService
        {
            OnValidate = _ => Task.FromResult(CreateIntegrityFailureResult(
                new GameFileRepairFailure(
                    targetPath,
                    "library",
                    GameFileRepairFailureReason.PublicationFailed,
                    "replace",
                    null)))
        };
        var service = CreateService(integrity: integrity);

        var exception = await Assert.ThrowsAsync<LaunchFailedException>(() => service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "Outside Path"),
            CreateAccount(),
            settings,
            progress: null));

        var analysis = Assert.IsType<LaunchFailureAnalysis>(exception.Report.Analysis);
        Assert.Equal("locked.jar", analysis.AffectedPath);
        Assert.DoesNotContain("private-path", analysis.AffectedPath);
    }

    [Fact]
    public async Task OfflineAccountSkinAddsManagedInjectorArgumentsAndTrustedJar()
    {
        var injectorPath = Path.Combine(TempRoot, "authlib-injector.jar");
        var offlineSkin = new FakeOfflineSkinLaunchService
        {
            Context = new OfflineSkinLaunchContext(
                "http://127.0.0.1:32123/",
                "prefetched-metadata")
        };
        var authlib = new FakeAuthlibInjectorProvisioningService
        {
            Artifact = new AuthlibInjectorArtifact(injectorPath, "1.2.7", 55)
        };
        var launcher = new FakeLauncherFactory();
        var integrity = new RecordingIntegrityService();
        var service = CreateService(
            integrity: integrity,
            launcher: launcher,
            authlibInjector: authlib,
            offlineSkin: offlineSkin);
        var settings = CreateSettings();
        var reports = new List<LauncherProgress>();

        var session = await service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "Offline Skin"),
            CreateAccountWithSkin(),
            settings,
            new InlineProgress(reports));

        Assert.Equal(1, offlineSkin.CallCount);
        Assert.Equal(1, authlib.CallCount);
        var arguments = launcher.Launcher.LastOption!.ExtraJvmArguments!
            .SelectMany(argument => argument.Values)
            .ToArray();
        Assert.Contains($"-javaagent:{injectorPath}=http://127.0.0.1:32123/", arguments);
        Assert.Contains("-Dauthlibinjector.side=client", arguments);
        Assert.Contains("-Dauthlibinjector.yggdrasil.prefetched=prefetched-metadata", arguments);
        Assert.Equal("{}", launcher.Launcher.LastOption.UserProperties);
        Assert.Equal("mojang", launcher.Launcher.LastOption.ArgumentDictionary!["user_type"]);
        Assert.Contains(
            Path.GetFullPath(injectorPath),
            integrity.FinalRequest!.AllowedAdditionalCommandFilePaths);
        Assert.Contains(reports, report =>
            report.Stage == LaunchProgressStages.PreparingOfflineSkin);
        Assert.Empty(session.Warnings);
    }

    [Fact]
    public async Task OfflineInjectorFailureContinuesWithoutPartialArguments()
    {
        var offlineSkin = new FakeOfflineSkinLaunchService
        {
            Context = new OfflineSkinLaunchContext(
                "http://127.0.0.1:32123/",
                "prefetched-metadata")
        };
        var authlib = new FakeAuthlibInjectorProvisioningService
        {
            Exception = new InvalidOperationException("No verified injector.")
        };
        var launcher = new FakeLauncherFactory();
        var service = CreateService(
            launcher: launcher,
            authlibInjector: authlib,
            offlineSkin: offlineSkin);
        var settings = CreateSettings();

        var session = await service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "Offline Injector Fallback"),
            CreateAccountWithSkin(),
            settings,
            progress: null);

        Assert.Equal(1, offlineSkin.CallCount);
        Assert.Equal(1, authlib.CallCount);
        Assert.DoesNotContain(
            GetJvmArgumentValues(launcher.Launcher.LastOption!),
            value => value.StartsWith("-javaagent:", StringComparison.Ordinal)
                || value.StartsWith("-Dauthlibinjector.", StringComparison.Ordinal));
        Assert.Equal(
            LaunchWarningKind.OfflineSkinUnavailable,
            Assert.Single(session.Warnings));
    }

    [Fact]
    public async Task ThirdPartyLaunchKeepsExistingInjectorFlow()
    {
        var offlineSkin = new FakeOfflineSkinLaunchService();
        var authlib = new FakeAuthlibInjectorProvisioningService
        {
            Artifact = new AuthlibInjectorArtifact("third-party-authlib.jar", "1.2.7", 55)
        };
        var launcher = new FakeLauncherFactory();
        var thirdPartySession = new LaunchAccountSession(
            "ThirdPartyPlayer",
            "third-party-token",
            "00112233445566778899aabbccddeeff",
            IsOffline: false,
            Kind: LauncherAccountKind.ThirdParty,
            ThirdParty: new ThirdPartyLaunchContext(
                "https://auth.example.test/api/yggdrasil/",
                "third-party-prefetched"));
        var service = CreateService(
            launcher: launcher,
            accountSession: new FakeAccountSession(thirdPartySession),
            authlibInjector: authlib,
            offlineSkin: offlineSkin);
        var settings = CreateSettings();
        var account = new LauncherAccount
        {
            Id = "third-party",
            DisplayName = "ThirdPartyPlayer",
            Uuid = "00112233-4455-6677-8899-aabbccddeeff",
            Kind = LauncherAccountKind.ThirdParty
        };

        var session = await service.LaunchAsync(
            CreateInstance(settings.MinecraftDirectory, "Third Party"),
            account,
            settings,
            progress: null);

        Assert.Equal(0, offlineSkin.CallCount);
        Assert.Equal(1, authlib.CallCount);
        var arguments = GetJvmArgumentValues(launcher.Launcher.LastOption!);
        Assert.Contains(
            "-javaagent:third-party-authlib.jar=https://auth.example.test/api/yggdrasil/",
            arguments);
        Assert.Contains(
            "-Dauthlibinjector.yggdrasil.prefetched=third-party-prefetched",
            arguments);
        Assert.DoesNotContain("-Dauthlibinjector.side=client", arguments);
        Assert.Empty(session.Warnings);
    }

    private static LaunchService CreateService(
        FakeRepairService? repair = null,
        FakeLauncherFactory? launcher = null,
        IGameFileIntegrityService? integrity = null,
        ILaunchCrashMonitor? crashMonitor = null,
        IJavaRuntimeSelectionService? javaSelection = null,
        IJavaRuntimeProvisioningService? javaProvisioning = null,
        ILaunchAccountSessionService? accountSession = null,
        IAuthlibInjectorProvisioningService? authlibInjector = null,
        IOfflineSkinLaunchService? offlineSkin = null,
        IGameStartupReadinessWaiter? startupReadinessWaiter = null,
        ILaunchProcessTerminator? processTerminator = null)
    {
        var resolvedAccountSession = accountSession ?? new FakeAccountSession();
        var resolvedLauncher = launcher ?? new FakeLauncherFactory();
        var resolvedCrashMonitor = crashMonitor ?? new NoOpCrashMonitor();
        var resolvedStartupWaiter = startupReadinessWaiter ?? new ImmediateStartupReadinessWaiter();
        return integrity is not null
            ? new LaunchService(
                resolvedAccountSession,
                integrity,
                resolvedLauncher,
                resolvedCrashMonitor,
                javaRuntimeSelectionService: javaSelection,
                javaRuntimeProvisioningService: javaProvisioning,
                authlibInjectorProvisioningService: authlibInjector,
                offlineSkinLaunchService: offlineSkin,
                gameStartupReadinessWaiter: resolvedStartupWaiter,
                launchProcessTerminator: processTerminator)
            : new LaunchService(
                resolvedAccountSession,
                repair ?? new FakeRepairService(),
                resolvedLauncher,
                resolvedCrashMonitor,
                javaRuntimeSelectionService: javaSelection,
                javaRuntimeProvisioningService: javaProvisioning,
                authlibInjectorProvisioningService: authlibInjector,
                offlineSkinLaunchService: offlineSkin,
                gameStartupReadinessWaiter: resolvedStartupWaiter,
                launchProcessTerminator: processTerminator);
    }

    private LauncherSettings CreateSettings() => new()
    {
        MinecraftDirectory = Path.Combine(TempRoot, ".minecraft")
    };

    private static GameInstance CreateInstance(string minecraftDirectory, string name) => new()
    {
        Id = name,
        Name = name,
        MinecraftVersion = "1.20.1",
        VersionName = name,
        InstanceDirectory = Path.Combine(minecraftDirectory, "versions", name),
        Loader = LoaderKind.Vanilla,
        MemoryMb = 4096
    };

    private static GameFileRepairResult CreateIntegrityFailureResult(
        params GameFileRepairFailure[] failures) => new(
        LaunchAllowed: false,
        RequiredCount: failures.Length,
        MissingCount: failures.Count(failure => failure.Reason == GameFileRepairFailureReason.Missing),
        CorruptedCount: failures.Count(failure => failure.Reason == GameFileRepairFailureReason.Corrupted),
        UnverifiableCount: 0,
        RepairableCount: failures.Length,
        RepairedCount: 0,
        FailedCount: failures.Length,
        Failures: failures);

    private static LauncherAccount CreateAccount() => new()
    {
        Id = "offline",
        DisplayName = "Player",
        Uuid = "00000000-0000-0000-0000-000000000001",
        Kind = LauncherAccountKind.Offline
    };

    private static LauncherAccount CreateAccountWithSkin()
    {
        var skin = new LauncherSkinRecord
        {
            Id = "shared-skin",
            Source = "file:///shared-skin.png",
            SkinModel = MinecraftSkinModel.Slim,
            ContentHash = "shared-hash"
        };
        return new LauncherAccount
        {
            Id = "offline",
            DisplayName = "Player",
            Uuid = "00000000-0000-0000-0000-000000000001",
            Kind = LauncherAccountKind.Offline,
            SkinSource = skin.Source,
            SkinModel = skin.SkinModel,
            SkinLibrary = [skin],
            ActiveSkinId = skin.Id
        };
    }

    private static string[] GetJvmArgumentValues(MLaunchOption option) =>
        option.ExtraJvmArguments?
            .SelectMany(argument => argument.Values)
            .ToArray()
        ?? [];

    private static Process CreateCommandProcess(string arguments) => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false
        }
    };

    private sealed class FakeAccountSession(LaunchAccountSession? session = null) : ILaunchAccountSessionService
    {
        public Task<LaunchAccountSession> CreateSessionAsync(LauncherAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult(session ?? new LaunchAccountSession(
                account.DisplayName,
                "super-secret-access-token",
                account.Uuid!,
                account.IsOffline));
    }

    private sealed class FailingAccountSession(LaunchAccountSessionException exception) : ILaunchAccountSessionService
    {
        public Task<LaunchAccountSession> CreateSessionAsync(
            LauncherAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromException<LaunchAccountSession>(exception);
    }

    private sealed class FakeOfflineSkinLaunchService : IOfflineSkinLaunchService
    {
        public int CallCount { get; private set; }
        public OfflineSkinLaunchContext? Context { get; init; }
        public Exception? Exception { get; init; }

        public Task<OfflineSkinLaunchContext?> PrepareAsync(
            LauncherAccount account,
            string sessionUuid,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Exception is null
                ? Task.FromResult(Context)
                : Task.FromException<OfflineSkinLaunchContext?>(Exception);
        }
    }

    private sealed class FakeAuthlibInjectorProvisioningService : IAuthlibInjectorProvisioningService
    {
        public int CallCount { get; private set; }
        public AuthlibInjectorArtifact Artifact { get; init; } =
            new("authlib-injector.jar", "1.2.7", 55);
        public Exception? Exception { get; init; }

        public Task<AuthlibInjectorArtifact> EnsureAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Exception is null
                ? Task.FromResult(Artifact)
                : Task.FromException<AuthlibInjectorArtifact>(Exception);
        }
    }

    private sealed class InlineProgress(List<LauncherProgress> reports) : IProgress<LauncherProgress>
    {
        public void Report(LauncherProgress value) => reports.Add(value);
    }

    private sealed class RecordingIntegrityService : IGameFileIntegrityService
    {
        public Func<CancellationToken, Task<GameFileRepairResult>>? OnValidate { get; init; }
        public Func<ProcessStartInfo, Task<GameFileRepairResult>>? OnFinalValidate { get; init; }
        public GameFileIntegrityRequest? FinalRequest { get; private set; }
        public int ValidateCallCount { get; private set; }
        public int FinalValidationCallCount { get; private set; }

        public GameFileRepairOptions? LastOptions { get; private set; }

        public Task<GameFileRepairResult> ValidateAndRepairAsync(
            GameFileIntegrityRequest request,
            GameFileRepairOptions options,
            IProgress<LauncherProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ValidateCallCount++;
            LastOptions = options;
            return OnValidate?.Invoke(cancellationToken) ?? Task.FromResult(GameFileRepairResult.Empty);
        }

        public Task<GameFileRepairResult> ValidateFinalLaunchCommandAsync(
            GameFileIntegrityRequest request,
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default)
        {
            FinalValidationCallCount++;
            FinalRequest = request;
            return OnFinalValidate?.Invoke(startInfo) ?? Task.FromResult(GameFileRepairResult.Empty);
        }
    }

    private sealed class FakeRepairService : IManagedVersionRepairService
    {
        public string? LastVersionName { get; private set; }
        public string? LastInstanceDirectory { get; private set; }
        public bool LastAllowRepair { get; private set; }
        public Func<Task>? OnRepair { get; init; }

        public Task RepairAsync(string minecraftDirectory, string versionName, string instanceDirectory,
            IProgress<LauncherProgress>? progress, bool allowRepair, CancellationToken cancellationToken = default,
            DownloadSourcePreference downloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference,
            int downloadSpeedLimitMbPerSecond = 0)
        {
            LastVersionName = versionName;
            LastInstanceDirectory = instanceDirectory;
            LastAllowRepair = allowRepair;
            return OnRepair?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class FakeJavaSelection(params object[] results) : IJavaRuntimeSelectionService
    {
        private readonly Queue<object> results = new(results);
        public int CallCount { get; private set; }

        public Task<JavaRuntimeInfo> SelectForLaunchAsync(GameInstance instance, LauncherSettings settings,
            LaunchRequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = results.Dequeue();
            if (result is Exception exception) throw exception;
            return Task.FromResult((JavaRuntimeInfo)result);
        }
    }

    private sealed class FakeJavaProvisioning : IJavaRuntimeProvisioningService
    {
        public int CallCount { get; private set; }
        public Task EnsureForLaunchAsync(GameInstance instance, LauncherSettings settings,
            IProgress<LauncherProgress>? progress, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLauncherFactory : ILaunchGameLauncherFactory
    {
        public FakeLauncher Launcher { get; } = new();
        public Func<string, MLaunchOption, Process>? BuildProcess { get; init; }
        public ILaunchGameLauncher Create(string minecraftDirectory, IProgress<LauncherProgress>? progress,
            int downloadSpeedLimitMbPerSecond = 0)
        {
            Launcher.BuildProcess = BuildProcess;
            return Launcher;
        }
    }

    private sealed class FakeLauncher : ILaunchGameLauncher
    {
        public string? LastVersionName { get; private set; }
        public MLaunchOption? LastOption { get; private set; }
        public Func<string, MLaunchOption, Process>? BuildProcess { get; set; }

        public ValueTask<Process> BuildProcessAsync(string versionName, MLaunchOption option, CancellationToken cancellationToken)
        {
            LastVersionName = versionName;
            LastOption = option;
            return ValueTask.FromResult(BuildProcess?.Invoke(versionName, option) ?? CreateCommandProcess("/c exit 0"));
        }
    }

    private sealed class NoOpCrashMonitor : ILaunchCrashMonitor
    {
        public ILaunchCrashMonitorSession CreateSession(string minecraftDirectory, string instanceDirectory, string versionName) => new Session();

        private sealed class Session : ILaunchCrashMonitorSession
        {
            public Task GameOutputReady { get; } = Task.Delay(Timeout.InfiniteTimeSpan);
            public void Configure(Process process) { }
            public void BeginMonitoring(Process process, LaunchDiagnosticContext context) { }
            public Task<LaunchCrashMonitorResult> CreateStartupExitResultAsync(
                Process process,
                LaunchDiagnosticContext context,
                CancellationToken cancellationToken) => throw new InvalidOperationException("The fake process did not exit during startup.");
            public Task CompleteCanceledStartupAsync(Process process) => Task.CompletedTask;
            public GameLaunchSession CreateGameLaunchSession(Process process, LaunchDiagnosticContext context) =>
                new(context.InstanceId, context.InstanceName, Task.FromResult(LaunchExitResult.Success));
        }
    }

    private sealed class ImmediateStartupReadinessWaiter : IGameStartupReadinessWaiter
    {
        public Task<GameStartupReadinessResult> WaitAsync(
            Process process,
            Task gameOutputReady,
            CancellationToken cancellationToken) =>
            Task.FromResult(GameStartupReadinessResult.WindowVisible);
    }

}
