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
using Launcher.App.Services;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.GameSettings;

public sealed class InstanceModManagementConflictTests
{
    [Fact]
    public async Task SearchMatchesLocalizedModNameWithoutChangingEnglishSearch()
    {
        using var culture = new TestUiCultureScope("zh-Hans");
        var animations = CreateMod(Path.Combine("instance", "mods", "not-enough-animations.jar"), isEnabled: true);
        animations.Name = "Not Enough Animations";
        animations.LocalizedName = "更多动画";
        var advancements = CreateMod(Path.Combine("instance", "mods", "better-advancements.jar"), isEnabled: false);
        advancements.Name = "Better Advancements";
        advancements.LocalizedName = "更好的进度";
        var service = new ControlledModService([animations, advancements], (_, _) => Task.CompletedTask);
        var status = new RecordingStatusService();
        using var localMods = new LocalModsViewModel(service, status, new NoOpDirectoryMonitor());
        var viewModel = CreateViewModel(localMods, status, new RecordingFloatingMessageService());
        await viewModel.SetSelectedInstanceAsync(CreateInstance());
        await viewModel.OnSectionActivatedAsync();

        viewModel.ModSearchQuery = "动画";
        Assert.Equal("更多动画 | Not Enough Animations", Assert.Single(viewModel.Mods).Title);

        viewModel.ModSearchQuery = "进度";
        Assert.Equal("更好的进度 | Better Advancements", Assert.Single(viewModel.Mods).Title);

        viewModel.ModSearchQuery = "advancements";
        Assert.Equal("更好的进度 | Better Advancements", Assert.Single(viewModel.Mods).Title);

        viewModel.ModSearchQuery = "更好";
        Assert.Equal("更好的进度 | Better Advancements", Assert.Single(viewModel.Mods).Title);
    }

    [Fact]
    public async Task NonChineseLanguageDoesNotSearchLocalizedModName()
    {
        using var culture = new TestUiCultureScope("en");
        var mod = CreateMod(Path.Combine("instance", "mods", "not-enough-animations.jar"), isEnabled: true);
        mod.Name = "Not Enough Animations";
        mod.LocalizedName = "更多动画";
        var status = new RecordingStatusService();
        using var localMods = new LocalModsViewModel(
            new ControlledModService([mod], (_, _) => Task.CompletedTask),
            status,
            new NoOpDirectoryMonitor());
        var viewModel = CreateViewModel(localMods, status, new RecordingFloatingMessageService());
        await viewModel.SetSelectedInstanceAsync(CreateInstance());
        await viewModel.OnSectionActivatedAsync();

        viewModel.ModSearchQuery = "动画";
        Assert.Empty(viewModel.Mods);

        viewModel.ModSearchQuery = "Animations";
        Assert.Equal("Not Enough Animations", Assert.Single(viewModel.Mods).Title);
    }

    [Fact]
    public async Task EnabledAndDisabledFilesWithSameBaseNameRemainDistinctListItems()
    {
        var enabled = CreateMod(Path.Combine("instance", "mods", "example.jar"), isEnabled: true);
        var disabled = CreateMod(Path.Combine("instance", "mods", "example.jar.disabled"), isEnabled: false);
        var service = new ControlledModService([enabled, disabled], (_, _) => Task.CompletedTask);
        var floating = new RecordingFloatingMessageService();
        var status = new RecordingStatusService();
        using var localMods = new LocalModsViewModel(service, status, new NoOpDirectoryMonitor());
        var viewModel = CreateViewModel(localMods, status, floating);
        await viewModel.SetSelectedInstanceAsync(CreateInstance());
        await viewModel.OnSectionActivatedAsync();

        var items = viewModel.Mods;

        Assert.Equal(2, items.Count);
        Assert.NotSame(items[0], items[1]);
        var enabledItem = Assert.Single(items, item => item.FileName == "example.jar");
        var disabledItem = Assert.Single(items, item => item.FileName == "example.jar.disabled");
        Assert.True(enabledItem.IsEnabled);
        Assert.False(disabledItem.IsEnabled);
    }

    [Fact]
    public async Task ToggleModEnabledShowsTargetFileConflictWithoutChangingSnapshot()
    {
        var sourcePath = Path.Combine("instance", "mods", "example.jar.disabled");
        var targetPath = Path.Combine("instance", "mods", "example.jar");
        var mod = CreateMod(sourcePath, isEnabled: false);
        var service = new ControlledModService(
            [mod],
            (_, _) => Task.FromException(new ModEnabledStateConflictException(targetPath)));
        var floating = new RecordingFloatingMessageService();
        var status = new RecordingStatusService();
        using var localMods = new LocalModsViewModel(service, status, new NoOpDirectoryMonitor());
        var viewModel = CreateViewModel(localMods, status, floating);
        await viewModel.SetSelectedInstanceAsync(CreateInstance());
        await viewModel.OnSectionActivatedAsync();
        var item = Assert.Single(viewModel.Mods);

        await viewModel.ToggleModEnabledCommand.ExecuteAsync(item);

        var expectedMessage = string.Format(Strings.Status_ModEnabledStateTargetExistsFormat, "example.jar");
        Assert.Equal(expectedMessage, Assert.Single(floating.Messages));
        Assert.Equal(expectedMessage, status.LastMessage);
        Assert.False(item.IsEnabled);
        Assert.Equal(sourcePath, item.FullPath);
        Assert.False(mod.IsEnabled);
        Assert.Equal(sourcePath, mod.FullPath);
    }

