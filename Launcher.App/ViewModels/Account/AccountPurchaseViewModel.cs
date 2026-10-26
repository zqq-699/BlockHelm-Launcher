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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Resources;
using Launcher.App.Services;
using Launcher.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Launcher.App.ViewModels.Account;

public sealed partial class AccountPurchaseViewModel : ObservableObject
{
    private readonly IExternalLinkService externalLinkService;
    private readonly IStatusService statusService;
    private readonly IFloatingMessageService floatingMessageService;
    private readonly ILogger<AccountPurchaseViewModel> logger;

    public AccountPurchaseViewModel(
        IExternalLinkService externalLinkService,
        IStatusService statusService,
        IFloatingMessageService floatingMessageService,
        ILogger<AccountPurchaseViewModel>? logger = null)
    {
        this.externalLinkService = externalLinkService;
        this.statusService = statusService;
        this.floatingMessageService = floatingMessageService;
        this.logger = logger ?? NullLogger<AccountPurchaseViewModel>.Instance;
    }

    [RelayCommand]
    private void OpenMinecraftPurchasePage()
    {
        try
        {
            logger.LogDebug("Opening the official Minecraft purchase page.");
            if (externalLinkService.TryOpen(LauncherProjectLinks.MinecraftPurchaseUrl))
            {
                logger.LogInformation("Official Minecraft purchase page opened.");
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Failed to open the official Minecraft purchase page. ErrorType={ErrorType}",
                exception.GetType().FullName);
            ReportFailure();
            return;
        }

        logger.LogWarning("Failed to open the official Minecraft purchase page.");
        ReportFailure();
    }

    private void ReportFailure()
    {
        statusService.Report(Strings.Status_OpenMinecraftPurchasePageFailed);
        floatingMessageService.Show(Strings.Status_OpenMinecraftPurchasePageFailed);
    }
}
