using System.IO;
using Xunit;

namespace Milieu.Tests;

public class LogTextTests
{
    [Fact] // [EDGE-hdri-backdrop-31]
    public void Sanitize_ReplacesPathAndDirectory()
    {
        const string image = @"C:\Users\someone\secret\pano.hdr";
        const string config = @"C:\Users\someone\AppData\Roaming\XIVLauncher\pluginConfigs\Milieu";
        var text = $"Could not find file '{image}'. Tmp {config}\\cache\\sky_1.atex.tmp also C:/Users/someone/secret/pano.hdr";
        var clean = LogText.Sanitize(text, image, config);
        Assert.DoesNotContain("someone", clean);
        Assert.DoesNotContain("secret", clean);
        Assert.Contains("pano.hdr", clean);
        Assert.Contains("sky_1.atex.tmp", clean);
        Assert.Equal("plain", LogText.Sanitize("plain", null, ""));

        var ex = new FileNotFoundException($"Could not find file '{image}'.");
        var described = LogText.Describe(ex, image);
        Assert.StartsWith("FileNotFoundException: ", described);
        Assert.DoesNotContain("someone", described);
    }
}
