/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.IO;
using CmlLib.Core;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Infrastructure.Minecraft;

internal sealed class InstanceMinecraftPath(string directory) : MinecraftPath(directory)
{
    public override string GetVersionJsonPath(string versionName)
        => InstanceVersionFiles.TryResolveVersionJsonPath(Path.Combine(Versions, versionName), versionName)
           ?? base.GetVersionJsonPath(versionName);

    public override string GetVersionJarPath(string versionName)
        => InstanceVersionFiles.ResolveJarPath(Path.Combine(Versions, versionName), versionName);
}
