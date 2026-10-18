/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Reflection;
using Launcher.App.Services;
using Launcher.App.ViewModels.Settings;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Settings;

public sealed class JavaSettingsEditorViewModelTests
{
    [Fact]
    public async Task DisplayAndAutomaticSearchUseDefaultDiscoveryWhileDeepSearchUsesFullSearch()
    {
        var discoveryService = new RecordingJavaRuntimeDiscoveryService();
        var viewModel = CreateViewModel(discoveryService);

        await viewModel.RefreshJavaRuntimesForDisplayAsync();
        await viewModel.RefreshJavaRuntimesAsync();
        await viewModel.DeepSearchJavaRuntimesAsync();

        Assert.Equal(2, discoveryService.DiscoverCallCount);
        Assert.Equal(1, discoveryService.SearchAllCallCount);
    }

    [Fact]
    public async Task SearchCommandsAreDisabledWhileDiscoveryIsRunning()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<JavaRuntimeInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryService = new RecordingJavaRuntimeDiscoveryService
        {
            DiscoverHandler = _ => completion.Task
        };
        var viewModel = CreateViewModel(discoveryService);

        var discoveryTask = viewModel.RefreshJavaRuntimesAsync();

        Assert.True(viewModel.IsJavaRuntimeScanRunning);
        Assert.False(viewModel.RefreshJavaRuntimesCommand.CanExecute(null));
        Assert.False(viewModel.DeepSearchJavaRuntimesCommand.CanExecute(null));

        completion.SetResult([]);
        await discoveryTask;

        Assert.False(viewModel.IsJavaRuntimeScanRunning);
        Assert.True(viewModel.RefreshJavaRuntimesCommand.CanExecute(null));
        Assert.True(viewModel.DeepSearchJavaRuntimesCommand.CanExecute(null));
    }

    private static JavaSettingsEditorViewModel CreateViewModel(
        IJavaRuntimeDiscoveryService discoveryService) => new(
        discoveryService,
        Stub<IStatusService>(),
        Stub<IFilePickerService>(),
        Stub<IFloatingMessageService>(),
        () => @"C:\Minecraft");

    private static T Stub<T>() where T : class =>
        DispatchProxy.Create<T, DefaultInterfaceProxy>();

    private sealed class RecordingJavaRuntimeDiscoveryService : IJavaRuntimeDiscoveryService
    {
        public int DiscoverCallCount { get; private set; }

        public int SearchAllCallCount { get; private set; }

        public Func<CancellationToken, Task<IReadOnlyList<JavaRuntimeInfo>>>? DiscoverHandler { get; init; }

        public Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
            string? minecraftDirectory,
            CancellationToken cancellationToken = default)
        {
            DiscoverCallCount++;
            return DiscoverHandler?.Invoke(cancellationToken)
                ?? Task.FromResult<IReadOnlyList<JavaRuntimeInfo>>([]);
        }

        public Task<IReadOnlyList<JavaRuntimeInfo>> SearchAllAsync(
            string? minecraftDirectory,
            CancellationToken cancellationToken = default)
        {
            SearchAllCallCount++;
            return Task.FromResult<IReadOnlyList<JavaRuntimeInfo>>([]);
        }

        public Task<JavaRuntimeInfo> DiscoverExecutableAsync(
            string executablePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public class DefaultInterfaceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void))
                return null;
            if (returnType == typeof(Task))
                return Task.CompletedTask;
            if (returnType.IsGenericType
                && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var valueType = returnType.GetGenericArguments()[0];
                var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(valueType)
                    .Invoke(null, [value]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
