/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Collections.Concurrent;
using Launcher.Application.Accounts;
using Launcher.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.Infrastructure.Accounts.ThirdParty;

internal sealed partial class ThirdPartyAccountService : IThirdPartyAccountService
{
    private const string ApiLocationHeader = "X-Authlib-Injector-API-Location";
    private readonly HttpClient httpClient;
    private readonly IThirdPartyAccountTokenStore tokenStore;
    private readonly IThirdPartyAccountAppearanceService? appearanceService;
    private readonly ILogger<ThirdPartyAccountService> logger;
    private readonly ConcurrentDictionary<string, PendingEmailLogin> pendingEmailLogins = new(StringComparer.Ordinal);
    private static readonly TimeSpan EmailLoginLifetime = TimeSpan.FromMinutes(10);

    public ThirdPartyAccountService(
        IThirdPartyAccountTokenStore tokenStore,
        LauncherPathProvider pathProvider,
        ILogger<ThirdPartyAccountService>? logger = null)
    {
        httpClient = LauncherHttpUserAgent.Apply(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        });
        this.tokenStore = tokenStore;
        this.logger = logger ?? NullLogger<ThirdPartyAccountService>.Instance;
        var thirdPartyDataDirectory = Path.Combine(pathProvider.DefaultAccountDataDirectory, "third-party");
        appearanceService = new ThirdPartyAccountAppearanceService(
            httpClient,
            new AccountAvatarService(httpClient, Path.Combine(thirdPartyDataDirectory, "avatars")),
            new AccountSkinCacheService(httpClient, Path.Combine(thirdPartyDataDirectory, "skins")),
            new AccountCapeCacheService(httpClient, Path.Combine(thirdPartyDataDirectory, "capes")),
            this.logger);
    }

    internal ThirdPartyAccountService(
        HttpClient httpClient,
        IThirdPartyAccountTokenStore tokenStore,
        IThirdPartyAccountAppearanceService? appearanceService = null,
        ILogger<ThirdPartyAccountService>? logger = null)
    {
        this.httpClient = LauncherHttpUserAgent.Apply(httpClient);
        this.tokenStore = tokenStore;
        this.appearanceService = appearanceService;
        this.logger = logger ?? NullLogger<ThirdPartyAccountService>.Instance;
    }

}
