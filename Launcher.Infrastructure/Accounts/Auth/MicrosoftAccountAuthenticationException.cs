/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Application.Accounts;

namespace Launcher.Infrastructure.Accounts;

internal sealed class MicrosoftAccountAuthenticationException : Exception
{
    public MicrosoftAccountAuthenticationException(
        LaunchAccountSessionFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public LaunchAccountSessionFailureReason Reason { get; }

    internal string? Stage { get; set; }
}
