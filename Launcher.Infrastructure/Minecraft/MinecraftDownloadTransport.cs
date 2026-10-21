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
using System.Net.Http;
using System.Diagnostics;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class MinecraftDownloadTransport
{
    private readonly HttpClient httpClient;
    private readonly DownloadRetryOptions retryOptions;
    private readonly Func<Uri, bool, CancellationToken, ValueTask<DownloadHostConcurrencyController.DownloadAdmissionLease>>?
        acquireAdmissionAsync;
    private readonly Func<Uri, DownloadHostConcurrencyController.DownloadAdmissionLease?>?
        tryAcquireOpportunisticAdmission;

    public MinecraftDownloadTransport(
        HttpClient httpClient,
        DownloadRetryOptions retryOptions,
        Func<Uri, bool, CancellationToken, ValueTask<DownloadHostConcurrencyController.DownloadAdmissionLease>>?
            acquireAdmissionAsync = null,
        Func<Uri, DownloadHostConcurrencyController.DownloadAdmissionLease?>?
            tryAcquireOpportunisticAdmission = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.retryOptions = retryOptions;
        this.acquireAdmissionAsync = acquireAdmissionAsync;
        this.tryAcquireOpportunisticAdmission = tryAcquireOpportunisticAdmission;
    }

    public async Task<DownloadTransportResult> SendAsync(
        string actualUrl,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configureRequest = null,
        DownloadRequestHeaders? sensitiveHeaders = null,
        bool applyColdStartJitter = false,
        bool opportunisticAdmission = false,
        DownloadHostConcurrencyController.DownloadAdmissionLease? preacquiredAdmission = null)
    {
        var originalUri = ParseHttpUri(actualUrl);
        var currentUri = originalUri;
        var suppliedAdmission = preacquiredAdmission;
        var stopwatch = Stopwatch.StartNew();
        var redirects = new List<Uri>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentUri.AbsoluteUri
        };

        for (var redirectCount = 0; ; redirectCount++)
        {
            DownloadHostConcurrencyController.DownloadAdmissionLease? admissionLease = null;
            try
            {
                if (suppliedAdmission is not null)
                {
                    admissionLease = suppliedAdmission;
                    suppliedAdmission = null;
                }
                else if (opportunisticAdmission)
                {
                    admissionLease = tryAcquireOpportunisticAdmission?.Invoke(currentUri)
                        ?? throw new OpportunisticDownloadAdmissionUnavailableException();
                }
                else if (acquireAdmissionAsync is not null)
                {
                    admissionLease = await acquireAdmissionAsync(
                        currentUri,
                        applyColdStartJitter,
                        cancellationToken).ConfigureAwait(false);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(retryOptions.ResponseHeadersTimeout);
                var response = await SendSingleAsync(
                    currentUri,
                    timeout.Token,
                    cancellationToken,
                    configureRequest,
                    sensitiveHeaders)
                    .ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    var result = new DownloadTransportResult(
                        response,
                        originalUri,
                        currentUri,
                        currentUri.Host,
                        redirects,
                        stopwatch.Elapsed,
                        admissionLease);
                    admissionLease = null;
                    return result;
                }

                if (redirectCount >= retryOptions.MaxRedirects)
                {
                    response.Dispose();
                    throw InvalidRedirect(
                        $"The HTTP redirect chain exceeded {retryOptions.MaxRedirects} hops.");
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    response.Dispose();
                    throw InvalidRedirect("The HTTP redirect response did not contain a Location header.");
                }

                Uri nextUri;
                try
                {
                    nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                }
                catch (UriFormatException exception)
                {
                    response.Dispose();
                    throw InvalidRedirect("The HTTP redirect target was invalid.", exception);
                }

                if (!IsHttpUri(nextUri) || !visited.Add(nextUri.AbsoluteUri))
                {
                    response.Dispose();
                    throw InvalidRedirect("The HTTP redirect target was unsupported or formed a loop.");
                }

                response.Dispose();
                redirects.Add(nextUri);
                currentUri = nextUri;
            }
            catch (DownloadAttemptException exception)
            {
                throw exception
                    .WithFinalHost(currentUri.Host)
                    .WithFinalOrigin(DownloadHostConcurrencyController.NormalizeOrigin(currentUri));
            }
            finally
            {
                if (admissionLease is not null)
                    await admissionLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendSingleAsync(
        Uri uri,
        CancellationToken timeoutToken,
        CancellationToken callerToken,
        Action<HttpRequestMessage>? configureRequest,
        DownloadRequestHeaders? sensitiveHeaders)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            configureRequest?.Invoke(request);
            if (request.RequestUri != uri)
            {
                throw InvalidRedirect("The configured download request URI changed before it was sent.");
            }
            sensitiveHeaders?.ApplyIfAllowed(request);
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new DownloadAttemptException(
                DownloadFailureDisposition.RetryCurrentSource,
                DownloadFailureReason.ResponseHeadersTimeout,
                $"The source did not return response headers within {retryOptions.ResponseHeadersTimeout}.",
                exception);
        }
        catch (HttpRequestException exception)
            when (exception.HttpRequestError is HttpRequestError.ConfigurationLimitExceeded)
        {
            throw InvalidRedirect(
                "The HTTP redirect chain was invalid or exceeded the transport limit.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadAttemptException(
                DownloadFailureDisposition.RetryCurrentSource,
                DownloadFailureReason.Network,
                "The HTTP request failed before response headers were received.",
                exception);
        }
    }

    private static DownloadAttemptException InvalidRedirect(
        string message,
        Exception? innerException = null)
    {
        return new DownloadAttemptException(
            DownloadFailureDisposition.SwitchSource,
            DownloadFailureReason.InvalidRedirect,
            message,
            innerException);
    }

    private static Uri ParseHttpUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsHttpUri(uri))
            throw InvalidRedirect("Download URLs must use HTTP or HTTPS.");
        return uri;
    }

    private static bool IsHttpUri(Uri uri) =>
        uri.IsAbsoluteUri
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return value is >= 300 and <= 399;
    }
}

internal sealed class OpportunisticDownloadAdmissionUnavailableException : Exception
{
}
