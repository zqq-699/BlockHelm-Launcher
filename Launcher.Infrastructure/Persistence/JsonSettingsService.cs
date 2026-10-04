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

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Persistence;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly TimeSpan BootstrapCrossProcessLockTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultCrossProcessLockRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 等待设置锁的上限。锁只在一次"读取+写入"期间被持有，正常情况下毫秒级就能拿到；
    /// 但网络盘上的陈旧 .lock 可能永远不释放，无上限的等待会把点击变成无响应，
    /// 所以超时后主动放弃，让调用方按一次保存失败处理。
    /// </summary>
    private static readonly TimeSpan DefaultCrossProcessLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string settingsPath;
    private readonly LauncherPathProvider pathProvider;
    private readonly ILogger<JsonSettingsService> logger;
    private readonly TimeSpan crossProcessLockTimeout;
    private readonly TimeSpan crossProcessLockRetryDelay;
    private readonly SemaphoreSlim ioLock = new(1, 1);
    private readonly ConditionalWeakTable<LauncherSettings, LauncherSettings> loadedBaselines = new();

    public JsonSettingsService(string? dataDirectory = null, ILogger<JsonSettingsService>? logger = null)
        : this(
            dataDirectory,
            logger,
            DefaultCrossProcessLockTimeout,
            DefaultCrossProcessLockRetryDelay)
    {
    }

    internal JsonSettingsService(
        string? dataDirectory,
        ILogger<JsonSettingsService>? logger,
        TimeSpan crossProcessLockTimeout,
        TimeSpan crossProcessLockRetryDelay)
    {
        if (crossProcessLockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(crossProcessLockTimeout));
        if (crossProcessLockRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(crossProcessLockRetryDelay));

        pathProvider = new LauncherPathProvider();
        var root = dataDirectory ?? pathProvider.DefaultDataDirectory;
        settingsPath = Path.Combine(root, "settings.json");
        this.logger = logger ?? NullLogger<JsonSettingsService>.Instance;
        this.crossProcessLockTimeout = crossProcessLockTimeout;
        this.crossProcessLockRetryDelay = crossProcessLockRetryDelay;
    }

    public string LoadLauncherLanguageForBootstrap() =>
        LoadLauncherBootstrapPreferences().LauncherLanguage;

    public LauncherBootstrapPreferences LoadLauncherBootstrapPreferences()
    {
        if (!File.Exists(settingsPath))
        {
            return new LauncherBootstrapPreferences(
                LauncherDefaults.DefaultLauncherLanguage,
                EnableDiagnosticLogging: false);
        }

        try
        {
            using var timeoutCancellation = new CancellationTokenSource(BootstrapCrossProcessLockTimeout);
            using var crossProcessLock = AcquireCrossProcessLockAsync(timeoutCancellation.Token)
                .GetAwaiter()
                .GetResult();
            using var stream = File.OpenRead(settingsPath);
            using var document = JsonDocument.Parse(stream);
            var language = document.RootElement.TryGetProperty(
                       nameof(LauncherSettings.LauncherLanguage),
                       out var languageProperty)
                   && languageProperty.ValueKind is JsonValueKind.String
                ? NormalizeLauncherLanguage(languageProperty.GetString())
                : LauncherDefaults.DefaultLauncherLanguage;
            var enableDiagnosticLogging = document.RootElement.TryGetProperty(
                    nameof(LauncherSettings.EnableDiagnosticLogging),
                    out var diagnosticLoggingProperty)
                && diagnosticLoggingProperty.ValueKind is JsonValueKind.True;
            return new LauncherBootstrapPreferences(language, enableDiagnosticLogging);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Timed out waiting for the launcher settings lock during WPF resource bootstrap. SettingsPath={SettingsPath} TimeoutMilliseconds={TimeoutMilliseconds}",
                settingsPath,
                BootstrapCrossProcessLockTimeout.TotalMilliseconds);
            return new LauncherBootstrapPreferences(
                LauncherDefaults.DefaultLauncherLanguage,
                EnableDiagnosticLogging: false);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            logger.LogWarning(
                exception,
                "Failed to read launcher language during WPF resource bootstrap. SettingsPath={SettingsPath}",
                settingsPath);
            return new LauncherBootstrapPreferences(
                LauncherDefaults.DefaultLauncherLanguage,
                EnableDiagnosticLogging: false);
        }
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadWithMetadataAsync(cancellationToken).ConfigureAwait(false)).Settings;

    public async Task<LauncherSettingsLoadResult> LoadWithMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        await ioLock.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var settingsFileExisted = File.Exists(settingsPath);
            var loadedSettings = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            TrackBaseline(loadedSettings, loadedSettings);
            logger.LogDebug(
                "Launcher settings loaded. SettingsPath={SettingsPath} WasCreated={WasCreated}",
                settingsPath,
                !settingsFileExisted);
            return new LauncherSettingsLoadResult(loadedSettings, WasCreated: !settingsFileExisted);
        }
        finally
        {
            ioLock.Release();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        await ioLock.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            LauncherSettings toSave;
            if (loadedBaselines.TryGetValue(settings, out var baseline))
            {
                ApplyChangedPersistedProperties(baseline, normalized, current);
                toSave = Normalize(current);
            }
            else
            {
                if (normalized.Revision != current.Revision)
                    throw new SettingsConcurrencyException(normalized.Revision, current.Revision);
                toSave = normalized;
            }
            toSave.Revision = checked(current.Revision + 1);
            await SaveCoreAsync(toSave, cancellationToken);
            CopyPersistedProperties(toSave, settings);
            TrackBaseline(settings, toSave);
            logger.LogDebug("Launcher settings saved. SettingsPath={SettingsPath}", settingsPath);
        }
        finally
        {
            ioLock.Release();
        }
    }

    public async Task<LauncherSettings> UpdateAsync(
        Action<LauncherSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var latest = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            update(latest);
            latest = Normalize(latest);
            latest.Revision = checked(latest.Revision + 1);
            await SaveCoreAsync(latest, cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Launcher settings updated atomically. SettingsPath={SettingsPath}", settingsPath);
            return latest;
        }
        finally
        {
            ioLock.Release();
        }
    }

    private async Task<LauncherSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            var defaultSettings = Normalize(new LauncherSettings
            {
                DataDirectory = Path.GetDirectoryName(settingsPath) ?? pathProvider.DefaultDataDirectory
            });
            await SaveCoreAsync(defaultSettings, cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Default launcher settings created. SettingsPath={SettingsPath}", settingsPath);
            return defaultSettings;
        }

        await using var stream = new FileStream(
            settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var loaded = await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return Normalize(loaded ?? new LauncherSettings());
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = settingsPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (waited.Elapsed >= crossProcessLockTimeout)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for the launcher settings lock. LockPath={lockPath} "
                        + $"TimeoutSeconds={crossProcessLockTimeout.TotalSeconds}",
                        exception);
                }

                await Task.Delay(crossProcessLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private void TrackBaseline(LauncherSettings key, LauncherSettings value)
    {
        loadedBaselines.Remove(key);
        loadedBaselines.Add(key, ClonePersistedSettings(value));
    }

    private static LauncherSettings ClonePersistedSettings(LauncherSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        return JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions) ?? new LauncherSettings();
    }

    private static void ApplyChangedPersistedProperties(
        LauncherSettings baseline,
        LauncherSettings proposed,
        LauncherSettings latest)
    {
        foreach (var property in GetPersistedProperties())
        {
            if (string.Equals(property.Name, nameof(LauncherSettings.Revision), StringComparison.Ordinal))
                continue;
            var baselineValue = property.GetValue(baseline);
            var proposedValue = property.GetValue(proposed);
            if (!PersistedValuesEqual(property.Name, baselineValue, proposedValue))
                property.SetValue(latest, proposedValue);
        }
    }

    private static bool PersistedValuesEqual(string propertyName, object? left, object? right)
    {
        if (string.Equals(propertyName, nameof(LauncherSettings.MinecraftDirectories), StringComparison.Ordinal)
            || string.Equals(
                propertyName,
                nameof(LauncherSettings.ExcludedMinecraftDirectories),
                StringComparison.Ordinal))
        {
            var leftDirectories = left as IEnumerable<string> ?? [];
            var rightDirectories = right as IEnumerable<string> ?? [];
            return leftDirectories.SequenceEqual(rightDirectories, MinecraftDirectoryPath.Comparer);
        }

        if (string.Equals(
                propertyName,
                nameof(LauncherSettings.MinecraftDirectoryDisplayNames),
                StringComparison.Ordinal))
        {
            var leftNames = left as IEnumerable<KeyValuePair<string, string>> ?? [];
            var rightNames = (right as IEnumerable<KeyValuePair<string, string>> ?? []).ToList();
            var leftNamesList = leftNames.ToList();
            return leftNamesList.Count == rightNames.Count
                   && leftNamesList.All(leftName => rightNames.Any(rightName =>
                       MinecraftDirectoryPath.Equals(leftName.Key, rightName.Key)
                       && string.Equals(leftName.Value, rightName.Value, StringComparison.Ordinal)));
        }

        return Equals(left, right);
    }

    private static void CopyPersistedProperties(LauncherSettings source, LauncherSettings destination)
    {
        foreach (var property in GetPersistedProperties())
            property.SetValue(destination, property.GetValue(source));
    }

    private static IReadOnlyList<System.Reflection.PropertyInfo> GetPersistedProperties() =>
        typeof(LauncherSettings)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(property => property.CanRead
                               && property.CanWrite
                               && property.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true).Length == 0)
            .ToArray();

    private async Task SaveCoreAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        await AtomicJsonFileWriter.WriteAsync(settingsPath, settings, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private LauncherSettings Normalize(LauncherSettings settings)
    {
        settings.Theme = NormalizeTheme(settings.Theme);
        settings.LauncherLanguage = NormalizeLauncherLanguage(settings.LauncherLanguage);
        var normalizedAccentColor = LauncherAccentColors.Normalize(settings.AccentColor);
        if (!string.IsNullOrWhiteSpace(settings.AccentColor)
            && !string.Equals(settings.AccentColor, normalizedAccentColor, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Invalid launcher accent color preference encountered in settings. AccentColor={AccentColor} FallingBackTo={FallbackAccentColor}",
                settings.AccentColor,
                normalizedAccentColor);
        }

        settings.AccentColor = normalizedAccentColor;
        var backgroundEffect = LauncherBackgroundEffects.Normalize(settings.LauncherBackgroundEffect);
        if (!string.IsNullOrWhiteSpace(settings.LauncherBackgroundEffect)
            && !string.Equals(settings.LauncherBackgroundEffect, backgroundEffect, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Invalid launcher background effect encountered in settings. BackgroundEffect={BackgroundEffect} FallingBackTo={FallbackBackgroundEffect}",
                settings.LauncherBackgroundEffect,
                backgroundEffect);
        }

        settings.LauncherBackgroundEffect = backgroundEffect;
        settings.LauncherBackgroundOpacityPercent = Math.Clamp(
            settings.LauncherBackgroundOpacityPercent,
            0,
            100);
        settings.MainWindowWidth = NormalizeWindowDimension(
            settings.MainWindowWidth,
            LauncherDefaults.DefaultMainWindowWidth,
            LauncherDefaults.MinimumMainWindowWidth);
        settings.MainWindowHeight = NormalizeWindowDimension(
            settings.MainWindowHeight,
            LauncherDefaults.DefaultMainWindowHeight,
            LauncherDefaults.MinimumMainWindowHeight);

        if (string.IsNullOrWhiteSpace(settings.DataDirectory))
            settings.DataDirectory = pathProvider.DefaultDataDirectory;

        settings.MinecraftDirectory = string.IsNullOrWhiteSpace(settings.MinecraftDirectory)
            ? MinecraftDirectoryPath.Normalize(pathProvider.DefaultMinecraftDirectory)
            : MinecraftDirectoryPath.Normalize(settings.MinecraftDirectory);
        settings.MinecraftDirectories = NormalizeMinecraftDirectories(
            settings.MinecraftDirectories,
            settings.MinecraftDirectory);
        settings.MinecraftDirectoryDisplayNames = NormalizeMinecraftDirectoryDisplayNames(
            settings.MinecraftDirectoryDisplayNames,
            settings.MinecraftDirectories);
        settings.ExcludedMinecraftDirectories = GetValidMinecraftDirectories(
                settings.ExcludedMinecraftDirectories)
            .Where(directory =>
                !settings.MinecraftDirectories.Contains(directory, MinecraftDirectoryPath.Comparer))
            .ToList();

        settings.DefaultMemoryMb = Math.Clamp(settings.DefaultMemoryMb, 1024, 32768);
        if (settings.DefaultMemorySettingsMode is not MemorySettingsMode.Auto
            && settings.DefaultMemorySettingsMode is not MemorySettingsMode.Manual)
        {
            settings.DefaultMemorySettingsMode = MemorySettingsMode.Auto;
        }

        if (settings.DownloadSourcePreference is not DownloadSourcePreference.Official
            && settings.DownloadSourcePreference is not DownloadSourcePreference.BmclApi)
        {
            settings.DownloadSourcePreference = LauncherDefaults.DefaultDownloadSourcePreference;
        }

        if (settings.DownloadSpeedLimitMbPerSecond < 0)
            settings.DownloadSpeedLimitMbPerSecond = 0;

        settings.MaximumDownloadConcurrency = Math.Clamp(
            settings.MaximumDownloadConcurrency,
            LauncherDefaults.MinimumDownloadConcurrency,
            LauncherDefaults.MaximumDownloadConcurrency);

        if (settings.UpdateChannel is not LauncherUpdateChannel.Release
            && settings.UpdateChannel is not LauncherUpdateChannel.Beta)
        {
            settings.UpdateChannel = LauncherDefaults.DefaultUpdateChannel;
        }

        if (settings.JavaSelectionMode is not JavaSelectionMode.Auto
            && settings.JavaSelectionMode is not JavaSelectionMode.Manual)
        {
            settings.JavaSelectionMode = JavaSelectionMode.Auto;
        }

        if (string.IsNullOrWhiteSpace(settings.SelectedJavaExecutablePath))
            settings.SelectedJavaExecutablePath = null;

        return settings;
    }

    private IReadOnlyList<string> GetValidMinecraftDirectories(IEnumerable<string>? directories)
    {
        var normalizedDirectories = new List<string>();
        var knownDirectories = new HashSet<string>(MinecraftDirectoryPath.Comparer);
        foreach (var directory in directories ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            try
            {
                var normalizedDirectory = MinecraftDirectoryPath.Normalize(directory);
                if (knownDirectories.Add(normalizedDirectory))
                    normalizedDirectories.Add(normalizedDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                logger.LogWarning(
                    exception,
                    "Invalid Minecraft directory encountered in launcher settings. Directory={MinecraftDirectory}",
                    directory);
            }
        }

        return normalizedDirectories;
    }

    private List<string> NormalizeMinecraftDirectories(
        IEnumerable<string>? directories,
        string currentDirectory)
    {
        var normalizedDirectories = GetValidMinecraftDirectories(directories).ToList();
        if (!normalizedDirectories.Contains(currentDirectory, MinecraftDirectoryPath.Comparer))
            normalizedDirectories.Insert(0, currentDirectory);
        return normalizedDirectories;
    }

    private Dictionary<string, string> NormalizeMinecraftDirectoryDisplayNames(
        IEnumerable<KeyValuePair<string, string>>? displayNames,
        IReadOnlyList<string> directories)
    {
        var namesByPath = new Dictionary<string, string>(MinecraftDirectoryPath.Comparer);
        foreach (var pair in displayNames ?? [])
        {
            try
            {
                namesByPath[MinecraftDirectoryPath.Normalize(pair.Key)] = pair.Value;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                logger.LogWarning(
                    exception,
                    "Invalid Minecraft directory display name path encountered in launcher settings. Directory={MinecraftDirectory}",
                    pair.Key);
            }
        }

        var normalizedNames = new Dictionary<string, string>(MinecraftDirectoryPath.Comparer);
        foreach (var directory in directories)
        {
            namesByPath.TryGetValue(directory, out var displayName);
            normalizedNames[directory] = MinecraftDirectoryDisplayName.NormalizeOrDefault(
                displayName,
                directory);
        }

        return normalizedNames;
    }

    private static double NormalizeWindowDimension(double value, double defaultValue, double minimumValue)
    {
        if (!double.IsFinite(value))
            return defaultValue;

        return Math.Max(value, minimumValue);
    }

    private static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
            return "Light";

        if (string.Equals(theme, LauncherDefaults.DefaultTheme, StringComparison.OrdinalIgnoreCase))
            return LauncherDefaults.DefaultTheme;

        return LauncherDefaults.DefaultTheme;
    }

    private static string NormalizeLauncherLanguage(string? language)
    {
        return LauncherLanguages.Normalize(language);
    }
}
