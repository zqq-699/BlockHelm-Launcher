using Launcher.App.Resources;
using Launcher.App.ViewModels.Download;
using Launcher.App.ViewModels.GameSettings;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Shared;

public sealed class MinecraftVersionTypeLabelTests
{
    [Theory]
    [InlineData("20w14infinite", true)]
    [InlineData("24w14a", false)]
    public void VersionItems_DisplayMatchingCategory(string version, bool isAprilFools)
    {
        var expected = isAprilFools ? Strings.Download_AprilFoolsCategory : Strings.Download_SnapshotCategory;
        var download = new DownloadMinecraftVersionItem(new MinecraftVersionInfo(version, "snapshot", false));
        var instance = new GameSettingsInstanceItem(new GameInstance { MinecraftVersion = version }, "snapshot");

        Assert.Equal(expected, download.TypeLabel);
        Assert.Equal(expected, instance.TypeLabel);
    }
}
