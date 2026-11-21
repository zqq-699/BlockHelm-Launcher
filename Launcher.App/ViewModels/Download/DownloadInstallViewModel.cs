/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.Utilities;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Download;

public sealed partial class DownloadInstallViewModel : ObservableObject
{
    private readonly IGameInstanceService instanceService;
    private readonly DownloadTasksPageViewModel downloadTasksPage;
    private readonly DownloadInstanceNameTracker instanceNameTracker;
    private readonly IUiDispatcher uiDispatcher;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly ILogger<DownloadInstallViewModel> logger;
    private int activeInstallCount;
    private long latestInstallSequence;

    [ObservableProperty]
    private bool isInstalling;

    [ObservableProperty]
    private string installStatusMessage = string.Empty;

    [ObservableProperty]
    private string installError = string.Empty;

    [ObservableProperty]
    private double installProgressPercent;

    internal DownloadInstallViewModel(
        IGameInstanceService instanceService,
        DownloadTasksPageViewModel downloadTasksPage,
        DownloadInstanceNameTracker instanceNameTracker,
        IUiDispatcher uiDispatcher,
        IFloatingMessageService floatingMessageService,
        ILogger<DownloadInstallViewModel>? logger = null)
    {
        this.instanceService = instanceService;
        this.downloadTasksPage = downloadTasksPage;
        this.instanceNameTracker = instanceNameTracker;
        this.uiDispatcher = uiDispatcher;
        this.floatingMessageService = floatingMessageService;
        this.logger = logger ?? NullLogger<DownloadInstallViewModel>.Instance;
    }

    public event EventHandler<GameInstance>? InstanceInstalled;

    public event Action? NameAvailabilityChanged;

    public bool HasInstallStatus => !string.IsNullOrWhiteSpace(InstallStatusMessage);

    public bool HasInstallError => !string.IsNullOrWhiteSpace(InstallError);

    public async Task InstallAsync(DownloadInstallRequest request)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var installSequence = Interlocked.Increment(ref latestInstallSequence);
        var installTask = downloadTasksPage.BeginTask(
            $"{request.LoaderDisplayName} {request.MinecraftVersion}",
            request.InstanceName);
        using var diagnosticOperation = DownloadDiagnostics.BeginOperation(logger, "InstanceInstall", installTask.Id);
        logger.LogInformation(
            "Starting instance installation. MinecraftVersion={MinecraftVersion} Loader={Loader} InstanceName={InstanceName}",
            request.MinecraftVersion,
            request.Loader,
            request.InstanceName);
        floatingMessageService.Show(Strings.Status_InstallStartingDownload);
        IsInstalling = Interlocked.Increment(ref activeInstallCount) > 0;
        InstallError = string.Empty;
        InstallProgressPercent = 0;
        InstallStatusMessage = Strings.Status_InstallPreparing;
        installTask.Report(new LauncherProgress(InstallProgressStages.Preparing, string.Empty, 0));
        instanceNameTracker.AddPending(request.InstanceName);
        NameAvailabilityChanged?.Invoke();

