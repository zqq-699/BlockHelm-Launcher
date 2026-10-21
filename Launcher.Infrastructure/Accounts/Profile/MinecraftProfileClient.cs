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

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Accounts;

internal sealed class MinecraftProfileClient
{
    private const string MinecraftProfileEndpoint = "https://api.minecraftservices.com/minecraft/profile";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public MinecraftProfileClient(HttpClient httpClient)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
    }

    public async Task<MinecraftProfileResponse> GetProfileAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateProfileRequest(HttpMethod.Get, MinecraftProfileEndpoint, accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "\u83b7\u53d6\u6b63\u7248\u8d26\u6237\u8d44\u6599\u5931\u8d25", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var profile = await JsonSerializer.DeserializeAsync<MinecraftProfileResponse>(stream, JsonOptions, cancellationToken);
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
            throw new InvalidOperationException("\u672a\u80fd\u8bfb\u53d6\u5230 Minecraft Java \u8d26\u6237\u8d44\u6599");

        return profile;
    }

    public async Task UploadSkinAsync(
        string accessToken,
        string skinFilePath,
        MinecraftSkinModel skinModel,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(skinFilePath))
            throw new FileNotFoundException("\u627e\u4e0d\u5230\u8981\u4e0a\u4f20\u7684\u76ae\u80a4\u6587\u4ef6", skinFilePath);

        await using var fileStream = File.OpenRead(skinFilePath);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(ToSkinVariant(skinModel), Encoding.UTF8), "variant");

        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", Path.GetFileName(skinFilePath));

        using var request = CreateProfileRequest(HttpMethod.Post, $"{MinecraftProfileEndpoint}/skins", accessToken);
        request.Content = content;
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "\u4e0a\u4f20\u76ae\u80a4\u5931\u8d25", cancellationToken);
    }

    private static string ToSkinVariant(MinecraftSkinModel skinModel)
    {
        return skinModel is MinecraftSkinModel.Slim ? "slim" : "classic";
    }

    public async Task SetActiveCapeAsync(
        string accessToken,
        string? capeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capeId))
        {
            using var deleteRequest = CreateProfileRequest(HttpMethod.Delete, $"{MinecraftProfileEndpoint}/capes/active", accessToken);
            using var deleteResponse = await httpClient.SendAsync(deleteRequest, cancellationToken);
            await EnsureSuccessAsync(deleteResponse, "\u79fb\u9664\u62ab\u98ce\u5931\u8d25", cancellationToken);
            return;
        }

        using var request = CreateProfileRequest(HttpMethod.Put, $"{MinecraftProfileEndpoint}/capes/active", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new ActiveCapeRequest(capeId), JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "\u66f4\u6362\u62ab\u98ce\u5931\u8d25", cancellationToken);
    }

    public async Task<MinecraftProfileResponse> ChangeNameAsync(
        string accessToken,
        string newName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("\u7528\u6237\u540d\u4e0d\u53ef\u4e3a\u7a7a");

        var escapedName = Uri.EscapeDataString(newName.Trim());
        using var request = CreateProfileRequest(HttpMethod.Put, $"{MinecraftProfileEndpoint}/name/{escapedName}", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "\u4fee\u6539\u6b63\u7248\u8d26\u6237\u540d\u5931\u8d25", cancellationToken);

        return await ReadProfileOrFetchAsync(response, accessToken, cancellationToken);
    }

    private async Task<MinecraftProfileResponse> ReadProfileOrFetchAsync(
        HttpResponseMessage response,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(content))
        {
            var profile = JsonSerializer.Deserialize<MinecraftProfileResponse>(content, JsonOptions);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.Id))
                return profile;
        }

        return await GetProfileAsync(accessToken, cancellationToken);
    }

    private static HttpRequestMessage CreateProfileRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string message,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        var profileError = TryReadMinecraftError(details);
        var readableDetails = profileError.Details ?? details;
        throw new MinecraftProfileRequestException(
            profileError.Kind,
            response.StatusCode,
            FormatErrorCode(response.StatusCode, profileError.Code),
            string.IsNullOrWhiteSpace(details)
            ? $"{message}\uff1aHTTP {(int)response.StatusCode}"
            : $"{message}\uff1a{readableDetails}");
    }

    private static string FormatErrorCode(HttpStatusCode statusCode, string? code)
    {
        var httpCode = $"HTTP {(int)statusCode}";
        return string.IsNullOrWhiteSpace(code) ? httpCode : $"{httpCode} / {code}";
    }

    private static (MinecraftProfileErrorKind Kind, string? Code, string? Details) TryReadMinecraftError(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return (MinecraftProfileErrorKind.Unknown, null, null);

        try
        {
            var node = JsonNode.Parse(details);
            var error = node?["error"]?.GetValue<string>();
            var errorType = node?["errorType"]?.GetValue<string>();
            var errorMessage = node?["errorMessage"]?.GetValue<string>()
                ?? node?["developerMessage"]?.GetValue<string>();

            var code = string.IsNullOrWhiteSpace(errorType) ? error : errorType;
            var kind = code?.ToUpperInvariant() switch
            {
                "DUPLICATE" => MinecraftProfileErrorKind.Duplicate,
                "NOT_ALLOWED" => MinecraftProfileErrorKind.NotAllowed,
                "CONSTRAINT_VIOLATION" => MinecraftProfileErrorKind.ConstraintViolation,
                _ => MinecraftProfileErrorKind.Unknown
            };
            var readable = kind switch
            {
                MinecraftProfileErrorKind.Duplicate => "\u8fd9\u4e2a\u7528\u6237\u540d\u5df2\u88ab\u4f7f\u7528",
                MinecraftProfileErrorKind.NotAllowed => "\u8fd9\u4e2a\u8d26\u6237\u6682\u65f6\u4e0d\u80fd\u6539\u540d\uff0c\u53ef\u80fd\u8fd8\u672a\u6ee1 30 \u5929\u51b7\u5374\u671f",
                MinecraftProfileErrorKind.ConstraintViolation => "\u7528\u6237\u540d\u683c\u5f0f\u4e0d\u7b26\u5408 Minecraft \u89c4\u5219",
                _ => null
            };

            return (kind, code, readable
                ?? errorMessage
                ?? error
                ?? errorType);
        }
        catch
        {
            return (MinecraftProfileErrorKind.Unknown, null, null);
        }
    }
}
