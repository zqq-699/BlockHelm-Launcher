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

using System.IO;
using Launcher.Application.Services;
using Launcher.Domain.Models;

namespace Launcher.Infrastructure.Minecraft;

public sealed class GameLanguageService : IGameLanguageService
{
    private const string LanguageKeyPrefix = "lang:";

    // 1.11（16w32a）把语言代码整体改成了全小写，同时重命名了语言文件。
    private static readonly MinecraftVersionNumber LowercaseLanguageCodeVersion = new(1, 11);
    private static readonly MinecraftSnapshotNumber LowercaseLanguageCodeSnapshot = new(16, 32);

    public async Task<string> ApplyLauncherLanguageAsync(
        GameInstance instance,
        string launcherLanguage,
        CancellationToken cancellationToken = default)
    {
        var instanceDirectory = Path.GetFullPath(instance.InstanceDirectory);
        using var instanceLease = Persistence.InstanceDirectoryOwnership.AcquireReadLease(instance);

        var optionsPath = Path.Combine(instanceDirectory, "options.txt");
        var minecraftLanguage = ResolveMinecraftLanguage(launcherLanguage, ResolveGameVersion(instance));
        var languageLine = LanguageKeyPrefix + minecraftLanguage;

        if (!File.Exists(optionsPath))
        {
            await File.WriteAllTextAsync(optionsPath, languageLine + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
            return minecraftLanguage;
        }

        var lines = (await File.ReadAllLinesAsync(optionsPath, cancellationToken).ConfigureAwait(false)).ToList();
        var languageLineIndex = lines.FindIndex(line =>
            line.StartsWith(LanguageKeyPrefix, StringComparison.OrdinalIgnoreCase));
        if (languageLineIndex >= 0)
        {
            // 语言代码在 1.11 以前大小写敏感，这里必须按序数比较，否则会漏掉需要改写大小写的情况。
            if (string.Equals(lines[languageLineIndex], languageLine, StringComparison.Ordinal))
                return minecraftLanguage;

            lines[languageLineIndex] = languageLine;
        }
        else
        {
            lines.Add(languageLine);
        }

        await File.WriteAllLinesAsync(optionsPath, lines, cancellationToken).ConfigureAwait(false);
        return minecraftLanguage;
    }

    /// <summary>
    /// 解析写入 options.txt 的语言代码。
    /// </summary>
    /// <remarks>
    /// 1.11 以前的语言代码是 <c>zh_CN</c> 这种写法，且大小写敏感：写成 <c>zh_cn</c> 时游戏
    /// 找不到该语言，会回退到 <c>en_US</c> 并把 options.txt 一起改掉，于是每次启动都变回英文。
    /// </remarks>
    internal static string ResolveMinecraftLanguage(string? launcherLanguage, string? minecraftVersion)
    {
        var languageCode = LauncherLanguages.Normalize(launcherLanguage) switch
        {
            LauncherLanguages.English => "en_us",
            LauncherLanguages.TraditionalChinese => "zh_tw",
            LauncherLanguages.Japanese => "ja_jp",
            _ => "zh_cn"
        };

        return UsesLowercaseLanguageCodes(minecraftVersion)
            ? languageCode
            : ToLegacyLanguageCode(languageCode);
    }

    /// <summary>
    /// 把 <c>zh_cn</c> 转成 1.11 以前使用的 <c>zh_CN</c>。
    /// </summary>
    private static string ToLegacyLanguageCode(string languageCode)
    {
        var separatorIndex = languageCode.IndexOf('_');
        return separatorIndex < 0
            ? languageCode
            : languageCode[..(separatorIndex + 1)] + languageCode[(separatorIndex + 1)..].ToUpperInvariant();
    }

    /// <summary>
    /// 版本号解析不出来时按新版处理：自定义版本名基本来自现代整合包，而写错的小写只影响旧版本。
    /// </summary>
    private static bool UsesLowercaseLanguageCodes(string? minecraftVersion)
    {
        if (MinecraftSnapshotNumber.TryParse(minecraftVersion, out var snapshot))
            return snapshot >= LowercaseLanguageCodeSnapshot;

        return !MinecraftVersionNumber.TryParse(minecraftVersion, out var version)
            || version >= LowercaseLanguageCodeVersion;
    }

    /// <summary>
    /// 优先用实例记录的游戏版本；缺失时退回版本名，它通常以游戏版本开头，例如
    /// <c>1.7.10-forge-10.13.4.1614-1.7.10</c>。
    /// </summary>
    private static string ResolveGameVersion(GameInstance instance) =>
        string.IsNullOrWhiteSpace(instance.MinecraftVersion)
            ? instance.VersionName
            : instance.MinecraftVersion;
}
