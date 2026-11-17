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

using Launcher.App.Resources;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

internal sealed class ResourcesRequiredDependencyPlanner
{
    private readonly IResourceDependencyPlanningService? planningService;
    private readonly ResourcesOnlineProjectPageOptions options;
    private readonly ILogger? logger;
    private readonly Action<string> reportStatus;

    public ResourcesRequiredDependencyPlanner(
        IResourceDependencyPlanningService? planningService,
        ResourcesOnlineProjectPageOptions options,
        ILogger? logger,
        Action<string> reportStatus)
    {
        this.planningService = planningService;
        this.options = options;
        this.logger = logger;
        this.reportStatus = reportStatus;
    }

    public async Task<RequiredDependencyInstallPlan> ResolveInstallPlanAsync(
        ResourcesModVersionItemViewModel item,
        GameInstance instance,
        string? projectId,
        Func<IReadOnlyList<ResourcesModDependencyRequirementItemViewModel>, Task<RequiredDependenciesDialogChoice>> requestDialogAsync,
        CancellationToken cancellationToken)
    {
        if (options.Kind is not ResourceProjectKind.Mod
            || item.Version.RequiredDependencies.Count == 0
            || planningService is null)
        {
            return RequiredDependencyInstallPlan.Continue;
        }

        var plan = await planningService.CreatePlanAsync(item.Version, instance, cancellationToken)
            .ConfigureAwait(false);
        var dialogItems = plan.Requirements.Select(candidate =>
            new ResourcesModDependencyRequirementItemViewModel(
                candidate.Dependency,
                candidate.MinimumVersion,
                candidate.InstallVersion,
                candidate.State,
                options.FallbackIconKey)).ToArray();
        if (plan.MissingDependencies.Count == 0)
        {
            logger?.LogInformation(
                "Resource project required dependencies are already installed. ProjectId={ProjectId} VersionId={VersionId} RequiredCount={RequiredCount} InstanceDirectory={InstanceDirectory}",
                projectId,
                item.Version.VersionId,
                dialogItems.Length,
                instance.InstanceDirectory);
            return RequiredDependencyInstallPlan.Continue;
        }

        var choice = await requestDialogAsync(dialogItems).ConfigureAwait(false);
        return new RequiredDependencyInstallPlan(choice, plan.MissingDependencies);
    }

    public Task InstallRequiredDependenciesAsync(
        IReadOnlyList<ResourceDependencyInstallCandidate> missingDependencies,
        GameInstance instance,
        string? projectId,
        IProgress<LauncherProgress>? taskProgress,
        Action<LauncherProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (planningService is null || missingDependencies.Count == 0)
            return Task.CompletedTask;

        IProgress<ResourceDependencyInstallProgress> progress = new Progress<ResourceDependencyInstallProgress>(value =>
        {
            var message = string.Format(
                Strings.Status_ModRequiredDependencyInstallingFormat,
                value.DependencyTitle);
            reportStatus(message);
            reportProgress?.Invoke(new LauncherProgress(ModProgressStages.DownloadingFile, message));
        });
        if (taskProgress is not null)
            progress = DownloadSpeedTaskProgress.Carry(taskProgress, progress);
        logger?.LogInformation(
            "Installing required resource dependencies. ProjectId={ProjectId} MissingCount={MissingCount} InstanceDirectory={InstanceDirectory}",
            projectId,
            missingDependencies.Count,
            instance.InstanceDirectory);
        return planningService.InstallRequiredDependenciesAsync(
            missingDependencies,
            instance,
            progress,
            cancellationToken);
    }
}

internal sealed record RequiredDependencyInstallPlan(
    RequiredDependenciesDialogChoice Choice,
    IReadOnlyList<ResourceDependencyInstallCandidate> MissingDependencies)
{
    public static RequiredDependencyInstallPlan Continue { get; } =
        new(RequiredDependenciesDialogChoice.ContinueWithoutDependencies, []);
}

internal enum RequiredDependenciesDialogChoice
{
    Cancel,
    ContinueWithoutDependencies,
    AutoInstallDependencies
}
