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
        DownloadHostConcurrencyController.DownloadAdmissionLease? admissionLease = null)
    {
        Response = response;
        OriginalUri = originalUri;
        FinalUri = finalUri;
        FinalHost = finalHost;
        RedirectChain = redirectChain;
        ResponseHeadersDuration = responseHeadersDuration;
        this.admissionLease = admissionLease;
    }

    public HttpResponseMessage Response { get; }
    public Uri OriginalUri { get; }
    public Uri FinalUri { get; }
    public string FinalHost { get; }
    public IReadOnlyList<Uri> RedirectChain { get; }
    public TimeSpan ResponseHeadersDuration { get; }
    public int RedirectCount => RedirectChain.Count;
    public string? AdmissionOrigin => admissionLease?.Origin;

    public ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref admissionLease, null);
        return lease is null ? ValueTask.CompletedTask : lease.DisposeAsync();
    }
}
