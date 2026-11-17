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
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.Utilities;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.App.ViewModels.Download;

/// <summary>
/// 跟踪活动与最近完成的下载任务，集中处理取消、短暂保留和窗口关闭等待。
/// </summary>
public sealed partial class DownloadTasksPageViewModel : ObservableObject
{
    // ObservableCollection 只在 UI 线程修改；backgroundTasksLock 仅保护不可观察的 Task 集合。
    private static readonly TimeSpan DefaultCompletedTaskRetention = TimeSpan.FromSeconds(3);
    private readonly TimeSpan completedTaskRetention;
    private readonly object backgroundTasksLock = new();
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly Dictionary<string, CancellationTokenSource> removalTokens = [];
    private readonly IUiDispatcher uiDispatcher;

    public DownloadTasksPageViewModel()
        : this(ImmediateUiDispatcher.Instance, null)
    {
    }

    public DownloadTasksPageViewModel(TimeSpan? completedTaskRetention)
        : this(ImmediateUiDispatcher.Instance, completedTaskRetention)
    {
    }

    public DownloadTasksPageViewModel(IUiDispatcher uiDispatcher)
        : this(uiDispatcher, null)
    {
    }

    private DownloadTasksPageViewModel(IUiDispatcher uiDispatcher, TimeSpan? completedTaskRetention)
    {
        this.uiDispatcher = uiDispatcher;
        this.completedTaskRetention = completedTaskRetention ?? DefaultCompletedTaskRetention;
        Tasks.CollectionChanged += Tasks_CollectionChanged;
    }

    public ObservableCollection<DownloadTaskItem> Tasks { get; } = [];

    public bool HasTasks => Tasks.Count > 0;

    public bool HasRunningTasks => RunningTaskCount > 0;

    public bool HasActiveOperations => HasRunningTasks || TrackedBackgroundTaskCount > 0;

    public int RunningTaskCount => Tasks.Count(task => task.State is DownloadTaskState.Running);

    internal int TrackedBackgroundTaskCount
    {
        get
        {
            lock (backgroundTasksLock)
                return backgroundTasks.Count;
        }
    }

    public event EventHandler<DownloadTaskItem>? TaskStarted;

    public event EventHandler? ActivityChanged;

    public DownloadTaskItem BeginTask(string title, string subtitle)
    {
        // 调用方可来自后台线程，通过 Dispatcher 建立任务后再返回稳定任务对象。
        if (uiDispatcher.HasAccess)
            return BeginTaskCore(title, subtitle);

        DownloadTaskItem? task = null;
        uiDispatcher.Invoke(() => task = BeginTaskCore(title, subtitle));
        return task!;
    }

    private DownloadTaskItem BeginTaskCore(string title, string subtitle)
    {
        var task = new DownloadTaskItem(title, subtitle);
        task.PropertyChanged += Task_PropertyChanged;
        Tasks.Insert(0, task);
        TaskStarted?.Invoke(this, task);
        return task;
    }

    [RelayCommand]
    public void CancelTask(DownloadTaskItem? task)
    {
        if (task is null)
            return;

        task.Cancel();
        RemoveTask(task, force: true);
    }

    public void CancelAllRunningTasks()
    {
        foreach (var task in Tasks.ToList())
        {
            if (task.State is DownloadTaskState.Running)
                task.Cancel("ApplicationExit");
        }
    }

    public void TrackBackgroundTask(Task task)
    {
        // 跟踪实际导入/安装 Task，使窗口关闭能等待清理完成，而非只看 UI 条目状态。
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted)
            return;

        lock (backgroundTasksLock)
        {
            backgroundTasks.Add(task);
        }
        NotifyActivityChanged();

