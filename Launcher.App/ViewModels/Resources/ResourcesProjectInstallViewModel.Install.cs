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

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.Utilities;
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels.Resources;

public sealed partial class ResourcesProjectInstallViewModel
{
internal async Task InstallAsync(
        ResourcesModVersionItemViewModel? item,
        ResourcesModInstallTargetItemViewModel? target,
        ResourcesModProjectItemViewModel? selectedProject)
    {
        // 每次安装冻结目标、版本和文件名，防止用户切换页面选择后影响在途操作。
        if (item is null || target is null || installationService is null)
            return;

        var context = new InstallOperationContext(item, target, selectedProject);
        try
        {
            if (target.IsLocalDownload)
                await DownloadToDirectoryAsync(context).ConfigureAwait(false);
            else if (target.IsNewInstanceInstall)
                await InstallModpackAsNewInstanceAsync(context).ConfigureAwait(false);
            else if (target.IsServerInstall)
                await InstallModpackAsServerAsync(context).ConfigureAwait(false);
            else
                await InstallIntoExistingInstanceAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Session?.IsCancellationRequested == true)
        {
            context.Session.CompleteCancellation();
            logger?.LogInformation(
                "Resource project installation canceled. ProjectId={ProjectId} VersionId={VersionId}",
                selectedProject?.Project.ProjectId,
                item.Version.VersionId);
        }
        catch (ResourceProjectIntegrityException exception)
        {
            PresentFailure(context, Strings.Status_ResourceProjectIntegrityFailed);
            logger?.LogWarning(
                exception,
                "Resource project integrity verification prevented installation. Kind={Kind} ProjectId={ProjectId} VersionId={VersionId} Reason={Reason} Algorithm={Algorithm}",
                options.Kind,
                selectedProject?.Project.ProjectId,
                item.Version.VersionId,
                exception.Reason,
                exception.Algorithm);
        }
        catch (ResourceProjectDestinationConflictException exception)
        {
            var message = exception.Reason is
                ResourceProjectDestinationConflictReason.OutsideInstanceContentDirectory
                or ResourceProjectDestinationConflictReason.InvalidFileName
                ? Strings.Status_ResourceProjectInstanceDestinationInvalid
                : string.Format(
                    Strings.Status_ResourceProjectDestinationConflictFormat,
                    Path.GetFileName(exception.DestinationPath));
            PresentFailure(context, message);
            logger?.LogWarning(
                exception,
                "Resource project destination changed or conflicts with existing content. Kind={Kind} ProjectId={ProjectId} VersionId={VersionId} Reason={Reason} FileName={FileName}",
                options.Kind,
                selectedProject?.Project.ProjectId,
                item.Version.VersionId,
                exception.Reason,
                Path.GetFileName(exception.DestinationPath));
        }
        catch (ServerDeploymentDirectoryExistsException exception)
        {
            ShowServerDirectoryExists(exception.Directory);
            logger?.LogInformation(
                "Server modpack deployment target already exists. ProjectId={ProjectId} VersionId={VersionId}",
                selectedProject?.Project.ProjectId,
                item.Version.VersionId);
        }
        catch (ResourceProjectDistributionRestrictedException exception)
        {
            PresentFailure(context, Strings.Status_ServerDistributionRestricted);
            logger?.LogWarning(
                exception,
                "Resource server pack distribution is restricted. ProjectId={ProjectId} VersionId={VersionId}",
                selectedProject?.Project.ProjectId,
                item.Version.VersionId);
        }
        catch (Exception exception)
        {
            var message = target.IsServerInstall
                ? Strings.Status_ServerDeployFailed
                : target.IsLocalDownload ? options.DownloadFailedText : options.InstallFailedText;
            PresentFailure(context, message);
            logger?.LogError(
                exception,
                "Resource project installation failed. Kind={Kind} ProjectId={ProjectId} VersionId={VersionId} InstanceId={InstanceId}",
                options.Kind,
                selectedProject?.Project.ProjectId,
                item.Version.VersionId,
                target.Instance?.Id);
        }
        finally
        {
            EndInstall(context);
        }
    }

