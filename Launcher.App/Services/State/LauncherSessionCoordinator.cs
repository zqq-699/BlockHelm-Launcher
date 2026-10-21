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

using System.ComponentModel;
using System.IO;
using Launcher.App.Models;
using Launcher.App.Resources;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.GameSettings;
using Launcher.App.ViewModels.Home;
using Launcher.App.ViewModels.Resources;
using Launcher.App.ViewModels.Settings;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.Services;

/// <summary>
/// 连接各顶层页面的会话状态，负责实例选择、设置变更和跨页面导航的同步，不承载具体业务实现。
/// </summary>
public sealed class LauncherSessionCoordinator : IDisposable
{
    // 页面 ViewModel 仍各自拥有局部状态；协调器只订阅跨页面事件并把同一个事实同步给相关页面。
    private readonly IDownloadSpeedLimitState downloadSpeedLimitState;
    private readonly IDownloadConcurrencyLimitState downloadConcurrencyLimitState;
    private readonly ISettingsService settingsService;
    private readonly IStatusService statusService;
    private readonly DownloadPageViewModel downloadPage;
    private readonly GameSettingsPageViewModel gameSettingsPage;
    private readonly ResourcesPageViewModel resourcesPage;
    private readonly SettingsPageViewModel settingsPage;
    private readonly GameManagementViewModel gameManagement;
    private readonly LauncherStateSyncService stateSyncService;
    private readonly ILogger<LauncherSessionCoordinator> logger;
    // 实例目录刷新统一串行化；页面激活只投影内存快照，不参与这把锁。
    private readonly SemaphoreSlim instanceSynchronizationLock = new(1, 1);
    // Shell 与设置页共享同一个持久化队列，关闭时由设置页的统一 Flush 一并落盘。
    private readonly SettingsPersistenceCoordinator settingsPersistence;
    private HomePageViewModel? homePage;
    private LauncherSettings? settings;
    private string currentPage = NavigationCatalog.HomePage;
    private bool isAttached;
    private bool isInitialized;

    public LauncherSessionCoordinator(
        IDownloadSpeedLimitState downloadSpeedLimitState,
        IDownloadConcurrencyLimitState downloadConcurrencyLimitState,
        ISettingsService settingsService,
        IStatusService statusService,
        DownloadPageViewModel downloadPage,
        GameSettingsPageViewModel gameSettingsPage,
        ResourcesPageViewModel resourcesPage,
        SettingsPageViewModel settingsPage,
        GameManagementViewModel gameManagement,
        LauncherStateSyncService stateSyncService,
        ILogger<LauncherSessionCoordinator>? logger = null,
        SettingsPersistenceCoordinator? settingsPersistence = null)
    {
        this.downloadSpeedLimitState = downloadSpeedLimitState;
        this.downloadConcurrencyLimitState = downloadConcurrencyLimitState;
        this.settingsService = settingsService;
        this.statusService = statusService;
        this.downloadPage = downloadPage;
        this.gameSettingsPage = gameSettingsPage;
        this.resourcesPage = resourcesPage;
        this.settingsPage = settingsPage;
        this.gameManagement = gameManagement;
        this.stateSyncService = stateSyncService;
        this.logger = logger ?? NullLogger<LauncherSessionCoordinator>.Instance;
        this.settingsPersistence = settingsPersistence ?? settingsPage.Persistence;
    }

    public event Action<string>? NavigationRequested;

    public event Action<double>? ProgressChanged;

