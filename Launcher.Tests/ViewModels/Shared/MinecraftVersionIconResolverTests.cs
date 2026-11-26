using Launcher.App.ViewModels.Shared;
using Launcher.Domain.Models;

namespace Launcher.Tests.ViewModels.Shared;

public sealed class MinecraftVersionIconResolverTests
{
    [Theory]
    [InlineData("snapshot", "20w14infinite", "/Assets/Icons/block/TNT_block.png")]
    [InlineData("snapshot", "1.RV-Pre1", "/Assets/Icons/block/TNT_block.png")]
    [InlineData("april_fools", "unknown", "/Assets/Icons/block/TNT_block.png")]
    [InlineData("snapshot", "24w14a", "/Assets/Icons/block/dirt_block.png")]
    public void Resolve_UsesAprilFoolsIconForRecognizedVersions(string type, string version, string expected)
    {
        Assert.Equal(expected, MinecraftVersionIconResolver.Resolve(type, version));
    }

    [Fact]
    public void Resolve_InstancePreservesCustomIcon()
    {
        var instance = new GameInstance { MinecraftVersion = "20w14infinite", IconSource = "custom.png" };

        Assert.Equal("custom.png", MinecraftVersionIconResolver.Resolve(instance, "snapshot"));
        instance.IconSource = null;
        Assert.Equal("/Assets/Icons/block/TNT_block.png", MinecraftVersionIconResolver.Resolve(instance, "snapshot"));
    }
}