    private bool TryBeginInstall(
        InstallOperationContext context,
        string targetKind,
        string targetIdentity)
    {
        var version = context.Item.Version;
        var project = context.Project?.Project;
        var versionIdentity = string.IsNullOrWhiteSpace(version.VersionId)
            ? $"{version.FileName}\u001f{version.PrimaryDownloadUrl}"
            : version.VersionId;
        var key = string.Join(
            '\u001f',
            options.Kind,
            project?.Source,
            project?.ProjectId,
            versionIdentity,
            targetKind,
            targetIdentity);
        lock (installStateLock)
        {
            if (!activeInstallKeys.Add(key))
                return false;

            context.ActiveInstallKey = key;
            activeInstallCount++;
            IsInstalling = true;
            return true;
        }
    }

    private void EndInstall(InstallOperationContext context)
    {
        var key = context.ActiveInstallKey;
        if (key is null)
            return;

        lock (installStateLock)
        {
            if (!activeInstallKeys.Remove(key))
                return;

            context.ActiveInstallKey = null;
            activeInstallCount = Math.Max(0, activeInstallCount - 1);
            IsInstalling = activeInstallCount > 0;
        }
    }

    private void ReportDuplicateInstall(InstallOperationContext context)
    {
        floatingMessageService?.Show(Strings.Status_ResourceProjectDownloadAlreadyRunning);
        reportStatus(Strings.Status_ResourceProjectDownloadAlreadyRunning);
        logger?.LogInformation(
            "Duplicate resource project installation request ignored. Kind={Kind} ProjectId={ProjectId} VersionId={VersionId} InstanceId={InstanceId}",
            options.Kind,
            context.Project?.Project.ProjectId,
            context.Item.Version.VersionId,
            context.Target.Instance?.Id);
    }

    private static string NormalizeTargetPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private async Task DownloadToDirectoryAsync(InstallOperationContext context)
    {
        // 同名文件不直接覆盖，先交还给用户确认，避免普通下载造成不可逆的数据替换。
        var destinationPath = filePickerService?.PickResourceProjectDestination(
            options.DownloadDirectoryPickerTitle,
            ResolveFileName(context.Item));
        if (string.IsNullOrWhiteSpace(destinationPath))
            return;
        destinationPath = NormalizeTargetPath(destinationPath);
        var targetDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;
        if (!TryBeginInstall(
                context,
                nameof(ResourceProjectInstallationTargetKind.LocalDirectory),
                destinationPath))
        {
            ReportDuplicateInstall(context);
            return;
        }
        BeginSession(context, targetDirectory);
        var request = new ResourceProjectInstallationRequest(
            context.Item.Version,
            ResourceProjectInstallationTargetKind.LocalDirectory,
            TargetDirectory: targetDirectory,
            DestinationPath: destinationPath);
        var preparation = await installationService!.PrepareAsync(
            request,
            context.Session!.CancellationToken).ConfigureAwait(false);
        request = request with { ExpectedDestinationState = preparation.DestinationState };
        BeginUserFeedback(context.Item);
        context.Session!.BeginPrimaryDownload(hasDependencies: false);
        await installationService.ExecuteAsync(request, context.Session.Progress, context.Session.CancellationToken)
            .ConfigureAwait(false);
        if (context.Session.CompleteCancellation())
            return;
        var message = string.Format(options.DownloadedFormat, ResolveFileName(context.Item));
        context.Session.Complete(message);
        reportStatus(message);
    }

