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
    [InlineData("vfxlight 0", 0)]
    [InlineData("vfxlight 1", 1)]
    [InlineData("  VFXLIGHT   2 ", 2)]
    [InlineData("vfxlight 3", 3)]
    [InlineData("vfxlight 4", null)]
    [InlineData("vfxlight -1", null)]
    [InlineData("vfxlight x", null)]
    [InlineData("vfxlight", null)]
    [InlineData("vfxlight 1 2", null)]
    [InlineData("off", null)]
    [InlineData("", null)]
    public void VfxLight_ParsesOnlyModesZeroToThree(string args, int? expected)
    {
        Assert.Equal(expected, Plugin.ParseVfxLight(args));
        Assert.Equal(args.Trim().StartsWith("vfxlight", System.StringComparison.OrdinalIgnoreCase), Plugin.IsVfxLightCommand(args));
    }
}
