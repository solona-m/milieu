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

    [Theory] // [AC-synced-lighting-11] [EDGE-synced-lighting-31]
    [InlineData("vfxlight 3")]
    [InlineData("vfxlight")]
    [InlineData("VFXLIGHT x")]
    public void VfxLightExperiment_Removed_ArgsJustToggleTheWindow(string args)
    {
        Assert.Equal(CommandAction.ToggleWindow, Plugin.ParseCommand(args));
        Assert.Null(typeof(Plugin).GetMethod("ParseVfxLight"));
        Assert.Null(typeof(Milieu.GameFiles.DomeAvfxBuilder).GetField("TestLightMode"));
    }
}
