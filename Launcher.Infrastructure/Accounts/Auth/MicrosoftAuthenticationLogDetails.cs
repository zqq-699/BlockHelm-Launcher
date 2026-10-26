/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net.Http;
using System.Net.Sockets;
using CmlLib.Core.Auth.Microsoft;
using Microsoft.Identity.Client;
using XboxAuthNet.OAuth;

namespace Launcher.Infrastructure.Accounts;

// Authentication exception messages and ToString() can contain OAuth responses or credentials.
// Log only code-defined stages, exception types, numeric status codes, and framework error categories.
internal sealed record MicrosoftAuthenticationLogDetails(
    string Stage,
    string ErrorType,
    int? HttpStatus,
    HttpRequestError? HttpRequestError,
    SocketError? SocketError,
    int? NativeErrorCode)
{
    internal static MicrosoftAuthenticationLogDetails From(Exception exception, string? stage = null)
    {
        var cause = exception.InnerException ?? exception;
        int? httpStatus = null;
        HttpRequestError? httpRequestError = null;
        SocketError? socketError = null;
        int? nativeErrorCode = null;

        for (var current = (Exception?)cause; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case MinecraftProfileRequestException profileException:
                    httpStatus ??= (int)profileException.StatusCode;
                    break;
                case JEAuthException jeException:
                    var status = MicrosoftAuthProvider.ResolveStatusCode(jeException);
                    if (status != 0)
                        httpStatus ??= status;
                    break;
                case MsalServiceException msalException when msalException.StatusCode is >= 100 and <= 599:
                    httpStatus ??= msalException.StatusCode;
                    break;
                case MicrosoftOAuthException oauthException when oauthException.StatusCode is >= 100 and <= 599:
                    httpStatus ??= oauthException.StatusCode;
                    break;
                case HttpRequestException requestException:
                    if (requestException.StatusCode is { } requestStatus)
                        httpStatus ??= (int)requestStatus;
                    httpRequestError ??= requestException.HttpRequestError;
                    break;
                case SocketException socketException:
                    socketError ??= socketException.SocketErrorCode;
                    nativeErrorCode ??= socketException.NativeErrorCode;
                    break;
            }
        }

        return new MicrosoftAuthenticationLogDetails(
            (exception as MicrosoftAccountAuthenticationException)?.Stage ?? stage ?? "Unknown",
            cause.GetType().FullName ?? cause.GetType().Name,
            httpStatus,
            httpRequestError,
            socketError,
            nativeErrorCode);
    }
}
