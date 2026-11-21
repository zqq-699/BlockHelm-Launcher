/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

/// <summary>
/// Projects low-level installation events onto the bounded progress range of a
/// download card.  The projection is deliberately local to one logical install
/// operation so retries and parallel callbacks cannot make the card regress.
/// </summary>
internal sealed class InstallCardProgressMapper : IProgress<LauncherProgress>, ISpeedMeterProgress, IParallelInstallProgress
{
    // Metadata, assets, five download prerequisites, and the Java installer.
    private static readonly double[] ParallelWeights = [5, 65, 3, 3, 3, 3, 3, 5];
    private readonly IProgress<LauncherProgress> innerProgress;
    private readonly bool hasExternalLoaderInstaller;
    private readonly bool hasOptionalContent;
    private readonly bool useUnifiedCard;
    private readonly object sync = new();
    private readonly double[] completedParts = new double[ParallelWeights.Length];
    private bool runningInstaller;
    private bool finalizing;
    private bool baseInstallCompleted;
    private bool optionalContentPending;
    private bool optionalContentCompleted;
    private double optionalContentPercent;
    private double lastPercent;

    public InstallCardProgressMapper(
        IProgress<LauncherProgress> innerProgress,
        LoaderKind loader,
        bool hasOptionalContent,
        bool useUnifiedCard = false)
    {
        this.innerProgress = innerProgress;
        hasExternalLoaderInstaller = loader is LoaderKind.Forge or LoaderKind.NeoForge;
        this.hasOptionalContent = hasOptionalContent;
        this.useUnifiedCard = useUnifiedCard;
    }

    public SpeedMeter? SpeedMeter => SpeedMeterProgress.TryGet(innerProgress);

    public bool TracksParallelInstall => useUnifiedCard && hasExternalLoaderInstaller;

    public void Report(LauncherProgress value)
    {
        lock (sync)
        {
            if (value.DownloadSpeedTelemetry is not null)
                innerProgress.Report(value);
            else if (useUnifiedCard)
                ReportUnified(value);
            else
                ReportLegacy(value);
        }
    }

    public void ReportParallelInstallProgress(ParallelInstallPart part, double percent)
    {
        if (!TracksParallelInstall)
            return;

        lock (sync)
        {
            var index = (int)part;
            completedParts[index] = Math.Max(completedParts[index], Math.Clamp(percent, 0, 100));
            if (ParallelInstallStarted && !finalizing)
                EmitParallel();
        }
    }

    private bool ParallelInstallStarted => completedParts[(int)ParallelInstallPart.Metadata] == 100;

    private double FinalizationStart => hasExternalLoaderInstaller && !ParallelInstallStarted ? 38
        : hasOptionalContent ? 85 : 90;

    private string FinalizationStage => hasOptionalContent
        ? InstallProgressStages.DownloadingGameFiles
        : InstallProgressStages.InstallingGame;

    private void EmitParallel()
    {
        var percent = 0d;
        for (var i = 0; i < completedParts.Length; i++)
            percent += ParallelWeights[i] * completedParts[i] / 100d;

        var stage = runningInstaller && completedParts[(int)ParallelInstallPart.Assets] == 100
            ? InstallProgressStages.InstallingGame
            : InstallProgressStages.DownloadingGameFiles;
        Emit(new LauncherProgress(stage, string.Empty, percent));
    }

    private void ReportUnified(LauncherProgress value)
    {
        if (hasOptionalContent && !optionalContentCompleted && value.Stage is ModProgressStages.DownloadingFile)
        {
            optionalContentPercent = Math.Max(optionalContentPercent, value.Percent ?? 0);
            if (optionalContentPending)
                EmitUnified(InstallProgressStages.DownloadingGameFiles, MapPercent(88, 95, optionalContentPercent));
            return;
        }

        if (baseInstallCompleted)
        {
            return;
        }

        if (value.Stage is InstallProgressStages.Queue or InstallProgressStages.Preparing)
        {
            if (!ParallelInstallStarted && !finalizing)
                Emit(value with { Percent = 0 });
            return;
        }

        if (value.Stage is InstallProgressStages.FinalizingVersion or InstallProgressStages.CompletingFiles)
        {
            finalizing = true;
            var boundary = FinalizationStart;
            EmitUnified(FinalizationStage,
                value.Stage is InstallProgressStages.CompletingFiles && boundary >= 85 ? boundary + 2 : boundary);
            return;
        }

        if (value.Stage is InstallProgressStages.RunningLoaderInstaller)
        {
            runningInstaller = true;
            if (ParallelInstallStarted && !finalizing)
                EmitParallel();
            else
                EmitUnified(InstallProgressStages.InstallingGame, 30);
            return;
        }

        if (ParallelInstallStarted && !finalizing)
            return;

        if (value.Stage is LaunchProgressStages.CheckingFiles or LaunchProgressStages.DownloadingFiles)
        {
            EmitUnified(
                finalizing ? FinalizationStage : InstallProgressStages.DownloadingGameFiles,
                finalizing ? MapPercent(FinalizationStart, hasOptionalContent ? 88d : 95d, value.Percent)
                    : MapPercent(5, hasOptionalContent ? 85d : 90d, value.Percent));
        }
        else if (!finalizing && value.Stage is InstallProgressStages.DownloadingLoaderInstaller
            or InstallProgressStages.DownloadingJava or LaunchProgressStages.DownloadingJava
            or ImportProgressStages.InstallingMinecraftBase)
        {
            EmitUnified(InstallProgressStages.DownloadingGameFiles,
                value.Stage is InstallProgressStages.DownloadingLoaderInstaller && TracksParallelInstall ? 0 : 5);
        }
    }

