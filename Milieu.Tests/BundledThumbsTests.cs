using System.IO;
using Milieu.Assets;
using Milieu.Gui;
using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class BundledThumbsTests
{
    [Fact] // [AC-hdri-backdrop-14]
    public void EveryPinnedStemHasEmbedded256x128Thumb()
    {
        var asm = typeof(Plugin).Assembly;
        foreach (var pin in BackdropPins.All)
        {
            var name = SceneGridViewModel.ThumbResource(pin.Stem);
            using var s = asm.GetManifestResourceStream(name);
            Assert.True(s != null, $"{name} is not embedded");
            var h = HeaderProbe.Jpeg(s!);
            Assert.NotNull(h);
            Assert.Equal((256, 128), (h!.Value.Width, h.Value.Height));
            Assert.True(s!.Length < 32 * 1024, $"{name} is {s.Length} bytes");
        }
        Assert.NotNull(asm.GetManifestResourceStream("Milieu.Resources.Thumbs.moon.jpg"));
    }
}
