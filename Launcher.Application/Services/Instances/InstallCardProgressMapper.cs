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
internal sealed class InstallCardProgressMapper : IProgress<LauncherProgress>, ISpeedMeterProgress
{
    private readonly IProgress<LauncherProgress> innerProgress;
    private readonly bool hasExternalLoaderInstaller;
    private readonly bool hasOptionalContent;
    private double lastPercent;

    public InstallCardProgressMapper(
        IProgress<LauncherProgress> innerProgress,
        LoaderKind loader,
        bool hasOptionalContent)
    {
        this.innerProgress = innerProgress;
        hasExternalLoaderInstaller = loader is LoaderKind.Forge or LoaderKind.NeoForge;
        this.hasOptionalContent = hasOptionalContent;
    }

    public SpeedMeter? SpeedMeter => SpeedMeterProgress.TryGet(innerProgress);

    public void Report(LauncherProgress value)
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
        Emit(new LauncherProgress(
            InstallProgressStages.CompletingFiles,
            string.Empty,
            hasOptionalContent ? 86 : 96));
    }

    public void ReportOptionalContentCompleted()
    {
        if (!hasOptionalContent)
            return;

        Emit(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty, 96));
    }

    public void ReportCommitStarted() =>
        Emit(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty, 96));

    public void ReportCommitCompleted() =>
        Emit(new LauncherProgress(InstallProgressStages.CompletingFiles, string.Empty, 99));

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
