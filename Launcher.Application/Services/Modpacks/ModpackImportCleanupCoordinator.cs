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
using Microsoft.Extensions.Logging;

namespace Launcher.Application.Services;

/// <summary>
/// 根据导入会话已完成的阶段逆向清理实例记录、暂存目录和整合包工作区。
/// </summary>
internal sealed class ModpackImportCleanupCoordinator
{
    private readonly IGameInstanceService instanceService;
    private readonly IModpackPackageService packageService;
    private readonly IModpackInstanceStagingService stagingService;
    private readonly ILogger logger;

    public ModpackImportCleanupCoordinator(
        IGameInstanceService instanceService,
        IModpackPackageService packageService,
        IModpackInstanceStagingService stagingService,
        ILogger logger)
    {
        this.instanceService = instanceService;
        this.packageService = packageService;
        this.stagingService = stagingService;
        this.logger = logger;
    }

    /// <summary>
    /// 按已完成阶段逆序清理失败导入；各步骤独立尽力执行且不响应原调用取消。
    /// </summary>
    public async Task CleanupFailedImportAsync(ModpackImportSession session)
    {
        if (session.StagedInstance is not null
            || session.ImportedInstance is not null
            || session.PreparedModpack is not null)
        {
            session.Progress?.Report(new LauncherProgress(ImportProgressStages.CleaningUp, string.Empty));
        }

        if (session.ImportedInstance is not null)
        {
            try
            {
                // 清理由失败或取消触发，不能继续使用已经取消的调用方令牌。
                await instanceService.DeleteInstanceAsync(session.ImportedInstance, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to delete partially imported instance. InstanceDirectory={InstanceDirectory}",
                    session.ImportedInstance.InstanceDirectory);
            }
        }

        if (session.StagedInstance is not null)
        {
            try
            {
                // 各清理步骤独立尽力执行，前一步失败也不能阻止后续临时文件清理。
                await stagingService.CleanupFailedImportAsync(
                        session.StagedInstance,
                        session.FinalVersionName,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to clean up staged modpack instance. InstanceName={InstanceName}",
                    session.StagedInstance.ResolvedInstanceName);
            }
        }

        if (session.PreparedModpack is null)
            return;

        try
        {
            await packageService.CleanupAsync(session.PreparedModpack, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to clean up prepared modpack workspace. WorkingDirectory={WorkingDirectory}",
                session.PreparedModpack.WorkingDirectory);
        }
    }

    /// <summary>
    /// 成功提交实例后仅删除不再需要的整合包准备工作区。
    /// </summary>
    public async Task CleanupSuccessfulImportAsync(PreparedModpack preparedModpack)
    {
        try
        {
            await packageService.CleanupAsync(preparedModpack, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to clean up modpack workspace after a successful import. WorkingDirectory={WorkingDirectory}",
                preparedModpack.WorkingDirectory);
        }
    }
}
