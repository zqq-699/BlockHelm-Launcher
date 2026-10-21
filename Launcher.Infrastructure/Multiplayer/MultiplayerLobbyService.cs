/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Multiplayer;

internal sealed class MultiplayerLobbyService : IMultiplayerLobbyService
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string TerracottaLockPath = Path.Combine(
        Path.GetTempPath(),
        "terracotta",
        "terracotta.lock");

    private readonly ITerracottaProvisioningService provisioningService;
    private readonly HttpClient httpClient;
    private readonly ILogger<MultiplayerLobbyService> logger;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private LobbyRuntime? runtime;
    private MultiplayerLobbySnapshot? current;

    public MultiplayerLobbyService(
        ITerracottaProvisioningService provisioningService,
        ILogger<MultiplayerLobbyService>? logger = null)
        : this(
            provisioningService,
            LauncherHttpUserAgent.Apply(new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            })),
            logger)
    {
    }

    internal MultiplayerLobbyService(
        ITerracottaProvisioningService provisioningService,
        HttpClient httpClient,
        ILogger<MultiplayerLobbyService>? logger = null)
    {
        this.provisioningService = provisioningService;
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.logger = logger ?? NullLogger<MultiplayerLobbyService>.Instance;
    }

    public MultiplayerLobbySnapshot? Current => Volatile.Read(ref current);

    public event Action<MultiplayerLobbySnapshot>? SnapshotChanged;

    public event Action<MultiplayerLobbyStopped>? Stopped;

    public async Task<MultiplayerLobbySnapshot> CreateHostAsync(
        string hostName,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime is not null)
            {
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.TerracottaBusy,
                    "A multiplayer lobby is already active.");
            }

            TerracottaModule module;
            try
            {
                module = provisioningService.TryGetAvailable()
                    ?? await provisioningService.EnsureAvailableAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.TerracottaUnavailable,
                    "The Terracotta module is unavailable.",
                    exception);
            }

            var normalizedHostName = NormalizeProfileText(hostName, "Player", 64);
            LobbyRuntime? createdRuntime = null;
            try
            {
                var endpoint = await GetOrStartEndpointAsync(module, cancellationToken).ConfigureAwait(false);
                createdRuntime = new LobbyRuntime(endpoint.Port, endpoint.OwnedProcess, LobbyRole.Host);
                var initialState = await GetStateAsync(endpoint.Port, cancellationToken).ConfigureAwait(false);
                if (initialState.Kind is not TerracottaStateKind.Waiting)
                {
                    throw new MultiplayerLobbyCreationException(
                        MultiplayerLobbyCreationFailure.TerracottaBusy,
                        "Another launcher is already using Terracotta.");
                }

                runtime = createdRuntime;
                PublishSnapshot(new MultiplayerLobbySnapshot(
                    string.Empty,
                    MultiplayerLobbyState.Creating,
                    []));
                await SendCommandAsync(
                    endpoint.Port,
                    BuildScanPath(normalizedHostName),
                    cancellationToken).ConfigureAwait(false);
                createdRuntime.ControllerStateStarted = true;

                var active = await WaitForHostAsync(createdRuntime, cancellationToken).ConfigureAwait(false);
                PublishSnapshot(active);
                createdRuntime.MonitorTask = MonitorRuntimeAsync(createdRuntime);
                logger.LogInformation(
                    "Terracotta multiplayer lobby created. Version={Version} PlayerCount={PlayerCount} OwnsProcess={OwnsProcess}",
                    module.Version,
                    active.Players.Count,
                    endpoint.OwnedProcess is not null);
                return active;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CleanupFailedCreationAsync(createdRuntime).ConfigureAwait(false);
                throw;
            }
            catch (MultiplayerLobbyCreationException)
            {
                await CleanupFailedCreationAsync(createdRuntime).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await CleanupFailedCreationAsync(createdRuntime).ConfigureAwait(false);
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.TerracottaStartupFailed,
                    "Terracotta could not create the lobby.",
                    exception);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<MultiplayerLobbySnapshot> JoinAsync(
        string roomCode,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime is not null)
            {
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.TerracottaBusy,
                    "A multiplayer lobby is already active.");
            }

            var normalizedRoomCode = NormalizeProfileText(roomCode, string.Empty, 256);
            if (normalizedRoomCode.Length == 0)
            {
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.InvalidRoomCode,
                    "The Terracotta room code is empty.");
            }

            TerracottaModule module;
            try
            {
                module = provisioningService.TryGetAvailable()
                    ?? await provisioningService.EnsureAvailableAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.TerracottaUnavailable,
                    "The Terracotta module is unavailable.",
                    exception);
            }

            var normalizedPlayerName = NormalizeProfileText(playerName, "Player", 64);
            LobbyRuntime? createdRuntime = null;
            try
            {
                var endpoint = await GetOrStartEndpointAsync(module, cancellationToken).ConfigureAwait(false);
                createdRuntime = new LobbyRuntime(endpoint.Port, endpoint.OwnedProcess, LobbyRole.Guest);
                var initialState = await GetStateAsync(endpoint.Port, cancellationToken).ConfigureAwait(false);
                if (initialState.Kind is not TerracottaStateKind.Waiting)
                {
                    throw new MultiplayerLobbyCreationException(
                        MultiplayerLobbyCreationFailure.TerracottaBusy,
                        "Another launcher is already using Terracotta.");
                }

                runtime = createdRuntime;
                PublishSnapshot(new MultiplayerLobbySnapshot(
                    normalizedRoomCode,
                    MultiplayerLobbyState.Joining,
                    []));
                await SendGuestCommandAsync(
                    endpoint.Port,
                    BuildGuestPath(normalizedRoomCode, normalizedPlayerName),
                    cancellationToken).ConfigureAwait(false);
                createdRuntime.ControllerStateStarted = true;

                var active = await WaitForGuestAsync(
                    createdRuntime,
                    normalizedRoomCode,
                    cancellationToken).ConfigureAwait(false);
                PublishSnapshot(active);
                createdRuntime.MonitorTask = MonitorRuntimeAsync(createdRuntime);
                logger.LogInformation(
                    "Terracotta multiplayer lobby joined. Version={Version} PlayerCount={PlayerCount} OwnsProcess={OwnsProcess}",
                    module.Version,
                    active.Players.Count,
                    endpoint.OwnedProcess is not null);
                return active;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CleanupFailedCreationAsync(createdRuntime).ConfigureAwait(false);
                throw;
            }
            catch (MultiplayerLobbyCreationException)
            {
                await CleanupFailedCreationAsync(createdRuntime).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await CleanupFailedCreationAsync(createdRuntime).ConfigureAwait(false);
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.RoomConnectionFailed,
                    "Terracotta could not join the lobby.",
                    exception);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Read(ref runtime)?.RequestStop();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeRuntime = runtime;
            if (activeRuntime is null)
                return;

            runtime = null;
            var snapshot = Current;
            if (snapshot is not null)
                PublishSnapshot(snapshot with { State = MultiplayerLobbyState.Stopping });
            await StopRuntimeAsync(activeRuntime, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref current, null);
            logger.LogInformation("Terracotta multiplayer lobby stopped by the user.");
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<TerracottaEndpoint> GetOrStartEndpointAsync(
        TerracottaModule module,
        CancellationToken cancellationToken)
    {
        var existingPort = TryReadExistingPort();
        if (existingPort is { } port
            && await TryValidateMetaAsync(port, module, requireExactVersion: false, cancellationToken)
                .ConfigureAwait(false))
        {
            logger.LogInformation("Using an existing Terracotta service instance.");
            return new TerracottaEndpoint(port, null);
        }

        var handoffPath = Path.Combine(
            Path.GetTempPath(),
            $"blockhelm-terracotta-{Guid.NewGuid():N}.json");
        Process? process = null;
        try
        {
            process = StartTerracotta(module, handoffPath);
            var endpointPort = await WaitForHandoffAsync(process, handoffPath, cancellationToken)
                .ConfigureAwait(false);
            var ownsProcess = await ClassifyHandoffProcessOwnershipAsync(
                () => process.HasExited,
                token => process.WaitForExitAsync(token),
                TimeSpan.FromMilliseconds(750),
                cancellationToken).ConfigureAwait(false);
            if (!await TryValidateMetaAsync(
                    endpointPort,
                    module,
                    requireExactVersion: ownsProcess,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new MultiplayerLobbyCreationException(
                    MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                    "The Terracotta service is incompatible.");
            }

            if (!ownsProcess)
            {
                process.Dispose();
                process = null;
                logger.LogInformation("Terracotta delegated to an existing service instance.");
            }
            else
            {
                logger.LogInformation("Terracotta service process started. ProcessId={ProcessId}", process.Id);
            }

            return new TerracottaEndpoint(endpointPort, process);
        }
        catch
        {
            if (process is not null)
                await StopOwnedProcessAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDeleteHandoffFile(handoffPath);
            TryDeleteHandoffFile(handoffPath + ".tmp");
        }
    }

    private Process StartTerracotta(TerracottaModule module, string handoffPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = module.ExecutablePath,
            WorkingDirectory = module.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--hmcl2");
        startInfo.ArgumentList.Add(handoffPath);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Terracotta did not start.");
        }

        _ = DrainOutputAsync(process.StandardOutput);
        _ = DrainOutputAsync(process.StandardError);
        return process;
    }

    private static async Task<int> WaitForHandoffAsync(
        Process process,
        string handoffPath,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandoffTimeout);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (File.Exists(handoffPath))
                {
                    string json;
                    try
                    {
                        json = await File.ReadAllTextAsync(handoffPath, timeout.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                        continue;
                    }

                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("port", out var portElement)
                        && portElement.TryGetInt32(out var port)
                        && port is > 0 and <= 65535)
                    {
                        return port;
                    }
                    throw new InvalidDataException("The Terracotta port handoff is invalid.");
                }

                if (process.HasExited)
                    throw new InvalidOperationException("Terracotta exited before publishing its HTTP port.");
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Terracotta did not publish its HTTP port in time.");
        }
    }

    internal static async Task<bool> ClassifyHandoffProcessOwnershipAsync(
        Func<bool> hasExited,
        Func<CancellationToken, Task> waitForExitAsync,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        if (hasExited())
            return false;
        try
        {
            await waitForExitAsync(cancellationToken)
                .WaitAsync(gracePeriod, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
        catch (TimeoutException)
        {
            return !hasExited();
        }
    }

    private async Task<bool> TryValidateMetaAsync(
        int port,
        TerracottaModule module,
        bool requireExactVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonAsync(port, "/meta", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;
            var targetOs = root.TryGetProperty("target_os", out var osElement)
                ? osElement.GetString()
                : null;
            var targetArchitecture = root.TryGetProperty("target_arch", out var archElement)
                ? archElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(version)
                || !string.Equals(targetOs, "windows", StringComparison.OrdinalIgnoreCase)
                || !ArchitectureMatches(module.Architecture, targetArchitecture))
            {
                return false;
            }

            return !requireExactVersion
                || string.Equals(version, module.Version, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or JsonException
            or InvalidDataException
            or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug(exception, "Terracotta metadata validation failed.");
            return false;
        }
    }

    private async Task<MultiplayerLobbySnapshot> WaitForHostAsync(
        LobbyRuntime activeRuntime,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            activeRuntime.Shutdown.Token);
        timeout.CancelAfter(StartupTimeout);
        var lastState = TerracottaStateKind.HostScanning;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                EnsureOwnedProcessRunning(activeRuntime);
                var state = await GetStateAsync(activeRuntime.Port, timeout.Token).ConfigureAwait(false);
                lastState = state.Kind;
                switch (state.Kind)
                {
                    case TerracottaStateKind.HostOk:
                        if (string.IsNullOrWhiteSpace(state.RoomCode))
                        {
                            throw new MultiplayerLobbyCreationException(
                                MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                                "Terracotta returned an invalid room code.");
                        }
                        return new MultiplayerLobbySnapshot(
                            state.RoomCode,
                            MultiplayerLobbyState.Active,
                            state.Players);
                    case TerracottaStateKind.Exception:
                        throw MapCreationException(state.ExceptionType);
                    case TerracottaStateKind.Waiting:
                        throw new MultiplayerLobbyCreationException(
                            MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                            "Terracotta returned to the waiting state during creation.");
                    case TerracottaStateKind.GuestConnecting:
                    case TerracottaStateKind.GuestStarting:
                    case TerracottaStateKind.GuestOk:
                    case TerracottaStateKind.Other:
                        throw new MultiplayerLobbyCreationException(
                            MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                            "Terracotta returned an incompatible host state.");
                }

                await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (activeRuntime.Shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MultiplayerLobbyCreationException(
                lastState is TerracottaStateKind.HostScanning
                    ? MultiplayerLobbyCreationFailure.MinecraftWorldUnavailable
                    : MultiplayerLobbyCreationFailure.TerracottaStartupFailed,
                "Terracotta did not create the lobby before the startup timeout.");
        }
    }

    private async Task<MultiplayerLobbySnapshot> WaitForGuestAsync(
        LobbyRuntime activeRuntime,
        string roomCode,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            activeRuntime.Shutdown.Token);
        timeout.CancelAfter(StartupTimeout);
        var canonicalRoomCode = roomCode;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                EnsureOwnedProcessRunning(activeRuntime);
                var state = await GetStateAsync(activeRuntime.Port, timeout.Token).ConfigureAwait(false);
                if (state.Kind is TerracottaStateKind.GuestConnecting or TerracottaStateKind.GuestStarting
                    && !string.IsNullOrWhiteSpace(state.RoomCode))
                {
                    canonicalRoomCode = state.RoomCode;
                }
                switch (state.Kind)
                {
                    case TerracottaStateKind.GuestOk:
                        return new MultiplayerLobbySnapshot(
                            canonicalRoomCode,
                            MultiplayerLobbyState.Active,
                            state.Players);
                    case TerracottaStateKind.Exception:
                        throw MapJoinException(state.ExceptionType);
                    case TerracottaStateKind.Waiting:
                        throw new MultiplayerLobbyCreationException(
                            MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                            "Terracotta returned to the waiting state while joining.");
                    case TerracottaStateKind.HostScanning:
                    case TerracottaStateKind.HostStarting:
                    case TerracottaStateKind.HostOk:
                    case TerracottaStateKind.Other:
                        throw new MultiplayerLobbyCreationException(
                            MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                            "Terracotta returned an incompatible guest state.");
                }

                await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (activeRuntime.Shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.RoomConnectionFailed,
                "Terracotta did not join the lobby before the startup timeout.");
        }
    }

    private async Task MonitorRuntimeAsync(LobbyRuntime activeRuntime)
    {
        string? lastSignature = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(activeRuntime.Shutdown.Token).ConfigureAwait(false))
            {
                EnsureOwnedProcessRunning(activeRuntime);
                var state = await GetStateAsync(activeRuntime.Port, activeRuntime.Shutdown.Token)
                    .ConfigureAwait(false);
                if (state.Kind is TerracottaStateKind.Exception)
                {
                    await StopUnexpectedlyAsync(
                        activeRuntime,
                        MapStopReason(state.ExceptionType),
                        null).ConfigureAwait(false);
                    return;
                }

                var expectedState = activeRuntime.Role is LobbyRole.Host
                    ? TerracottaStateKind.HostOk
                    : TerracottaStateKind.GuestOk;
                if (state.Kind != expectedState)
                {
                    await StopUnexpectedlyAsync(
                        activeRuntime,
                        MultiplayerLobbyStopReason.TerracottaServiceFailed,
                        null).ConfigureAwait(false);
                    return;
                }

                var signature = string.Join('\n', state.Players.Select(player =>
                    $"{player.MachineId}\u001f{player.DisplayName}\u001f{player.Kind}"));
                if (string.Equals(signature, lastSignature, StringComparison.Ordinal))
                    continue;
                lastSignature = signature;
                var snapshot = Current;
                if (snapshot is not null && ReferenceEquals(runtime, activeRuntime))
                    PublishSnapshot(snapshot with { Players = state.Players });
            }
        }
        catch (OperationCanceledException) when (activeRuntime.Shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await StopUnexpectedlyAsync(
                activeRuntime,
                activeRuntime.OwnedProcess?.HasExited is true
                    ? MultiplayerLobbyStopReason.TerracottaExited
                    : MultiplayerLobbyStopReason.TerracottaServiceFailed,
                exception).ConfigureAwait(false);
        }
    }

    private async Task StopUnexpectedlyAsync(
        LobbyRuntime stoppedRuntime,
        MultiplayerLobbyStopReason reason,
        Exception? exception)
    {
        await operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(runtime, stoppedRuntime))
                return;
            runtime = null;
            await StopRuntimeAsync(stoppedRuntime, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref current, null);
        }
        finally
        {
            operationLock.Release();
        }

        logger.LogWarning(exception,
            "Terracotta multiplayer lobby stopped unexpectedly. Reason={Reason}",
            reason);
        Stopped?.Invoke(new MultiplayerLobbyStopped(reason, exception));
    }

    private async Task CleanupFailedCreationAsync(LobbyRuntime? createdRuntime)
    {
        if (ReferenceEquals(runtime, createdRuntime))
            runtime = null;
        Volatile.Write(ref current, null);
        if (createdRuntime is not null)
            await StopRuntimeAsync(createdRuntime, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task StopRuntimeAsync(LobbyRuntime activeRuntime, CancellationToken cancellationToken)
    {
        activeRuntime.RequestStop();
        if (activeRuntime.ControllerStateStarted)
        {
            try
            {
                await SendCommandAsync(activeRuntime.Port, "/state/ide", cancellationToken).ConfigureAwait(false);
                await WaitForWaitingAsync(activeRuntime.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException
                or IOException
                or JsonException
                or InvalidDataException
                or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
                logger.LogDebug(exception, "Terracotta did not confirm the waiting state during cleanup.");
            }
        }

        if (activeRuntime.OwnedProcess is not null)
        {
            try
            {
                if (!activeRuntime.OwnedProcess.HasExited)
                    await SendCommandAsync(activeRuntime.Port, "/panic?peaceful=true", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException
                or IOException
                or InvalidDataException
                or OperationCanceledException)
            {
                logger.LogDebug(exception, "Terracotta did not accept the graceful shutdown command.");
            }

            await StopOwnedProcessAsync(activeRuntime.OwnedProcess).ConfigureAwait(false);
        }

        activeRuntime.Dispose();
    }

    private async Task WaitForWaitingAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = await GetStateAsync(port, timeout.Token).ConfigureAwait(false);
            if (state.Kind is TerracottaStateKind.Waiting)
                return;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<TerracottaState> GetStateAsync(int port, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(port, "/state", cancellationToken).ConfigureAwait(false);
        return ParseState(document.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(
        int port,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateLoopbackUri(port, path));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var data = await ReadLimitedContentAsync(response.Content, timeout.Token).ConfigureAwait(false);
        return JsonDocument.Parse(data);
    }

    private async Task SendCommandAsync(
        int port,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateLoopbackUri(port, path));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendGuestCommandAsync(
        int port,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, CreateLoopbackUri(port, path));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest)
        {
            throw new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.InvalidRoomCode,
                "Terracotta rejected the room code.");
        }

        response.EnsureSuccessStatusCode();
    }

    private static Uri CreateLoopbackUri(int port, string path) =>
        new($"http://127.0.0.1:{port}{path}", UriKind.Absolute);

    private static async Task<byte[]> ReadLimitedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The Terracotta response is too large.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The Terracotta response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static TerracottaState ParseState(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("state", out var stateElement)
            || stateElement.ValueKind is not JsonValueKind.String)
        {
            throw new InvalidDataException("The Terracotta state response is invalid.");
        }

        var kind = stateElement.GetString() switch
        {
            "waiting" => TerracottaStateKind.Waiting,
            "host-scanning" => TerracottaStateKind.HostScanning,
            "host-starting" => TerracottaStateKind.HostStarting,
            "host-ok" => TerracottaStateKind.HostOk,
            "guest-connecting" => TerracottaStateKind.GuestConnecting,
            "guest-starting" => TerracottaStateKind.GuestStarting,
            "guest-ok" => TerracottaStateKind.GuestOk,
            "exception" => TerracottaStateKind.Exception,
            _ => TerracottaStateKind.Other
        };
        var roomCode = root.TryGetProperty("room", out var roomElement)
            && roomElement.ValueKind is JsonValueKind.String
                ? NormalizeProfileText(roomElement.GetString(), string.Empty, 64)
                : string.Empty;
        int? exceptionType = root.TryGetProperty("type", out var typeElement)
            && typeElement.TryGetInt32(out var type)
                ? type
                : null;
        var players = new List<MultiplayerLobbyPlayer>();
        var machineIds = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("profiles", out var profilesElement)
            && profilesElement.ValueKind is JsonValueKind.Array)
        {
            foreach (var profile in profilesElement.EnumerateArray())
            {
                if (profile.ValueKind is not JsonValueKind.Object)
                    continue;
                var machineId = GetString(profile, "machine_id", 128);
                if (machineId.Length == 0 || !machineIds.Add(machineId))
                    continue;
                var name = NormalizeProfileText(GetString(profile, "name", 64), "Player", 64);
                var vendor = NormalizeProfileText(GetString(profile, "vendor", 128), "Terracotta", 128);
                var profileKind = GetString(profile, "kind", 16);
                var isHost = string.Equals(
                    profileKind,
                    "HOST",
                    StringComparison.OrdinalIgnoreCase);
                players.Add(new MultiplayerLobbyPlayer(
                    name,
                    machineId,
                    vendor,
                    isHost
                        ? MultiplayerLobbyPlayerKind.Host
                        : MultiplayerLobbyPlayerKind.Guest,
                    IsLocal: string.Equals(
                            profileKind,
                            "LOCAL",
                            StringComparison.OrdinalIgnoreCase)
                        || kind is TerracottaStateKind.HostOk && isHost));
            }
        }

        return new TerracottaState(kind, roomCode, exceptionType, players);
    }

    internal static string BuildScanPath(string hostName) =>
        $"/state/scanning?player={Uri.EscapeDataString(NormalizeProfileText(hostName, "Player", 64))}";

    internal static string BuildGuestPath(string roomCode, string playerName) =>
        $"/state/guesting?room={Uri.EscapeDataString(NormalizeProfileText(roomCode, string.Empty, 256))}"
        + $"&player={Uri.EscapeDataString(NormalizeProfileText(playerName, "Player", 64))}";

    private static string GetString(JsonElement element, string propertyName, int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not JsonValueKind.String)
        {
            return string.Empty;
        }
        return NormalizeProfileText(value.GetString(), string.Empty, maximumLength);
    }

    private static string NormalizeProfileText(string? value, string fallback, int maximumLength)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .Where(character => !char.IsControl(character))
            .ToArray());
        if (normalized.Length == 0)
            normalized = fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static MultiplayerLobbyCreationException MapCreationException(int? exceptionType) =>
        exceptionType switch
        {
            3 => new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.TerracottaStartupFailed,
                "Terracotta's EasyTier host stopped during creation."),
            4 => new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.MinecraftWorldUnavailable,
                "The Minecraft LAN world is no longer available."),
            _ => new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                "Terracotta reported a host creation error.")
        };

    private static MultiplayerLobbyCreationException MapJoinException(int? exceptionType) =>
        exceptionType switch
        {
            0 or 1 => new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.RoomConnectionFailed,
                "Terracotta could not reach the room host."),
            2 => new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.RoomConnectionFailed,
                "Terracotta's EasyTier guest stopped while joining."),
            _ => new MultiplayerLobbyCreationException(
                MultiplayerLobbyCreationFailure.TerracottaProtocolFailed,
                "Terracotta reported a guest connection error.")
        };

    private static MultiplayerLobbyStopReason MapStopReason(int? exceptionType) => exceptionType switch
    {
        3 => MultiplayerLobbyStopReason.TerracottaExited,
        4 => MultiplayerLobbyStopReason.MinecraftWorldClosed,
        _ => MultiplayerLobbyStopReason.TerracottaServiceFailed
    };

    private static bool ArchitectureMatches(string moduleArchitecture, string? targetArchitecture) =>
        moduleArchitecture switch
        {
            "x86_64" => string.Equals(targetArchitecture, "x86_64", StringComparison.OrdinalIgnoreCase),
            "arm64" => string.Equals(targetArchitecture, "aarch64", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetArchitecture, "arm64", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static int? TryReadExistingPort()
    {
        try
        {
            using var stream = new FileStream(
                TerracottaLockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[2];
            if (stream.Read(buffer) != buffer.Length)
                return null;
            var port = (buffer[0] << 8) | buffer[1];
            return port is > 0 and <= 65535 ? port : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void EnsureOwnedProcessRunning(LobbyRuntime activeRuntime)
    {
        if (activeRuntime.OwnedProcess?.HasExited is true)
            throw new InvalidOperationException("Terracotta exited unexpectedly.");
    }

    private void PublishSnapshot(MultiplayerLobbySnapshot snapshot)
    {
        Volatile.Write(ref current, snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    private static async Task DrainOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            if (!process.HasExited)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or TimeoutException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteHandoffFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed record TerracottaState(
        TerracottaStateKind Kind,
        string RoomCode,
        int? ExceptionType,
        IReadOnlyList<MultiplayerLobbyPlayer> Players);

    internal enum TerracottaStateKind
    {
        Waiting,
        HostScanning,
        HostStarting,
        HostOk,
        GuestConnecting,
        GuestStarting,
        GuestOk,
        Exception,
        Other
    }

    private sealed record TerracottaEndpoint(int Port, Process? OwnedProcess);

    private sealed class LobbyRuntime : IDisposable
    {
        private int disposed;

        public LobbyRuntime(int port, Process? ownedProcess, LobbyRole role)
        {
            Port = port;
            OwnedProcess = ownedProcess;
            Role = role;
        }

        public int Port { get; }

        public Process? OwnedProcess { get; }

        public LobbyRole Role { get; }

        public bool ControllerStateStarted { get; set; }

        public CancellationTokenSource Shutdown { get; } = new();

        public Task? MonitorTask { get; set; }

        public void RequestStop()
        {
            if (!Shutdown.IsCancellationRequested)
                Shutdown.Cancel();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            RequestStop();
            Shutdown.Dispose();
        }
    }

    private enum LobbyRole
    {
        Host,
        Guest
    }
}
