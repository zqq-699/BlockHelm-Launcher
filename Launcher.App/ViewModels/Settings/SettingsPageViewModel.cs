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
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Diagnostics;
using Launcher.App.Logging;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.Shell;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Settings;

public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly SettingsPersistenceCoordinator persistence;
    private readonly bool ownsPersistence;

    [ObservableProperty]
    private SettingsSectionItem? selectedSection;

    [ObservableProperty]
    private SettingsSectionViewModelBase? currentSectionViewModel;

    public SettingsPageViewModel(
        ISettingsService settingsService,
        IStatusService statusService,
        ISystemMemoryService systemMemoryService,
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        IMinecraftDirectoryFileSystem minecraftDirectoryFileSystem,
        MinecraftDirectoryManagementService minecraftDirectoryManagementService,
        IFilePickerService filePickerService,
        ICustomFileDownloadService customFileDownloadService,
        IInstanceFolderService instanceFolderService,
        IFloatingMessageService floatingMessageService,
        IThemeService themeService,
        IExternalLinkService externalLinkService,
        ILauncherUpdateService launcherUpdateService,
        ILauncherSelfUpdateService launcherSelfUpdateService,
        IApplicationExitService applicationExitService,
        IInfoReferenceProjectCatalog infoReferenceProjectCatalog,
        ILogger<SettingsFeedbackDialogViewModel>? feedbackDialogLogger = null,
        ILogger<InfoSettingsViewModel>? infoSettingsLogger = null,
        ILogger<SettingsPageViewModel>? logger = null,
        ILogger<CustomFileDownloadViewModel>? customFileDownloadLogger = null,
        DownloadTasksPageViewModel? downloadTasksPage = null,
        ILauncherLogLevelController? logLevelController = null,
        LauncherBackgroundViewModel? launcherBackground = null,
        SettingsPersistenceCoordinator? settingsPersistence = null)
    {
        var resolvedLogger = logger ?? NullLogger<SettingsPageViewModel>.Instance;
        persistence = settingsPersistence
            ?? new SettingsPersistenceCoordinator(settingsService, statusService, resolvedLogger);
        ownsPersistence = settingsPersistence is null;

        General = new GeneralSettingsViewModel(
            persistence,
            statusService,
            filePickerService,
            instanceFolderService,
            minecraftDirectoryFileSystem,
            minecraftDirectoryManagementService,
            downloadTasksPage,
            logLevelController,
            resolvedLogger);
        var resolvedDownloadTasksPage = downloadTasksPage ?? new DownloadTasksPageViewModel();
        Download = new DownloadSettingsViewModel(
            persistence,
            new CustomFileDownloadViewModel(
                customFileDownloadService,
                filePickerService,
                floatingMessageService,
                resolvedDownloadTasksPage,
                customFileDownloadLogger));
        Language = new LanguageSettingsViewModel(persistence);
        LaunchMemory = new LaunchMemorySettingsViewModel(persistence, systemMemoryService);
        Java = new JavaSettingsViewModel(
            persistence,
            javaRuntimeDiscoveryService,
            statusService,
            filePickerService,
            floatingMessageService,
            () => General.MinecraftDirectory);
        Theme = new ThemeSettingsViewModel(
            persistence,
            themeService,
            launcherBackground);
        Feedback = new SettingsFeedbackDialogViewModel(
            statusService,
            floatingMessageService,
            externalLinkService,
            feedbackDialogLogger);
        Info = new InfoSettingsViewModel(
            persistence,
            statusService,
            floatingMessageService,
            externalLinkService,
            launcherUpdateService,
            launcherSelfUpdateService,
            applicationExitService,
            infoReferenceProjectCatalog,
            infoSettingsLogger);
        ControlList = new ControlListSettingsViewModel(persistence);

        Download.DownloadSourceChanged += (_, args) => DownloadSourceChanged?.Invoke(this, args);
        Download.MaximumDownloadConcurrencyChanged += (_, args) =>
            MaximumDownloadConcurrencyChanged?.Invoke(this, args);
        Download.DownloadSpeedLimitChanged += (_, args) => DownloadSpeedLimitChanged?.Invoke(this, args);
        General.MinecraftDirectoryChanged += (_, args) => MinecraftDirectoryChanged?.Invoke(this, args);
        LaunchMemory.LaunchDefaultsChanged += (_, _) => LaunchDefaultsChanged?.Invoke(this, EventArgs.Empty);
        Java.LaunchDefaultsChanged += (_, _) => LaunchDefaultsChanged?.Invoke(this, EventArgs.Empty);

        Sections =
        [
            new(SettingsPageSection.General, Strings.Settings_SectionGeneral, "instance_setting_page/general_setting"),
            new(SettingsPageSection.Download, Strings.Settings_SectionDownload, "setting_page/download"),
            new(SettingsPageSection.Language, Strings.Settings_SectionLanguage, "setting_page/earth"),
            new(SettingsPageSection.LaunchMemory, Strings.Settings_SectionLaunchMemory, "instance_setting_page/launch"),
            new(SettingsPageSection.Java, Strings.Settings_SectionJava, "instance_setting_page/java"),
            new(SettingsPageSection.Theme, Strings.Settings_SectionTheme, "setting_page/theme"),
            new(SettingsPageSection.Info, Strings.Settings_SectionInfo, "setting_page/info"),
            new(SettingsPageSection.Feedback, Strings.Settings_SectionFeedback, "setting_page/message")
        ];
        SelectedSection = Sections[0];
    }

    public ObservableCollection<SettingsSectionItem> Sections { get; }
    internal SettingsPersistenceCoordinator Persistence => persistence;
    public GeneralSettingsViewModel General { get; }
    public DownloadSettingsViewModel Download { get; }
    public LanguageSettingsViewModel Language { get; }
    public LaunchMemorySettingsViewModel LaunchMemory { get; }
    public JavaSettingsViewModel Java { get; }
    public ThemeSettingsViewModel Theme { get; }
    public SettingsFeedbackDialogViewModel Feedback { get; }
    public InfoSettingsViewModel Info { get; }
    public ControlListSettingsViewModel ControlList { get; }

    public event EventHandler? LaunchDefaultsChanged;
    public event EventHandler<SettingsDownloadSourceChangedEventArgs>? DownloadSourceChanged;
    public event EventHandler<SettingsMaximumDownloadConcurrencyChangedEventArgs>? MaximumDownloadConcurrencyChanged;
    public event EventHandler<SettingsDownloadSpeedLimitChangedEventArgs>? DownloadSpeedLimitChanged;
    public event EventHandler<SettingsMinecraftDirectoryChangedEventArgs>? MinecraftDirectoryChanged;

    public string SectionTitle => SelectedSection?.Title ?? Strings.Settings_SectionGeneral;
    public bool IsGeneralSection => SelectedSection?.Section is SettingsPageSection.General;
    public bool IsDownloadSection => SelectedSection?.Section is SettingsPageSection.Download;
    public bool IsLanguageSection => SelectedSection?.Section is SettingsPageSection.Language;
    public bool IsLaunchMemorySection => SelectedSection?.Section is SettingsPageSection.LaunchMemory;
    public bool IsJavaSection => SelectedSection?.Section is SettingsPageSection.Java;
    public bool IsThemeSection => SelectedSection?.Section is SettingsPageSection.Theme;
    public bool IsInfoSection => SelectedSection?.Section is SettingsPageSection.Info;
    public bool UsesVirtualizedScrollHost => SelectedSection?.Section is SettingsPageSection.Java or SettingsPageSection.Info;

    public void PrimeFromSettings(LauncherSettings settings)
    {
        persistence.Prime(settings);
        General.Load(settings);
        Download.Load(settings);
        Language.Load(settings);
        LaunchMemory.Load(settings);
        Java.Load(settings);
        Theme.Load(settings);
        Info.Load(settings);
    }

    public void ShowJavaSection()
    {
        SelectSectionCore(Sections.FirstOrDefault(section => section.Section is SettingsPageSection.Java));
    }

    public Task FlushPendingSettingsAsync(CancellationToken cancellationToken = default) =>
        persistence.FlushAsync(cancellationToken);

    public void Dispose()
    {
        General.Dispose();
        if (ownsPersistence)
            persistence.Dispose();
    }

    [RelayCommand]
    private void SelectSection(SettingsSectionItem? section)
    {
        SelectSectionCore(section);
    }

    private void SelectSectionCore(SettingsSectionItem? section)
    {
        if (section is null)
            return;

        if (section.Section is SettingsPageSection.Feedback)
        {
            Feedback.Open();
            return;
        }

        if (!UiPerformanceLog.IsEnabled || ReferenceEquals(SelectedSection, section))
        {
            SelectedSection = section;
            return;
        }

        var previous = SelectedSection?.Section;
        var startedAt = Stopwatch.GetTimestamp();
        SelectedSection = section;
        Serilog.Log.Debug(
            "Settings section selection measured. From={FromSection} To={ToSection} SelectionMs={SelectionMs:F1}",
            previous, section.Section, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    partial void OnSelectedSectionChanged(SettingsSectionItem? value)
    {
        foreach (var section in Sections)
            section.IsSelected = ReferenceEquals(section, value);

        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(IsDownloadSection));
        OnPropertyChanged(nameof(IsLanguageSection));
        OnPropertyChanged(nameof(IsLaunchMemorySection));
        OnPropertyChanged(nameof(IsJavaSection));
        OnPropertyChanged(nameof(IsThemeSection));
        OnPropertyChanged(nameof(IsInfoSection));
        OnPropertyChanged(nameof(UsesVirtualizedScrollHost));
        CurrentSectionViewModel = value?.Section switch
        {
            SettingsPageSection.General => General,
            SettingsPageSection.Download => Download,
            SettingsPageSection.Language => Language,
            SettingsPageSection.LaunchMemory => LaunchMemory,
            SettingsPageSection.Java => Java,
            SettingsPageSection.Theme => Theme,
            SettingsPageSection.Info => Info,
            SettingsPageSection.ControlList => ControlList,
            _ => General
        };
    }
}
