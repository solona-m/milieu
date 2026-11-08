using System.IO;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class HeaderProbeTests
{
    private static HeaderProbe.Header? Jpeg(byte[] b) => HeaderProbe.Jpeg(new MemoryStream(b));

    [Fact] // [EDGE-hdri-backdrop-56]
    public void Jpeg_BaselineProgressiveTruncated()
    {
        using var t = new TempDir();
        var real = Jpeg(File.ReadAllBytes(SyntheticImages.WriteJpg(t.Path, "b.jpg", 16, 8)));
        Assert.Equal(new HeaderProbe.Header(16, 8, 8, 3, false), real);

        foreach (byte sof in new byte[] { 0xC0, 0xC1 })
            Assert.False(Jpeg(SyntheticImages.JpegHeader(sof, 64, 32))!.Value.Progressive);
        foreach (byte sof in new byte[] { 0xC2, 0xC6, 0xCA, 0xCE })
        {
            var h = Jpeg(SyntheticImages.JpegHeader(sof, 64, 32))!.Value;
            Assert.True(h.Progressive);
            Assert.Equal((64, 32), (h.Width, h.Height));
        }

        Assert.Null(Jpeg([0xFF, 0xD8]));                                   // truncated after SOI
        Assert.Null(Jpeg([1, 2, 3, 4]));                                   // not a JPEG
        Assert.Null(Jpeg([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x40]));           // segment runs off the end
        Assert.Null(Jpeg([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]));           // scan data before any frame header

        // Through the production path, an unreadable header is refused at probe time with DecodeFailed.
        var bad = t.File("bad.jpg");
        File.WriteAllBytes(bad, [0xFF, 0xD8]);
        Assert.Equal(MessageKey.DecodeFailed, Assert.Throws<MilieuException>(() => ImageLoader.Probe(bad)).Status.Key);
        var prog = t.File("prog.jpg");
        File.WriteAllBytes(prog, SyntheticImages.JpegHeader(0xCA, 100, 50));
        Assert.True(ImageLoader.Probe(prog).Progressive);
    }

    [Fact]
    public void Png_ReadsIhdr()
    {
        using var t = new TempDir();
        using (var s = File.OpenRead(SyntheticImages.WritePngHeaderOnly(t.Path, "a.png", 300, 150, 16, 6)))
            Assert.Equal(new HeaderProbe.Header(300, 150, 16, 4, false), HeaderProbe.Png(s));
        using (var s = File.OpenRead(SyntheticImages.WritePngHeaderOnly(t.Path, "g.png", 8, 4, 8, 0)))
            Assert.Equal(1, HeaderProbe.Png(s)!.Value.Components);
        Assert.Null(HeaderProbe.Png(new MemoryStream([0x89, 0x50, 0x4E, 0x47])));
        using (var s = File.OpenRead(SyntheticImages.WritePngHeaderOnly(t.Path, "bad.png", 8, 4, 8, 5)))
            Assert.Null(HeaderProbe.Png(s));   // colour type 5 doesn't exist
    }
}
