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
private void ApplySelectedInstanceChanged(GameSettingsInstanceItem? value)
    {
        // 先停止旧实例观察，再切换所有子 ViewModel，最后激活当前可见分区。
        var instance = value?.Instance;
        var editableInstance = persistence.SetInstance(instance);
        General.SetSelectedInstance(value, editableInstance);
        Launch.SetSelectedInstance(editableInstance);
        Java.SetSelectedInstance(editableInstance);
        ModManagement.OnSelectedInstanceChanged(instance);
        SaveManagement.OnSelectedInstanceChanged(instance);
        ResourcePackManagement.OnSelectedInstanceChanged(instance);
        ShaderPackManagement.OnSelectedInstanceChanged(instance);
        Backup.OnSelectedInstanceChanged(instance);
        Export.OnSelectedInstanceChanged(instance);
        OnPropertyChanged(nameof(HasSelectedInstance));

        ActivateCurrentSection();
    }

    private void RefreshSelectedInstanceReference(GameSettingsInstanceItem? value)
    {
        // 无变化刷新保留编辑上下文；设置或目录已变化时接收新的受保护快照。
        var instance = value?.Instance;
        var editableInstance = persistence.SetInstance(instance);
        General.SetSelectedInstance(value, editableInstance);
        Launch.SetSelectedInstance(editableInstance);
        Java.SetSelectedInstance(editableInstance);
        var shouldReactivateCurrentSection =
            ModManagement.RefreshSelectedInstanceReference(instance)
            | SaveManagement.RefreshSelectedInstanceReference(instance)
            | ResourcePackManagement.RefreshSelectedInstanceReference(instance)
            | ShaderPackManagement.RefreshSelectedInstanceReference(instance);
        Backup.OnSelectedInstanceChanged(instance);
        Export.OnSelectedInstanceChanged(instance);

        if (shouldReactivateCurrentSection)
            ActivateCurrentSection();
    }

    private void ActivateCurrentSection()
    {
        if (isPageActive && SelectedInstance is not null && CurrentSectionViewModel is { } section)
            _ = ObserveSectionActivationAsync(section);
    }

    private async Task ObserveSectionActivationAsync(GameSettingsDetailsSectionViewModelBase section)
    {
        // 激活可能异步刷新；异常只报告状态，不能让 fire-and-forget 任务失去观察。
        try
        {
            await section.OnSectionActivatedAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to activate game settings section. SectionType={SectionType} InstanceDirectory={InstanceDirectory}",
                section.GetType().Name,
                SelectedInstance?.Instance.InstanceDirectory);
        }
    }
}