        _ = RemoveTrackedBackgroundTaskWhenCompletedAsync(task);
    }

    public async Task<bool> WaitForTrackedBackgroundTasksAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // 使用总超时等待当前快照；新任务不会无限延长已经开始的关闭流程。
        Task[] tasks;
        lock (backgroundTasksLock)
            tasks = backgroundTasks.ToArray();

        if (tasks.Length == 0)
            return true;

        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (DownloadTaskItem task in e.OldItems)
            {
                CancelScheduledRemoval(task);
                task.PropertyChanged -= Task_PropertyChanged;
            }
        }

        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(RunningTaskCount));
        NotifyActivityChanged();
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DownloadTaskItem task || e.PropertyName != nameof(DownloadTaskItem.State))
            return;

        if (task.State is DownloadTaskState.Completed)
            ScheduleRemoval(task);
        else
            CancelScheduledRemoval(task);

        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(RunningTaskCount));
        NotifyActivityChanged();
    }

    private void ScheduleRemoval(DownloadTaskItem task)
    {
        // 完成条目短暂保留供用户确认结果，重新进入活动态会取消计划删除。
        CancelScheduledRemoval(task);
        var cancellation = new CancellationTokenSource();
        removalTokens[task.Id] = cancellation;
        _ = RemoveCompletedTaskAfterDelayAsync(task, cancellation.Token);
    }

    private void CancelScheduledRemoval(DownloadTaskItem task)
    {
        if (!removalTokens.Remove(task.Id, out var cancellation))
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task RemoveCompletedTaskAfterDelayAsync(DownloadTaskItem task, CancellationToken cancellationToken)
    {
        // 延迟完成后再次校验终态，防止复用或状态修正时误删活动任务。
        try
        {
            await Task.Delay(completedTaskRetention, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            RemoveTask(task, force: false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RemoveTrackedBackgroundTaskWhenCompletedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (backgroundTasksLock)
                backgroundTasks.Remove(task);
            NotifyActivityChanged();
        }
    }

    private void NotifyActivityChanged()
    {
        if (!uiDispatcher.HasAccess)
        {
            uiDispatcher.Post(NotifyActivityChanged);
            return;
        }

        OnPropertyChanged(nameof(HasActiveOperations));
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveTask(DownloadTaskItem task, bool force)
    {
        if (!uiDispatcher.HasAccess)
        {
            uiDispatcher.Post(() => RemoveTask(task, force));
            return;
        }

        if (force || task.State is DownloadTaskState.Completed)
            Tasks.Remove(task);
    }
}

/// <summary>
/// 表示单个可取消下载任务及其线程安全进度投影。
/// </summary>
public sealed partial class DownloadTaskItem : ObservableObject
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly string? diagnosticOperationId;
    private readonly IDisposable speedMeterLifetime;
    private readonly IProgress<LauncherProgress> speedMeterProgress;

    public DownloadTaskItem(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
        diagnosticOperationId = DownloadDiagnostics.Capture().TryGetValue("OperationId", out var operationId) ? operationId as string : null;
        var synchronizationContext = SynchronizationContext.Current;
        void ReportOnCapturedContext(LauncherProgress value)
        {
            if (synchronizationContext is null || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
                Report(value);
            else
                synchronizationContext.Post(static state =>
                {
                    var (task, progress) = ((DownloadTaskItem, LauncherProgress))state!;
                    task.Report(progress);
                }, (this, value));
        }
        speedMeterProgress = DownloadSpeedTaskProgress.Create(
            report: Report,
            reportTelemetry: ReportOnCapturedContext,
            out speedMeterLifetime);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Title { get; }

    public string Subtitle { get; }

    public CancellationToken CancellationToken => cancellation.Token;

    public bool IsCancellationRequested => cancellation.IsCancellationRequested;

    [ObservableProperty]
    private DownloadTaskState state = DownloadTaskState.Running;

    [ObservableProperty]
    private string statusMessage = Strings.DownloadTask_Preparing;

    [ObservableProperty]
    private string downloadSpeedText = string.Empty;

    [ObservableProperty]
    private double progressPercent;

    public string StateText => State switch
    {
        DownloadTaskState.Completed => Strings.DownloadTask_Completed,
        DownloadTaskState.Failed => Strings.DownloadTask_Failed,
        _ => Strings.DownloadTask_Running
    };

    public string ProgressText => $"{Math.Clamp(ProgressPercent, 0, 100):0}%";

    public bool IsRunning => State is DownloadTaskState.Running;

    public bool IsFailed => State is DownloadTaskState.Failed;

    public bool HasDownloadSpeedText => !string.IsNullOrWhiteSpace(DownloadSpeedText);

    internal IProgress<LauncherProgress> CreateProgress(Action<LauncherProgress> report) =>
        DownloadSpeedTaskProgress.Forward(speedMeterProgress, report);

    public void Report(LauncherProgress progress)
    {
        if (State is DownloadTaskState.Completed or DownloadTaskState.Failed)
            return;

        if (progress.DownloadSpeedTelemetry is not null)
        {
            DownloadSpeedText = LauncherProgressTextFormatter.FormatDownloadSpeed(progress.DownloadSpeedTelemetry);
            return;
        }

        State = DownloadTaskState.Running;
        StatusMessage = progress.Message;

        if (progress.Percent is { } percent)
            // A progress report describes ongoing work. Only Complete() may
            // publish 100%, otherwise later installation stages can appear to
            // run after the card has already claimed completion.
            ProgressPercent = Math.Clamp(Math.Max(ProgressPercent, percent), 0, 99);
    }

    public void Complete(string message)
    {
        speedMeterLifetime.Dispose();
        // 终态方法同时冻结取消语义并触发页面计划移除。
        State = DownloadTaskState.Completed;
        StatusMessage = message;
        ProgressPercent = 100;
        DownloadSpeedText = string.Empty;
    }

    public void Fail(string message)
    {
        speedMeterLifetime.Dispose();
        State = DownloadTaskState.Failed;
        StatusMessage = message;
        DownloadSpeedText = string.Empty;
    }

    public void Cancel() => Cancel("User");

    internal void Cancel(string cancellationOrigin)
    {
        Serilog.Log.ForContext("OperationId", diagnosticOperationId ?? Id).Information("Download task cancellation requested. TaskId={TaskId} CancellationOrigin={CancellationOrigin}", Id, cancellationOrigin);
        speedMeterLifetime.Dispose();
        if (!cancellation.IsCancellationRequested)
            cancellation.Cancel();

        DownloadSpeedText = string.Empty;
    }

    partial void OnStateChanged(DownloadTaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    partial void OnDownloadSpeedTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDownloadSpeedText));
    }

}

public enum DownloadTaskState
{
    Running,
    Completed,
    Failed
}