    public void Attach(HomePageViewModel homePage)
    {
        ArgumentNullException.ThrowIfNull(homePage);
        if (isAttached)
            throw new InvalidOperationException("The launcher session coordinator is already attached.");

        // Attach/Dispose 必须严格成对；重复订阅会让一次安装或设置变更被处理多次。
        this.homePage = homePage;
        downloadPage.InstanceInstalled += DownloadPage_InstanceInstalled;
        resourcesPage.ModpackImported += DownloadPage_InstanceInstalled;
        resourcesPage.ModpackManualDownloadsRequested += ResourcesPage_ModpackManualDownloadsRequested;
        gameManagement.PropertyChanged += GameManagement_PropertyChanged;
        gameSettingsPage.LaunchInstanceRequested += GameSettingsPage_LaunchInstanceRequested;
        gameSettingsPage.OnlineResourceInstallRequested += GameSettingsPage_OnlineResourceInstallRequested;
        gameSettingsPage.InstancesChanged += GameSettingsPage_InstancesChanged;
        gameSettingsPage.InstanceListActivated += GameSettingsPage_InstanceListActivated;
        settingsPage.LaunchDefaultsChanged += SettingsPage_LaunchDefaultsChanged;
        settingsPage.DownloadSourceChanged += SettingsPage_DownloadSourceChanged;
        settingsPage.MaximumDownloadConcurrencyChanged += SettingsPage_MaximumDownloadConcurrencyChanged;
        settingsPage.DownloadSpeedLimitChanged += SettingsPage_DownloadSpeedLimitChanged;
        settingsPage.MinecraftDirectoryChanged += SettingsPage_MinecraftDirectoryChanged;
        isAttached = true;
    }

    public async Task PrimeAsync(LauncherSettings settings)
    {
        // Prime 只注入已加载的设置和磁盘实例快照，让首屏尽早可用；网络目录等延迟工作留给 Initialize。
        this.settings = settings;
        downloadSpeedLimitState.SetDownloadSpeedLimitMbPerSecond(settings.DownloadSpeedLimitMbPerSecond);
        downloadConcurrencyLimitState.SetMaximumDownloadConcurrency(settings.MaximumDownloadConcurrency);
        await gameManagement.PrimeInstancesAsync(settings);
        homePage?.SetSettings(settings);
        SynchronizeHomeInstances();
        homePage?.Initialize(settings, gameManagement.SelectedInstance);
        downloadPage.PrimeFromSettings(settings);
        gameSettingsPage.PrimeFromSettings(settings);
        settingsPage.PrimeFromSettings(settings);
    }

    public async Task InitializeAsync()
    {
        if (isInitialized || settings is null)
            return;

        // 初始化只执行一次。GameManagement 完成实例同步后，才能把稳定选择传给首页。
        homePage?.SetSettings(settings);
        await gameManagement.InitializeAsync(settings);
        if (homePage is not null)
        {
            SynchronizeHomeInstances();
            homePage.Initialize(settings, gameManagement.SelectedInstance);
        }

        isInitialized = true;
        if (NavigationCatalog.IsPage(currentPage, NavigationCatalog.GameSettingsPage))
            SynchronizeGameSettingsInstances();
    }

    public async Task ActivatePageAsync(string page)
    {
        currentPage = page;
        var isGameSettingsPage = NavigationCatalog.IsPage(page, NavigationCatalog.GameSettingsPage);
        var isDownloadPage = NavigationCatalog.IsPage(page, NavigationCatalog.DownloadPage);
        gameSettingsPage.SetShellPageActive(isGameSettingsPage);
        if (isDownloadPage && settings is not null)
            await downloadPage.EnsureVersionsLoadedAsync();

        if (!isInitialized)
        {
            // Prime 在窗口显示前已经读取本地实例。启动期进入游戏设置时直接投影该快照，
            // 不等待后续的网络初始化，也不触发下载页版本目录请求。
            if (isGameSettingsPage && settings is not null)
                SynchronizeGameSettingsInstances();
            return;
        }

        SynchronizeVisibleInstanceProjection();
    }

    public async Task RefreshExternalInstanceCatalogAsync()
    {
        if (!isInitialized)
            return;

        await instanceSynchronizationLock.WaitAsync();
        try
        {
            await gameManagement.RefreshInstancesAsync();
            SynchronizeVisibleInstanceProjection();
        }
        finally
        {
            instanceSynchronizationLock.Release();
        }
    }

    public Task<bool> SelectLaunchInstanceAsync(GameInstance instance)
    {
        return gameManagement.SelectLaunchInstanceAsync(instance);
    }

