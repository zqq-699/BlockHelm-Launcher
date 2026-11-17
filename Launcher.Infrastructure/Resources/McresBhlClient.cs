/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Services;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;

using Launcher.Infrastructure.Minecraft;

namespace Launcher.Infrastructure.Resources;

internal sealed class McresBhlClient(
    HttpClient httpClient,
    IMcresBhlApiKeyResolver apiKeyResolver,
    ILogger logger)
{
    private const string BaseUrl = "https://www.mcresource.cn/api/bhl";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ResourceProjectRelatedWebsite?> GetRelatedWebsiteAsync(
        ResourceProjectReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!Supports(reference.Kind) || string.IsNullOrWhiteSpace(reference.ProjectId))
            return null;

        var provider = reference.Source switch
        {
            ResourceProjectSource.Modrinth => "modrinth",
            ResourceProjectSource.CurseForge => "curseforge",
            _ => null
        };
        if (provider is null)
            return null;

        var apiKey = await apiKeyResolver.TryResolveAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogDebug(
                "Skipped MCRES related website lookup because the BHL API key is unavailable. Source={Source} ProjectId={ProjectId}",
                reference.Source,
                reference.ProjectId);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var lookup = await LookupAsync(provider, reference.ProjectId, apiKey, timeout.Token).ConfigureAwait(false);
            if (lookup is null)
                return null;

            var detail = await LoadDetailAsync(lookup, apiKey, timeout.Token).ConfigureAwait(false);
            if (!TryValidateMcresWebsiteUrl(detail?.Url, out var websiteUri))
                return null;

            logger.LogDebug(
                "Resolved MCRES related website. Source={Source} ProjectId={ProjectId} ResourceType={ResourceType} ResourceId={ResourceId}",
                reference.Source,
                reference.ProjectId,
                lookup.ResourceType,
                lookup.ResourceId);
            return new ResourceProjectRelatedWebsite(
                reference.Source,
                reference.ProjectId,
                "MCRES",
                websiteUri.AbsoluteUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "MCRES related website lookup timed out. Source={Source} ProjectId={ProjectId} ErrorType={ErrorType}",
                reference.Source,
                reference.ProjectId,
                exception.GetType().Name);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogWarning(
                exception,
                "MCRES related website lookup failed. Source={Source} ProjectId={ProjectId} ErrorType={ErrorType}",
                reference.Source,
                reference.ProjectId,
                exception.GetType().Name);
        }

        return null;
    }

    private async Task<McresLookupResource?> LookupAsync(
        string provider,
        string projectId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"{BaseUrl}/lookup?provider={Uri.EscapeDataString(provider)}&project_id={Uri.EscapeDataString(projectId)}&key={Uri.EscapeDataString(apiKey)}";
        using var response = await DownloadHttpDiagnostics.GetAsync(httpClient, logger, requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "MCRES lookup returned HTTP {StatusCode}. Provider={Provider} ProjectId={ProjectId}",
                (int)response.StatusCode,
                provider,
                projectId);
            return null;
        }

        var result = await DownloadHttpDiagnostics.DeserializeJsonAsync<McresLookupResponse>(response, logger, cancellationToken, JsonOptions)
            .ConfigureAwait(false);
        if (result is null || result.Code is 404 || result.Resources is null || result.Resources.Count == 0)
            return null;
        if (result.Code is not 0)
        {
            logger.LogWarning(
                "MCRES lookup returned application code {Code}. Provider={Provider} ProjectId={ProjectId}",
                result.Code,
                provider,
                projectId);
            return null;
        }

        var first = result.Resources[0];
        return first.ResourceId > 0 && IsSupportedResourceType(first.ResourceType) ? first : null;
    }

    private async Task<McresDetailResponse?> LoadDetailAsync(
        McresLookupResource resource,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"{BaseUrl}/detail/{Uri.EscapeDataString(resource.ResourceType)}/{resource.ResourceId}?key={Uri.EscapeDataString(apiKey)}";
        using var response = await DownloadHttpDiagnostics.GetAsync(httpClient, logger, requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "MCRES detail returned HTTP {StatusCode}. ResourceType={ResourceType} ResourceId={ResourceId}",
                (int)response.StatusCode,
                resource.ResourceType,
                resource.ResourceId);
            return null;
        }

        var result = await DownloadHttpDiagnostics.DeserializeJsonAsync<McresDetailResponse>(response, logger, cancellationToken, JsonOptions)
            .ConfigureAwait(false);
        if (result is null || result.Code is 404)
            return null;
        if (result.Code is not 0)
        {
            logger.LogWarning(
                "MCRES detail returned application code {Code}. ResourceType={ResourceType} ResourceId={ResourceId}",
                result.Code,
                resource.ResourceType,
                resource.ResourceId);
            return null;
        }

        return result;
    }

    private static bool Supports(ResourceProjectKind kind) =>
        kind is ResourceProjectKind.ResourcePack or ResourceProjectKind.ShaderPack or ResourceProjectKind.World;

    private static bool IsSupportedResourceType(string? resourceType) =>
        resourceType is "resourcepack" or "shaderpack" or "map";

    private static bool TryValidateMcresWebsiteUrl(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && candidate.Host.Equals("www.mcresource.cn", StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private sealed class McresLookupResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("resources")]
        public List<McresLookupResource>? Resources { get; init; }
    }

    private sealed class McresLookupResource
    {
        [JsonPropertyName("resource_type")]
        public string ResourceType { get; init; } = string.Empty;

        [JsonPropertyName("resource_id")]
        public int ResourceId { get; init; }
    }

    private sealed class McresDetailResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }
}
