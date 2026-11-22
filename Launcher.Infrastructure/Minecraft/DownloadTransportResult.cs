/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net.Http;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class DownloadTransportResult : IAsyncDisposable
{
    private DownloadHostConcurrencyController.DownloadAdmissionLease? admissionLease;

    public DownloadTransportResult(
        HttpResponseMessage response,
        Uri originalUri,
        Uri finalUri,
        string finalHost,
        IReadOnlyList<Uri> redirectChain,
        TimeSpan responseHeadersDuration,
        TimeSpan queueDuration,
        string? requestedRange,
        DownloadHostConcurrencyController.DownloadAdmissionLease? admissionLease = null)
    {
        Response = response;
        OriginalUri = originalUri;
        FinalUri = finalUri;
        FinalHost = finalHost;
        RedirectChain = redirectChain;
        ResponseHeadersDuration = responseHeadersDuration;
        QueueDuration = queueDuration;
        RequestedRange = requestedRange;
        this.admissionLease = admissionLease;
    }

    public HttpResponseMessage Response { get; }
    public Uri OriginalUri { get; }
    public Uri FinalUri { get; }
    public string FinalHost { get; }
    public IReadOnlyList<Uri> RedirectChain { get; }
    public TimeSpan ResponseHeadersDuration { get; }
    public TimeSpan QueueDuration { get; }
    public string? RequestedRange { get; }
    public int RedirectCount => RedirectChain.Count;
    public string? RedirectHosts => RedirectCount == 0 ? null : string.Join(" -> ", RedirectChain.Select(uri => uri.Host));
    public string? AdmissionOrigin => admissionLease?.Origin;

    public DownloadAttemptException DescribeFailure(DownloadAttemptException exception) => exception
        .WithFinalHost(FinalHost)
        .WithRedirectCount(RedirectCount)
        .WithFinalOrigin(AdmissionOrigin)
        .WithDetail("EntryUrl", DownloadUriLogSanitizer.Sanitize(OriginalUri))
        .WithDetail("FinalUrl", DownloadUriLogSanitizer.Sanitize(FinalUri))
        .WithDetail("RedirectHosts", RedirectHosts)
        .WithDetail("QueueDurationMs", QueueDuration.TotalMilliseconds)
        .WithDetail("ResponseHeadersMs", ResponseHeadersDuration.TotalMilliseconds)
        .WithDetail("ResponseStatusCode", (int)Response.StatusCode)
        .WithDetail("HttpVersion", Response.Version.ToString())
        .WithDetail("RequestedRange", RequestedRange)
        .WithDetail("ContentRange", Response.Content.Headers.ContentRange?.ToString())
        .WithDetail("ContentLength", Response.Content.Headers.ContentLength)
        .WithDetail("ContentType", Response.Content.Headers.ContentType?.MediaType)
        .WithDetail("ServerRetryAfter", Response.Headers.RetryAfter?.ToString());

    public ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref admissionLease, null);
        return lease is null ? ValueTask.CompletedTask : lease.DisposeAsync();
    }
}
