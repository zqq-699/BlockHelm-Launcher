using System.IO;
using System.Net;
using System.Net.Http;

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
        CancellationToken cancellationToken)
    {
        ValidateInitialUri(initialUri, kind);
        var currentUri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, currentUri),
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateSourceUnavailableException("The update source timed out.");
            }
            catch (HttpRequestException exception)
            {
                throw new UpdateSourceUnavailableException("The update source could not be reached.", exception);
            }

            var finalUri = response.RequestMessage?.RequestUri ?? currentUri;
            ValidateRedirectUri(finalUri);
            if (!IsRedirect(response.StatusCode))
            {
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    throw new UpdateSourceUnavailableException($"The update source returned HTTP {(int)statusCode}.");
                }
                return response;
            }

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new UpdateSecurityException("The update source exceeded the redirect limit.");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new UpdateSecurityException("The update source returned a redirect without a location.");
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
            throw new UpdateSecurityException("The update response exceeded the size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > maximumBytes)
                throw new UpdateSecurityException("The update response exceeded the size limit.");
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
            throw new UpdateSecurityException("The update URL is not an approved official source.");
    }

    private static void ValidateRedirectUri(Uri uri) => ValidateHttps(uri);

    private static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new UpdateSecurityException("Only HTTPS update URLs are allowed.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