    private async Task InstallModpackAsNewInstanceAsync(InstallOperationContext context)
    {
        // 整合包导入拥有独立的安装、清理和手动下载结果，本层只映射页面事件与反馈。
        if (!TryBeginInstall(
                context,
                nameof(ResourceProjectInstallationTargetKind.NewModpackInstance),
                string.Empty))
        {
            ReportDuplicateInstall(context);
            return;
        }
        BeginSession(context, context.Target.Title);
        BeginUserFeedback(context.Item);
        context.Session!.BeginModpackImport();
        var result = (await installationService!.ExecuteAsync(
            new ResourceProjectInstallationRequest(
                context.Item.Version,
                ResourceProjectInstallationTargetKind.NewModpackInstance,
                Project: context.Project?.Project),
            context.Session!.Progress,
            context.Session.CancellationToken).ConfigureAwait(false)).ModpackImportResult
            ?? ModpackImportResult.Failure(ModpackImportFailureReason.UnexpectedError);
        if (context.Session.CompleteCancellation())
            return;
        if (result.IsSuccess && result.ImportedInstance is not null)
        {
            CompleteModpackImport(context, result);
            return;
        }
        PresentFailure(context, MapModpackImportFailureMessage(result.FailureReason));
    }

    private async Task InstallModpackAsServerAsync(InstallOperationContext context)
    {
        var parentDirectory = filePickerService?.PickFolder(Strings.FilePicker_ServerInstallDirectoryTitle);
        if (string.IsNullOrWhiteSpace(parentDirectory))
            return;
        if (!TryBeginInstall(
                context,
                nameof(ResourceProjectInstallationTargetKind.NewServerDirectory),
                NormalizeTargetPath(parentDirectory)))
        {
            ReportDuplicateInstall(context);
            return;
        }
        BeginSession(context, parentDirectory);
        var request = new ResourceProjectInstallationRequest(
            context.Item.Version,
            ResourceProjectInstallationTargetKind.NewServerDirectory,
            TargetDirectory: parentDirectory,
            Project: context.Project?.Project);
        var preparation = await installationService!.PrepareAsync(
            request,
            context.Session!.CancellationToken).ConfigureAwait(false);
        if (preparation.TargetExists)
        {
            context.Session.Dismiss();
            ShowServerDirectoryExists(preparation.TargetPath ?? parentDirectory);
            return;
        }

        floatingMessageService?.Show(Strings.Status_ServerDeploying);
        reportStatus(Strings.Status_ServerDeploying);
        context.Session!.BeginModpackImport();
        var result = await installationService.ExecuteAsync(
            request,
            context.Session.Progress,
            context.Session.CancellationToken).ConfigureAwait(false);
        if (context.Session.CompleteCancellation())
            return;
        var installedPath = result.InstalledPath ?? parentDirectory;
        var message = string.Format(Strings.Status_ServerDeployedFormat, installedPath);
        context.Session.Complete(message);
        reportStatus(message);
        floatingMessageService?.Show(message);
        logger?.LogInformation(
            "Resource modpack deployed as server. ProjectId={ProjectId} VersionId={VersionId}",
            context.Project?.Project.ProjectId,
            context.Item.Version.VersionId);
    }

