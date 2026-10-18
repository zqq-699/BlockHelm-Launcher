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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Settings;

/// <summary>
/// 管理 Java 自动/手动选择、运行时发现和自定义 Java 导入，并向设置页报告选择变化。
/// </summary>
public sealed partial class JavaSettingsEditorViewModel : ObservableObject
{
    // 列表是发现结果的 UI 投影，保存值使用可执行文件路径作为跨刷新稳定身份。
    private const string JavaSelectionAutoId = "auto";
    private const string JavaSelectionManualId = "manual";

    private readonly IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService;
    private readonly IStatusService statusService;
    private readonly IFilePickerService filePickerService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly Func<string?> minecraftDirectoryProvider;
    private CancellationTokenSource? javaRuntimeScanCancellationTokenSource;
    private string? savedSelectedJavaExecutablePath;
    private bool suppressSelectionChanged;

    [ObservableProperty]
    private SettingsJavaSelectionOption? selectedJavaSelectionOption;

    [ObservableProperty]
    private SettingsJavaRuntimeItem? selectedJavaRuntime;

    [ObservableProperty]
    private bool isJavaRuntimeScanRunning;

    [ObservableProperty]
    private string javaRuntimeListMessage = Strings.Settings_JavaListEmpty;

    [ObservableProperty]
    private bool isEditorEnabled = true;

    public JavaSettingsEditorViewModel(
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        IStatusService statusService,
        IFilePickerService filePickerService,
        IFloatingMessageService floatingMessageService,
        Func<string?> minecraftDirectoryProvider)
    {
        this.javaRuntimeDiscoveryService = javaRuntimeDiscoveryService;
        this.statusService = statusService;
        this.filePickerService = filePickerService;
        this.floatingMessageService = floatingMessageService;
        this.minecraftDirectoryProvider = minecraftDirectoryProvider;

        JavaSelectionOptions.Add(new SettingsJavaSelectionOption(JavaSelectionAutoId, Strings.Settings_JavaSelectionAuto));
        JavaSelectionOptions.Add(new SettingsJavaSelectionOption(JavaSelectionManualId, Strings.Settings_JavaSelectionManual));
        SelectedJavaSelectionOption = JavaSelectionOptions[0];
    }

    public ObservableCollection<SettingsJavaSelectionOption> JavaSelectionOptions { get; } = [];

    public ObservableCollection<SettingsJavaRuntimeItem> JavaRuntimes { get; } = [];

    public bool HasJavaRuntimeListMessage => !string.IsNullOrWhiteSpace(JavaRuntimeListMessage);

    public bool IsJavaManualSelection => SelectedMode is JavaSelectionMode.Manual;

    public JavaSelectionMode SelectedMode => SelectedJavaSelectionOption?.Id == JavaSelectionManualId
        ? JavaSelectionMode.Manual
        : JavaSelectionMode.Auto;

    public string? SelectedExecutablePath => SelectedMode is JavaSelectionMode.Manual && SelectedJavaRuntime is not null
        ? SelectedJavaRuntime.ExecutablePath
        : savedSelectedJavaExecutablePath;

    public event EventHandler? JavaSelectionChanged;

