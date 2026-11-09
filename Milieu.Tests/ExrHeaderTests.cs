using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Imaging.Exr;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class ExrHeaderTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private const string Corrupt = "pano.exr couldn't be read.";

    private static string LayoutText(string feature) =>
        $"pano.exr has {feature}, which Milieu can't read. Save it as a single-part RGB EXR, or as .hdr.";

    private string File(ExrSpec s) => SyntheticExr.Write(dir.Path, "pano.exr", s);

    private string FileBytes(byte[] b)
    {
        var p = dir.File("pano.exr");
        System.IO.File.WriteAllBytes(p, b);
        return p;
    }

    private static MilieuException Refused(string path) => Assert.Throws<MilieuException>(() => ImageLoader.Probe(path));

    private static ExrHeaderInfo Read(ExrSpec s) => ExrHeader.Read(new MemoryStream(SyntheticExr.Header(s)));

    private static ExrSpec Rgb(params string[] names) => new() { Channels = names.Select(n => new ExrCh(n)).ToList() };

    /// <summary>A valid header with extra raw bytes appended just before the terminating null.</summary>
    private static byte[] HeaderWith(Action<BinaryWriter> extra)
    {
        var h = SyntheticExr.Header(new ExrSpec());
        var ms = new MemoryStream();
        ms.Write(h, 0, h.Length - 1);
        var w = new BinaryWriter(ms);
        extra(w);
        w.Write((byte)0);
        return ms.ToArray();
    }

    [Theory] // [AC-exr-support-5] [EDGE-exr-support-5]
    [InlineData(5, "PXR24")]
    [InlineData(6, "B44")]
    [InlineData(7, "B44A")]
    [InlineData(8, "DWAA")]
    [InlineData(9, "DWAB")]
    [InlineData(10, "HTJ2K256")]
    [InlineData(11, "HTJ2K32")]
    [InlineData(42, "unknown (id 42)")]
    public void UnsupportedCompression_Named(int id, string name)
    {
        var e = Refused(File(new ExrSpec { Compression = id }));
        Assert.Equal(MessageKey.UnsupportedExrCompression, e.Status.Key);
        Assert.Equal(Severity.Error, e.Status.Severity);
        Assert.Equal($"pano.exr uses {name} compression, which Milieu can't read. Re-save it with ZIP or PIZ compression, or as .hdr.",
                     e.Status.Text);
    }

    [Fact] // [AC-exr-support-6] [EDGE-exr-support-3] [EDGE-exr-support-4]
    public void MultipartAndDeep_UnsupportedLayout()
    {
        var multi = Refused(File(new ExrSpec { ExtraFlags = 0x1000 }));
        Assert.Equal(MessageKey.UnsupportedExrLayout, multi.Status.Key);
        Assert.Equal(LayoutText("a multi-part layout"), multi.Status.Text);
        var deep = Refused(File(new ExrSpec { ExtraFlags = 0x800 }));
        Assert.Equal(MessageKey.UnsupportedExrLayout, deep.Status.Key);
        Assert.Equal(LayoutText("deep data"), deep.Status.Text);
    }

    [Fact] // [AC-exr-support-6] [EDGE-exr-support-35]
    public void Envmap()
    {
        Assert.Equal(LayoutText("a cube-map environment"), Refused(File(new ExrSpec { Envmap = 1 })).Status.Text);
        Assert.Equal(16, ImageLoader.Probe(File(new ExrSpec { Envmap = 0 })).Width);
        Assert.Equal(16, ImageLoader.Probe(File(new ExrSpec())).Width);
        var bad = Refused(File(new ExrSpec { Envmap = 7 }));
        Assert.Equal(MessageKey.DecodeFailed, bad.Status.Key);
        Assert.Equal(Corrupt, bad.Status.Text);
    }

    [Fact] // [AC-exr-support-7] [EDGE-exr-support-23]
    public void LayerSelection()
    {
        var beauty = Read(Rgb("beauty.B", "beauty.G", "beauty.R"));
        Assert.Equal(["beauty.R", "beauty.G", "beauty.B"], beauty.Selected.Select(i => beauty.Channels[i].Name));

        var combined = Read(Rgb("ViewLayer.Combined.B", "ViewLayer.Combined.G", "ViewLayer.Combined.R",
                                "ViewLayer.Denoised.B", "ViewLayer.Denoised.G", "ViewLayer.Denoised.R",
                                "AView.Diffuse.B", "AView.Diffuse.G", "AView.Diffuse.R"));
        Assert.Equal(["ViewLayer.Combined.R", "ViewLayer.Combined.G", "ViewLayer.Combined.B"],
                     combined.Selected.Select(i => combined.Channels[i].Name));

        // The unprefixed group wins over layers.
        var plain = Read(Rgb("B", "G", "R", "beauty.B", "beauty.G", "beauty.R"));
        Assert.Equal(["R", "G", "B"], plain.Selected.Select(i => plain.Channels[i].Name));

        Assert.Equal(LayoutText("several RGB layers and no main one"),
                     Refused(File(Rgb("a.B", "a.G", "a.R", "b.B", "b.G", "b.R"))).Status.Text);
        Assert.Equal(LayoutText("no R, G, B channels"), Refused(File(Rgb("Z"))).Status.Text);
    }

    [Theory] // [AC-exr-support-8] [EDGE-exr-support-1] [EDGE-exr-support-2]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("flags")]
    public void BadMagicOrVersion_DecodeFailed(string what)
    {
        var bytes = SyntheticExr.Build(what switch
        {
            "version" => new ExrSpec { Version = 3 },
            "flags" => new ExrSpec { ExtraFlags = 0x2000 },
            _ => new ExrSpec(),
        }).Bytes;
        if (what == "magic") bytes[0] = 0x77;
        var e = Refused(FileBytes(bytes));
        Assert.Equal(MessageKey.DecodeFailed, e.Status.Key);
        Assert.Equal(Corrupt, e.Status.Text);
    }

    [Theory] // [EDGE-exr-support-6]
    [InlineData("channels")]
    [InlineData("compression")]
    [InlineData("dataWindow")]
    [InlineData("tiles")]
    public void MissingRequiredAttribute_DecodeFailed(string attr)
    {
        var s = new ExrSpec
        {
            OmitChannels = attr == "channels",
            OmitCompression = attr == "compression",
            OmitDataWindow = attr == "dataWindow",
        };
        if (attr == "tiles") { s.TileWidth = 8; s.TileHeight = 8; s.OmitTiles = true; }
        Assert.Equal(Corrupt, Refused(File(s)).Status.Text);
    }

    [Fact] // [EDGE-exr-support-7]
    public void AttributeBounds()
    {
        void Junk(BinaryWriter w, int size)
        {
            w.Write(Encoding.ASCII.GetBytes("junk\0string\0"));
            w.Write(size);
        }

        Assert.Equal(Corrupt, Refused(FileBytes(HeaderWith(w => Junk(w, -1)))).Status.Text);
        Assert.Equal(Corrupt, Refused(FileBytes(HeaderWith(w => { Junk(w, 100); w.Write(new byte[10]); }))).Status.Text);

        // Over the 16 MiB header cap, with the bytes really present (so it isn't the EOF check).
        var big = (int)ExrLimits.MaxHeaderBytes + 1;
        Assert.Equal(Corrupt, Refused(FileBytes(HeaderWith(w => { Junk(w, big); w.Write(new byte[big]); }))).Status.Text);

        // A name not null-terminated within 255 bytes.
        Assert.Equal(Corrupt, Refused(FileBytes(HeaderWith(w => { w.Write(Enumerable.Repeat((byte)'a', 256).ToArray()); w.Write((byte)0); }))).Status.Text);

        // A 1 MiB unknown attribute is skipped.
        var mib = 1024 * 1024;
        var ok = FileBytes(HeaderWith(w => { Junk(w, mib); w.Write(new byte[mib]); }));
        Assert.Equal(16, ImageLoader.Probe(ok).Width);

        // A claimed 2 GB attribute is refused without a large allocation.
        var claimed = FileBytes(HeaderWith(w => Junk(w, int.MaxValue)));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = Refused(claimed);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < mib);
        Assert.Equal(Corrupt, e.Status.Text);
    }

    [Theory] // [EDGE-exr-support-8]
    [InlineData(10, 0, 5, 7)]
    [InlineData(0, 9, 15, 2)]
    [InlineData(int.MinValue, 0, int.MaxValue, 7)]
    [InlineData(0, int.MinValue, 15, int.MaxValue)]
    public void DataWindowInvertedOrOverflow_DecodeFailed(int x0, int y0, int x1, int y1)
    {
        var box = new byte[16];
        BitConverter.TryWriteBytes(box.AsSpan(0), x0);
        BitConverter.TryWriteBytes(box.AsSpan(4), y0);
        BitConverter.TryWriteBytes(box.AsSpan(8), x1);
        BitConverter.TryWriteBytes(box.AsSpan(12), y1);
        var s = new ExrSpec { OmitDataWindow = true };
        s.ExtraAttributes.Add(("dataWindow", "box2i", box));
        Assert.Equal(Corrupt, Refused(File(s)).Status.Text);
    }

    [Fact] // [EDGE-exr-support-25]
    public void Subsampled_UnsupportedLayout()
    {
        var s = new ExrSpec { Channels = [new("B"), new("G"), new("R", 1, 2, 2)] };
        var e = Refused(File(s));
        Assert.Equal(MessageKey.UnsupportedExrLayout, e.Status.Key);
        Assert.Equal(LayoutText("subsampled channels"), e.Status.Text);
    }

    [Fact] // [EDGE-exr-support-26]
    public void TileSizeZero_DecodeFailed() =>
        Assert.Equal(Corrupt, Refused(SyntheticExr.WriteHeaderOnly(dir.Path, "pano.exr", new ExrSpec { TileWidth = 0, TileHeight = 8 })).Status.Text);

    [Fact] // [EDGE-exr-support-26]
    public void TileSizeOverInt_DecodeFailed() =>
        Assert.Equal(Corrupt, Refused(SyntheticExr.WriteHeaderOnly(dir.Path, "pano.exr",
                                                                   new ExrSpec { TileWidth = unchecked((int)0x80000000u), TileHeight = 8 })).Status.Text);

    [Theory] // [AC-exr-support-8] [EDGE-exr-support-26]
    [InlineData("level mode 3")]
    [InlineData("rounding mode 2")]
    [InlineData("line order 3")]
    public void InvalidTileModeOrLineOrder_DecodeFailed(string what)
    {
        var s = what switch
        {
            "level mode 3" => new ExrSpec { TileWidth = 8, TileHeight = 8, LevelMode = 3 },
            "rounding mode 2" => new ExrSpec { TileWidth = 8, TileHeight = 8, LevelMode = 0x20 },
            _ => new ExrSpec { LineOrder = 3 },
        };
        Assert.Equal(Corrupt, Refused(SyntheticExr.WriteHeaderOnly(dir.Path, "pano.exr", s)).Status.Text);
    }

    [Fact] // [EDGE-exr-support-26]
    public void TooManyTiles_UnsupportedLayout()
    {
        var path = SyntheticExr.WriteHeaderOnly(dir.Path, "pano.exr", new ExrSpec { Width = 2048, Height = 1024, TileWidth = 1, TileHeight = 1 });
        var e = Refused(path);
        Assert.Equal(MessageKey.UnsupportedExrLayout, e.Status.Key);
        Assert.Equal(LayoutText("very small tiles"), e.Status.Text);
    }

    [Fact] // [EDGE-exr-support-27]
    public void BlockTooLarge_UnsupportedLayout()
    {
        var channels = new List<ExrCh> { new("B", 2), new("G", 2), new("R", 2) };
        for (var i = 0; i < 1021; i++) channels.Add(new ExrCh($"c{i:0000}", 2));
        var path = SyntheticExr.WriteHeaderOnly(dir.Path, "pano.exr",
                                                new ExrSpec { Width = 8192, Height = 16, Compression = 3, Channels = channels });
        var e = Refused(path);
        Assert.Equal(MessageKey.UnsupportedExrLayout, e.Status.Key);
        Assert.Equal(LayoutText("very large tiles or too many channels"), e.Status.Text);

        // Huge tiles too.
        var tiles = SyntheticExr.WriteHeaderOnly(dir.Path, "pano.exr",
                                                 new ExrSpec { Width = 8192, Height = 4096, TileWidth = 8192, TileHeight = 4096 });
        Assert.Equal(LayoutText("very large tiles or too many channels"), Refused(tiles).Status.Text);
    }

    [Theory] // [EDGE-exr-support-28]
    [InlineData("no terminator")]
    [InlineData("empty name")]
    [InlineData("pixel type 3")]
    [InlineData("1025 channels")]
    public void MalformedChlist_DecodeFailed(string what)
    {
        var rgb = SyntheticExr.Chlist([new("B"), new("G"), new("R")]);
        var chlist = what switch
        {
            "no terminator" => rgb[..^1],
            "empty name" => [0, .. new byte[16], .. rgb],
            "pixel type 3" => SyntheticExr.Chlist([new("B"), new("G", 3), new("R")]),
            _ => SyntheticExr.Chlist(Enumerable.Range(0, 1025).Select(i => new ExrCh(i < 3 ? "BGR"[i].ToString() : $"c{i:0000}"))),
        };
        Assert.Equal(Corrupt, Refused(File(new ExrSpec { RawChlist = chlist })).Status.Text);
    }

    [Theory] // [EDGE-exr-support-33]
    [InlineData(0x400)]
    [InlineData(0)]
    public void LongNames_AcceptedWithOrWithoutFlag(int flag)
    {
        const string layer = "a_forty_byte_long_render_layer_name_xyz";   // 39 bytes + '.' + component = 41
        Assert.True(layer.Length + 2 > 31);
        var s = new ExrSpec { ExtraFlags = flag, Channels = [new(layer + ".B"), new(layer + ".G"), new(layer + ".R")] };
        var path = File(s);
        var img = ImageLoader.Load(path, ImageKind.Exr, default);
        Assert.Equal(SyntheticExr.Expected(s, 3, 2, 2), img.Hdr![(2 * 16 + 3) * 3]);   // R is stored third
        Assert.Equal(SyntheticExr.Expected(s, 3, 2, 0), img.Hdr![(2 * 16 + 3) * 3 + 2]);

        // A 255-byte name is the longest accepted.
        var longest = new string('n', 253) + ".R";
        Assert.Equal(255, longest.Length);
        var h = Read(Rgb("x.B", "x.G", "x.R", longest));
        Assert.Equal(4, h.Channels.Count);
    }

    [Fact] // [EDGE-exr-support-37]
    public void CaseMatching()
    {
        Assert.Equal(LayoutText("several RGB layers and no main one"), Refused(File(Rgb("B", "G", "R", "r"))).Status.Text);
        Assert.Equal(LayoutText("several RGB layers and no main one"), Refused(File(Rgb("Y", "y"))).Status.Text);

        var alpha = Read(Rgb("A", "B", "G", "R", "a"));
        Assert.Equal(["R", "G", "B"], alpha.Selected.Select(i => alpha.Channels[i].Name));

        var lower = Read(Rgb("b", "g", "r"));
        Assert.Equal(["r", "g", "b"], lower.Selected.Select(i => lower.Channels[i].Name));
        Assert.Equal(16, ImageLoader.Load(File(Rgb("b", "g", "r")), ImageKind.Exr, default).Width);
    }
}
