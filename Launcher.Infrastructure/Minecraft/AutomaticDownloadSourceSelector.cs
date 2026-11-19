/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>Process-scoped network hint, independent of individual install tasks.</summary>
internal sealed class AutomaticDownloadSourceSelector : IDisposable
{
    private static readonly Lazy<AutomaticDownloadSourceSelector> shared = new(() => new(observeNetwork: true));
    internal static AutomaticDownloadSourceSelector Shared => shared.Value;
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(3);
    private static readonly string[] Endpoints =
        ["https://cloudflare.com/cdn-cgi/trace", "https://www.cloudflare-cn.com/cdn-cgi/trace"];
    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private readonly Func<string?> systemRegion;
    private readonly TimeSpan budget;
    private readonly object sync = new();
    private readonly bool observeNetwork;
    private readonly bool ownsClient;
    private Task<Selection>? pending;
    private DateTimeOffset expires;
    private long generation;

    internal AutomaticDownloadSourceSelector(HttpClient? client = null, TimeProvider? clock = null,
        Func<string?>? systemRegion = null, TimeSpan? budget = null, bool observeNetwork = false)
    {
        this.client = client ?? LauncherHttpUserAgent.Apply(new HttpClient(new SocketsHttpHandler
        {
            UseProxy = true, AllowAutoRedirect = true, MaxAutomaticRedirections = 3,
            ConnectTimeout = ProbeBudget, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan });
        ownsClient = client is null;
        this.clock = clock ?? TimeProvider.System;
        this.systemRegion = systemRegion ?? (() => RegionInfo.CurrentRegion.TwoLetterISORegionName);
        this.budget = budget ?? ProbeBudget;
        this.observeNetwork = observeNetwork;
        if (observeNetwork) NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    internal async Task<Selection> SelectAsync(ILogger logger, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<Selection> task;
        lock (sync)
        {
            if (pending is null || pending.IsCompleted && clock.GetUtcNow() >= expires)
                pending = ProbeAsync(logger, generation);
            task = pending;
        }
        // One canceled install must not cancel the shared, independently bounded probe.
        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    internal void Invalidate()
    {
        lock (sync) { generation++; pending = null; expires = default; }
    }

    private void OnNetworkChanged(object? sender, EventArgs args) => Invalidate();

    private async Task<Selection> ProbeAsync(ILogger logger, long startedGeneration)
    {
        try
        {
            return await ProbeCoreAsync(logger, startedGeneration).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Region detection is optional. Cache this fallback too, so parallel
            // downloads do not repeatedly probe a failing dependency.
            lock (sync)
            {
                if (generation == startedGeneration)
                    expires = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
            }
            try
            {
                logger.LogWarning("Automatic download source detection failed; using mirror preference. FailureType={FailureType}",
                    exception.GetType().Name);
            }
            catch (Exception) { /* A broken logging provider must not stop downloads either. */ }
            return new Selection(DownloadSourcePreference.BmclApi, null, "UnknownRegionMirrorFallback");
        }
    }

    private async Task<Selection> ProbeCoreAsync(ILogger logger, long startedGeneration)
    {
        using var timeout = new CancellationTokenSource(budget);
        var queries = Endpoints.Select(endpoint => ReadCountryAsync(endpoint, timeout.Token)).ToList();
        try
        {
            string? country = null;
            while (queries.Count > 0)
            {
                var completed = await Task.WhenAny(queries).ConfigureAwait(false);
                queries.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                if (result == "CN")
                {
                    country = result;
                    break;
                }
                country ??= result;
            }
            var fromTrace = country is not null;
            if (!fromTrace)
            {
                try { country = NormalizeCountry(systemRegion()); }
                catch (ArgumentException) { country = null; }
            }
            var selection = new Selection(country is null or "CN" ? DownloadSourcePreference.BmclApi : DownloadSourcePreference.Official,
                country, fromTrace ? "Trace" : country is not null ? "SystemRegion" : "UnknownRegionMirrorFallback");
            lock (sync)
            {
                if (generation == startedGeneration)
                    expires = clock.GetUtcNow() + (fromTrace ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
            }
            logger.LogInformation("Automatic download source selected. Region={Region} Reason={Reason} EffectivePreference={EffectivePreference}",
                selection.Region, selection.Reason, selection.Preference);
            return selection;
        }
        finally
        {
            // Also cancel and observe remaining requests when an unexpected
            // exception interrupts the probe before its normal completion.
            timeout.Cancel();
            foreach (var query in queries) _ = ObserveAsync(query);
        }
    }

    private async Task<string?> ReadCountryAsync(string endpoint, CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 4096) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > 4096) return null;
            var locations = Encoding.UTF8.GetString(bytes, 0, count).Split('\n')
                .Where(line => line.StartsWith("loc=", StringComparison.Ordinal)).ToArray();
            return locations.Length == 1 ? NormalizeCountry(locations[0][4..].Trim()) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return null; // No response content, IP, or raw network message belongs in logs.
        }
    }

    private static string? NormalizeCountry(string? value) => value is { Length: 2 }
        && value.All(c => c is >= 'A' and <= 'Z') && value is not ("XX" or "IV" or "ZZ") ? value : null;

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Losing probes cannot leak unobserved failures. */ }
    }

    public void Dispose()
    {
        if (observeNetwork) NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        if (ownsClient) client.Dispose();
    }

    internal sealed record Selection(DownloadSourcePreference Preference, string? Region, string Reason);
}
