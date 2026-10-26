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

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts;

internal sealed class OfflineSkinYggdrasilService : IOfflineSkinLaunchService, IDisposable
{
    private const int MaximumSkinBytes = 4 * 1024 * 1024;
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumRequestBodyBytes = 16 * 1024;
    private const int MaximumBatchNames = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IMinecraftSkinFileValidator skinFileValidator;
    private readonly ILogger<OfflineSkinYggdrasilService> logger;
    private readonly ConcurrentDictionary<string, OfflineSkinProfile> profilesByUuid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> profileUuidsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> textures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim startupLock = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly RSA signingKey = RSA.Create(2048);
    private readonly object signingGate = new();
    private TcpListener? listener;
    private Task? acceptLoopTask;
    private Uri? apiRoot;
    private bool disposed;

    public OfflineSkinYggdrasilService(
        IMinecraftSkinFileValidator skinFileValidator,
        ILogger<OfflineSkinYggdrasilService>? logger = null)
    {
        this.skinFileValidator = skinFileValidator;
        this.logger = logger ?? NullLogger<OfflineSkinYggdrasilService>.Instance;
    }

    public async Task<OfflineSkinLaunchContext?> PrepareAsync(
        LauncherAccount account,
        string sessionUuid,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!account.IsOffline)
            return null;

        var activeSkin = account.SkinLibrary.FirstOrDefault(skin =>
                string.Equals(skin.Id, account.ActiveSkinId, StringComparison.Ordinal))
            ?? account.SkinLibrary.FirstOrDefault(skin =>
                account.SkinModel == skin.SkinModel
                && string.Equals(skin.Source, account.SkinSource, StringComparison.Ordinal));
        var skinSource = activeSkin?.Source ?? account.SkinSource;
        var skinModel = activeSkin?.SkinModel ?? account.SkinModel;
        if (string.IsNullOrWhiteSpace(skinSource))
            return null;
        if (skinModel is null)
            throw new InvalidDataException("The active offline skin model is missing.");

