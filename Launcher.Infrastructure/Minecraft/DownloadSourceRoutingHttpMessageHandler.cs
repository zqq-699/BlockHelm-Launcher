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
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Modpacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class DownloadSourceRoutingHttpMessageHandler : DelegatingHandler
{
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private readonly HttpClient transportClient;
    private readonly MinecraftDownloadRequestExecutor executor;
    private readonly DownloadSourcePreference preference;
    private readonly ILogger logger;

    public DownloadSourceRoutingHttpMessageHandler(
        DownloadSourcePreference preference,
        DownloadConcurrencyCategory category,
        HttpMessageHandler innerHandler,
        ILogger? logger = null,
        DownloadBandwidthLimiter? bandwidthLimiter = null,
        IImportConcurrencyLimiter? limiter = null,
        DownloadRetryOptions? retryOptions = null,
        DownloadHostConcurrencyController? hostConcurrencyController = null)
        : base(innerHandler)
    {
        this.preference = preference;
        this.logger = logger ?? NullLogger.Instance;
        transportClient = LauncherHttpUserAgent.Apply(new HttpClient(innerHandler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        executor = new MinecraftDownloadRequestExecutor(
            transportClient,
            logger ?? NullLogger.Instance,
            bandwidthLimiter,
            limiter,
            category,
            retryOptions,
            hostConcurrencyController: hostConcurrencyController);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var originalUrl = request.RequestUri?.AbsoluteUri
            ?? throw new InvalidOperationException("Download request URL is missing.");
        if (!IsAllowedMetadataRequest(request.RequestUri!))
        {
            logger.LogError(
                "CmlLib binary request was rejected because it bypasses the unified file downloader. Path={Path}",
                request.RequestUri!.GetLeftPart(UriPartial.Path));
            throw new InvalidDataException(
                $"CmlLib attempted an unmanaged binary download through the metadata handler: {request.RequestUri!.GetLeftPart(UriPartial.Path)}");
        }
        var buffered = await executor.ExecuteAsync(
            originalUrl,
            preference,
            categoryHint: null,
            async (context, token) =>
            {
                if (context.Response.Content.Headers.ContentLength is > MaximumMetadataBytes)
                    throw new DownloadContentValidationException("CmlLib metadata response exceeded the permitted size.");
                var bytes = await ReadBoundedMetadataAsync(context.Response.Content, token).ConfigureAwait(false);
                ValidateBufferedMetadata(originalUrl, bytes);
                return BufferedResponse.Capture(context.Response, bytes);
            },
            cancellationToken).ConfigureAwait(false);

        return buffered.CreateResponse(request);
    }

    private static bool IsAllowedMetadataRequest(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pom", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
            || path.Contains("version_manifest", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedMetadataAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (memory.Length + read > MaximumMetadataBytes)
                throw new DownloadContentValidationException("CmlLib metadata response exceeded the permitted size.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            transportClient.Dispose();
        base.Dispose(disposing);
    }

    private static void ValidateBufferedMetadata(string originalUrl, byte[] bytes)
    {
        var firstContentByte = bytes.FirstOrDefault(value => !char.IsWhiteSpace((char)value));
        var path = new Uri(originalUrl, UriKind.Absolute).AbsolutePath;
        var expectsJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.Contains("version_manifest", StringComparison.OrdinalIgnoreCase);
        var expectsXml = path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        if (firstContentByte is (byte)'{' or (byte)'[' || expectsJson)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    throw new DownloadContentValidationException(
                        "CmlLib metadata JSON has an invalid root value.");
                }

                if (path.Contains("version_manifest", StringComparison.OrdinalIgnoreCase)
                    && (!document.RootElement.TryGetProperty("versions", out var versions)
                        || versions.ValueKind is not JsonValueKind.Array))
                {
                    throw new DownloadContentValidationException(
                        "Minecraft version manifest is missing a versions array.");
                }
            }
            catch (DownloadContentValidationException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new DownloadContentValidationException(
                    "CmlLib metadata response is not valid JSON.",
                    exception);
            }

            return;
        }

        if (firstContentByte == (byte)'<' || expectsXml)
        {
            try
            {
                var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
                if (document.Root?.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase) is true)
                {
                    throw new DownloadContentValidationException(
                        "CmlLib metadata endpoint returned an HTML document.");
                }
            }
            catch (DownloadContentValidationException)
            {
                throw;
            }
            catch (System.Xml.XmlException exception)
            {
                throw new DownloadContentValidationException(
                    "CmlLib metadata response is not valid XML.",
                    exception);
            }
        }
    }

    private sealed record BufferedResponse(
        HttpStatusCode StatusCode,
        string? ReasonPhrase,
        Version Version,
        IReadOnlyList<KeyValuePair<string, string[]>> Headers,
        IReadOnlyList<KeyValuePair<string, string[]>> ContentHeaders,
        byte[] Content)
    {
        public static BufferedResponse Capture(HttpResponseMessage response, byte[] content)
        {
            return new BufferedResponse(
                response.StatusCode,
                response.ReasonPhrase,
                response.Version,
                response.Headers.Select(header =>
                    new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToList(),
                response.Content.Headers.Select(header =>
                    new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToList(),
                content);
        }

        public HttpResponseMessage CreateResponse(HttpRequestMessage request)
        {
            var content = new ByteArrayContent(Content);
            foreach (var header in ContentHeaders)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);

            var response = new HttpResponseMessage(StatusCode)
            {
                ReasonPhrase = ReasonPhrase,
                Version = Version,
                RequestMessage = request,
                Content = content
            };
            foreach (var header in Headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
    }
}
