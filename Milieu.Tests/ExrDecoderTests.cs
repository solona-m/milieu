using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Imaging.Exr;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class ExrDecoderTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private const long MiB = 1024 * 1024;

    private static (int Width, int Height, float[] Rgb) Decode(byte[] bytes, ExrDecodeTrace? trace = null) =>
        ExrDecoder.Decode(new MemoryStream(bytes, writable: false), default, trace);

    private static MilieuException LoadFails(string path) =>
        Assert.Throws<MilieuException>(() => ImageLoader.Load(path, ImageKind.Exr, default));

    private string Path(byte[] bytes, string name = "pano.exr")
    {
        var p = dir.File(name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>The stored channel indices of R, G and B (by final component) in a spec.</summary>
    private static int[] Rgb(ExrSpec s, string prefix = "") =>
        new[] { "R", "G", "B" }.Select(n => s.Channels.FindIndex(c => c.Name == prefix + n)).ToArray();

    private static void AssertDecoded(ExrSpec s, (int Width, int Height, float[] Rgb) img, int[]? sel = null)
    {
        sel ??= Rgb(s);
        Assert.Equal((s.Width, s.Height), (img.Width, img.Height));
        for (var y = 0; y < s.Height; y++)
            for (var x = 0; x < s.Width; x++)
                for (var k = 0; k < 3; k++)
                {
                    var expected = SyntheticExr.Expected(s, x, y, sel[k]);
                    var actual = img.Rgb[(y * s.Width + x) * 3 + k];
                    if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
                        Assert.Fail($"({x},{y}) channel {k}: expected {expected}, got {actual}");
                }
    }

    /// <summary>How many level-0 chunks are stored compressed (packed size below the block's unpacked size).</summary>
    private static int CompressedChunks(byte[] bytes)
    {
        var h = ExrHeader.Read(new MemoryStream(bytes));
        var n = 0;
        for (var i = 0; i < h.ChunkCount; i++)
        {
            var off = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)h.OffsetTablePosition + 8 * i));
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)off + h.ChunkHeaderBytes - 4));
            int bw, bh;
            if (h.Tiled)
            {
                var tx = i % h.TilesX;
                var ty = i / h.TilesX;
                bw = Math.Min(h.TileWidth, h.Width - tx * h.TileWidth);
                bh = Math.Min(h.TileHeight, h.Height - ty * h.TileHeight);
            }
            else
            {
                bw = h.Width;
                bh = Math.Min(h.LinesPerBlock, h.Height - i * h.LinesPerBlock);
            }
            if (size < bw * bh * h.BytesPerPixel) n++;
        }
        return n;
    }

    /// <summary>Smooth values (runs of equal samples), so every codec really compresses the synthetic files.</summary>
    private static float Smooth(int x, int y, int c) => (x / 4 + y / 4 + c) % 8 / 4f;

    public static TheoryData<string, int, bool, bool> OfficialNames()
    {
        var d = new TheoryData<string, int, bool, bool>();
        foreach (var (codec, id) in new[] { ("Rle", 1), ("Zips", 2), ("Zip", 3), ("Piz", 4) })
            foreach (var half in new[] { true, false })
                foreach (var smooth in new[] { false, true })
                    d.Add(codec + (half ? "Half" : "Float") + (smooth ? "Smooth" : ""), id, half, smooth);
        return d;
    }

    [OracleFixtureTheory] // [AC-exr-support-2] [EDGE-exr-support-19]
    [MemberData(nameof(OfficialNames))]
    public void OfficialFixtures_DecodeExactly(string name, int compression, bool half, bool smooth)
    {
        var bytes = ExrOracleFixtures.Bytes(name);
        var path = Path(bytes, name + ".exr");
        var probe = ImageLoader.Probe(path);
        Assert.Equal((ImageKind.Exr, 11, 35, half ? 16 : 32), (probe.Kind, probe.Width, probe.Height, probe.BitsPerChannel));
        Assert.Equal(compression, ExrHeader.Read(new MemoryStream(bytes)).Compression);
        // The smooth fixtures prove the codec itself ran: the library stored their chunks compressed (a tiny partial last
        // block may still be raw).
        if (smooth) Assert.True(CompressedChunks(bytes) > 0);
        Func<int, int, int, float> f = smooth ? ExrOracleFixtures.FormulaS : ExrOracleFixtures.FormulaA;
        var img = ImageLoader.Load(path, ImageKind.Exr, default);
        for (var y = 0; y < 35; y++)
            for (var x = 0; x < 11; x++)
                for (var c = 0; c < 3; c++)
                    Assert.Equal(BitConverter.SingleToInt32Bits(f(x, y, c)), BitConverter.SingleToInt32Bits(img.Hdr![(y * 11 + x) * 3 + c]));
    }

    [Theory] // [AC-exr-support-2]
    [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 1)] [InlineData(3, 2)]
    [InlineData(4, 1)] [InlineData(4, 2)]
    public void Decodes_AllSupportedCompressions_HalfAndFloat(int compression, int type)
    {
        var s = new ExrSpec
        {
            Width = 37, Height = 19, Compression = compression, Channels = [new("B", type), new("G", type), new("R", type)], Value = Smooth,
        };
        var build = SyntheticExr.Build(s);
        if (compression != 0) Assert.True(CompressedChunks(build.Bytes) > 0);
        AssertDecoded(s, Decode(build.Bytes));
    }

    [Fact] // [AC-exr-support-4] [EDGE-exr-support-10]
    public void Load_RefusesOversizeWithoutAllocating()
    {
        var path = SyntheticExr.WriteHeaderOnly(dir.Path, "big.exr", new ExrSpec { Width = 8193, Height = 4096, Compression = 3 });
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<MilieuException>(() => ImageLoader.Load(path, ImageKind.Exr, default));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < MiB);
        Assert.Equal(MessageKey.TooLargeHdr, e.Status.Key);
    }

    [Fact] // [AC-exr-support-4] [EDGE-exr-support-10]
    public void Decode_OversizeHeader_RefusedByDecoder()
    {
        // The decoder's own cap check (defence in depth behind ImageLoader.Load's), on a header-only stream.
        var ms = new MemoryStream(SyntheticExr.Header(new ExrSpec { Width = 8193, Height = 4096, Compression = 3 }), writable: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<ExrFormatException>(() => ExrDecoder.Decode(ms, default));
        Assert.Equal("image larger than the HDR cap", e.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < MiB);
    }

    [Theory, Trait("Speed", "Slow")] // [AC-exr-support-4]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode8K_ManagedAllocationsBounded(int compression)
    {
        var s = new ExrSpec
        {
            Width = 8192, Height = 4096, Compression = compression,
            Value = (x, y, c) => ((x >> 5) + (y >> 5) + c) % 64 / 8f,
        };
        var path = SyntheticExr.Write(dir.Path, "8k.exr", s);
        ExrHeaderInfo h;
        using (var hs = File.OpenRead(path)) h = ExrHeader.Read(hs);
        var bound = 12L * h.Width * h.Height + (compression == 4 ? 3 : 2) * h.MaxBlockBytes + 8L * h.ChunkCount + 2 * MiB;

        using var fs = File.OpenRead(path);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var img = ExrDecoder.Decode(fs, default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= bound, $"allocated {allocated} > bound {bound}");
        Assert.Equal(s.Value(8000, 4000, 2), img.Rgb[(4000L * 8192 + 8000) * 3]);   // R is stored third
    }

    [Theory] // [AC-exr-support-7] [EDGE-exr-support-26]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    public void Tiled_OneLevelAndMipmap_Level0(int compression, int levelMode)
    {
        var s = new ExrSpec { Width = 37, Height = 19, Compression = compression, TileWidth = 16, TileHeight = 8, LevelMode = levelMode, Value = Smooth };
        var build = SyntheticExr.Build(s);
        Assert.Equal(9, build.ChunkOffsets.Length);
        Assert.True(CompressedChunks(build.Bytes) > 0);
        var trace = new ExrDecodeTrace();
        AssertDecoded(s, Decode(build.Bytes, trace));
        Assert.Equal(9, trace.Chunks);
    }

    [OracleFixtureTheory] // [AC-exr-support-7] [EDGE-exr-support-26]
    [InlineData("TiledPizHalf")]
    [InlineData("TiledPizHalfSmooth")]
    public void OfficialTiledPiz_DecodesExactly(string name)
    {
        var bytes = ExrOracleFixtures.Bytes(name);
        var h = ExrHeader.Read(new MemoryStream(bytes));
        Assert.True(h.Tiled);
        Assert.Equal((4, 16, 8, 37, 19, 9), (h.Compression, h.TileWidth, h.TileHeight, h.Width, h.Height, h.ChunkCount));
        var smooth = name.EndsWith("Smooth");
        if (smooth) Assert.True(CompressedChunks(bytes) > 0);
        Func<int, int, int, float> f = smooth ? ExrOracleFixtures.FormulaS : ExrOracleFixtures.FormulaA;
        var img = Decode(bytes);
        for (var y = 0; y < 19; y++)
            for (var x = 0; x < 37; x++)
                for (var c = 0; c < 3; c++)
                    Assert.Equal(f(x, y, c), img.Rgb[(y * 37 + x) * 3 + c]);
    }

    [Fact] // [AC-exr-support-7] [EDGE-exr-support-21]
    public void AlphaAndExtraChannels_Ignored()
    {
        foreach (var comp in new[] { 0, 3, 4 })
        {
            var s = new ExrSpec
            {
                Compression = comp,
                Channels = [new("A"), new("B"), new("G", 2), new("R"), new("Z", 2), new("other.X", 0)],
                Value = (x, y, c) => c == 5 ? x + y : SyntheticExr.FormulaA(x, y, c),
            };
            AssertDecoded(s, Decode(SyntheticExr.Build(s).Bytes));
        }
    }

    [Fact] // [AC-exr-support-7] [EDGE-exr-support-24]
    public void LuminanceOnly_Grey_And_Chroma_Rejected()
    {
        var s = new ExrSpec { Channels = [new("Y")], Compression = 4 };
        AssertDecoded(s, Decode(SyntheticExr.Build(s).Bytes), [0, 0, 0]);

        var chroma = SyntheticExr.Write(dir.Path, "pano.exr", new ExrSpec { Channels = [new("BY"), new("RY"), new("Y")] });
        var e = Assert.Throws<MilieuException>(() => ImageLoader.Probe(chroma));
        Assert.Equal("pano.exr has luminance/chroma channels, which Milieu can't read. Save it as a single-part RGB EXR, or as .hdr.", e.Status.Text);
    }

    private static List<byte[]> FuzzBases()
    {
        var specs = new[]
        {
            new ExrSpec { Compression = 0 },
            new ExrSpec { Compression = 1 },
            new ExrSpec { Compression = 3 },
            new ExrSpec { Compression = 4 },
            new ExrSpec { Compression = 3, TileWidth = 8, TileHeight = 4 },
            new ExrSpec { Compression = 4, Channels = [new("B", 2), new("G", 0), new("R")], Value = (x, y, c) => x + y * 3 + c },
        };
        return specs.Select(s => SyntheticExr.Build(s).Bytes).ToList();
    }

    private void Fuzz(List<byte[]> bases, int iterations, int seed)
    {
        var rng = new Random(seed);
        var path = dir.File("fuzz.exr");
        for (var it = 0; it < iterations; it++)
        {
            var b = bases[rng.Next(bases.Count)];
            byte[] m;
            switch (rng.Next(3))
            {
                case 0:
                    m = (byte[])b.Clone();
                    for (int k = 0, n = rng.Next(1, 5); k < n; k++) m[rng.Next(m.Length)] = (byte)rng.Next(256);
                    break;
                case 1:
                    var at = rng.Next(b.Length);
                    var ins = new byte[rng.Next(1, 9)];
                    rng.NextBytes(ins);
                    m = [.. b.AsSpan(0, at), .. ins, .. b.AsSpan(at)];
                    break;
                default:
                    m = b[..rng.Next(b.Length)];
                    break;
            }
            File.WriteAllBytes(path, m);

            long bound;
            try
            {
                var h = ExrHeader.Read(new MemoryStream(m));
                bound = 12L * h.Width * h.Height + 3 * h.MaxBlockBytes + 8L * h.ChunkCount + MiB;
            }
            catch (Exception) { bound = MiB; }

            var before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                var p = ImageLoader.Probe(path);
                ImageLoader.Load(path, p.Kind, default);
            }
            catch (MilieuException e)
            {
                Assert.Contains(e.Status.Key, new[] { MessageKey.DecodeFailed, MessageKey.UnsupportedExrCompression,
                                                      MessageKey.UnsupportedExrLayout, MessageKey.TooLargeHdr });
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated <= bound, $"iteration {it}: allocated {allocated} > {bound}");
        }
    }

    [Fact] // [AC-exr-support-8]
    public void Fuzz_Quick() => Fuzz(FuzzBases(), 300, 20261109);

    [Fact, Trait("Speed", "Slow")] // [AC-exr-support-8]
    public void Fuzz_Extended()
    {
        var bases = FuzzBases();
        if (ExrOracleFixtures.Available) bases.AddRange(ExrOracleFixtures.All.Keys.Select(ExrOracleFixtures.Bytes));
        Fuzz(bases, 5000, 7);
    }

    [Fact] // [EDGE-exr-support-9]
    public void NonZeroOrigin_AndDisplayWindowIgnored()
    {
        var s = new ExrSpec { XMin = -5, YMin = -3, Width = 16, Height = 8, DisplayWindow = (-100, -100, 100, 100), Compression = 3 };
        var path = SyntheticExr.Write(dir.Path, "pano.exr", s);
        var probe = ImageLoader.Probe(path);
        Assert.Equal((16, 8), (probe.Width, probe.Height));
        var img = ImageLoader.Load(path, ImageKind.Exr, default);
        AssertDecoded(s, (img.Width, img.Height, img.Hdr!));
    }

    [Theory] // [EDGE-exr-support-11]
    [InlineData("inside table")]
    [InlineData("past eof")]
    [InlineData("header doesn't fit")]
    public void BadOffsets_RefusedBeforeOutputAllocated(string what)
    {
        var s = new ExrSpec { Width = 2048, Height = 1024, Compression = 3, Value = (_, _, _) => 1f };
        var build = SyntheticExr.Build(s);
        var b = build.Bytes;
        long bad = what switch
        {
            "inside table" => build.OffsetTablePos + 8,
            "past eof" => b.Length + 100,
            _ => b.Length - 4,
        };
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan((int)build.OffsetTablePos + 8 * 3), bad);
        var ms = new MemoryStream(b, writable: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ExrFormatException>(() => ExrDecoder.Decode(ms, default));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 12L * 2048 * 1024);
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(b)).Status.Key);
    }

    [Theory] // [EDGE-exr-support-11]
    [InlineData(false)]
    [InlineData(true)]
    public void ChunkCoordMismatch_DecodeFailed(bool tiled)
    {
        var s = tiled ? new ExrSpec { Width = 37, Height = 19, TileWidth = 16, TileHeight = 8, Compression = 3 } : new ExrSpec { Compression = 1 };
        var build = SyntheticExr.Build(s);
        var b = build.Bytes;
        // Scanline: chunk 1's y becomes chunk 2's; tiled: chunk 1's tile x becomes 0.
        var off = (int)build.ChunkOffsets[1];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off), tiled ? 0 : 2);
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(b)).Status.Key);
    }

    [Theory] // [EDGE-exr-support-12]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("past file")]
    [InlineData("over unpacked")]
    public void BadPackedSize_DecodeFailed(string what)
    {
        var s = new ExrSpec { Compression = 3, Height = 64, ForceRaw = true };
        var build = SyntheticExr.Build(s);
        var b = build.Bytes;
        var unpacked = 16 * 16 * 6;
        var at = (int)build.ChunkOffsets[0] + 4;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), what switch
        {
            "zero" => 0,
            "negative" => -5,
            "past file" => int.MaxValue,
            _ => unpacked + 1,
        });
        Assert.True(b.Length - at > unpacked + 1);
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(b)).Status.Key);
    }

    [Fact] // [EDGE-exr-support-12]
    public void PackedPastEndOfFile_UnderUnpacked_DecodeFailed()
    {
        // The last chunk claims one byte more than the file has left, while still within its unpacked size: only the
        // "bytes left in the file" bound can refuse it.
        var build = SyntheticExr.Build(new ExrSpec { Compression = 3, Height = 40, ForceRaw = true });
        var last = (int)build.ChunkOffsets.Max();
        var unpacked = 8 * 16 * 6;   // the 8-line partial last block
        var cut = build.Bytes[..^10];
        var remaining = cut.Length - (last + 8);
        BinaryPrimitives.WriteInt32LittleEndian(cut.AsSpan(last + 4), remaining + 1);
        Assert.True(remaining + 1 <= unpacked);
        var e = Assert.Throws<ExrFormatException>(() => ExrDecoder.Decode(new MemoryStream(cut), default));
        Assert.Equal("chunk size out of range", e.Message);
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(cut)).Status.Key);
    }

    [Theory] // [EDGE-exr-support-13]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void PackedEqualsUnpacked_TreatedAsRaw(int compression)
    {
        var s = new ExrSpec { Compression = compression, Height = 40, ForceRaw = true };
        var bytes = SyntheticExr.Build(s).Bytes;
        Assert.Equal(0, CompressedChunks(bytes));
        AssertDecoded(s, Decode(bytes));
    }

    [Theory] // [EDGE-exr-support-14]
    [InlineData(false)]
    [InlineData(true)]
    public void ZipShortOrBomb_DecodeFailed(bool bomb)
    {
        var s = new ExrSpec
        {
            Compression = 3, Height = 32,
            Value = (x, _, _) => x % 4 / 4f,
            PackedOverride = (i, unpacked) => i == 1
                ? SyntheticExr.Zlib(bomb ? new byte[unpacked.Length * 100] : SyntheticExr.Predict(unpacked)[..^7])
                : SyntheticExr.Zlib(SyntheticExr.Predict(unpacked)),
        };
        var b = SyntheticExr.Build(s).Bytes;
        var h = ExrHeader.Read(new MemoryStream(b));
        var bound = 12L * h.Width * h.Height + 2 * h.MaxBlockBytes + 8L * h.ChunkCount + MiB;
        var ms = new MemoryStream(b, writable: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ExrFormatException>(() => ExrDecoder.Decode(ms, default));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before <= bound);
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(b)).Status.Key);
    }

    [Theory] // [EDGE-exr-support-15]
    [InlineData(new byte[] { 0xFC, 1, 2, 3, 4 }, 3)]   // literal run of 4 into 3 bytes
    [InlineData(new byte[] { 0x05, 9 }, 4)]            // repeat run of 6 into 4 bytes
    [InlineData(new byte[] { 0xFC, 1, 2 }, 4)]         // literal run truncated
    [InlineData(new byte[] { 0x03 }, 4)]               // repeat run missing its value
    [InlineData(new byte[] { 0x01, 7 }, 4)]            // too short
    public void RleOverrunOrTruncated_DecodeFailed(byte[] rle, int outLength)
    {
        Assert.Throws<ExrFormatException>(() => ExrCompression.RleExpand(rle, new byte[outLength]));

        // The same through a file.
        var s = new ExrSpec { Compression = 1, PackedOverride = (i, unpacked) => i == 2 ? rle : unpacked };
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(SyntheticExr.Build(s).Bytes)).Status.Key);
    }

    [Fact] // [EDGE-exr-support-15]
    public void Rle_ExpandThenPredictor()
    {
        // A literal run (-2: 10, 20), then a repeat run (count 2: three 5s).
        byte[] rle = [0xFE, 10, 20, 0x02, 5];
        var expanded = new byte[5];
        ExrCompression.RleExpand(rle, expanded);
        Assert.Equal(new byte[] { 10, 20, 5, 5, 5 }, expanded);
        var output = new byte[5];
        ExrCompression.Reconstruct(expanded, output);
        // Predictor: 10, 10+20-128=158, 158+5-128=35, 35+5-128=168, 168+5-128=45; then halves [10,158,35] and [168,45] interleave.
        Assert.Equal(new byte[] { 10, 168, 158, 45, 35 }, output);
    }

    [Fact] // [EDGE-exr-support-17]
    public void TruncatedChunk_DecodeFailed()
    {
        foreach (var comp in new[] { 0, 3, 4 })
        {
            var build = SyntheticExr.Build(new ExrSpec { Compression = comp, Height = 40 });
            var last = build.ChunkOffsets.Max();
            var cut = build.Bytes[..(int)(last + 8 + 3)];
            Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(cut)).Status.Key);
            Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(build.Bytes[..^1])).Status.Key);
        }
    }

    [Fact] // [EDGE-exr-support-18]
    public void LineOrders_DecodeIdentically()
    {
        float[]? first = null;
        foreach (var order in new[] { 0, 1, 2 })
        {
            var s = new ExrSpec { Compression = 3, Width = 20, Height = 70, LineOrder = order };
            var build = SyntheticExr.Build(s);
            if (order != 0) Assert.NotEqual(build.ChunkOffsets.Order().ToArray(), build.ChunkOffsets);
            var img = Decode(build.Bytes);
            AssertDecoded(s, img);
            first ??= img.Rgb;
            Assert.Equal(first, img.Rgb);
        }
    }

    [Fact] // [EDGE-exr-support-19]
    public void PartialLastBlock_AndWidth1()
    {
        foreach (var comp in new[] { 3, 4 })
        {
            var s = new ExrSpec { Compression = comp, Width = 11, Height = 35 };
            AssertDecoded(s, Decode(SyntheticExr.Build(s).Bytes));
            var w1 = new ExrSpec { Compression = comp, Width = 1, Height = 35 };
            AssertDecoded(w1, Decode(SyntheticExr.Build(w1).Bytes));
        }
    }

    [Fact] // [EDGE-exr-support-20]
    public void SpecialHalfValues_PassThrough()
    {
        ushort[] bits = [0x7C00, 0xFC00, 0xBC00, 0x0001, 0x7E00];   // +Inf, -Inf, -1, smallest subnormal, NaN
        var s = new ExrSpec { Width = 5, Height = 1, Compression = 4, Bits = (x, _, _) => bits[x] };
        var img = Decode(SyntheticExr.Build(s).Bytes);
        Assert.Equal(float.PositiveInfinity, img.Rgb[0]);
        Assert.Equal(float.NegativeInfinity, img.Rgb[3]);
        Assert.Equal(-1f, img.Rgb[6]);
        Assert.Equal(BitConverter.SingleToInt32Bits((float)BitConverter.UInt16BitsToHalf(1)), BitConverter.SingleToInt32Bits(img.Rgb[9]));
        Assert.Equal(5.9604645E-08f, img.Rgb[9]);
        Assert.True(float.IsNaN(img.Rgb[12]));

        Assert.Equal(255, ToneMapper.Map(img.Rgb[0], 1, ToneMapOperator.Aces));
        Assert.Equal(0, ToneMapper.Map(img.Rgb[6], 1, ToneMapOperator.Aces));
        Assert.Equal(0, ToneMapper.Map(img.Rgb[12], 1, ToneMapOperator.Aces));
        var subnormal = ToneMapper.ToByte(ToneMapper.SrgbEncode(ToneMapper.Operator(img.Rgb[9], ToneMapOperator.None)));
        Assert.Equal(subnormal, ToneMapper.Map(img.Rgb[9], 1, ToneMapOperator.None));
    }

    [Fact] // [EDGE-exr-support-22]
    public void MixedPixelTypes()
    {
        foreach (var comp in new[] { 0, 4 })
        {
            var s = new ExrSpec
            {
                Compression = comp,
                Channels = [new("B", 0), new("G", 2), new("R", 1)],
                Value = (x, y, c) => c == 0 ? x * 1000 + y : SyntheticExr.FormulaA(x, y, c) * (c == 1 ? 1.1f : 1f),
            };
            AssertDecoded(s, Decode(SyntheticExr.Build(s).Bytes));
        }
    }

    [Theory] // [EDGE-exr-support-26]
    [InlineData("level")]
    [InlineData("coords")]
    public void TiledBadLevelOrCoords_DecodeFailed(string what)
    {
        var build = SyntheticExr.Build(new ExrSpec { Width = 37, Height = 19, TileWidth = 16, TileHeight = 8, Compression = 4 });
        var b = build.Bytes;
        var off = (int)build.ChunkOffsets[2];
        if (what == "level") BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off + 8), 1);
        else BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off), 99);
        Assert.Equal(MessageKey.DecodeFailed, LoadFails(Path(b)).Status.Key);
    }

    [Fact] // [EDGE-exr-support-36]
    public void StoredChannelOrder_OrdinalNotCulture()
    {
        // Ordinal order puts "B" before "a.R" (culture order would put "a.R" first).
        Assert.True(string.CompareOrdinal("B", "a.R") < 0);
        Assert.True(string.Compare("B", "a.R", StringComparison.InvariantCulture) > 0);
        Func<int, int, int, float> value = (x, y, c) => (c + 1) * 10 + (x % 4) / 4f;
        var s = new ExrSpec { Compression = 3, Channels = [new("B"), new("G"), new("R"), new("a.R")], Value = value };
        var img = Decode(SyntheticExr.Build(s).Bytes);
        AssertDecoded(s, img);
        Assert.DoesNotContain(img.Rgb, v => v >= 40);   // a.R's values (40+) never leak in

        // A file storing the channels unsorted decodes by its stored order.
        var unsorted = new ExrSpec { Compression = 4, Channels = [new("R"), new("a.R"), new("G", 2), new("B")], Value = value };
        AssertDecoded(unsorted, Decode(SyntheticExr.Build(unsorted).Bytes));
    }
}
