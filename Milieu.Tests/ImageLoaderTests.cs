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

    [Fact] // [EDGE-hdri-backdrop-6] [AC-exr-support-9]
    public void UnsupportedExtension_Rejected()
    {
        using var t = new TempDir();
        var tif = t.File("pano.tif");
        File.WriteAllBytes(tif, [1, 2, 3]);
        var e = Assert.Throws<MilieuException>(() => ImageLoader.Probe(tif));
        Assert.Equal(MessageKey.UnsupportedFormat, e.Status.Key);
        Assert.Equal("pano.tif: only .hdr, .exr, .png and .jpg are supported.", e.Status.Text);
    }

    [Fact] // [AC-exr-support-1]
    public void FileDialogFilter_ListsEveryExtension()
    {
        Assert.Equal("Panoramas{.hdr,.exr,.png,.jpg,.jpeg}", ImageLoader.FileDialogFilter);
        Assert.Equal(new[] { ".hdr", ".exr", ".png", ".jpg", ".jpeg" }, ImageLoader.SupportedExtensions);
        Assert.All(ImageLoader.SupportedExtensions, e => Assert.NotNull(ImageLoader.KindOf("pano" + e)));
    }

    [Fact] // [EDGE-exr-support-30]
    public void KindOf_ExrAnyCase()
    {
        foreach (var name in new[] { "a.exr", "a.EXR", "a.Exr", @"C:\x\y.eXr" })
            Assert.Equal(ImageKind.Exr, ImageLoader.KindOf(name));
        Assert.True(ImageKind.Exr.IsHdr());
        Assert.True(ImageKind.Hdr.IsHdr());
        Assert.False(ImageKind.Png.IsHdr());
        Assert.False(ImageKind.Jpeg.IsHdr());
    }

    [Fact] // [AC-exr-support-4] [EDGE-exr-support-10]
    public void Exr_LimitsLikeHdr()
    {
        using var t = new TempDir();
        var big = SyntheticExr.WriteHeaderOnly(t.Path, "big.exr", new ExrSpec { Width = 8193, Height = 4096, Compression = 3 });
        Assert.Equal(MessageKey.TooLargeHdr, Refused(big));
        Assert.Equal("big.exr is 8193x4096. HDR images can be at most 8192x4096; downscale it first.",
                     Assert.Throws<MilieuException>(() => ImageLoader.CheckLimits(ImageLoader.Probe(big), "big.exr", 8192)).Status.Text);
        Assert.Equal(MessageKey.TooLargeHdr, Refused(SyntheticExr.WriteHeaderOnly(t.Path, "tall.exr", new ExrSpec { Width = 4096, Height = 4097, Compression = 3 })));

        var ok = ImageLoader.Probe(SyntheticExr.WriteHeaderOnly(t.Path, "ok.exr", new ExrSpec { Width = 8192, Height = 4096, Compression = 4 }));
        Assert.Empty(ImageLoader.CheckLimits(ok, "ok.exr", 8192));
        Assert.Equal((ImageKind.Exr, 8192, 4096, 16, 3), (ok.Kind, ok.Width, ok.Height, ok.BitsPerChannel, ok.Components));

        var mixed = ImageLoader.Probe(SyntheticExr.WriteHeaderOnly(t.Path, "f.exr",
            new ExrSpec { Channels = [new("A"), new("B"), new("G", 2), new("R")] }));
        Assert.Equal(32, mixed.BitsPerChannel);
    }

    [Fact] // [EDGE-exr-support-34]
    public void Load_PassesMilieuExceptionThrough()
    {
        using var t = new TempDir();
        var big = SyntheticExr.WriteHeaderOnly(t.Path, "big.exr", new ExrSpec { Width = 8193, Height = 4096, Compression = 3 });
        Assert.Equal(MessageKey.TooLargeHdr, Assert.Throws<MilieuException>(() => ImageLoader.Load(big, ImageKind.Exr, default)).Status.Key);
        var dwaa = SyntheticExr.Write(t.Path, "d.exr", new ExrSpec { Compression = 8 });
        var e = Assert.Throws<MilieuException>(() => ImageLoader.Load(dwaa, ImageKind.Exr, default));
        Assert.Equal(MessageKey.UnsupportedExrCompression, e.Status.Key);
        Assert.Equal("d.exr uses DWAA compression, which Milieu can't read. Re-save it with ZIP or PIZ compression, or as .hdr.", e.Status.Text);
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
