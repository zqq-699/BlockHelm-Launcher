/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Launcher.Application.Services;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Minecraft;

/// <summary>Counts sends, including requests which fail before any body progress is reported.</summary>
internal sealed class DownloadNetworkObservation : IDisposable
{
    private static readonly AsyncLocal<DownloadNetworkObservation?> CurrentSlot = new();
    private readonly DownloadNetworkObservation? previous;
    private int sends;
    private int retries;
    private readonly ConcurrentDictionary<string, byte> candidates = new(StringComparer.Ordinal);
    public int CandidateCount => candidates.Count;
    public static void SelectCandidate(string url) => CurrentSlot.Value?.candidates.TryAdd(url, 0);
    public static DownloadNetworkObservation? Current => CurrentSlot.Value;
    public int Sends => Volatile.Read(ref sends);
    public int Retries => Volatile.Read(ref retries);
    public DownloadNetworkObservation()
    {
        previous = CurrentSlot.Value;
        CurrentSlot.Value = this;
    }
    public static void Sent() { if (CurrentSlot.Value is { } value) Interlocked.Increment(ref value.sends); }
    public static void Retried() { if (CurrentSlot.Value is { } value) Interlocked.Increment(ref value.retries); }
    public void Dispose() => CurrentSlot.Value = previous;
}

/// <summary>Observes independent HTTP clients without changing redirects, credentials or retry policy.</summary>
internal static class DownloadHttpDiagnostics
{
    private sealed record ResponseEvidence(string? Host, IReadOnlyDictionary<string, object?> Context);
    private static readonly ConditionalWeakTable<HttpResponseMessage, ResponseEvidence> Responses = new();

    public static Task<byte[]> GetBytesAsync(HttpClient client, ILogger? logger, string url, CancellationToken token) =>
        ObserveAsync(logger, $"RemoteImage:{Host(url)}", () => client.GetByteArrayAsync(url, token), token, client.Timeout);

    public static Task<T?> GetFromJsonAsync<T>(HttpClient client, ILogger? logger, string url, CancellationToken cancellationToken) =>
        ObserveAsync(logger, $"{typeof(T).Name}:{Host(url)}", () => client.GetFromJsonAsync<T>(url, cancellationToken), cancellationToken, client.Timeout);