        try
        {
            using var installProgress = new DownloadInstallProgress(
                installTask,
                installSequence,
                ReportInstallProgress,
                uiDispatcher);
            var instance = await instanceService.CreateInstanceAsync(
                request.MinecraftVersion,
                request.Loader,
                request.LoaderVersion,
                request.InstanceName,
                installTask.CreateProgress(installProgress.Report),
                installTask.CancellationToken,
                request.DownloadSourcePreference,
                request.DownloadSpeedLimitMbPerSecond,
                installFabricApi: request.FabricApiVersionId is not null,
                fabricApiVersionId: request.FabricApiVersionId,
                quiltStandardLibraryVersionId: request.QuiltStandardLibraryVersionId);

            // The install service only receives the version id and therefore cannot retain the
            // catalog type. Publish the type already known by the download page so category
            // projections can include the new instance without rescanning the versions directory.
            instance.VersionType = request.MinecraftVersionType;
            instanceNameTracker.RemovePending(request.InstanceName);
            instanceNameTracker.AddExisting(instance.Name);
            instanceNameTracker.AddExisting(instance.VersionName);
            var completionMessage = string.Format(Strings.Status_InstanceInstalledFormat, instance.Name);
            SetLatestInstallCompletion(installSequence, completionMessage);
            installTask.Complete(completionMessage);
            logger.LogInformation(
                "Instance installation completed. InstanceDirectory={InstanceDirectory} MinecraftVersion={MinecraftVersion} Loader={Loader} DurationMs={DurationMs}",
                instance.InstanceDirectory,
                request.MinecraftVersion,
                request.Loader,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            InstanceInstalled?.Invoke(this, instance);
        }
        catch (OperationCanceledException) when (installTask.IsCancellationRequested)
        {
            instanceNameTracker.RemovePending(request.InstanceName);
            if (installSequence == latestInstallSequence)
            {
                InstallError = string.Empty;
                InstallStatusMessage = string.Empty;
                InstallProgressPercent = 0;
            }
            logger.LogInformation(
                "Instance installation canceled. MinecraftVersion={MinecraftVersion} Loader={Loader} InstanceName={InstanceName} DurationMs={DurationMs}",
                request.MinecraftVersion,
                request.Loader,
                request.InstanceName,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            downloadTasksPage.CancelTask(installTask);
        }
        catch (DuplicateGameInstanceNameException exception)
        {
            instanceNameTracker.RemovePending(request.InstanceName);
            SetLatestInstallFailure(installSequence, Strings.Status_DuplicateInstanceName);
            installTask.Fail(Strings.Status_DuplicateInstanceName);
            logger.LogWarning(
                "Instance installation rejected because the name is unavailable. InstanceName={InstanceName} DurationMs={DurationMs}",
                request.InstanceName,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            logger.LogDebug(exception, "Instance installation name conflict details. InstanceName={InstanceName}", request.InstanceName);
        }
        catch (JavaRuntimeSelectionException exception)
        {
            instanceNameTracker.RemovePending(request.InstanceName);
            SetLatestInstallFailure(installSequence, Strings.Status_JavaSelectionFailed);
            installTask.Fail(Strings.Status_JavaSelectionFailed);
            logger.LogError(
                exception,
                "Instance installation could not select a compatible Java runtime. MinecraftVersion={MinecraftVersion} Loader={Loader} InstanceName={InstanceName} FailureReason={FailureReason} RequiredMajorVersion={RequiredMajorVersion} CurrentMajorVersion={CurrentMajorVersion} DurationMs={DurationMs}",
                request.MinecraftVersion,
                request.Loader,
                request.InstanceName,
                exception.Reason,
                exception.RequiredMajorVersion,
                exception.CurrentMajorVersion,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            instanceNameTracker.RemovePending(request.InstanceName);
            var message = exception is MinecraftAssetInstallationException
                ? Strings.Status_MinecraftAssetInstallFailed
                : Strings.Status_InstallFailed;
            SetLatestInstallFailure(installSequence, message);
            installTask.Fail(message);
            logger.LogError(
                exception,
                "Instance installation failed. MinecraftVersion={MinecraftVersion} Loader={Loader} InstanceName={InstanceName} DurationMs={DurationMs}",
                request.MinecraftVersion,
                request.Loader,
                request.InstanceName,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref activeInstallCount);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref activeInstallCount, 0);
                remaining = 0;
            }
            IsInstalling = remaining > 0;
            NameAvailabilityChanged?.Invoke();
        }
    }

    partial void OnInstallStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasInstallStatus));
    }

    partial void OnInstallErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasInstallError));
    }

    private void ReportInstallProgress(
        DownloadTaskItem installTask,
        LauncherProgress progress,
        long installSequence)
    {
        var displayProgress = progress with { Message = LauncherProgressTextFormatter.Format(progress) };
        if (installSequence == latestInstallSequence)
        {
            InstallError = string.Empty;
            InstallStatusMessage = displayProgress.Message;
            if (displayProgress.Percent is { } percent)
                InstallProgressPercent = Math.Clamp(Math.Max(InstallProgressPercent, percent), 0, 99);
        }
        installTask.Report(displayProgress);
    }

    private void SetLatestInstallCompletion(long installSequence, string message)
    {
        if (installSequence != latestInstallSequence)
            return;
        InstallError = string.Empty;
        InstallProgressPercent = 100;
        InstallStatusMessage = message;
    }

    private void SetLatestInstallFailure(long installSequence, string message)
    {
        if (installSequence != latestInstallSequence)
            return;
        InstallError = message;
        InstallStatusMessage = string.Empty;
    }
}
