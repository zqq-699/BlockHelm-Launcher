using System.IO;
using Launcher.Application.Services;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Launcher.Infrastructure.Updates;

internal sealed class UpdateSecurityException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class UpdateSourceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal enum OfficialUpdateUriKind
{
    Manifest,
    Executable
}

internal static class OfficialUpdateHttp
{
    private const int MaxRedirects = 5;

    public static HttpClient CreateClient(TimeSpan timeout) =>
        LauncherHttpUserAgent.Apply(new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout
        });

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri initialUri,
        OfficialUpdateUriKind kind,
        CancellationToken cancellationToken, ILogger? logger = null)
    {
        using var diagnosticRequest = DownloadDiagnostics.BeginRequest(logger);
        var stopwatch = Stopwatch.StartNew();
        ValidateInitialUri(initialUri, kind);
        var currentUri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            HttpResponseMessage response;
            logger?.LogInformation("Official update HTTP request started. Endpoint={Endpoint} Host={Host} RedirectCount={RedirectCount}", kind, currentUri.Host, redirectCount);
            try
            {
                response = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, currentUri),
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateSourceUnavailableException("The update source timed out.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new UpdateSourceUnavailableException("The update source could not be reached.", exception);
            }

            logger?.LogInformation("Official update HTTP response. Endpoint={Endpoint} Host={Host} StatusCode={StatusCode} ContentLength={ContentLength} DurationMs={DurationMs}", kind, currentUri.Host, (int)response.StatusCode, response.Content.Headers.ContentLength, stopwatch.ElapsedMilliseconds);
            var finalUri = response.RequestMessage?.RequestUri ?? currentUri;
            ValidateRedirectUri(finalUri);
            if (!IsRedirect(response.StatusCode))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    throw SafeExceptionLog.Describe(new UpdateSourceUnavailableException("The update source returned an unsuccessful status."), "UpdateHttpStatus", ("StatusCode", (int)statusCode), ("Host", currentUri.Host));
                }
                return response;
            }

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update source exceeded the redirect limit."), "ComponentValidation", ("Reason", "The update source exceeded the redirect limit."));
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update source returned a redirect without a location."), "ComponentValidation", ("Reason", "The update source returned a redirect without a location."));
            currentUri = location.IsAbsoluteUri ? location : new Uri(finalUri, location);
            ValidateRedirectUri(currentUri);
        }
    }

    public static async Task<byte[]> ReadLimitedBytesAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw SafeExceptionLog.Describe(new UpdateSecurityException("The update response exceeded the size limit."), "ComponentValidation", ("Reason", "The update response exceeded the size limit."));
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > maximumBytes)
                throw SafeExceptionLog.Describe(new UpdateSecurityException("The update response exceeded the size limit."), "ComponentValidation", ("Reason", "The update response exceeded the size limit."));
            destination.Write(buffer, 0, read);
        }
    }

    public static void ValidateInitialUri(Uri uri, OfficialUpdateUriKind kind)
    {
        ValidateHttps(uri);
        var valid = kind switch
        {
            OfficialUpdateUriKind.Manifest => uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase),
            OfficialUpdateUriKind.Executable => uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!valid)
            throw SafeExceptionLog.Describe(new UpdateSecurityException("The update URL is not an approved official source."), "ComponentValidation", ("Reason", "The update URL is not an approved official source."));
    }

    private static void ValidateRedirectUri(Uri uri) => ValidateHttps(uri);

    private static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw SafeExceptionLog.Describe(new UpdateSecurityException("Only HTTPS update URLs are allowed."), "ComponentValidation", ("Reason", "Only HTTPS update URLs are allowed."));
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