    public static Task<T?> GetFromJsonAsync<T>(HttpClient client, ILogger? logger, string url, JsonSerializerOptions options, CancellationToken cancellationToken) =>
        ObserveAsync(logger, $"{typeof(T).Name}:{Host(url)}", () => client.GetFromJsonAsync<T>(url, options, cancellationToken), cancellationToken, client.Timeout);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, ILogger? logger, CancellationToken token)
    {
        return await ObserveAsync(logger, "MetadataRequest", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            logger?.LogInformation("Independent download request started. Method={Method} Host={Host}", request.Method.Method, request.RequestUri?.Host);
            var response = await client.SendAsync(request, token).ConfigureAwait(false);
            RecordResponse(response, request.RequestUri?.Host, logger, stopwatch.ElapsedMilliseconds);
            return response;
        }, token, client.Timeout).ConfigureAwait(false);
    }

    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(HttpClient client, ILogger? logger, string url,
        T value, JsonSerializerOptions options, CancellationToken token) =>
        ObserveAsync(logger, $"MetadataPost:{typeof(T).Name}:{Host(url)}", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync(url, value, options, token).ConfigureAwait(false);
            RecordResponse(response, Host(url), logger, stopwatch.ElapsedMilliseconds);
            return response;
        }, token, client.Timeout);

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, ILogger? logger, string url,
        HttpCompletionOption completion, CancellationToken token) =>
        ObserveAsync(logger, $"MetadataGet:{Host(url)}", async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await client.GetAsync(url, completion, token).ConfigureAwait(false);
            RecordResponse(response, Host(url), logger, stopwatch.ElapsedMilliseconds);
            return response;
        }, token, client.Timeout);

    public static Task<T?> ReadFromJsonAsync<T>(HttpResponseMessage response, ILogger? logger,
        CancellationToken cancellationToken, JsonSerializerOptions? options = null) =>
        ObserveJsonParseAsync(response, logger, () => response.Content.ReadFromJsonAsync<T>(options, cancellationToken));

    public static Task<T?> DeserializeJsonAsync<T>(HttpResponseMessage response, ILogger? logger,
        CancellationToken cancellationToken, JsonSerializerOptions options) =>
        ObserveJsonParseAsync(response, logger, async () =>
        {
            // Preserve callers which intentionally deserialize raw UTF-8 bytes rather than
            // letting HttpContent's JSON helper interpret the declared response charset.
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
        });

    private static async Task<T?> ObserveJsonParseAsync<T>(HttpResponseMessage response, ILogger? logger, Func<Task<T?>> parse)
    {
        Responses.TryGetValue(response, out var evidence);
        using var context = evidence is null ? DownloadDiagnostics.BeginOperation(logger, "MetadataReadParse")
            : DownloadDiagnostics.Restore(logger, evidence.Context);
        return await DownloadDiagnostics.RunStageAsync(logger, "MetadataReadParse", async () =>
        {
            try
            {
                return await parse().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException)
            {
                throw SafeExceptionLog.Describe(exception, "MetadataReadParse", ("ExpectedFormat", "JSON"),
                    ("Resource", typeof(T).Name), ("Host", evidence?.Host ?? response.RequestMessage?.RequestUri?.Host),
                    ("StatusCode", (int)response.StatusCode), ("ContentType", response.Content.Headers.ContentType?.MediaType));
            }
        }).ConfigureAwait(false);
    }

    private static void RecordResponse(HttpResponseMessage response, string? host, ILogger? logger, long durationMs)
    {
        Responses.GetValue(response, _ => new(host, DownloadDiagnostics.Capture()));
        logger?.Log(response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning,
            "Independent download HTTP response. Host={Host} StatusCode={StatusCode} ContentType={ContentType} ContentLength={ContentLength} DurationMs={DurationMs}",
            host, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength, durationMs);
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "InvalidUri";

    public static async Task<T> ObserveAsync<T>(ILogger? logger, string resource, Func<Task<T>> action,
        CancellationToken callerCancellation = default, TimeSpan? configuredTimeout = null)
    {
        using var operation = DownloadDiagnostics.BeginOperation(logger, resource);
        using var request = DownloadDiagnostics.BeginRequest(logger);
        var stopwatch = Stopwatch.StartNew();
        logger?.LogInformation("Independent download started. Resource={Resource} ConfiguredTimeoutMs={ConfiguredTimeoutMs}", resource, configuredTimeout?.TotalMilliseconds);
        using var heartbeat = new Timer(_ => logger?.LogInformation("Independent download still running. Resource={Resource} DurationMs={DurationMs}", resource, stopwatch.ElapsedMilliseconds),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        try
        {
            var result = await action().ConfigureAwait(false);
            logger?.LogInformation("Independent download completed. Resource={Resource} DurationMs={DurationMs}", resource, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            logger?.LogInformation("Independent download canceled. Resource={Resource} CancellationOrigin=CallerCancellation DurationMs={DurationMs}", resource, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            var timeoutConfirmed = exception.InnerException is TimeoutException;
            SafeExceptionLog.Describe(exception, timeoutConfirmed ? "RequestTimeout" : "NonCallerCancellation",
                ("Resource", resource), ("ConfiguredTimeoutMs", configuredTimeout?.TotalMilliseconds));
            logger?.LogWarning(exception, "Independent download failed. Resource={Resource} CancellationOrigin={CancellationOrigin} DurationMs={DurationMs}",
                resource, timeoutConfirmed ? "InternalTimeout" : "NonCallerCancellation", stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Independent download failed. Resource={Resource} DurationMs={DurationMs}", resource, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