        var skinPath = ResolveLocalPath(skinSource)
            ?? throw new InvalidDataException("The active offline skin is not a local file.");
        var skinFile = new FileInfo(skinPath);
        if (!skinFile.Exists || skinFile.Length is <= 0 or > MaximumSkinBytes)
            throw new InvalidDataException("The active offline skin file is missing or too large.");
        var validation = await skinFileValidator.ValidateAsync(skinPath, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
            throw new InvalidDataException("The active offline skin is not a valid Minecraft skin.");

        var skinBytes = await ReadSkinBytesAsync(skinPath, cancellationToken).ConfigureAwait(false);
        if (!HasPngSignature(skinBytes))
            throw new InvalidDataException("The active offline skin is not a PNG file.");
        var compactUuid = NormalizeUuid(sessionUuid);
        var root = await EnsureServerStartedAsync(cancellationToken).ConfigureAwait(false);
        var textureHash = Convert.ToHexString(SHA256.HashData(skinBytes)).ToLowerInvariant();
        var textureUrl = new Uri(root, $"textures/{textureHash}").AbsoluteUri;
        var textureValueBytes = CreateTextureValue(
            compactUuid,
            account.DisplayName,
            textureUrl,
            skinModel.Value);
        var textureValue = Convert.ToBase64String(textureValueBytes);
        byte[] signatureBytes;
        lock (signingGate)
        {
            signatureBytes = signingKey.SignData(
                Encoding.UTF8.GetBytes(textureValue),
                HashAlgorithmName.SHA1,
                RSASignaturePadding.Pkcs1);
        }

        var profile = new OfflineSkinProfile(
            compactUuid,
            account.DisplayName,
            textureValue,
            Convert.ToBase64String(signatureBytes));
        profilesByUuid[compactUuid] = profile;
        profileUuidsByName[account.DisplayName] = compactUuid;
        textures[textureHash] = skinBytes;

        logger.LogInformation(
            "Offline skin profile registered. AccountId={AccountId} Uuid={Uuid} SkinModel={SkinModel} TextureHash={TextureHash} Port={Port}",
            account.Id,
            compactUuid,
            skinModel,
            textureHash,
            root.Port);
        return new OfflineSkinLaunchContext(
            root.AbsoluteUri,
            Convert.ToBase64String(CreateMetadataResponse()));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetimeCancellation.Cancel();
        listener?.Stop();
        try
        {
            acceptLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        signingKey.Dispose();
        startupLock.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task<Uri> EnsureServerStartedAsync(CancellationToken cancellationToken)
    {
        if (apiRoot is not null)
            return apiRoot;

        await startupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (apiRoot is not null)
                return apiRoot;

            var startedListener = new TcpListener(IPAddress.Loopback, 0);
            startedListener.Start();
            var endpoint = (IPEndPoint)startedListener.LocalEndpoint;
            listener = startedListener;
            apiRoot = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            acceptLoopTask = AcceptLoopAsync(startedListener, lifetimeCancellation.Token);
            logger.LogInformation(
                "Offline skin Yggdrasil service started. Address={Address} Port={Port}",
                IPAddress.Loopback,
                endpoint.Port);
            return apiRoot;
        }
        catch
        {
            listener?.Stop();
            listener = null;
            apiRoot = null;
            throw;
        }
        finally
        {
            startupLock.Release();
        }
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await activeListener.AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError("Offline skin Yggdrasil accept loop stopped unexpectedly. ErrorType={ErrorType}",
                exception.GetType().FullName);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellation)
    {
        using (client)
        using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation))
        {
            requestCancellation.CancelAfter(RequestTimeout);
            var cancellationToken = requestCancellation.Token;
            await using var stream = client.GetStream();
            try
            {
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(stream, CreateResponse(request), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                try
                {
                    await WriteResponseAsync(
                        stream,
                        new HttpResponse(400, "Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(exception.Message)),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogDebug("Offline skin Yggdrasil request failed. ErrorType={ErrorType}",
                    exception.GetType().FullName);
            }
        }
    }

    private HttpResponse CreateResponse(HttpRequest request)
    {
        var requestUri = Uri.TryCreate(
            new Uri("http://127.0.0.1/"),
            request.Target,
            out var parsed)
            ? parsed
            : null;
        if (requestUri is null)
            return JsonResponse(400, new { error = "Bad request target." });

        var path = requestUri.AbsolutePath;
        if (request.Method == "GET" && path == "/")
            return new HttpResponse(200, "OK", "application/json; charset=utf-8", CreateMetadataResponse());

        if (request.Method == "POST" && path == "/api/profiles/minecraft")
            return CreateBatchProfileResponse(request.Body);

        const string profilePrefix = "/sessionserver/session/minecraft/profile/";
        if (request.Method == "GET" && path.StartsWith(profilePrefix, StringComparison.Ordinal))
        {
            var uuid = NormalizeUuidOrEmpty(path[profilePrefix.Length..]);
            if (uuid.Length == 0 || !profilesByUuid.TryGetValue(uuid, out var profile))
                return new HttpResponse(204, "No Content", null, []);
            var includeSignature = string.Equals(
                GetQueryValue(requestUri.Query, "unsigned"),
                "false",
                StringComparison.OrdinalIgnoreCase);
            return JsonResponse(200, CreateProfilePayload(profile, includeSignature));
        }

        const string texturePrefix = "/textures/";
        if (request.Method == "GET" && path.StartsWith(texturePrefix, StringComparison.Ordinal))
        {
            var hash = path[texturePrefix.Length..];
            return hash.Length > 0 && textures.TryGetValue(hash, out var texture)
                ? new HttpResponse(200, "OK", "image/png", texture, "public, max-age=31536000, immutable")
                : new HttpResponse(404, "Not Found", null, []);
        }

        return new HttpResponse(404, "Not Found", null, []);
    }

    private HttpResponse CreateBatchProfileResponse(byte[] body)
    {
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(body);
            if (names is null || names.Length > MaximumBatchNames)
                return JsonResponse(400, new { error = "Invalid profile query." });

            var profiles = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => profileUuidsByName.TryGetValue(name, out var uuid)
                    && profilesByUuid.TryGetValue(uuid, out var profile)
                        ? new ProfileSummary(profile.Uuid, profile.Name)
                        : null)
                .Where(profile => profile is not null)
                .ToArray();
            return JsonResponse(200, profiles);
        }
        catch (JsonException)
        {
            return JsonResponse(400, new { error = "Invalid JSON." });
        }
    }

