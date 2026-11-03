/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Media;
using System.Collections.Specialized;
using Launcher.App.Services;
using Launcher.App.ViewModels.Settings;
using Launcher.Domain.Models;
using Launcher.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Tests.ViewModels.Settings;

public sealed class CustomAccentColorTests
{
    [Theory]
    [InlineData("#12aBcD", "#12ABCD")]
    [InlineData("Blue", "Blue")]
    [InlineData("#123", "Blue")]
    [InlineData("#12345G", "Blue")]
    [InlineData("#12345678", "Blue")]
    [InlineData("123456", "Blue")]
    public void NormalizePreservesValidColorsAndRejectsInvalidInput(string input, string expected)
    {
        Assert.Equal(expected, LauncherAccentColors.Normalize(input));
    }

    [Fact]
    public async Task CustomInputRequiresConfirmationAndRestoresSavedValue()
    {
        var settings = new LauncherSettings { AccentColor = "Purple" };
        var service = new TestSettingsService(settings);
        using var persistence = new SettingsPersistenceCoordinator(service, new Status(), NullLogger.Instance);
        persistence.Prime(settings);
        var theme = new Theme();
        var viewModel = new ThemeSettingsViewModel(persistence, theme, null);
        viewModel.Load(settings);
        viewModel.SelectedAccentColorOption = viewModel.AccentColorOptions.Last();
        viewModel.CustomAccentColorText = "#123";
        Assert.Equal("Purple", settings.AccentColor);
        Assert.Null(theme.Accent);
        Assert.False(viewModel.ApplyCustomAccentColorCommand.CanExecute(null));

        viewModel.CustomAccentColorText = "#12ab34";
        Assert.Null(theme.Accent);
        Assert.Equal("Purple", settings.AccentColor);
        Assert.True(viewModel.ApplyCustomAccentColorCommand.CanExecute(null));
        viewModel.ApplyCustomAccentColorCommand.Execute(null);
        Assert.Equal("#12AB34", theme.Accent);
        Assert.False(viewModel.ApplyCustomAccentColorCommand.CanExecute(null));
        viewModel.CustomAccentColorText = "#12AB34";
        Assert.False(viewModel.ApplyCustomAccentColorCommand.CanExecute(null));
        await persistence.FlushAsync();
        Assert.Equal("#12AB34", (await service.LoadAsync()).AccentColor);

        viewModel.CustomAccentColorText = "#12ab3z";
        Assert.False(viewModel.ApplyCustomAccentColorCommand.CanExecute(null));
        Assert.Equal("#12AB34", settings.AccentColor);
        var restored = new ThemeSettingsViewModel(persistence, theme, null);
        restored.Load(settings);
        Assert.True(restored.IsCustomAccentColorVisible);
        Assert.Equal("#12AB34", restored.CustomAccentColorText);
        restored.SelectedAccentColorOption = restored.AccentColorOptions[0];
        Assert.Equal("Blue", theme.Accent);
        restored.SelectedAccentColorOption = restored.AccentColorOptions.Last();
        Assert.Equal("#12AB34", theme.Accent);
        Assert.Equal("#12AB34", settings.AccentColor);
        Assert.False(restored.ApplyCustomAccentColorCommand.CanExecute(null));

        restored.SelectedAccentColorOption = restored.AccentColorOptions[0];
        restored.CustomAccentColorText = "#invalid";
        restored.SelectedAccentColorOption = restored.AccentColorOptions.Last();
        Assert.Equal("Blue", theme.Accent);
        Assert.Equal("Blue", settings.AccentColor);
        Assert.False(restored.ApplyCustomAccentColorCommand.CanExecute(null));
    }

    [Fact]
    public void CustomPaletteSuppliesAllPresetKeysAndUpdatesAcrossThemeChanges()
    {
        var manager = new ThemeResourceLayerManager(_ => new ResourceDictionary());
        var resources = new ResourceDictionary();
        manager.ApplyLayers(resources, EffectiveTheme.Dark, "#123456", false, false);
        var palette = resources.MergedDictionaries[1];
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), ((SolidColorBrush)palette["Brush.Accent.Primary"]).Color);
        Assert.Equal(Color.FromArgb(0x66, 0x12, 0x34, 0x56), ((SolidColorBrush)palette["Brush.Input.TextBox.BorderFocused"]).Color);
        Assert.Equal(24, palette.Count);
        manager.ApplyLayers(resources, EffectiveTheme.Light, "#ABCDEF", false, false);
        palette = resources.MergedDictionaries[1];
        Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF), ((SolidColorBrush)palette["LauncherAccentBrush"]).Color);
        manager.ApplyLayers(resources, EffectiveTheme.Dark, "Blue", false, false);
        Assert.False(resources.MergedDictionaries[1].Contains("Brush.Accent.Primary"));
    }

    [Fact]
    public void ChangingAccentDoesNotRemoveTheActiveThemeOrBackgroundLayers()
    {
        var manager = new ThemeResourceLayerManager(_ => new ResourceDictionary());
        var resources = new ResourceDictionary();
        manager.ApplyLayers(resources, EffectiveTheme.Dark, "Blue", true, true);
        var theme = resources.MergedDictionaries[0];
        var image = resources.MergedDictionaries[2];
        var blur = resources.MergedDictionaries[3];
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)resources.MergedDictionaries).CollectionChanged += (_, e) => changes.Add(e);

        manager.ApplyLayers(resources, EffectiveTheme.Dark, "#123456", true, true);
        manager.ApplyLayers(resources, EffectiveTheme.Dark, "#654321", true, true);

        Assert.Same(theme, resources.MergedDictionaries[0]);
        Assert.Same(image, resources.MergedDictionaries[2]);
        Assert.Same(blur, resources.MergedDictionaries[3]);
        Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action));
        Assert.Equal(2, changes.Count);
    }

    private sealed class Status : IStatusService
    {
        public event Action<string>? MessageReported { add { } remove { } }
        public void Report(string message) { }
    }

    private sealed class Theme : IThemeService
    {
        public string? Accent { get; private set; }
        public EffectiveTheme EffectiveTheme => EffectiveTheme.Dark;
        public string BackgroundEffect => string.Empty;
        public event EventHandler<EffectiveThemeChangedEventArgs>? EffectiveThemeChanged { add { } remove { } }
        public event EventHandler<BackgroundEffectChangedEventArgs>? BackgroundEffectChanged { add { } remove { } }
        public void ApplyAccent(string? accentColor) => Accent = accentColor;
        public void ApplyPreference(string? theme, bool followSystem, int backgroundOpacityPercent) { }
        public void ApplyBackgroundOpacity(int opacityPercent) { }
        public void ApplyBackgroundEffect(string? backgroundEffect, bool enableImageControlBlur) { }
    }
}