    private async Task InstallIntoExistingInstanceAsync(InstallOperationContext context)
    {
        // 在主文件写入前规划 Required 依赖，用户取消时不会留下主资源已安装但依赖缺失的状态。
        var instance = context.Target.Instance;
        if (instance is null)
            return;
        if (options.Kind is ResourceProjectKind.DataPack)
        {
            await DownloadDataPackForInstanceAsync(context, instance).ConfigureAwait(false);
            return;
        }
        var installDirectory = await installationService!
            .EnsureInstanceContentDirectoryAsync(options.Kind, instance);
        string? destinationPath = null;
        if (options.Kind is not ResourceProjectKind.World)
        {
            destinationPath = filePickerService?.PickResourceProjectDestination(
                options.DownloadDirectoryPickerTitle,
                ResolveFileName(context.Item),
                installDirectory);
            if (string.IsNullOrWhiteSpace(destinationPath))
                return;
            destinationPath = NormalizeTargetPath(destinationPath);
            if (!IsDirectChildPath(destinationPath, installDirectory))
            {
                floatingMessageService?.Show(Strings.Status_ResourceProjectInstanceDestinationInvalid);
                reportStatus(Strings.Status_ResourceProjectInstanceDestinationInvalid);
                return;
            }
        }
        var instanceIdentity = string.IsNullOrWhiteSpace(instance.Id)
            ? NormalizeTargetPath(instance.InstanceDirectory)
            : instance.Id;
        if (!TryBeginInstall(
                context,
                nameof(ResourceProjectInstallationTargetKind.ExistingInstance),
                destinationPath ?? instanceIdentity))
        {
            ReportDuplicateInstall(context);
            return;
        }
        BeginSession(context, context.Target.Title);
        var request = new ResourceProjectInstallationRequest(
            context.Item.Version,
            ResourceProjectInstallationTargetKind.ExistingInstance,
            Instance: instance,
            DestinationPath: destinationPath);
        var preparation = await installationService!.PrepareAsync(
            request,
            context.Session!.CancellationToken).ConfigureAwait(false);
        request = request with { ExpectedDestinationState = preparation.DestinationState };
        var dependencyPlan = await dependencyPlanner.ResolveInstallPlanAsync(
            context.Item,
            instance,
            context.Project?.Project.ProjectId,
            items => RequestDependenciesDialogAsync(items, context.Session.CancellationToken),
            context.Session.CancellationToken).ConfigureAwait(false);
        if (dependencyPlan.Choice is RequiredDependenciesDialogChoice.Cancel)
        {
            context.Session.Dismiss();
            return;
        }

        floatingMessageService?.Show(options.DownloadingText);
        if (dependencyPlan.Choice is RequiredDependenciesDialogChoice.AutoInstallDependencies
            && !await InstallDependenciesAsync(context, dependencyPlan, instance).ConfigureAwait(false))
        {
            return;
        }

        BeginUserFeedback(context.Item);
        context.Session!.BeginPrimaryDownload(
            dependencyPlan.Choice is RequiredDependenciesDialogChoice.AutoInstallDependencies
                && dependencyPlan.MissingDependencies.Count > 0);
        await installationService.ExecuteAsync(request, context.Session.Progress, context.Session.CancellationToken)
            .ConfigureAwait(false);
        if (context.Session.CompleteCancellation())
            return;
        var message = string.Format(options.InstalledFormat, context.Project?.Title ?? context.Item.Title);
        context.Session.Complete(message);
        reportStatus(message);
    }

    private async Task DownloadDataPackForInstanceAsync(
        InstallOperationContext context,
        GameInstance instance)
    {
        var destinationPath = filePickerService?.PickResourceProjectDestination(
            options.DownloadDirectoryPickerTitle,
            ResolveFileName(context.Item),
            instance.InstanceDirectory);
        if (string.IsNullOrWhiteSpace(destinationPath))
            return;

        destinationPath = NormalizeTargetPath(destinationPath);
        var targetDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;
        if (!TryBeginInstall(
                context,
                nameof(ResourceProjectInstallationTargetKind.LocalDirectory),
                destinationPath))
        {
            ReportDuplicateInstall(context);
            return;
        }

        BeginSession(context, context.Target.Title);
        var request = new ResourceProjectInstallationRequest(
            context.Item.Version,
            ResourceProjectInstallationTargetKind.LocalDirectory,
            TargetDirectory: targetDirectory,
            DestinationPath: destinationPath);
        var preparation = await installationService!.PrepareAsync(
            request,
            context.Session!.CancellationToken).ConfigureAwait(false);
        request = request with { ExpectedDestinationState = preparation.DestinationState };

        BeginUserFeedback(context.Item);
        context.Session.BeginPrimaryDownload(hasDependencies: false);
        await installationService.ExecuteAsync(request, context.Session.Progress, context.Session.CancellationToken)
            .ConfigureAwait(false);
        if (context.Session.CompleteCancellation())
            return;

        var message = string.Format(options.DownloadedFormat, ResolveFileName(context.Item));
        context.Session.Complete(message);
        reportStatus(message);
    }

    private static bool IsDirectChildPath(string path, string expectedDirectory)
    {
        try
        {
            return string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(path)),
                Path.GetFullPath(expectedDirectory),
                StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }
}
