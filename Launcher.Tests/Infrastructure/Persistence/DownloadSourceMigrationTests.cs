/*
 * BlockHelm Launcher
 * Copyright (C) 2026 Quan Zhou
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Text.Json.Nodes;
using Launcher.Domain.Models;
using Launcher.Infrastructure.Persistence;

namespace Launcher.Tests.Infrastructure.Persistence;

public sealed class DownloadSourceMigrationTests : TestTempDirectory
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(999)]
    public async Task ExistingSettingsAreMigratedOnceAndSubsequentManualChoiceIsPreserved(int oldPreference)
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "settings.json");
        await File.WriteAllTextAsync(path, new JsonObject
        {
            ["Revision"] = 7, ["Theme"] = "Light", ["DownloadSourcePreference"] = oldPreference
        }.ToJsonString());
        var service = new JsonSettingsService(TempRoot);
        var settings = await service.LoadAsync();
        Assert.Equal(DownloadSourcePreference.Auto, settings.DownloadSourcePreference);
        Assert.Equal(1, settings.DownloadSourceSelectionVersion);
        Assert.Equal(8, settings.Revision);
        Assert.Equal("Light", settings.Theme);
        var secondService = new JsonSettingsService(TempRoot);
        var otherCopy = await secondService.LoadAsync();
        settings.DownloadSourcePreference = DownloadSourcePreference.Official;
        await service.SaveAsync(settings);
        otherCopy.MaximumDownloadConcurrency = 16;
        await secondService.SaveAsync(otherCopy);
        var result = await new JsonSettingsService(TempRoot).LoadAsync();
        Assert.Equal(DownloadSourcePreference.Official, result.DownloadSourcePreference);
        Assert.Equal(16, result.MaximumDownloadConcurrency);
        Assert.Equal(1, result.DownloadSourceSelectionVersion);
        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(path))!["DownloadSourceSelectionVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task MigrationWriteFailureStillLoadsSettingsAndRetriesWithoutChangingTheStoredRevision()
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, "settings.json");
        const string original = """{"Revision":7,"Theme":"Light","DownloadSourcePreference":1}""";
        await File.WriteAllTextAsync(path, original);
        var service = new JsonSettingsService(TempRoot);
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var settings = await service.LoadAsync();
            Assert.Equal(DownloadSourcePreference.Auto, settings.DownloadSourcePreference);
            Assert.Equal("Light", settings.Theme);
            Assert.Equal(7, settings.Revision);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(TempRoot, "*.tmp"));
        }

        var retried = await new JsonSettingsService(TempRoot).LoadAsync();
        Assert.Equal(DownloadSourcePreference.Auto, retried.DownloadSourcePreference);
        Assert.Equal(8, retried.Revision);
        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(path))!["DownloadSourceSelectionVersion"]!.GetValue<int>());
        retried.DownloadSourcePreference = DownloadSourcePreference.BmclApi;
        await new JsonSettingsService(TempRoot).SaveAsync(retried);
        Assert.Equal(DownloadSourcePreference.BmclApi, (await service.LoadAsync()).DownloadSourcePreference);
    }

    [Fact]
    public async Task FreshSettingsDefaultToAutoAndManualMirrorSurvivesRestart()
    {
        var service = new JsonSettingsService(TempRoot);
        Assert.Equal(DownloadSourcePreference.Auto, (await service.LoadAsync()).DownloadSourcePreference);
        await service.UpdateAsync(settings => settings.DownloadSourcePreference = DownloadSourcePreference.BmclApi);
        Assert.Equal(DownloadSourcePreference.BmclApi, (await new JsonSettingsService(TempRoot).LoadAsync()).DownloadSourcePreference);
    }
}
