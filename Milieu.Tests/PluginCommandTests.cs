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
}