    private static double MapPercent(double start, double end, double? percent) =>
        start + (end - start) * Math.Clamp(percent ?? 0, 0, 100) / 100;

    private void EmitUnified(string stage, double percent) => Emit(new LauncherProgress(stage, string.Empty, percent));

    private void ReportLegacy(LauncherProgress value)
    {
        var percent = value.Stage switch
        {
            InstallProgressStages.Queue => 0d,
            InstallProgressStages.Preparing => 4d,
            InstallProgressStages.DownloadingLoaderInstaller => MapLoaderBoundary(12d),
            ImportProgressStages.InstallingMinecraftBase when hasExternalLoaderInstaller => MapLoaderBoundary(
                12d + (14d * Math.Clamp(value.Percent ?? 0, 0, 100) / 100d)),
            InstallProgressStages.CheckingJava => MapLoaderBoundary(26d),
            InstallProgressStages.DownloadingJava => MapLoaderBoundary(
                26d + (4d * Math.Clamp(value.Percent ?? 0, 0, 100) / 100d)),
            InstallProgressStages.RunningLoaderInstaller => MapLoaderBoundary(30d),
            InstallProgressStages.FinalizingVersion => MapLoaderBoundary(38d),
            InstallProgressStages.CompletingFiles => MapLoaderBoundary(38d),
            LaunchProgressStages.CheckingFiles or LaunchProgressStages.DownloadingFiles =>
                MapFileProgress(value.Percent),
            ModProgressStages.DownloadingFile when hasOptionalContent => 86d,
            _ => value.Percent
        };

        Emit(value with { Percent = percent });
    }

    public void ReportBaseInstallCompleted()
    {
        lock (sync)
        {
            var optionalPending = hasOptionalContent && !optionalContentCompleted;
            ReportMilestone(
                useUnifiedCard && optionalPending ? InstallProgressStages.DownloadingGameFiles : InstallProgressStages.InstallingGame,
                optionalPending ? (useUnifiedCard ? MapPercent(88, 95, optionalContentPercent) : 86) : 96,
                allowOptionalProgress: optionalPending);
        }
    }

    public void ReportOptionalContentCompleted()
    {
        lock (sync)
        {
            optionalContentCompleted = true;
            if (hasOptionalContent && baseInstallCompleted)
                ReportMilestone(InstallProgressStages.InstallingGame, 96);
        }
    }

    public void ReportCommitStarted() =>
        ReportMilestone(InstallProgressStages.InstallingGame, useUnifiedCard ? 97 : 96);

    public void ReportCommitCompleted() =>
        ReportMilestone(InstallProgressStages.InstallingGame, 99);

    private void ReportMilestone(string stage, double percent, bool allowOptionalProgress = false)
    {
        lock (sync)
        {
            finalizing = true;
            baseInstallCompleted = true;
            optionalContentPending = allowOptionalProgress;
            EmitUnified(useUnifiedCard ? stage : InstallProgressStages.CompletingFiles, percent);
        }
    }

    private double? MapFileProgress(double? rawPercent)
    {
        var start = hasExternalLoaderInstaller ? 38d : 4d;
        var end = hasOptionalContent ? 86d : 96d;
        if (rawPercent is null)
            return Math.Max(lastPercent, start);

        return start + ((end - start) * Math.Clamp(rawPercent.Value, 0, 100) / 100d);
    }

    private double MapLoaderBoundary(double boundary)
    {
        if (!hasExternalLoaderInstaller)
            return Math.Max(lastPercent, 4d);

        var end = hasOptionalContent ? 86d : 96d;
        return 4d + ((end - 4d) * (boundary - 4d) / (96d - 4d));
    }

    private void Emit(LauncherProgress value)
    {
        var percent = value.Percent is { } reported
            ? Math.Clamp(Math.Max(lastPercent, reported), 0, 99)
            : lastPercent;
        lastPercent = percent;
        innerProgress.Report(value with { Percent = percent });
    }
}

internal interface IParallelInstallProgress
{
    bool TracksParallelInstall { get; }
    void ReportParallelInstallProgress(ParallelInstallPart part, double percent);
}

internal enum ParallelInstallPart
{
    Metadata,
    Assets,
    Client,
    VanillaLibraries,
    Installer,
    Mappings,
    InstallerDependencies,
    Loader
}