    private byte[] CreateMetadataResponse()
    {
        string publicKey;
        lock (signingGate)
            publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            meta = new Dictionary<string, object>
            {
                ["implementationName"] = "BlockHelm Offline Skin Service",
                ["implementationVersion"] = "1",
                ["serverName"] = "BlockHelm Offline Skin Service",
                ["feature.non_email_login"] = true
            },
            skinDomains = new[] { "127.0.0.1", "localhost" },
            signaturePublickey = publicKey
        });
    }

    private static object CreateProfilePayload(
        OfflineSkinProfile profile,
        bool includeSignature)
    {
        var property = new Dictionary<string, string>
        {
            ["name"] = "textures",
            ["value"] = profile.TextureValue
        };
        if (includeSignature)
            property["signature"] = profile.TextureSignature;
        return new
        {
            id = profile.Uuid,
            name = profile.Name,
            properties = new[] { property }
        };
    }

    private static byte[] CreateTextureValue(
        string uuid,
        string username,
        string textureUrl,
        MinecraftSkinModel model)
    {
        object skin = model == MinecraftSkinModel.Slim
            ? new { url = textureUrl, metadata = new { model = "slim" } }
            : new { url = textureUrl };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            profileId = uuid,
            profileName = username,
            textures = new Dictionary<string, object> { ["SKIN"] = skin }
        });
    }

    private static async Task<byte[]> ReadSkinBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumSkinBytes)
            throw new InvalidDataException("The active offline skin file is missing or too large.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        return bytes.StartsWith(signature);
    }

    private static string? ResolveLocalPath(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return uri.IsFile ? Path.GetFullPath(uri.LocalPath) : null;
        return Path.IsPathFullyQualified(source) ? Path.GetFullPath(source) : null;
    }

    private static string NormalizeUuid(string value)
    {
        var normalized = NormalizeUuidOrEmpty(value);
        return normalized.Length == 0
            ? throw new InvalidDataException("The offline skin profile UUID is invalid.")
            : normalized;
    }

    private static string NormalizeUuidOrEmpty(string value)
    {
        var compact = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return Guid.TryParseExact(compact, "N", out var parsed)
            ? parsed.ToString("N")
            : string.Empty;
    }

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.OrdinalIgnoreCase))
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }

    private static async Task<HttpRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var matched = 0;
        byte[] terminator = [13, 10, 13, 10];
        while (matched < terminator.Length)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new HttpRequestException("Unexpected end of request headers.");
            headerBytes.Add(buffer[0]);
            if (headerBytes.Count > MaximumHeaderBytes)
                throw new HttpRequestException("Request headers are too large.");
            matched = buffer[0] == terminator[matched]
                ? matched + 1
                : buffer[0] == terminator[0] ? 1 : 0;
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
            throw new HttpRequestException("Invalid request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
                break;
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new HttpRequestException("Invalid request header.");
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthText)
            && (!int.TryParse(contentLengthText, out contentLength)
                || contentLength < 0
                || contentLength > MaximumRequestBodyBytes))
        {
            throw new HttpRequestException("Request body is too large.");
        }
        var body = new byte[contentLength];
        if (contentLength > 0)
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new HttpRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1],
            body);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(response.StatusCode)
            .Append(' ')
            .Append(response.Reason)
            .Append("\r\nConnection: close\r\nContent-Length: ")
            .Append(response.Body.Length)
            .Append("\r\n");
        if (!string.IsNullOrWhiteSpace(response.ContentType))
            headers.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(response.CacheControl))
            headers.Append("Cache-Control: ").Append(response.CacheControl).Append("\r\n");
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken)
            .ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HttpResponse JsonResponse(int statusCode, object payload)
    {
        return new HttpResponse(
            statusCode,
            statusCode == 200 ? "OK" : "Bad Request",
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    private sealed record OfflineSkinProfile(
        string Uuid,
        string Name,
        string TextureValue,
        string TextureSignature);

    private sealed record ProfileSummary(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record HttpRequest(string Method, string Target, byte[] Body);

    private sealed record HttpResponse(
        int StatusCode,
        string Reason,
        string? ContentType,
        byte[] Body,
        string? CacheControl = null);
}
