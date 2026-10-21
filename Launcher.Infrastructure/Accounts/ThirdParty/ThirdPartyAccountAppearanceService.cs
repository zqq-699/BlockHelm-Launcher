/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts.ThirdParty;

internal interface IThirdPartyAccountAppearanceService
{
    Task<ThirdPartyAccountProfileSnapshot> GetProfileAsync(
        Uri apiRoot,
        string profileId,
        string accountId,
        CancellationToken cancellationToken);
}

internal sealed record ThirdPartyAccountProfileSnapshot(
    bool IsAvailable,
    string? ProfileId,
    string? ProfileName,
    string? AvatarSource,
    string? SkinSource,
    MinecraftSkinModel? SkinModel,
    LauncherSkinRecord? Skin,
    AccountCapeOption? Cape)
{
    public static ThirdPartyAccountProfileSnapshot Unavailable { get; } =
        new(false, null, null, null, null, null, null, null);
}

internal sealed class ThirdPartyAccountAppearanceService : IThirdPartyAccountAppearanceService
{
    private const int MaximumTexturesPayloadLength = 1024 * 1024;
    private const string CurrentCapeId = "third-party-current-cape";
    private readonly HttpClient httpClient;
    private readonly AccountAvatarService avatarService;
    private readonly AccountSkinCacheService skinCacheService;
    private readonly AccountCapeCacheService capeCacheService;
    private readonly ILogger logger;

    public ThirdPartyAccountAppearanceService(
        HttpClient httpClient,
        AccountAvatarService avatarService,
        AccountSkinCacheService skinCacheService,
        AccountCapeCacheService capeCacheService,
        ILogger? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.avatarService = avatarService;
        this.skinCacheService = skinCacheService;
        this.capeCacheService = capeCacheService;
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<ThirdPartyAccountProfileSnapshot> GetProfileAsync(
        Uri apiRoot,
        string profileId,
        string accountId,
        CancellationToken cancellationToken)
    {
        try
        {
            var expectedProfileId = NormalizeUuid(profileId);
            var profileUri = new Uri(
                EnsureTrailingSlash(apiRoot),
                $"sessionserver/session/minecraft/profile/{expectedProfileId.Replace("-", string.Empty, StringComparison.Ordinal)}");
            using var response = await httpClient.GetAsync(profileUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Third-party profile request failed. AccountId={AccountId} StatusCode={StatusCode}",
                    accountId,
                    (int)response.StatusCode);
                return ThirdPartyAccountProfileSnapshot.Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var profile = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = profile.RootElement;
            var returnedProfileId = NormalizeUuid(GetRequiredString(root, "id"));
            var profileName = GetRequiredString(root, "name");
            if (!string.Equals(expectedProfileId, returnedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Third-party profile UUID mismatch. AccountId={AccountId}",
                    accountId);
                return ThirdPartyAccountProfileSnapshot.Unavailable;
            }

            var textures = ParseTextures(root);
            LauncherSkinRecord? skin = null;
            string? avatarSource = null;
            if (textures.Skin is { } skinTexture)
            {
                skin = await skinCacheService.GetOrCreateSkinRecordFromUrlAsync(
                    accountId,
                    skinTexture.Url.AbsoluteUri,
                    skinTexture.Model,
                    [],
                    forceRefresh: true,
                    useSharedLibrary: false,
                    cancellationToken).ConfigureAwait(false);
                avatarSource = await avatarService.GetOrCreateAvatarSourceAsync(
                    accountId,
                    skinTexture.Url.AbsoluteUri,
                    forceRefresh: true,
                    cancellationToken,
                    useRemoteFallback: false).ConfigureAwait(false);
                if (skin is null || string.IsNullOrWhiteSpace(avatarSource))
                    return ThirdPartyAccountProfileSnapshot.Unavailable;
            }

            AccountCapeOption? cape = null;
            if (textures.CapeUrl is { } capeUrl)
            {
                var capeSource = await capeCacheService.GetOrCreateCapeSourceAsync(
                    accountId,
                    CurrentCapeId,
                    capeUrl.AbsoluteUri,
                    forceRefresh: true,
                    cancellationToken,
                    useRemoteFallback: false).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(capeSource))
                    return ThirdPartyAccountProfileSnapshot.Unavailable;

                cape = new AccountCapeOption
                {
                    Id = CurrentCapeId,
                    DisplayName = string.Empty,
                    ImageUrl = capeSource,
                    IsActive = true,
                    IsNone = false
                };
            }

            return new ThirdPartyAccountProfileSnapshot(
                true,
                returnedProfileId,
                profileName,
                avatarSource,
                skin?.Source,
                skin?.SkinModel,
                skin,
                cape);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Third-party account profile could not be loaded. AccountId={AccountId}",
                accountId);
            return ThirdPartyAccountProfileSnapshot.Unavailable;
        }
    }

    internal static ThirdPartyTextures ParseTextures(JsonElement profile)
    {
        if (!profile.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Array)
        {
            return ThirdPartyTextures.Empty;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), "textures", StringComparison.Ordinal)
                || !property.TryGetProperty("value", out var value))
            {
                continue;
            }

            var encoded = value.GetString();
            if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaximumTexturesPayloadLength)
                throw new JsonException("The profile textures property is invalid.");

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            using var texturesDocument = JsonDocument.Parse(json);
            if (!texturesDocument.RootElement.TryGetProperty("textures", out var textureSet)
                || textureSet.ValueKind != JsonValueKind.Object)
            {
                return ThirdPartyTextures.Empty;
            }

            ThirdPartySkinTexture? skin = null;
            if (textureSet.TryGetProperty("SKIN", out var skinElement))
            {
                var skinUrl = GetHttpTextureUrl(skinElement);
                var model = skinElement.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("model", out var modelProperty)
                    && string.Equals(modelProperty.GetString(), "slim", StringComparison.OrdinalIgnoreCase)
                        ? MinecraftSkinModel.Slim
                        : MinecraftSkinModel.Classic;
                skin = new ThirdPartySkinTexture(skinUrl, model);
            }

            var capeUrl = textureSet.TryGetProperty("CAPE", out var capeElement)
                ? GetHttpTextureUrl(capeElement)
                : null;
            return new ThirdPartyTextures(skin, capeUrl);
        }

        return ThirdPartyTextures.Empty;
    }

    private static Uri GetHttpTextureUrl(JsonElement texture)
    {
        if (!texture.TryGetProperty("url", out var urlProperty)
            || !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var url)
            || (!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new JsonException("The profile texture URL is invalid.");
        }

        return url;
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new JsonException($"The profile field '{name}' is missing.");
    }

    private static string NormalizeUuid(string value)
    {
        if (!Guid.TryParse(value, out var uuid))
            throw new JsonException("The profile UUID is invalid.");

        return uuid.ToString("D");
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
}

internal sealed record ThirdPartyTextures(ThirdPartySkinTexture? Skin, Uri? CapeUrl)
{
    public static ThirdPartyTextures Empty { get; } = new(null, null);
}

internal sealed record ThirdPartySkinTexture(Uri Url, MinecraftSkinModel Model);
