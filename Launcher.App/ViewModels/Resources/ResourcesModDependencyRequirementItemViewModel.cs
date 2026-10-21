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

namespace Launcher.App.ViewModels.Resources;

public sealed class ResourcesModDependencyRequirementItemViewModel
{
    public ResourcesModDependencyRequirementItemViewModel(
        ResourceProjectDependency dependency,
        ResourceProjectVersion? minimumVersion,
        ResourceProjectVersion? installVersion,
        ResourceDependencyRequirementState state,
        string fallbackIconKey = "instance_setting_page/mod")
    {
        Dependency = dependency;
        Project = dependency.Project;
        MinimumVersion = minimumVersion;
        InstallVersion = installVersion;
        State = state;
        IconSource = string.IsNullOrWhiteSpace(Project.IconUrl)
            ? null
            : Project.IconUrl;
        IconKey = string.IsNullOrWhiteSpace(IconSource)
            ? fallbackIconKey
            : string.Empty;
    }

    public ResourceProject Project { get; }

    public ResourceProjectDependency Dependency { get; }

    public ResourceProjectVersion? MinimumVersion { get; }

    public ResourceProjectVersion? InstallVersion { get; }

    public ResourceDependencyRequirementState State { get; }

    public bool IsInstalled => State is ResourceDependencyRequirementState.Installed;

    public string? IconSource { get; }

    public string IconKey { get; }

    public string Title => ResourcesModProjectItemViewModel.FormatTitle(Project);

    public string VersionText => string.Format(
        Strings.Resources_ModRequiredDependencyVersionFormat,
        ResolveVersionText(InstallVersion));

    public string MinimumVersionText => string.Format(
        Strings.Resources_ModRequiredDependencyMinimumVersionFormat,
        ResolveVersionText(MinimumVersion));

    public string InstallVersionText => string.Format(
        Strings.Resources_ModRequiredDependencyInstallVersionFormat,
        ResolveVersionText(InstallVersion));

    public string StateText => State switch
    {
        ResourceDependencyRequirementState.Installed => Strings.Resources_ModRequiredDependencyInstalled,
        ResourceDependencyRequirementState.UpdateRequired => Strings.Resources_ModRequiredDependencyUpdateRequired,
        _ => Strings.Resources_ModRequiredDependencyMissing
    };

    private static string ResolveVersionText(ResourceProjectVersion? version)
    {
        if (version is null)
            return Strings.Resources_ModRequiredDependencyVersionUnresolved;

        if (!string.IsNullOrWhiteSpace(version.Name)
            && !string.IsNullOrWhiteSpace(version.VersionNumber)
            && !string.Equals(version.Name, version.VersionNumber, StringComparison.OrdinalIgnoreCase))
        {
            return $"{version.Name} {version.VersionNumber}";
        }

        if (!string.IsNullOrWhiteSpace(version.Name))
            return version.Name;

        if (!string.IsNullOrWhiteSpace(version.VersionNumber))
            return version.VersionNumber;

        return string.IsNullOrWhiteSpace(version.VersionId)
            ? Strings.Resources_ModRequiredDependencyVersionUnresolved
            : version.VersionId;
    }
}
