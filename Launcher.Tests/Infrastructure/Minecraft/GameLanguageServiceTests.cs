/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 *
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Launcher.Domain.Models;
using Launcher.Infrastructure.Minecraft;

namespace Launcher.Tests.Infrastructure.Minecraft;

public sealed class GameLanguageServiceTests : TestTempDirectory
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LanguageSynchronizationDoesNotRecreateOrModifyAnObsoleteInstance(bool replace)
    {
        var instance = CreateInstance("1.20.1");
        Directory.CreateDirectory(instance.InstanceDirectory);
        Launcher.Infrastructure.Persistence.InstanceDirectoryOwnership.Observe(instance);
        Directory.Move(instance.InstanceDirectory, instance.InstanceDirectory + "-moved");
        if (replace) Directory.CreateDirectory(instance.InstanceDirectory);
        await Assert.ThrowsAnyAsync<Exception>(() => new GameLanguageService().ApplyLauncherLanguageAsync(instance, LauncherLanguages.SimplifiedChinese));
        Assert.Equal(replace, Directory.Exists(instance.InstanceDirectory));
        Assert.False(File.Exists(Path.Combine(instance.InstanceDirectory, "options.txt")));
    }

    // 1.11（16w32a）之前的语言代码大小写敏感：写成 zh_cn 游戏找不到该语言，会回退成
    // en_US 并改写 options.txt，于是每次启动都变回英文。
    [Theory]
    [InlineData("1.7.10", "zh_CN")]
    [InlineData("1.10.2", "zh_CN")]
    [InlineData("1.11", "zh_cn")]
    [InlineData("1.11-pre1", "zh_cn")]
    [InlineData("1.12.2", "zh_cn")]
    [InlineData("1.21.4", "zh_cn")]
    [InlineData("16w31b", "zh_CN")]
    [InlineData("16w32a", "zh_cn")]
    [InlineData("17w06a", "zh_cn")]
    [InlineData("15w51b", "zh_CN")]
    public void LanguageCodeCasingFollowsTheGameVersion(string minecraftVersion, string expected)
    {
        Assert.Equal(
            expected,
            GameLanguageService.ResolveMinecraftLanguage(LauncherLanguages.SimplifiedChinese, minecraftVersion));
    }

    [Theory]
    [InlineData(LauncherLanguages.English, "en_US")]
    [InlineData(LauncherLanguages.TraditionalChinese, "zh_TW")]
    [InlineData(LauncherLanguages.Japanese, "ja_JP")]
    [InlineData(LauncherLanguages.SimplifiedChinese, "zh_CN")]
    public void EveryLanguageGetsTheLegacyCasingOnOldVersions(string launcherLanguage, string expected)
    {
        Assert.Equal(expected, GameLanguageService.ResolveMinecraftLanguage(launcherLanguage, "1.7.10"));
    }

    // 自定义版本名基本来自现代整合包，解析不出来时按新版处理。
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("我的整合包")]
    public void UnparsableVersionsKeepTheModernCasing(string? minecraftVersion)
    {
        Assert.Equal(
            "zh_cn",
            GameLanguageService.ResolveMinecraftLanguage(LauncherLanguages.SimplifiedChinese, minecraftVersion));
    }

    [Fact]
    public async Task ExistingLanguageLineIsRewrittenForLegacyVersions()
    {
        var instance = CreateInstance("1.7.10");
        var optionsPath = Path.Combine(instance.InstanceDirectory, "options.txt");
        Directory.CreateDirectory(instance.InstanceDirectory);
        await File.WriteAllLinesAsync(optionsPath, ["version:1343", "lang:en_US", "fov:0.5"]);

        var applied = await new GameLanguageService()
            .ApplyLauncherLanguageAsync(instance, LauncherLanguages.SimplifiedChinese);

        Assert.Equal("zh_CN", applied);
        // 其余设置必须原样保留，语言同步不能顺手清掉玩家的游戏设置。
        Assert.Equal(["version:1343", "lang:zh_CN", "fov:0.5"], await File.ReadAllLinesAsync(optionsPath));
    }

    [Fact]
    public async Task ManuallyFixedLanguageIsLeftAlone()
    {
        var instance = CreateInstance("1.7.10");
        var optionsPath = Path.Combine(instance.InstanceDirectory, "options.txt");
        Directory.CreateDirectory(instance.InstanceDirectory);
        await File.WriteAllLinesAsync(optionsPath, ["lang:zh_CN"]);
        var writtenAt = File.GetLastWriteTimeUtc(optionsPath);

        await new GameLanguageService().ApplyLauncherLanguageAsync(instance, LauncherLanguages.SimplifiedChinese);

        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(optionsPath));
    }

    [Fact]
    public async Task LanguageLineIsAppendedWhenTheFileHasNone()
    {
        var instance = CreateInstance("1.20.1");
        var optionsPath = Path.Combine(instance.InstanceDirectory, "options.txt");
        Directory.CreateDirectory(instance.InstanceDirectory);
        await File.WriteAllLinesAsync(optionsPath, ["fov:0.5"]);

        await new GameLanguageService().ApplyLauncherLanguageAsync(instance, LauncherLanguages.SimplifiedChinese);

        Assert.Equal(["fov:0.5", "lang:zh_cn"], await File.ReadAllLinesAsync(optionsPath));
    }

    // 旧实例可能没有记录 MinecraftVersion，版本名通常以游戏版本开头。
    [Fact]
    public async Task VersionNameIsUsedWhenTheInstanceHasNoRecordedGameVersion()
    {
        var instance = CreateInstance(string.Empty);
        instance.VersionName = "1.7.10-forge-10.13.4.1614-1.7.10";
        Directory.CreateDirectory(instance.InstanceDirectory);

        var applied = await new GameLanguageService()
            .ApplyLauncherLanguageAsync(instance, LauncherLanguages.SimplifiedChinese);

        Assert.Equal("zh_CN", applied);
    }

    private GameInstance CreateInstance(string minecraftVersion) => new()
    {
        MinecraftVersion = minecraftVersion,
        VersionName = minecraftVersion,
        InstanceDirectory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"))
    };
}