    public async Task<bool> SetHomeLaunchMenuPinnedAsync(bool isPinned)
    {
        if (settings is null)
            return false;

        var previousValue = settings.IsHomeLaunchMenuPinned;
        if (previousValue == isPinned)
            return true;

        // 先乐观更新共享设置，保存失败再回滚，确保内存状态与磁盘状态最终一致。
        settings.IsHomeLaunchMenuPinned = isPinned;
        try
        {
            await settingsService.UpdateAsync(latest => latest.IsHomeLaunchMenuPinned = isPinned);
            logger.LogInformation("Home launch menu pin preference saved. IsPinned={IsPinned}", isPinned);
            return true;
        }
        catch (Exception exception)
        {
            settings.IsHomeLaunchMenuPinned = previousValue;
            logger.LogWarning(
                exception,
                "Failed to save home launch menu pin preference. IsPinned={IsPinned}",
                isPinned);
            return false;
        }
    }

    /// <summary>
    /// 记录侧边菜单的折叠偏好。走防抖队列而不是直接落盘：点击不必等待写入完成，
    /// 写入失败由队列记录、提示并在下次变更时重试，既不卡 UI 也不会终止进程。
    /// </summary>
    public void SetMenuExpanded(bool isExpanded)
    {
        settingsPersistence.Update(latest => latest.IsMenuExpanded = isExpanded);
    }

    public void Dispose()
    {
        if (!isAttached)
            return;

        // 协调器通常与主窗口同寿命，但仍显式退订，防止窗口重建后旧实例继续响应页面事件。
        downloadPage.InstanceInstalled -= DownloadPage_InstanceInstalled;
        resourcesPage.ModpackImported -= DownloadPage_InstanceInstalled;
        resourcesPage.ModpackManualDownloadsRequested -= ResourcesPage_ModpackManualDownloadsRequested;
        gameManagement.PropertyChanged -= GameManagement_PropertyChanged;
        gameSettingsPage.LaunchInstanceRequested -= GameSettingsPage_LaunchInstanceRequested;
        gameSettingsPage.OnlineResourceInstallRequested -= GameSettingsPage_OnlineResourceInstallRequested;
        gameSettingsPage.InstancesChanged -= GameSettingsPage_InstancesChanged;
        gameSettingsPage.InstanceListActivated -= GameSettingsPage_InstanceListActivated;
        settingsPage.LaunchDefaultsChanged -= SettingsPage_LaunchDefaultsChanged;
        settingsPage.DownloadSourceChanged -= SettingsPage_DownloadSourceChanged;
        settingsPage.MaximumDownloadConcurrencyChanged -= SettingsPage_MaximumDownloadConcurrencyChanged;
        settingsPage.DownloadSpeedLimitChanged -= SettingsPage_DownloadSpeedLimitChanged;
        settingsPage.MinecraftDirectoryChanged -= SettingsPage_MinecraftDirectoryChanged;
        instanceSynchronizationLock.Dispose();
        isAttached = false;
    }

    private void SynchronizeHomeInstances()
    {
        homePage?.ApplyInstanceCatalog(
            gameManagement.Instances,
            gameManagement.InstanceCatalogRevision);
        homePage?.SetSelectedInstance(gameManagement.SelectedInstance);
    }

    private void SynchronizeGameSettingsInstances()
    {
        if (gameSettingsPage.IsDetailsStep)
            return;

        gameSettingsPage.ApplyInstanceCatalog(
            gameManagement.Instances,
            gameManagement.InstanceCatalogRevision);
    }

    private void SynchronizeVisibleInstanceProjection()
    {
        if (NavigationCatalog.IsPage(currentPage, NavigationCatalog.HomePage))
            SynchronizeHomeInstances();
        else if (NavigationCatalog.IsPage(currentPage, NavigationCatalog.GameSettingsPage))
            SynchronizeGameSettingsInstances();
    }

