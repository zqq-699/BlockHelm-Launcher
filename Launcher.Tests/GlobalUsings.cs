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

global using Launcher.App.ViewModels.Account;
global using Launcher.App.ViewModels.Download;
global using Launcher.App.ViewModels.GameSettings;
global using Launcher.App.ViewModels.Home;
global using Launcher.App.ViewModels.Resources;
global using Launcher.App.ViewModels.Settings;
global using Launcher.App.ViewModels.Shared;
global using Launcher.App.ViewModels.Shell;
global using Launcher.Domain.Models;
global using Launcher.Tests.Fakes;
global using Launcher.Tests.Helpers;
global using static Launcher.Tests.Helpers.TestMinecraftDirectoryDiscoveries;

// Test classes share process-wide download limits, log configuration, and WPF state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
