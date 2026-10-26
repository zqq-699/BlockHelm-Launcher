/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts;

internal sealed class MicrosoftClientIdProvider
{
    internal const string EnvironmentVariableName = "BLOCKHELM_MICROSOFT_CLIENT_ID";
    internal const string LocalConfigurationFileName = "microsoft-client-id";
    internal const string EmbeddedResourceName = "Launcher.Infrastructure.Accounts.microsoft-client-id";

    private readonly ILogger<MicrosoftClientIdProvider> logger;
    private readonly Func<string?> embeddedValueProvider;
    private readonly Func<string?> environmentValueProvider;
    private readonly Func<IEnumerable<string>> localConfigurationPathProvider;

    public MicrosoftClientIdProvider(
        ILogger<MicrosoftClientIdProvider>? logger = null,
        Func<string?>? embeddedValueProvider = null,
        Func<string?>? environmentValueProvider = null,
        Func<IEnumerable<string>>? localConfigurationPathProvider = null)
    {
        this.logger = logger ?? NullLogger<MicrosoftClientIdProvider>.Instance;
        this.embeddedValueProvider = embeddedValueProvider ?? ReadEmbeddedValue;
        this.environmentValueProvider = environmentValueProvider
            ?? (() => Environment.GetEnvironmentVariable(EnvironmentVariableName));
        this.localConfigurationPathProvider = localConfigurationPathProvider ?? EnumerateDefaultLocalConfigurationPaths;
    }

    public string GetRequiredClientId()
    {
        var embeddedValue = embeddedValueProvider();
        if (TryNormalize(embeddedValue, out var clientId))
        {
            logger.LogDebug(
                "Resolved Microsoft application client ID from embedded configuration. ResourceName={ResourceName}",
                EmbeddedResourceName);
            return clientId;
        }

        foreach (var path in localConfigurationPathProvider().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var value = File.ReadAllText(path);
                if (TryNormalize(value, out clientId))
                {
                    logger.LogDebug(
                        "Resolved Microsoft application client ID from local configuration. ConfigurationPath={ConfigurationPath}",
                        path);
                    return clientId;
                }

                logger.LogWarning(
                    "Ignored invalid Microsoft application client ID configuration. ConfigurationPath={ConfigurationPath}",
                    path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(
                    "Failed to read Microsoft application client ID configuration. ConfigurationPath={ConfigurationPath} ErrorType={ErrorType}",
                    path,
                    exception.GetType().FullName);
            }
        }

        var environmentValue = environmentValueProvider();
        if (TryNormalize(environmentValue, out clientId))
        {
            logger.LogDebug(
                "Resolved Microsoft application client ID from environment configuration. VariableName={VariableName}",
                EnvironmentVariableName);
            return clientId;
        }

        throw new MicrosoftAuthenticationConfigurationException(
            "Microsoft account login is unavailable because the application client ID is not configured.");
    }

    internal static bool TryNormalize(string? value, out string clientId)
    {
        var normalized = value?.Trim();
        if (Guid.TryParse(normalized, out var parsed) && parsed != Guid.Empty)
        {
            clientId = parsed.ToString("D");
            return true;
        }

        clientId = string.Empty;
        return false;
    }

    private static string? ReadEmbeddedValue()
    {
        try
        {
            using var stream = typeof(MicrosoftClientIdProvider)
                .Assembly
                .GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateDefaultLocalConfigurationPaths()
    {
        yield return Path.Combine(
            Directory.GetCurrentDirectory(),
            ".local-secrets",
            LocalConfigurationFileName);
        yield return Path.Combine(
            AppContext.BaseDirectory,
            ".local-secrets",
            LocalConfigurationFileName);
    }
}