    private void GameManagement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameManagementViewModel.SelectedInstance))
            homePage?.SetSelectedInstance(gameManagement.SelectedInstance);

        if (e.PropertyName == nameof(GameManagementViewModel.ProgressPercent))
            ProgressChanged?.Invoke(gameManagement.ProgressPercent);
    }

    private void DownloadPage_InstanceInstalled(object? sender, GameInstance instance)
    {
        Observe(HandleInstalledInstanceAsync(instance), "synchronize an installed instance");
    }

    private async Task HandleInstalledInstanceAsync(GameInstance instance)
    {
        // Cancel queued watcher work first. If a directory refresh already started before the
        // installation committed, ApplyUpdatedInstanceAsync waits for it and then reapplies the
        // authoritative local result so that stale discovery cannot remove the new instance.
        stateSyncService.AcknowledgeLocalStateChange();
        await gameManagement.ApplyUpdatedInstanceAsync(instance);
        SynchronizeVisibleInstanceProjection();

        try
        {
            // 持久化选择失败时仍在本次会话中选中新实例；用户可以立即看到并处理安装结果。
            var saved = await gameManagement.SelectLaunchInstanceAsync(instance);
            if (!saved)
                gameManagement.SelectedInstance = instance;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to persist installed instance selection. InstanceId={InstanceId}", instance.Id);
            gameManagement.SelectedInstance = instance;
        }

        stateSyncService.AcknowledgeLocalStateChange();
        SynchronizeVisibleInstanceProjection();
    }

    private void ResourcesPage_ModpackManualDownloadsRequested(
        object? sender,
        ResourcesModpackManualDownloadsRequestedEventArgs args)
    {
        downloadPage.ModpackManualDownloadsDialog.Show(args.Instance, args.ManualDownloads);
    }

    private void GameSettingsPage_LaunchInstanceRequested(GameInstance instance)
    {
        Observe(HandleGameSettingsLaunchRequestAsync(instance), "select a launch instance from game settings");
    }

    private async Task HandleGameSettingsLaunchRequestAsync(GameInstance instance)
    {
        try
        {
            var saved = await gameManagement.SelectLaunchInstanceAsync(instance);
            if (!saved)
            {
                statusService.Report(Strings.Status_LaunchInstanceSelectionFailed);
                return;
            }

            NavigationRequested?.Invoke(NavigationCatalog.HomePage);
            statusService.Report(string.Format(Strings.Status_LaunchInstanceSelectedFormat, instance.Name));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to select launch instance from game settings. InstanceId={InstanceId}", instance.Id);
            statusService.Report(Strings.Status_LaunchInstanceSelectionFailed);
        }
    }

    private void GameSettingsPage_OnlineResourceInstallRequested(GameInstance instance, ResourceProjectKind kind)
    {
        Observe(
            OpenOnlineResourcesForInstanceAsync(instance, kind),
            $"open online {kind} resources for an instance");
    }

    private void GameSettingsPage_InstanceListActivated()
    {
        if (NavigationCatalog.IsPage(currentPage, NavigationCatalog.GameSettingsPage))
            SynchronizeGameSettingsInstances();
    }

    private async Task OpenOnlineResourcesForInstanceAsync(GameInstance instance, ResourceProjectKind kind)
    {
        NavigationRequested?.Invoke(NavigationCatalog.ResourcesPage);
        await resourcesPage.OpenOnlineProjectsForInstanceAsync(instance, kind);
    }

    private void GameSettingsPage_InstancesChanged(GameSettingsInstancesChangedEventArgs args)
    {
        Observe(HandleGameSettingsInstancesChangedAsync(args), "apply a game settings instance change");
    }

    private async Task HandleGameSettingsInstancesChangedAsync(GameSettingsInstancesChangedEventArgs args)
    {
        // Instance mutations are already propagated incrementally below. Rearm the filesystem
        // monitor so the launcher's own metadata write cannot queue a stale full-page refresh.
        stateSyncService.AcknowledgeLocalStateChange();

        // 已完成的本地变更携带权威实例或 ID，直接串行合并，避免再扫描目录。
        if (args.Kind is GameSettingsInstancesChangedKind.Updated && args.UpdatedInstance is not null)
        {
            await gameManagement.ApplyUpdatedInstanceAsync(args.UpdatedInstance);
            SynchronizeVisibleInstanceProjection();
            return;
        }

        if (args.Kind is GameSettingsInstancesChangedKind.Deleted
            && !string.IsNullOrWhiteSpace(args.DeletedInstanceId))
        {
            await gameManagement.RemoveInstanceAsync(args.DeletedInstanceId);
            SynchronizeVisibleInstanceProjection();
            return;
        }

        await SynchronizeInstancesFromGameSettingsAsync();
    }

    private async Task SynchronizeInstancesFromGameSettingsAsync()
    {
        try
        {
            await gameManagement.RefreshInstancesAsync();
            SynchronizeVisibleInstanceProjection();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to synchronize instances from game settings.");
            statusService.Report(Strings.Status_LoadInstancesFailed);
        }
    }

    private void SettingsPage_LaunchDefaultsChanged(object? sender, EventArgs e)
    {
        if (settings is null)
            return;

        homePage?.SetSettings(settings);
        gameSettingsPage.PrimeFromSettings(settings);
    }

    private void SettingsPage_DownloadSourceChanged(object? sender, SettingsDownloadSourceChangedEventArgs e)
    {
        // 设置页已经负责落盘，这里只传播运行时状态，避免每个消费者分别读取设置文件。
        if (settings is not null)
            settings.DownloadSourcePreference = e.Preference;
        downloadPage.ApplyDownloadSourcePreference(e.Preference);
        gameManagement.ApplyDownloadSourcePreference(e.Preference);
    }

    private void SettingsPage_MaximumDownloadConcurrencyChanged(
        object? sender,
        SettingsMaximumDownloadConcurrencyChangedEventArgs e)
    {
        if (settings is not null)
            settings.MaximumDownloadConcurrency = e.MaximumDownloadConcurrency;
        downloadConcurrencyLimitState.SetMaximumDownloadConcurrency(e.MaximumDownloadConcurrency);
    }

    private void SettingsPage_DownloadSpeedLimitChanged(object? sender, SettingsDownloadSpeedLimitChangedEventArgs e)
    {
        if (settings is not null)
            settings.DownloadSpeedLimitMbPerSecond = e.DownloadSpeedLimitMbPerSecond;
        downloadSpeedLimitState.SetDownloadSpeedLimitMbPerSecond(e.DownloadSpeedLimitMbPerSecond);
        downloadPage.ApplyDownloadSpeedLimit(e.DownloadSpeedLimitMbPerSecond);
        gameManagement.ApplyDownloadSpeedLimit(e.DownloadSpeedLimitMbPerSecond);
    }

    private void SettingsPage_MinecraftDirectoryChanged(object? sender, SettingsMinecraftDirectoryChangedEventArgs e)
    {
        if (settings is null)
            return;

        // 目录切换会同时使首页、下载页和游戏设置中的实例视图失效，因此从共享管理器统一重载。
        settings.MinecraftDirectory = e.MinecraftDirectory;
        homePage?.SetSettings(settings);
        downloadPage.ApplyMinecraftDirectory(e.MinecraftDirectory);
        gameSettingsPage.PrimeFromSettings(settings);
        stateSyncService.AcknowledgeLocalStateChange();
        Observe(RefreshMinecraftDirectoryInstancesAsync(), "refresh instances after changing the Minecraft directory");
    }

    private async Task RefreshMinecraftDirectoryInstancesAsync()
    {
        try
        {
            await gameManagement.RefreshInstancesAsync();
            await downloadPage.InstanceOptions.RefreshNameAvailabilityAsync();
            var latestSettings = await settingsService.LoadAsync();
            if (settings is not null
                && MinecraftDirectoryPath.Equals(
                    settings.MinecraftDirectory,
                    latestSettings.MinecraftDirectory))
            {
                settings.DefaultInstanceId = latestSettings.DefaultInstanceId;
            }
            SynchronizeVisibleInstanceProjection();
            stateSyncService.AcknowledgeLocalStateChange();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to refresh instances after changing the Minecraft directory.");
            statusService.Report(Strings.Status_LoadInstancesFailed);
        }
    }

    private void Observe(Task task, string operation)
    {
        // 事件处理器不能返回 Task；统一观察后台任务，避免 fire-and-forget 异常成为未观察异常。
        _ = ObserveAsync(task, operation);
    }

    private async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to {Operation}.", operation);
        }
    }
}
