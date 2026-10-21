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
using Launcher.App.ViewModels.Download;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.GameSettings;

public sealed partial class GameSettingsDetailsViewModel
{
private void Persistence_InstanceSaved(GameInstance instance)
    {
        InstanceSettingsSaved?.Invoke(instance);
    }

    private void General_DeleteInstanceRequested(GameSettingsInstanceItem instance)
    {
        DeleteInstanceRequested?.Invoke(instance);
    }

    private void ModManagement_OnlineModInstallRequested(GameInstance instance)
    {
        OnlineResourceInstallRequested?.Invoke(instance, ResourceProjectKind.Mod);
    }

    private void SaveManagement_OnlineSaveInstallRequested(GameInstance instance)
    {
        OnlineResourceInstallRequested?.Invoke(instance, ResourceProjectKind.World);
    }

    private void ResourcePackManagement_OnlineResourcePackInstallRequested(GameInstance instance)
    {
        OnlineResourceInstallRequested?.Invoke(instance, ResourceProjectKind.ResourcePack);
    }

    private void ShaderPackManagement_OnlineShaderPackInstallRequested(GameInstance instance)
    {
        OnlineResourceInstallRequested?.Invoke(instance, ResourceProjectKind.ShaderPack);
    }

    private void ModManagement_DeleteModsRequested(ModDeleteRequest request)
    {
        DeleteModsRequested?.Invoke(request);
    }

    private void SaveManagement_DeleteSavesRequested(SaveDeleteRequest request)
    {
        DeleteSavesRequested?.Invoke(request);
    }

    private void SaveManagement_SaveImportFailedRequested(SaveImportFailureRequest request)
    {
        SaveImportFailedRequested?.Invoke(request);
    }

    private void ResourcePackManagement_DeleteResourcePacksRequested(ResourcePackDeleteRequest request)
    {
        DeleteResourcePacksRequested?.Invoke(request);
    }

    private void ResourcePackManagement_ResourcePackImportFailedRequested(ResourcePackImportFailureRequest request)
    {
        ResourcePackImportFailedRequested?.Invoke(request);
    }

    private void ShaderPackManagement_DeleteShaderPacksRequested(ShaderPackDeleteRequest request)
    {
        DeleteShaderPacksRequested?.Invoke(request);
    }

    private void ShaderPackManagement_ShaderPackImportFailedRequested(ShaderPackImportFailureRequest request)
    {
        ShaderPackImportFailedRequested?.Invoke(request);
    }

    private void ModManagement_ImportModConflictRequested(ModImportConflictRequest request)
    {
        ImportModConflictRequested?.Invoke(request);
    }
}
