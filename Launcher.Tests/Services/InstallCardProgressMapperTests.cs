/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.Services;

public sealed class InstallCardProgressMapperTests
{
    [Theory]
    [InlineData(LoaderKind.Forge, true)]
    [InlineData(LoaderKind.NeoForge, false)]
    public void ParallelCardWaitsForBothAssetsAndTheInstaller(LoaderKind loader, bool assetsFinishFirst)
    {
        var card = new RecordingProgress();
        var mapper = new InstallCardProgressMapper(card, loader, hasOptionalContent: false, useUnifiedCard: true);
        // The installer can finish downloading before metadata or assets are available.
        mapper.ReportParallelInstallProgress(ParallelInstallPart.Installer, 100);
        mapper.ReportParallelInstallProgress(ParallelInstallPart.Metadata, 100);
        mapper.ReportParallelInstallProgress(ParallelInstallPart.Client, 100);
        mapper.ReportParallelInstallProgress(ParallelInstallPart.VanillaLibraries, 100);
        mapper.ReportParallelInstallProgress(ParallelInstallPart.Mappings, 100);
        mapper.ReportParallelInstallProgress(ParallelInstallPart.InstallerDependencies, 100);
        Assert.Equal((InstallProgressStages.DownloadingGameFiles, 20d), card.Latest);

        if (assetsFinishFirst)
        {
            mapper.ReportParallelInstallProgress(ParallelInstallPart.Assets, 100);
            mapper.Report(new LauncherProgress(InstallProgressStages.RunningLoaderInstaller, string.Empty));
            Assert.Equal((InstallProgressStages.InstallingGame, 85d), card.Latest);
            mapper.ReportParallelInstallProgress(ParallelInstallPart.Loader, 100);
        }
        else
        {
            mapper.Report(new LauncherProgress(InstallProgressStages.RunningLoaderInstaller, string.Empty));
            mapper.ReportParallelInstallProgress(ParallelInstallPart.Loader, 100);
            Assert.Equal((InstallProgressStages.DownloadingGameFiles, 25d), card.Latest);
            mapper.ReportParallelInstallProgress(ParallelInstallPart.Assets, 40);
            mapper.ReportParallelInstallProgress(ParallelInstallPart.Assets, 10);
            Assert.Equal((InstallProgressStages.DownloadingGameFiles, 51d), card.Latest);
            mapper.ReportParallelInstallProgress(ParallelInstallPart.Assets, 100);
        }

        Assert.Equal((InstallProgressStages.InstallingGame, 90d), card.Latest);
        mapper.Report(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty));
        mapper.Report(new LauncherProgress(LaunchProgressStages.DownloadingFiles, string.Empty, 100));
        Assert.Equal((InstallProgressStages.InstallingGame, 95d), card.Latest);
        mapper.ReportBaseInstallCompleted();
        mapper.ReportCommitStarted();
        mapper.ReportCommitCompleted();
        Assert.Equal((InstallProgressStages.InstallingGame, 99d), card.Latest);
    }

    [Theory]
    [InlineData(LoaderKind.Vanilla, false)]
    [InlineData(LoaderKind.Fabric, true)]
    [InlineData(LoaderKind.Quilt, true)]
    public void OtherLoadersUseTheSameCardStages(LoaderKind loader, bool optionalContent)
    {
        var card = new RecordingProgress();
        var mapper = new InstallCardProgressMapper(card, loader, optionalContent, useUnifiedCard: true);
        mapper.Report(new LauncherProgress(InstallProgressStages.Preparing, string.Empty));
        Assert.Equal((InstallProgressStages.Preparing, 0d), card.Latest);
        mapper.Report(new LauncherProgress(LaunchProgressStages.DownloadingFiles, "file.jar", 100));
        Assert.Equal((InstallProgressStages.DownloadingGameFiles, optionalContent ? 85d : 90d), card.Latest);
        mapper.Report(new LauncherProgress(InstallProgressStages.FinalizingVersion, string.Empty));
        Assert.Equal(optionalContent ? InstallProgressStages.DownloadingGameFiles : InstallProgressStages.InstallingGame,
            card.Latest.Stage);
        mapper.ReportBaseInstallCompleted();
        if (optionalContent)
        {
            mapper.Report(new LauncherProgress(ModProgressStages.DownloadingFile, "library.jar", 100));
            Assert.Equal((InstallProgressStages.DownloadingGameFiles, 95d), card.Latest);
            mapper.ReportOptionalContentCompleted();
        }
        mapper.ReportCommitStarted();
        mapper.Report(new LauncherProgress(LaunchProgressStages.DownloadingFiles, string.Empty, 100));
        Assert.Equal((InstallProgressStages.InstallingGame, 97d), card.Latest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FabricCardStaysInDownloadStageUntilBothBranchesFinish(bool apiFinishesFirst)
    {
        var card = new RecordingProgress();
        var mapper = new InstallCardProgressMapper(card, LoaderKind.Fabric, hasOptionalContent: true, useUnifiedCard: true);
        mapper.Report(new LauncherProgress(ModProgressStages.DownloadingFile, string.Empty, 80));
        mapper.Report(new LauncherProgress(InstallProgressStages.FinalizingVersion, string.Empty));
        Assert.Equal((InstallProgressStages.DownloadingGameFiles, 85d), card.Latest);
        if (apiFinishesFirst)
            mapper.ReportOptionalContentCompleted();

        mapper.Report(new LauncherProgress(LaunchProgressStages.DownloadingFiles, string.Empty, 100));
        Assert.Equal((InstallProgressStages.DownloadingGameFiles, 88d), card.Latest);
        mapper.ReportBaseInstallCompleted();
        if (!apiFinishesFirst)
        {
            Assert.Equal((InstallProgressStages.DownloadingGameFiles, 93.6d), card.Latest);
            mapper.Report(new LauncherProgress(ModProgressStages.DownloadingFile, string.Empty, 30));
            Assert.Equal((InstallProgressStages.DownloadingGameFiles, 93.6d), card.Latest);
            mapper.ReportOptionalContentCompleted();
        }

        Assert.Equal((InstallProgressStages.InstallingGame, 96d), card.Latest);
        mapper.Report(new LauncherProgress(ModProgressStages.DownloadingFile, string.Empty, 100));
        Assert.Equal((InstallProgressStages.InstallingGame, 96d), card.Latest);
    }

    [Fact]
    public void LegacyForgeReservesProgressForFilesDownloadedAfterJava()
    {
        var card = new RecordingProgress();
        var mapper = new InstallCardProgressMapper(card, LoaderKind.Forge, hasOptionalContent: false, useUnifiedCard: true);
        mapper.Report(new LauncherProgress(InstallProgressStages.RunningLoaderInstaller, string.Empty));
        mapper.Report(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty));
        Assert.Equal((InstallProgressStages.InstallingGame, 38d), card.Latest);
        mapper.Report(new LauncherProgress(LaunchProgressStages.DownloadingFiles, string.Empty, 50));
        Assert.Equal((InstallProgressStages.InstallingGame, 66.5d), card.Latest);
    }

    private sealed class RecordingProgress : IProgress<LauncherProgress>
    {
        public (string Stage, double Percent) Latest { get; private set; }

        public void Report(LauncherProgress value) =>
            Latest = (value.Stage, Assert.IsType<double>(value.Percent));
    }
}
