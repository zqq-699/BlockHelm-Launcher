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

using System.Net;
using Launcher.Application.Services;
using System.Net.Http;

namespace Launcher.Infrastructure.Minecraft;

internal sealed record DownloadRetryOptions
{
    public static DownloadRetryOptions Default { get; } = new();

    // Small install profiles and metadata should release a stalled source
    // quickly; the lookup scheduler starts alternatives after 1.5 seconds.
    public static DownloadRetryOptions InteractiveMetadata { get; } = new()
    {
        MaxAttemptsPerSource = 2,
        ResponseHeadersTimeout = TimeSpan.FromSeconds(5),
        FirstByteTimeout = TimeSpan.FromSeconds(5),
        BodyIdleTimeout = TimeSpan.FromSeconds(8),
        SlowBodyReadThreshold = null
    };

    public int MaxAttemptsPerSource { get; init; } = 4;
    // Deferring a cooling source does not consume an actual file attempt.
    public int MaxFileAttemptsPerSource { get; init; } = 4;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan ResponseHeadersTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BodyIdleTimeout { get; init; } = TimeSpan.FromSeconds(10);
    // Measure throughput over accumulated network read time, excluding time
    // spent outside reads (such as local writes and bandwidth throttling).
    public TimeSpan? SlowBodyReadThreshold { get; init; } = TimeSpan.FromSeconds(10);
    public long MinimumBodyBytesPerSecond { get; init; } = 2048;
    public TimeSpan MaximumEstimatedRemainingTime { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan ThrottleRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMilliseconds(1400);
    public int MaxRedirects { get; init; } = 20;
}

internal enum DownloadFailureDisposition
{
    RetryCurrentSource,
    SwitchSource,
    Abort
}

internal enum DownloadFailureReason
{
    Network,
    Dns,
    ResponseHeadersTimeout,
    FirstByteTimeout,
    BodyIdleTimeout,
    BodyTooSlow,
    BodyInterrupted,
    HttpStatus,
    InvalidRedirect,
    SourceTemporarilyAvoided,
    InvalidContent,
    HashMismatch,
    LocalFileSystem,
    ResourceConflict
}

internal class DownloadAttemptException : Exception, ISafeDiagnosticException
{
    public DownloadAttemptException(
        DownloadFailureDisposition disposition,
        DownloadFailureReason reason,
        string message,
        Exception? innerException = null,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        Disposition = disposition;
        Reason = reason;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public string DiagnosticCode => Reason.ToString();
    public Dictionary<string, object?> Details { get; } = new();
    public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>(Details)
    {
        ["FailureDisposition"] = Disposition.ToString(), ["StatusCode"] = (int?)StatusCode,
        ["FinalHost"] = FinalHost, ["RetryAfterMs"] = RetryAfter?.TotalMilliseconds
    };
    public DownloadAttemptException WithDetail(string key, object? value) { Details[key] = value; return this; }

    public DownloadFailureDisposition Disposition { get; private set; }
    public DownloadFailureReason Reason { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public bool IsTransientMirrorForbidden { get; init; }
    public bool IsHostThrottled => StatusCode is HttpStatusCode.TooManyRequests || IsTransientMirrorForbidden;
    public string? FinalHost { get; private set; }
    public string? FinalOrigin { get; private set; }
    public int? RedirectCount { get; private set; }

    public DownloadAttemptException WithRedirectCount(int redirectCount)
    {
        RedirectCount = redirectCount;
        return this;
    }

    public DownloadAttemptException WithFinalHost(string? finalHost)
    {
        FinalHost = finalHost;
        return this;
    }

    public DownloadAttemptException WithFinalOrigin(string? finalOrigin)
    {
        FinalOrigin = finalOrigin;
        return this;
    }

    public DownloadAttemptException WithDisposition(DownloadFailureDisposition disposition)
    {
        Disposition = disposition;
        return this;
    }
}

internal sealed class DownloadCooldownWaitException : DownloadAttemptException
{
    public DownloadCooldownWaitException(TimeSpan remaining, TimeSpan budget, TimeSpan waited)
        : base(DownloadFailureDisposition.SwitchSource, DownloadFailureReason.HttpStatus,
            "The host cooldown exceeds the request's remaining wait budget.",
            statusCode: HttpStatusCode.TooManyRequests, retryAfter: remaining)
    {
        WithDetail("Reason", "HostCooldownWaitBudgetExceeded");
        WithDetail("CooldownWaitBudgetMs", budget.TotalMilliseconds);
        WithDetail("CooldownWaitedMs", waited.TotalMilliseconds);
    }
}

internal sealed class DownloadBodyInterruptedException : DownloadAttemptException
{
    public DownloadBodyInterruptedException(string message, Exception? innerException = null)
        : base(
            DownloadFailureDisposition.RetryCurrentSource,
            DownloadFailureReason.BodyInterrupted,
            message,
            innerException)
    {
    }
}

internal sealed class DownloadTimeoutException : DownloadAttemptException
{
    public DownloadTimeoutException(DownloadFailureReason reason, string message, Exception? innerException = null)
        : base(DownloadFailureDisposition.RetryCurrentSource, reason, message, innerException)
    {
    }
}

internal sealed class DownloadBodyTooSlowException : DownloadAttemptException
{
    public DownloadBodyTooSlowException(long bytesRead, TimeSpan readDuration, double? estimatedRemainingSeconds = null)
        : base(
            estimatedRemainingSeconds.HasValue
                ? DownloadFailureDisposition.SwitchSource
                : DownloadFailureDisposition.RetryCurrentSource,
            DownloadFailureReason.BodyTooSlow,
            $"The response body produced {bytesRead} bytes in {readDuration.TotalMilliseconds:0} ms "
            + $"({CalculateBytesPerSecond(bytesRead, readDuration):0.##} B/s).")
    {
        BytesRead = bytesRead;
        ReadDuration = readDuration;
        BytesPerSecond = CalculateBytesPerSecond(bytesRead, readDuration);
        EstimatedRemainingSeconds = estimatedRemainingSeconds;
        if (estimatedRemainingSeconds.HasValue)
            WithDetail("EstimatedRemainingSeconds", estimatedRemainingSeconds);
    }

    public long BytesRead { get; }
    public TimeSpan ReadDuration { get; }
    public double BytesPerSecond { get; }
    public double? EstimatedRemainingSeconds { get; }

    private static double CalculateBytesPerSecond(long bytesRead, TimeSpan readDuration) =>
        readDuration.TotalSeconds <= 0 ? double.PositiveInfinity : bytesRead / readDuration.TotalSeconds;
}

internal sealed class DownloadSourceAvoidedException()
    : DownloadAttemptException(
        DownloadFailureDisposition.SwitchSource,
        DownloadFailureReason.SourceTemporarilyAvoided,
        "The download node was temporarily avoided while another source was available.");

internal sealed class DownloadContentValidationException : DownloadAttemptException
{
    public DownloadContentValidationException(string message, Exception? innerException = null)
        : base(
            DownloadFailureDisposition.SwitchSource,
            DownloadFailureReason.InvalidContent,
            message,
            innerException)
    {
    }
}

internal sealed class DownloadHashMismatchException : DownloadAttemptException
{
    public DownloadHashMismatchException(string message)
        : base(
            DownloadFailureDisposition.SwitchSource,
            DownloadFailureReason.HashMismatch,
            message)
    {
    }
}

internal sealed class DownloadLocalFileException : DownloadAttemptException
{
    public DownloadLocalFileException(string message, Exception innerException)
        : base(
            DownloadFailureDisposition.Abort,
            DownloadFailureReason.LocalFileSystem,
            message,
            innerException)
    {
    }
}

internal sealed class DownloadNoResultException : Exception
{
    public DownloadNoResultException(string message)
        : base(message)
    {
    }
}

internal sealed record DownloadLookupResult<T>(bool Found, T? Value)
{
    public static DownloadLookupResult<T> Success(T value) => new(true, value);
    public static DownloadLookupResult<T> NotFound() => new(false, default);
}

internal sealed record DownloadAttemptContext(
    ResolvedDownloadRequest Resolution,
    HttpResponseMessage Response,
    DownloadTransportResult Transport,
    int AttemptNumber)
{
    public Func<bool>? HasReadyAlternative { get; init; }
}
