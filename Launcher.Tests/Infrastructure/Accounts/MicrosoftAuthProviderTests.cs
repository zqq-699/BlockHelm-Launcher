/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using CmlLib.Core.Auth.Microsoft;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Launcher.Application.Accounts;
using Launcher.Infrastructure.Accounts;

namespace Launcher.Tests.Infrastructure.Accounts;

public sealed class MicrosoftAuthProviderTests
{
    [Theory]
    [InlineData("401: Unauthorized", LaunchAccountSessionFailureReason.ReauthenticationRequired)]
    [InlineData("403: Forbidden", LaunchAccountSessionFailureReason.AuthenticationApplicationNotAuthorized)]
    [InlineData("503: ServiceUnavailable", LaunchAccountSessionFailureReason.AuthenticationServerUnavailable)]
    public void JeAuthExceptionWithoutStatusCodeIsClassifiedFromMessage(
        string message,
        LaunchAccountSessionFailureReason expectedReason)
    {
        // CmlLib 在响应体不是预期 JSON 时抛出的异常只有消息，StatusCode 保持为 0。
        var jeException = new JEAuthException(message);
        Assert.Equal(0, jeException.StatusCode);

        var translated = MicrosoftAuthProvider.TranslateAuthenticationException(jeException);

        Assert.Equal(expectedReason, translated.Reason);
    }

    [Theory]
    [InlineData(401, LaunchAccountSessionFailureReason.ReauthenticationRequired)]
    [InlineData(403, LaunchAccountSessionFailureReason.AuthenticationApplicationNotAuthorized)]
    [InlineData(503, LaunchAccountSessionFailureReason.AuthenticationServerUnavailable)]
    public void JeAuthExceptionWithStatusCodeKeepsUsingTheReportedStatus(
        int statusCode,
        LaunchAccountSessionFailureReason expectedReason)
    {
        var jeException = new JEAuthException("UNAUTHORIZED", "UNAUTHORIZED", "rejected", statusCode);

        var translated = MicrosoftAuthProvider.TranslateAuthenticationException(jeException);

        Assert.Equal(expectedReason, translated.Reason);
    }

    [Theory]
    [InlineData("Minecraft authentication failed.")]
    [InlineData("401k plan")]
    [InlineData("99: Nonsense")]
    public void JeAuthExceptionWithoutRecoverableStatusFallsBackToInvalidResponse(string message)
    {
        var jeException = new JEAuthException(message);

        var translated = MicrosoftAuthProvider.TranslateAuthenticationException(jeException);

        Assert.Equal(LaunchAccountSessionFailureReason.InvalidAuthenticationResponse, translated.Reason);
    }

    [Fact]
    public void AuthenticationLogDetailsKeepStatusAndExcludeRawAuthenticationResponse()
    {
        const string secret = "sensitive-access-token";
        var original = new JEAuthException($"429: retry; access_token={secret}");
        var translated = MicrosoftAuthProvider.TranslateAuthenticationException(original);
        translated.Stage = "InteractiveAuthentication";

        var details = MicrosoftAuthenticationLogDetails.From(translated);

        Assert.Equal("InteractiveAuthentication", details.Stage);
        Assert.Equal(typeof(JEAuthException).FullName, details.ErrorType);
        Assert.Equal(429, details.HttpStatus);
        Assert.DoesNotContain(secret, details.ToString());
    }

    [Fact]
    public void AuthenticationLogDetailsKeepNetworkCategoryWithoutExceptionMessage()
    {
        const string secret = "sensitive-refresh-token";
        var networkError = new HttpRequestException(
            $"Authorization: Bearer {secret}",
            new SocketException((int)SocketError.HostNotFound),
            HttpStatusCode.BadGateway);
        var translated = MicrosoftAuthProvider.TranslateAuthenticationException(networkError);
        translated.Stage = "EntitlementVerification";

        var details = MicrosoftAuthenticationLogDetails.From(translated);

        Assert.Equal("EntitlementVerification", details.Stage);
        Assert.Equal(502, details.HttpStatus);
        Assert.Equal(SocketError.HostNotFound, details.SocketError);
        Assert.DoesNotContain(secret, details.ToString());
    }

    [Fact]
    public void AuthenticationLogDetailsExcludeUntrustedServerErrorFields()
    {
        const string secret = "sensitive-authorization-code";
        var original = new JEAuthException(
            $"code={secret}",
            $"error={secret}",
            $"response={secret}",
            400);
        var translated = MicrosoftAuthProvider.TranslateAuthenticationException(original);

        var details = MicrosoftAuthenticationLogDetails.From(translated, "InteractiveAuthentication");

        Assert.Equal(400, details.HttpStatus);
        Assert.DoesNotContain(secret, details.ToString());
    }

    [Fact]
    public void AuthenticationLogDetailsKeepProfileStatusWithoutResponseBody()
    {
        const string secret = "sensitive-profile-response";
        var exception = new MinecraftProfileRequestException(
            MinecraftProfileErrorKind.Unknown,
            HttpStatusCode.BadGateway,
            $"error={secret}",
            $"response={secret}");

        var details = MicrosoftAuthenticationLogDetails.From(exception, "ProfileLookup");

        Assert.Equal("ProfileLookup", details.Stage);
        Assert.Equal(502, details.HttpStatus);
        Assert.DoesNotContain(secret, details.ToString());
    }
}
