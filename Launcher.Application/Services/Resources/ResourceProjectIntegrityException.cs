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

using Launcher.Domain.Models;

namespace Launcher.Application.Services;

public enum ResourceProjectIntegrityFailureReason
{
    MissingTrustedHash,
    InvalidMetadata,
    LengthMismatch,
    HashMismatch
}

public sealed class ResourceProjectIntegrityException : Exception, ISafeDiagnosticException
{
    public ResourceProjectIntegrityException(
        string versionId,
        ResourceProjectIntegrityFailureReason reason,
        ResourceFileHashAlgorithm? algorithm = null,
        Exception? innerException = null)
        : base($"Resource project integrity verification failed. VersionId={versionId} Reason={reason}", innerException)
    {
        VersionId = versionId;
        Reason = reason;
        Algorithm = algorithm;
    }

    public string VersionId { get; }

    public ResourceProjectIntegrityFailureReason Reason { get; }

    public ResourceFileHashAlgorithm? Algorithm { get; }
    public string DiagnosticCode => "ResourceIntegrity";
    public IReadOnlyDictionary<string, object?> DiagnosticFields => new Dictionary<string, object?>
    {
        ["VersionId"] = VersionId, ["IntegrityFailure"] = Reason.ToString(), ["HashAlgorithm"] = Algorithm?.ToString()
    };
}
