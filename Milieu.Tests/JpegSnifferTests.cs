using System.IO;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class JpegSnifferTests
{
    [Fact] // [EDGE-hdri-backdrop-56]
    public void Baseline_Progressive_Truncated()
    {
        using var t = new TempDir();
        var baseline = File.ReadAllBytes(SyntheticImages.WriteJpg(t.Path, "b.jpg", 16, 16));
        Assert.False(JpegSniffer.IsProgressive(baseline));

        Assert.False(JpegSniffer.IsProgressive(SyntheticImages.JpegHeader(0xC0, 64, 32)));
        Assert.False(JpegSniffer.IsProgressive(SyntheticImages.JpegHeader(0xC1, 64, 32)));
        foreach (byte sof in new byte[] { 0xC2, 0xC6, 0xCA, 0xCE })
            Assert.True(JpegSniffer.IsProgressive(SyntheticImages.JpegHeader(sof, 64, 32)));

        Assert.True(JpegSniffer.IsProgressive(new byte[] { 0xFF, 0xD8 }));         // truncated after SOI
        Assert.True(JpegSniffer.IsProgressive(new byte[] { 1, 2, 3, 4 }));         // not a JPEG at all
        Assert.True(JpegSniffer.IsProgressive(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x40 })); // segment runs off the end
    }
}
