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

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public interface IResourceCatalogService
{
    Task<ResourceProjectRelatedWebsite?> GetRelatedWebsiteAsync(
        ResourceProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return GetRelatedWebsiteAsync(
            new ResourceProjectReference(project.Kind, project.Source, project.ProjectId),
            cancellationToken);
    }

    Task<ResourceProjectRelatedWebsite?> GetRelatedWebsiteAsync(
        ResourceProjectReference reference,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResourceProjectRelatedWebsite?>(null);
    }

    Task<ResourceProject?> GetProjectAsync(
        ResourceProjectReference reference,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResourceProject?>(null);
    }

    Task<ResourceCatalogSearchResult> SearchProjectsAsync(
        ResourceCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        return SearchModsAsync(request, cancellationToken);
    }

    Task<ResourceCatalogSearchResult> SearchModsAsync(
        ResourceCatalogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ResourceProjectVersionsResult> GetProjectVersionsAsync(
        ResourceProjectVersionsRequest request,
        CancellationToken cancellationToken = default);

    Task<ResourceProjectDependenciesResult> GetProjectDependenciesAsync(
        ResourceProjectDependenciesRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ResourceProjectDependenciesResult());
    }

    Task<string> InstallProjectVersionAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken = default);

    Task<string> DownloadProjectVersionAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        CancellationToken cancellationToken = default);

    Task<bool> ProjectVersionDownloadExistsAsync(
        ResourceProjectVersion version,
        string targetDirectory,
        CancellationToken cancellationToken = default);

    Task<bool> ProjectVersionInstallExistsAsync(
        ResourceProjectVersion version,
        GameInstance instance,
        CancellationToken cancellationToken = default);
}
