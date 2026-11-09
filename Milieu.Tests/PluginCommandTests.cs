using Xunit;

namespace Milieu.Tests;

public class PluginCommandTests
{
    [Fact] // [AC-hdri-backdrop-7]
    public void MilieuOff_CallsHide()
    {
        Assert.Equal(CommandAction.Hide, Plugin.ParseCommand("off"));
        Assert.Equal(CommandAction.Hide, Plugin.ParseCommand(" OFF "));
        Assert.Equal(CommandAction.ToggleWindow, Plugin.ParseCommand(""));
        Assert.Equal(CommandAction.ToggleWindow, Plugin.ParseCommand("on"));
        Assert.Equal(CommandAction.ToggleWindow, Plugin.ParseCommand("offset"));
    }

    [Theory]
    [InlineData("vfxlight 0", 0, true)]
    [InlineData("vfxlight 1", 1, true)]
    [InlineData("  VFXLIGHT   2 ", 2, true)]
    [InlineData("vfxlight 3", 3, true)]
    [InlineData("vfxlight 4", 4, true)]
    [InlineData("vfxlight 5", null, true)]
    [InlineData("vfxlight -1", null, true)]
    [InlineData("vfxlight +1", null, true)]
    [InlineData("vfxlight x", null, true)]
    [InlineData("vfxlight", null, true)]
    [InlineData("vfxlight 1 2", null, true)]
    [InlineData("vfxlightx 1", null, false)]
    [InlineData("off", null, false)]
    [InlineData("", null, false)]
    public void VfxLight_ParsesOnlyModesZeroToFour(string args, int? expected, bool isVfxLight)
    {
        Assert.Equal(expected, Plugin.ParseVfxLight(args));
        Assert.Equal(isVfxLight, Plugin.IsVfxLightCommand(args));
    }

    [Fact]
    public void VfxLight_DoesNotChangeOffHandling()
    {
        Assert.Equal(CommandAction.Hide, Plugin.ParseCommand("off"));
        Assert.Equal(CommandAction.ToggleWindow, Plugin.ParseCommand("vfxlight 1"));
    }
}
