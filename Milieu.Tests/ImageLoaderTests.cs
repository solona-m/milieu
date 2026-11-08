using System.IO;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class ImageLoaderTests
{
    private static MessageKey Refused(string path, int outW = 8192)
    {
        var e = Assert.Throws<MilieuException>(() => ImageLoader.CheckLimits(ImageLoader.Probe(path), Path.GetFileName(path), outW));
        return e.Status.Key;
    }

    [Fact] // [AC-hdri-backdrop-6] [EDGE-hdri-backdrop-8]
    public void Limits_HdrCappedLdrUpTo134Mpx()
    {
        using var t = new TempDir();
        Assert.Equal(MessageKey.TooLargeHdr, Refused(SyntheticImages.WriteHdrHeaderOnly(t.Path, "big.hdr", 8193, 4096)));
        Assert.Empty(ImageLoader.CheckLimits(ImageLoader.Probe(SyntheticImages.WriteHdrHeaderOnly(t.Path, "ok.hdr", 8192, 4096)), "ok.hdr", 8192));

        var strip = ImageLoader.Probe(SyntheticImages.WritePngHeaderOnly(t.Path, "strip.png", 21184, 3848));
        var warnings = ImageLoader.CheckLimits(strip, "strip.png", 8192);
        Assert.Equal(MessageKey.LargeImage, Assert.Single(warnings).Key);

        Assert.Equal(MessageKey.TooLargeLdr, Refused(SyntheticImages.WritePngHeaderOnly(t.Path, "huge.png", 16384, 8193)));
        Assert.Empty(ImageLoader.CheckLimits(ImageLoader.Probe(SyntheticImages.WritePngHeaderOnly(t.Path, "eight.png", 8192, 4096)), "eight.png", 8192));
    }

    [Fact] // [EDGE-hdri-backdrop-6]
    public void UnsupportedExtension_Rejected()
    {
        using var t = new TempDir();
        var exr = t.File("pano.exr");
        File.WriteAllBytes(exr, [1, 2, 3]);
        var e = Assert.Throws<MilieuException>(() => ImageLoader.Probe(exr));
        Assert.Equal(MessageKey.UnsupportedFormat, e.Status.Key);
        Assert.Equal("pano.exr: only .hdr, .png and .jpg are supported.", e.Status.Text);
    }

    [Fact] // [EDGE-hdri-backdrop-7]
    public void CorruptFile_DecodeFailed()
    {
        using var t = new TempDir();
        var png = t.File("broken.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        Assert.Equal(MessageKey.DecodeFailed, Assert.Throws<MilieuException>(() => ImageLoader.Probe(png)).Status.Key);

        // A header that probes fine but has no pixel data fails at decode.
        var empty = SyntheticImages.WritePngHeaderOnly(t.Path, "empty.png", 64, 32);
        Assert.Equal(64, ImageLoader.Probe(empty).Width);
        Assert.Equal(MessageKey.DecodeFailed,
            Assert.Throws<MilieuException>(() => ImageLoader.Load(empty, ImageKind.Png, default)).Status.Key);
    }

    [Fact] // [EDGE-hdri-backdrop-13]
    public void AlphaGray16bit_BecomeOpaque()
    {
        using var t = new TempDir();
        var rgba = SyntheticImages.WritePng(t.Path, "alpha.png", 8, 4, comp: StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha);
        var img = ImageLoader.Load(rgba, ImageKind.Png, default);
        Assert.False(img.IsHdr);
        Assert.Equal(8 * 4 * 3, img.Rgb!.Length);

        var grey = SyntheticImages.WritePng(t.Path, "grey.png", 8, 4, comp: StbImageWriteSharp.ColorComponents.Grey);
        var g = ImageLoader.Load(grey, ImageKind.Png, default);
        Assert.Equal(8 * 4 * 3, g.Rgb!.Length);
        Assert.Equal(g.Rgb[0], g.Rgb[1]);
        Assert.Equal(g.Rgb[1], g.Rgb[2]);

        var probe = ImageLoader.Probe(rgba);
        Assert.Equal(4, probe.Components);
        Assert.Equal(8, probe.BitsPerChannel);
    }

    [Fact]
    public void Probe_ReportsJpegProgressivenessAndFileSize()
    {
        using var t = new TempDir();
        var jpg = SyntheticImages.WriteJpg(t.Path, "a.jpg", 16, 8);
        var p = ImageLoader.Probe(jpg);
        Assert.Equal(ImageKind.Jpeg, p.Kind);
        Assert.False(p.Progressive);
        Assert.Equal(new FileInfo(jpg).Length, p.FileBytes);
        Assert.Equal((16, 8), (p.Width, p.Height));
        var missing = t.File("gone.png");
        Assert.Equal(MessageKey.FileMissing, Assert.Throws<MilieuException>(() => ImageLoader.Probe(missing)).Status.Key);
        Assert.DoesNotContain(t.Path, Assert.Throws<MilieuException>(() => ImageLoader.Probe(missing)).Status.Text);
        Assert.True(new[] { ".HDR", ".JPEG", ".Png" }.All(e => ImageLoader.KindOf("x" + e) != null));
    }
}