    public void LoadSelection(JavaSelectionMode mode, string? selectedJavaExecutablePath)
    {
        // 先恢复模式与保存路径，再异步发现；即使运行时列表尚未加载，用户选择也不会丢失。
        suppressSelectionChanged = true;
        try
        {
            savedSelectedJavaExecutablePath = string.IsNullOrWhiteSpace(selectedJavaExecutablePath)
                ? null
                : selectedJavaExecutablePath;
            SelectedJavaSelectionOption = GetJavaSelectionOption(mode);
            SelectedJavaRuntime = null;
            UpdateJavaRuntimeSelectionAfterListChanged();
        }
        finally
        {
            suppressSelectionChanged = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshJavaRuntimes))]
    public async Task RefreshJavaRuntimesAsync()
    {
        await RefreshJavaRuntimesCoreAsync(allowWhenDisabled: false, deepSearch: false);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshJavaRuntimes))]
    public async Task DeepSearchJavaRuntimesAsync()
    {
        await RefreshJavaRuntimesCoreAsync(allowWhenDisabled: false, deepSearch: true);
    }

    public async Task RefreshJavaRuntimesForDisplayAsync()
    {
        await RefreshJavaRuntimesCoreAsync(allowWhenDisabled: true, deepSearch: false);
    }

    private async Task RefreshJavaRuntimesCoreAsync(bool allowWhenDisabled, bool deepSearch)
    {
        // 手动按钮遵守 CanExecute，展示页面的后台刷新可在禁用状态下预热列表。
        if (IsJavaRuntimeScanRunning || !allowWhenDisabled && !IsEditorEnabled)
            return;

        javaRuntimeScanCancellationTokenSource?.Cancel();
        javaRuntimeScanCancellationTokenSource?.Dispose();

        var cancellationTokenSource = new CancellationTokenSource();
        javaRuntimeScanCancellationTokenSource = cancellationTokenSource;

        IsJavaRuntimeScanRunning = true;
        JavaRuntimeListMessage = Strings.Settings_JavaListLoading;

        try
        {
            // 发现完成后用路径协调旧选择，避免重建列表导致 ComboBox 跳到首项。
            var discoveredRuntimes = deepSearch
                ? await javaRuntimeDiscoveryService.SearchAllAsync(
                    minecraftDirectoryProvider(), cancellationTokenSource.Token)
                : await javaRuntimeDiscoveryService.DiscoverAsync(
                    minecraftDirectoryProvider(), cancellationTokenSource.Token);

            JavaRuntimes.Clear();
            foreach (var runtime in discoveredRuntimes)
                JavaRuntimes.Add(new SettingsJavaRuntimeItem(runtime));

            await EnsureSavedSelectedJavaRuntimePresentAsync(cancellationTokenSource.Token);
            UpdateJavaRuntimeSelectionAfterListChanged();
            JavaRuntimeListMessage = JavaRuntimes.Count == 0
                ? Strings.Settings_JavaListEmpty
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            JavaRuntimes.Clear();
            JavaRuntimeListMessage = Strings.Settings_JavaListEmpty;
            statusService.Report(Strings.Status_JavaScanFailed);
        }
        finally
        {
            if (ReferenceEquals(javaRuntimeScanCancellationTokenSource, cancellationTokenSource))
            {
                IsJavaRuntimeScanRunning = false;
                cancellationTokenSource.Dispose();
                javaRuntimeScanCancellationTokenSource = null;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportJavaRuntime))]
    public async Task ImportJavaRuntimeAsync()
    {
        // 用户选的是可执行文件，服务负责验证 Java 版本和架构后才加入候选列表。
        if (!IsEditorEnabled)
            return;

        var executablePath = filePickerService.PickJavaExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        try
        {
            var runtime = await javaRuntimeDiscoveryService.ImportExecutableAsync(executablePath);
            var alreadyExists = !AddJavaRuntime(runtime);
            var importedRuntime = JavaRuntimes.First(item => IsSameJavaRuntime(item, runtime));

            // 自动模式只扩充持久化候选，不改变用户设置；手动模式下则直接采用刚导入的运行时。
            if (IsJavaManualSelection)
            {
                suppressSelectionChanged = true;
                try
                {
                    savedSelectedJavaExecutablePath = runtime.ExecutablePath;
                    SelectedJavaRuntime = importedRuntime;
                }
                finally
                {
                    suppressSelectionChanged = false;
                }

                RaiseJavaSelectionChanged();
            }

            if (alreadyExists)
                floatingMessageService.Show(Strings.Status_JavaAlreadyExists);

            JavaRuntimeListMessage = JavaRuntimes.Count == 0
                ? Strings.Settings_JavaListEmpty
                : string.Empty;
            if (!alreadyExists)
                statusService.Report(Strings.Status_JavaImported);
        }
        catch (Exception)
        {
            statusService.Report(Strings.Status_JavaImportFailed);
        }
    }

    partial void OnSelectedJavaSelectionOptionChanged(
        SettingsJavaSelectionOption? oldValue,
        SettingsJavaSelectionOption? newValue)
    {
        if (newValue is null)
        {
            var wasSuppressingSelectionChanged = suppressSelectionChanged;
            suppressSelectionChanged = true;
            try
            {
                SelectedJavaSelectionOption = oldValue ?? JavaSelectionOptions[0];
            }
            finally
            {
                suppressSelectionChanged = wasSuppressingSelectionChanged;
            }
            return;
        }

        OnPropertyChanged(nameof(IsJavaManualSelection));
        OnPropertyChanged(nameof(SelectedMode));

        if (IsJavaManualSelection)
        {
            UpdateJavaRuntimeSelectionAfterListChanged();
        }
        else
        {
            suppressSelectionChanged = true;
            try
            {
                SelectedJavaRuntime = null;
            }
            finally
            {
                suppressSelectionChanged = false;
            }
        }

        RaiseJavaSelectionChanged();
    }

    partial void OnSelectedJavaRuntimeChanged(SettingsJavaRuntimeItem? value)
    {
        if (suppressSelectionChanged)
            return;

        if (!IsJavaManualSelection)
        {
            if (value is not null)
            {
                suppressSelectionChanged = true;
                try
                {
                    SelectedJavaRuntime = null;
                }
                finally
                {
                    suppressSelectionChanged = false;
                }
            }

            return;
        }

        if (value is not null)
            savedSelectedJavaExecutablePath = value.ExecutablePath;

        OnPropertyChanged(nameof(SelectedExecutablePath));
        RaiseJavaSelectionChanged();
    }

    partial void OnIsJavaRuntimeScanRunningChanged(bool value)
    {
        RefreshJavaRuntimesCommand.NotifyCanExecuteChanged();
        DeepSearchJavaRuntimesCommand.NotifyCanExecuteChanged();
    }

    partial void OnJavaRuntimeListMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasJavaRuntimeListMessage));
    }

    partial void OnIsEditorEnabledChanged(bool value)
    {
        RefreshJavaRuntimesCommand.NotifyCanExecuteChanged();
        DeepSearchJavaRuntimesCommand.NotifyCanExecuteChanged();
        ImportJavaRuntimeCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefreshJavaRuntimes()
    {
        return IsEditorEnabled && !IsJavaRuntimeScanRunning;
    }

    private bool CanImportJavaRuntime()
    {
        return IsEditorEnabled;
    }

    private bool AddJavaRuntime(JavaRuntimeInfo runtime)
    {
        if (JavaRuntimes.Any(item => IsSameJavaRuntime(item, runtime)))
            return false;

        var newItem = new SettingsJavaRuntimeItem(runtime);
        var insertIndex = 0;
        while (insertIndex < JavaRuntimes.Count
            && (JavaRuntimes[insertIndex].MajorVersion ?? 0) > (newItem.MajorVersion ?? 0))
        {
            insertIndex++;
        }

        JavaRuntimes.Insert(insertIndex, newItem);
        return true;
    }

    private async Task EnsureSavedSelectedJavaRuntimePresentAsync(CancellationToken cancellationToken)
    {
        // 自动发现可能漏掉便携式 Java；对已保存路径单独探测以保持历史设置可用。
        if (!IsJavaManualSelection || string.IsNullOrWhiteSpace(savedSelectedJavaExecutablePath))
            return;

        if (JavaRuntimes.Any(item => IsSameExecutablePath(item.ExecutablePath, savedSelectedJavaExecutablePath)))
            return;

        try
        {
            var runtime = await javaRuntimeDiscoveryService.DiscoverExecutableAsync(
                savedSelectedJavaExecutablePath,
                cancellationToken);
            AddJavaRuntime(runtime);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private void UpdateJavaRuntimeSelectionAfterListChanged()
    {
        // 优先保存路径，其次当前对象，最后首个候选；自动模式不强制选择具体运行时。
        if (!IsJavaManualSelection)
        {
            SelectedJavaRuntime = null;
            return;
        }

        var savedRuntime = string.IsNullOrWhiteSpace(savedSelectedJavaExecutablePath)
            ? null
            : JavaRuntimes.FirstOrDefault(item => IsSameExecutablePath(item.ExecutablePath, savedSelectedJavaExecutablePath));
        var currentRuntime = SelectedJavaRuntime is null
            ? null
            : JavaRuntimes.FirstOrDefault(item => IsSameExecutablePath(item.ExecutablePath, SelectedJavaRuntime.ExecutablePath));

        SelectedJavaRuntime = savedRuntime ?? currentRuntime ?? JavaRuntimes.FirstOrDefault();
    }

    private SettingsJavaSelectionOption GetJavaSelectionOption(JavaSelectionMode mode)
    {
        var targetId = mode is JavaSelectionMode.Manual ? JavaSelectionManualId : JavaSelectionAutoId;
        return JavaSelectionOptions.FirstOrDefault(option => option.Id == targetId) ?? JavaSelectionOptions[0];
    }

    private void RaiseJavaSelectionChanged()
    {
        if (suppressSelectionChanged)
            return;

        OnPropertyChanged(nameof(SelectedExecutablePath));
        JavaSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSameJavaRuntime(SettingsJavaRuntimeItem item, JavaRuntimeInfo runtime)
    {
        if (IsSameExecutablePath(item.ExecutablePath, runtime.ExecutablePath))
            return true;

        if (string.IsNullOrWhiteSpace(runtime.Version))
            return false;

        return string.Equals(item.InstallationDirectory, runtime.InstallationDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.VersionText, runtime.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Architecture, runtime.Architecture, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameExecutablePath(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
