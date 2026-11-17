/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Repositories;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record LoaderInstallerJavaRuntimeRequest(
    string MinecraftVersion,
    string VersionName,
    LoaderKind Loader,
    string? LoaderVersion,
    string MinecraftDirectory,
    DownloadSourcePreference DownloadSourcePreference,
    int DownloadSpeedLimitMbPerSecond,
    IProgress<LauncherProgress>? Progress = null);

internal interface ILoaderInstallerJavaRuntimeResolver
{
    Task<JavaRuntimeInfo> ResolveAsync(
        LoaderInstallerJavaRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

internal interface ILoaderInstallerJavaRuntimeProvisioner
{
    Task ProvisionAsync(
        LoaderInstallerJavaRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

internal interface ILoaderInstallerJavaRequirementResolver
{
    Task<JavaRuntimeCompatibilityRequirement> ResolveRequirementAsync(
        LoaderInstallerJavaRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a validated Java runtime for Forge-like installers, provisioning one only when
/// neither the configured runtime nor another discovered runtime satisfies the game metadata.
/// </summary>
internal sealed class LoaderInstallerJavaRuntimeResolver : ILoaderInstallerJavaRuntimeResolver
{
    private readonly Func<CancellationToken, Task<LauncherSettings>> loadSettingsAsync;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<GameInstance>>> loadInstancesAsync;
    private readonly IJavaRuntimeSelectionService javaRuntimeSelectionService;
    private readonly IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService;
    private readonly ILoaderInstallerJavaRequirementResolver requirementResolver;
    private readonly ILoaderInstallerJavaRuntimeProvisioner runtimeProvisioner;
    private readonly ILogger logger;

    public LoaderInstallerJavaRuntimeResolver(
        ISettingsService settingsService,
        IGameInstanceRepository instanceRepository,
        IJavaRuntimeSelectionService javaRuntimeSelectionService,
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        ILoaderInstallerJavaRequirementResolver requirementResolver,
        ILoaderInstallerJavaRuntimeProvisioner runtimeProvisioner,
        ILogger<LoaderInstallerJavaRuntimeResolver>? logger = null)
        : this(
            settingsService.LoadAsync,
            instanceRepository.GetAllAsync,
            javaRuntimeSelectionService,
            javaRuntimeDiscoveryService,
            requirementResolver,
            runtimeProvisioner,
            logger)
    {
    }

    internal LoaderInstallerJavaRuntimeResolver(
        Func<CancellationToken, Task<LauncherSettings>> loadSettingsAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<GameInstance>>> loadInstancesAsync,
        IJavaRuntimeSelectionService javaRuntimeSelectionService,
        IJavaRuntimeDiscoveryService javaRuntimeDiscoveryService,
        ILoaderInstallerJavaRequirementResolver requirementResolver,
        ILoaderInstallerJavaRuntimeProvisioner runtimeProvisioner,
        ILogger? logger = null)
    {
        this.loadSettingsAsync = loadSettingsAsync;
        this.loadInstancesAsync = loadInstancesAsync;
        this.javaRuntimeSelectionService = javaRuntimeSelectionService;
        this.javaRuntimeDiscoveryService = javaRuntimeDiscoveryService;
        this.requirementResolver = requirementResolver;
        this.runtimeProvisioner = runtimeProvisioner;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<JavaRuntimeInfo> ResolveAsync(
        LoaderInstallerJavaRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftDirectory);

        var loadedSettings = await loadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var settings = CreateSelectionSettings(loadedSettings, request.MinecraftDirectory);
        var instances = await loadInstancesAsync(request.MinecraftDirectory, cancellationToken)
            .ConfigureAwait(false);
        var storedInstance = instances.FirstOrDefault(instance =>
                string.Equals(instance.VersionName, request.VersionName, StringComparison.OrdinalIgnoreCase))
            ?? instances.FirstOrDefault(instance =>
                string.Equals(instance.Name, request.VersionName, StringComparison.OrdinalIgnoreCase));
        var selectionInstance = CreateSelectionInstance(storedInstance, request);
        var requirement = await requirementResolver
            .ResolveRequirementAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var requiredMajorVersion = requirement.RecommendedMajorVersion;

        var effectiveSelection = JavaRuntimeSelectionService.ResolveEffectiveSelection(selectionInstance, settings);
        var configuredRuntime = effectiveSelection.Mode is JavaSelectionMode.Manual
            ? await TrySelectConfiguredRuntimeAsync(
                selectionInstance,
                settings,
                requirement,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (configuredRuntime is not null)
            return LogResolvedRuntime(configuredRuntime, request, storedInstance is not null, requirement, provisioned: false);

        var discoveredRuntime = await DiscoverCompatibleRuntimeAsync(
            request.MinecraftDirectory,
            requirement,
            cancellationToken).ConfigureAwait(false);
        if (discoveredRuntime is not null)
            return LogResolvedRuntime(discoveredRuntime, request, storedInstance is not null, requirement, provisioned: false);

        request.Progress?.Report(new LauncherProgress(InstallProgressStages.DownloadingJava, string.Empty));
        logger.LogInformation(
            "No compatible Java runtime was found for loader installer; provisioning Java. VersionName={VersionName} MinecraftVersion={MinecraftVersion} Loader={Loader} LoaderVersion={LoaderVersion} JavaRequirement={JavaRequirement} MinecraftDirectory={MinecraftDirectory}",
            request.VersionName,
            request.MinecraftVersion,
            request.Loader,
            request.LoaderVersion,
            requirement,
            request.MinecraftDirectory);
        try
        {
            await runtimeProvisioner.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new JavaRuntimeSelectionException(
                requiredMajorVersion is int required
                    ? $"Unable to prepare the required Java {required} runtime."
                    : "Unable to prepare a Java runtime for the loader installer.",
                exception,
                JavaRuntimeSelectionFailureReason.AutomaticRuntimeMissing,
                requiredMajorVersion);
        }

        var provisionedRuntime = await DiscoverCompatibleRuntimeAsync(
            request.MinecraftDirectory,
            requirement,
            cancellationToken).ConfigureAwait(false);
        if (provisionedRuntime is null)
        {
            throw new JavaRuntimeSelectionException(
                requiredMajorVersion is int required
                    ? $"Java provisioning completed, but no Java {required} or newer runtime was found."
                    : "Java provisioning completed, but no usable Java runtime was found.",
                JavaRuntimeSelectionFailureReason.AutomaticRuntimeNotFound,
                requiredMajorVersion);
        }

        return LogResolvedRuntime(provisionedRuntime, request, storedInstance is not null, requirement, provisioned: true);
    }

    private async Task<JavaRuntimeInfo?> TrySelectConfiguredRuntimeAsync(
        GameInstance instance,
        LauncherSettings settings,
        JavaRuntimeCompatibilityRequirement requirement,
        CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await javaRuntimeSelectionService
                .SelectForLaunchAsync(instance, settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (requirement.IsCompatible(runtime))
                return runtime;

            logger.LogDebug(
                "Configured Java runtime incompatibility details. JavaPath={JavaPath} JavaVersion={JavaVersion} JavaRequirement={JavaRequirement}",
                runtime.ExecutablePath,
                runtime.Version,
                requirement);
            logger.LogWarning(
                "Configured Java runtime is incompatible with the loader installer. JavaVersion={JavaVersion} JavaRequirement={JavaRequirement}",
                runtime.Version,
                requirement);
        }
        catch (JavaRuntimeSelectionException exception)
        {
            logger.LogDebug(
                exception,
                "Configured Java runtime selection failed for loader installer. JavaRequirement={JavaRequirement}",
                requirement);
            logger.LogWarning(
                "Configured Java runtime could not be used for the loader installer; another compatible runtime will be selected. JavaRequirement={JavaRequirement}",
                requirement);
        }

        return null;
    }

    private async Task<JavaRuntimeInfo?> DiscoverCompatibleRuntimeAsync(
        string minecraftDirectory,
        JavaRuntimeCompatibilityRequirement requirement,
        CancellationToken cancellationToken)
    {
        var runtimes = await javaRuntimeDiscoveryService
            .DiscoverAsync(minecraftDirectory, cancellationToken)
            .ConfigureAwait(false);
        return JavaRuntimeSelectionService.SelectBestRuntime(runtimes, requirement);
    }

    private JavaRuntimeInfo LogResolvedRuntime(
        JavaRuntimeInfo runtime,
        LoaderInstallerJavaRuntimeRequest request,
        bool usedStoredInstance,
        JavaRuntimeCompatibilityRequirement requirement,
        bool provisioned)
    {
        logger.LogInformation(
            "Resolved Java runtime for loader installer. VersionName={VersionName} MinecraftVersion={MinecraftVersion} Loader={Loader} LoaderVersion={LoaderVersion} UsedStoredInstance={UsedStoredInstance} JavaRequirement={JavaRequirement} Provisioned={Provisioned} JavaPath={JavaPath} JavaVersion={JavaVersion} JavaArchitecture={JavaArchitecture} JavaSource={JavaSource}",
            request.VersionName,
            request.MinecraftVersion,
            request.Loader,
            request.LoaderVersion,
            usedStoredInstance,
            requirement,
            provisioned,
            runtime.ExecutablePath,
            runtime.Version,
            runtime.Architecture,
            runtime.Source);
        return runtime;
    }

    private static LauncherSettings CreateSelectionSettings(LauncherSettings source, string minecraftDirectory)
    {
        return new LauncherSettings
        {
            MinecraftDirectory = minecraftDirectory,
            JavaSelectionMode = source.JavaSelectionMode,
            SelectedJavaExecutablePath = source.SelectedJavaExecutablePath
        };
    }

    private static GameInstance CreateSelectionInstance(
        GameInstance? storedInstance,
        LoaderInstallerJavaRuntimeRequest request)
    {
        if (storedInstance is null)
        {
            return new GameInstance
            {
                MinecraftVersion = request.MinecraftVersion,
                VersionName = request.VersionName,
                Loader = request.Loader,
                LoaderVersion = request.LoaderVersion,
                JavaSettingsMode = LaunchSettingsMode.UseGlobal
            };
        }

        return new GameInstance
        {
            Name = storedInstance.Name,
            MinecraftVersion = string.IsNullOrWhiteSpace(storedInstance.MinecraftVersion)
                ? request.MinecraftVersion
                : storedInstance.MinecraftVersion,
            VersionName = string.IsNullOrWhiteSpace(storedInstance.VersionName)
                ? request.VersionName
                : storedInstance.VersionName,
            Loader = request.Loader,
            LoaderVersion = request.LoaderVersion,
            InstanceDirectory = storedInstance.InstanceDirectory,
            JavaSettingsMode = storedInstance.JavaSettingsMode,
            JavaSelectionMode = storedInstance.JavaSelectionMode,
            SelectedJavaExecutablePath = storedInstance.SelectedJavaExecutablePath
        };
    }
}