    [Fact]
    public async Task EnableSelectedModsContinuesSafeItemsAndShowsOnlyFirstConflict()
    {
        var firstConflict = CreateMod(Path.Combine("instance", "mods", "first.jar.disabled"), isEnabled: false);
        var secondConflict = CreateMod(Path.Combine("instance", "mods", "second.jar.disabled"), isEnabled: false);
        var safe = CreateMod(Path.Combine("instance", "mods", "safe.jar.disabled"), isEnabled: false);
        var firstTarget = Path.Combine("instance", "mods", "first.jar");
        var secondTarget = Path.Combine("instance", "mods", "second.jar");
        var service = new ControlledModService(
            [firstConflict, secondConflict, safe],
            (mod, _) => mod.FileName switch
            {
                "first.jar.disabled" => Task.FromException(new ModEnabledStateConflictException(firstTarget)),
                "second.jar.disabled" => Task.FromException(new ModEnabledStateConflictException(secondTarget)),
                _ => Task.CompletedTask
            });
        var floating = new RecordingFloatingMessageService();
        var status = new RecordingStatusService();
        using var localMods = new LocalModsViewModel(service, status, new NoOpDirectoryMonitor());
        var viewModel = CreateViewModel(localMods, status, floating);
        await viewModel.SetSelectedInstanceAsync(CreateInstance());
        await viewModel.OnSectionActivatedAsync();
        viewModel.ToggleMultiSelectModeCommand.Execute(null);
        viewModel.SelectAllModsCommand.Execute(null);

        await viewModel.EnableSelectedModsCommand.ExecuteAsync(null);

        Assert.Equal(
            string.Format(Strings.Status_ModEnabledStateTargetExistsFormat, "first.jar"),
            Assert.Single(floating.Messages));
        Assert.Equal(
            string.Format(Strings.Status_SelectedModsEnablePartialFailedFormat, 1, 2),
            status.LastMessage);
        Assert.Equal("first.jar.disabled", firstConflict.FileName);
        Assert.Equal("second.jar.disabled", secondConflict.FileName);
        Assert.Equal("safe.jar", safe.FileName);
        Assert.True(safe.IsEnabled);
        Assert.Equal(3, service.SetEnabledCalls);
    }

    [Fact]
    public async Task ToggleModEnabledKeepsGenericFailureForUnrelatedIoError()
    {
        var sourcePath = Path.Combine("instance", "mods", "example.jar.disabled");
        var mod = CreateMod(sourcePath, isEnabled: false);
        var service = new ControlledModService(
            [mod],
            (_, _) => Task.FromException(new IOException("controlled failure")));
        var floating = new RecordingFloatingMessageService();
        var status = new RecordingStatusService();
        using var localMods = new LocalModsViewModel(service, status, new NoOpDirectoryMonitor());
        var viewModel = CreateViewModel(localMods, status, floating);
        await viewModel.SetSelectedInstanceAsync(CreateInstance());
        await viewModel.OnSectionActivatedAsync();

        await viewModel.ToggleModEnabledCommand.ExecuteAsync(Assert.Single(viewModel.Mods));

        Assert.Empty(floating.Messages);
        Assert.Equal(Strings.Status_SelectedModsEnableFailed, status.LastMessage);
        Assert.False(mod.IsEnabled);
        Assert.Equal(sourcePath, mod.FullPath);
    }

    private static InstanceModManagementSettingsViewModel CreateViewModel(
        LocalModsViewModel localMods,
        IStatusService status,
        IFloatingMessageService floating) => new(
        null!,
        localMods,
        status,
        null!,
        null!,
        null!,
        floating);

    private static GameInstance CreateInstance() => new()
    {
        Id = "instance",
        Name = "Instance",
        InstanceDirectory = "instance",
        MinecraftVersion = "1.21.1",
        Loader = LoaderKind.Fabric
    };

    private static LocalMod CreateMod(string fullPath, bool isEnabled) => new()
    {
        Name = Path.GetFileNameWithoutExtension(fullPath),
        FileName = Path.GetFileName(fullPath),
        FullPath = fullPath,
        IsEnabled = isEnabled
    };

    private sealed class ControlledModService(
        IReadOnlyList<LocalMod> mods,
        Func<LocalMod, bool, Task> setEnabled) : IModService
    {
        public int SetEnabledCalls { get; private set; }

        public Task<IReadOnlyList<LocalMod>> GetModsAsync(
            GameInstance instance,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(mods);

        public Task<LocalMod> ImportAsync(
            GameInstance instance,
            string sourceJarPath,
            bool overwriteExisting = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetEnabledAsync(
            LocalMod mod,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            SetEnabledCalls++;
            return setEnabled(mod, enabled);
        }

        public Task DeleteAsync(LocalMod mod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingStatusService : IStatusService
    {
        public event Action<string>? MessageReported;

        public string? LastMessage { get; private set; }

        public void Report(string message)
        {
            LastMessage = message;
            MessageReported?.Invoke(message);
        }
    }

    private sealed class RecordingFloatingMessageService : IFloatingMessageService
    {
        public event Action<FloatingMessageRequest>? MessageRequested;

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
            MessageRequested?.Invoke(new FloatingMessageRequest(message));
        }

        public void ShowDragHint(object source, string message)
        {
            Messages.Add(message);
            MessageRequested?.Invoke(new FloatingMessageRequest(message, AutoHide: false));
        }

        public void ClearDragHint(object source) => ClearDragHint();

        public void ClearDragHint()
        {
            MessageRequested?.Invoke(new FloatingMessageRequest(string.Empty));
        }
    }

    private sealed class NoOpDirectoryMonitor : IInstanceDirectoryMonitor
    {
        public IInstanceDirectoryWatch Watch(GameInstance instance, InstanceDirectoryKind directoryKind) =>
            new NoOpDirectoryWatch();
    }

    private sealed class NoOpDirectoryWatch : IInstanceDirectoryWatch
    {
        public event EventHandler<InstanceDirectoryChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }
}
