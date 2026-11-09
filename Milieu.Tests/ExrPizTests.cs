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
using Xunit.Abstractions;

namespace Milieu.Tests;

public sealed class ExrPizTests(ITestOutputHelper output)
{
    private static byte[] Decompress(byte[] piz, int nx, int ny, int[] sizes, int bytes, ExrDecodeTrace? trace = null)
    {
        var dst = new byte[bytes];
        new ExrPiz().Decompress(piz, dst, nx, ny, sizes, new ushort[bytes / 2], trace);
        return dst;
    }

    /// <summary>A valid 16x8 RGB HALF PIZ block and its uncompressed bytes.</summary>
    private static (byte[] Piz, byte[] Raw) ValidBlock()
    {
        var s = new ExrSpec();
        var raw = SyntheticExr.Unpacked(s, 0, 0, 16, 8);
        return (SyntheticExr.PizCompress(raw, 16, 8, [1, 1, 1]), raw);
    }

    /// <summary>A Huffman stream over symbols 0 (code 00) and the run-length symbol 1 (code 01), with the given data bits.</summary>
    private static byte[] TwoSymbolStream(byte[] data, int nBits)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0); w.Write(1); w.Write(2); w.Write(nBits); w.Write(0);
        w.Write(new byte[] { 0b0000_1000, 0b0010_0000 });   // lengths 2, 2 (6 bits each), padded
        w.Write(data);
        return ms.ToArray();
    }

    [Theory] // [EDGE-exr-support-16]
    [InlineData("bitmap range")]
    [InlineData("bitmap past data")]
    [InlineData("huffman length past data")]
    [InlineData("im/iM")]
    [InlineData("nBits past data")]
    [InlineData("nBits past table")]
    [InlineData("invalid code")]
    [InlineData("short huffman output")]
    [InlineData("long huffman output")]
    [InlineData("short wavelet data")]
    public void MalformedPiz_DecodeFailed(string what)
    {
        var (piz, raw) = ValidBlock();
        Assert.Equal(raw, Decompress(piz, 16, 8, [1, 1, 1], raw.Length));   // the unmodified block is fine

        int min = BinaryPrimitives.ReadUInt16LittleEndian(piz), max = BinaryPrimitives.ReadUInt16LittleEndian(piz.AsSpan(2));
        var lenAt = 4 + (min <= max ? max - min + 1 : 0);
        var huf = lenAt + 4;
        switch (what)
        {
            case "bitmap range":
                BinaryPrimitives.WriteUInt16LittleEndian(piz, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(piz.AsSpan(2), 8192);
                break;
            case "bitmap past data":
                BinaryPrimitives.WriteUInt16LittleEndian(piz, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(piz.AsSpan(2), 8191);
                break;
            case "huffman length past data":
                BinaryPrimitives.WriteInt32LittleEndian(piz.AsSpan(lenAt), piz.Length);
                break;
            case "im/iM":
                BinaryPrimitives.WriteInt32LittleEndian(piz.AsSpan(huf + 4), 70000);
                break;
            case "nBits past data":
                BinaryPrimitives.WriteInt32LittleEndian(piz.AsSpan(huf + 12), int.MaxValue);
                break;
            case "nBits past table":
                // Fits the bytes after the 20-byte header, but not the bytes left after the code table.
                BinaryPrimitives.WriteInt32LittleEndian(piz.AsSpan(huf + 12), 8 * (piz.Length - huf - 20));
                break;
            case "short wavelet data":
                var nBits = BinaryPrimitives.ReadInt32LittleEndian(piz.AsSpan(huf + 12));
                BinaryPrimitives.WriteInt32LittleEndian(piz.AsSpan(huf + 12), nBits / 2);
                break;
            case "invalid code":
                Assert.Throws<ExrFormatException>(() => new ExrPiz().HufUncompress(TwoSymbolStream([0b1100_0000], 2), new ushort[1]));
                return;
            case "short huffman output":
                Assert.Throws<ExrFormatException>(() => new ExrPiz().HufUncompress(TwoSymbolStream([0b0000_0000], 4), new ushort[3]));
                return;
            case "long huffman output":
                Assert.Throws<ExrFormatException>(() => new ExrPiz().HufUncompress(TwoSymbolStream([0b0000_0000], 8), new ushort[3]));
                return;
        }
        Assert.Throws<ExrFormatException>(() => Decompress(piz, 16, 8, [1, 1, 1], raw.Length));
    }

    [Fact] // [EDGE-exr-support-38]
    public void KnownAnswer_Wavelet14And16()
    {
        // wdec14 (maxValue < 2^14). Source 2x2 [10 20; 30 40] encoded by hand with wenc14 (m = (a+b)>>1, d = a-b, as shorts):
        // rows (10,20) → (15,-10); (30,40) → (35,-10); columns (15,35) → (25,-20); (-10,-10) → (-10,0).
        // Stored as [25, -10, -20, 0]; wdec14 (a = l + (h&1) + (h>>1), b = a - h) must give back [10, 20, 30, 40].
        ushort[] w14 = [25, unchecked((ushort)-10), unchecked((ushort)-20), 0];
        ExrPiz.Wav2Decode(w14, 2, 1, 2, 2, (ushort)((1 << 14) - 1));
        Assert.Equal(new ushort[] { 10, 20, 30, 40 }, w14);

        // wdec16 (maxValue ≥ 2^14). Source [100 50000; 20000 65535] encoded by hand with wenc16
        // (ao = a + 32768 mod 2^16, m = (ao+b)>>1, d = ao-b; if d < 0, m += 32768; both mod 2^16):
        // (100,50000) → (8666,48404); (20000,65535) → (26383,52769); (8666,26383) → (33908,15051); (48404,52769) → (1434,28403).
        // wdec16: b = (l - (h>>1)) mod 2^16, a = (h + b - 32768) mod 2^16.
        ushort[] w16 = [33908, 1434, 15051, 28403];
        ExrPiz.Wav2Decode(w16, 2, 1, 2, 2, 1 << 14);
        Assert.Equal(new ushort[] { 100, 50000, 20000, 65535 }, w16);
    }

    [Fact] // [EDGE-exr-support-38]
    public void KnownAnswer_Huffman()
    {
        // Symbols 5 (length 1), 6 (unused), 7 (length 2) and the run-length symbol 8 = iM (length 2).
        // Canonical codes: 5 → "1", 7 → "00", 8 → "01". Table: 6-bit lengths 1, 0, 2, 2 → 000001 000000 000010 000010.
        // Data: 5 ("1"), run of 4 more ("01" + 00000100), 7 ("00") → 1 01 00000100 00 = 13 bits → 0xA0 0x80.
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(5); w.Write(8); w.Write(3); w.Write(13); w.Write(0);
        w.Write(new byte[] { 0x04, 0x00, 0x82, 0xA0, 0x80 });
        var outBuf = new ushort[6];
        new ExrPiz().HufUncompress(ms.ToArray(), outBuf);
        Assert.Equal(new ushort[] { 5, 5, 5, 5, 5, 7 }, outBuf);

        // Codes longer than 14 bits (more than 16,383 symbols with one flat length) round-trip too.
        var rng = new Random(5);
        var words = Enumerable.Range(0, 20000).Select(i => (ushort)(i * 3 % 65536)).OrderBy(_ => rng.Next()).ToArray();
        var decoded = new ushort[words.Length];
        new ExrPiz().HufUncompress(SyntheticExr.HufCompress(words), decoded);
        Assert.Equal(words, decoded);
    }

    [Fact] // [EDGE-exr-support-38]
    public void Float32Channel_InterleavedStride()
    {
        // One 4x2 FLOAT channel: each sample is two 16-bit words, wavelet-coded as two interleaved planes (stride 2).
        float[] values = [1.5f, -2.25f, 1e-3f, 3e5f, 7f, 0.1f, -0.5f, 12345.678f];
        var raw = new byte[32];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), values[i]);
        var dst = Decompress(SyntheticExr.PizCompress(raw, 4, 2, [2]), 4, 2, [2], 32);
        for (var i = 0; i < 8; i++) Assert.Equal(values[i], BinaryPrimitives.ReadSingleLittleEndian(dst.AsSpan(i * 4)));

        // UINT behaves the same, next to a HALF channel.
        var s = new ExrSpec { Width = 5, Height = 3, Channels = [new("A", 1), new("B", 0)], Value = (x, y, c) => c == 0 ? x / 4f : 70000 + x * 977 + y };
        var mixed = SyntheticExr.Unpacked(s, 0, 0, 5, 3);
        Assert.Equal(mixed, Decompress(SyntheticExr.PizCompress(mixed, 5, 3, [1, 2]), 5, 3, [1, 2], mixed.Length));
    }

    [OracleFixtureFact] // [EDGE-exr-support-38]
    public void Float32Channel_InterleavedStride_Official()
    {
        // The smooth FLOAT PIZ fixture, whose two 32-line chunks the library stored compressed.
        var bytes = ExrOracleFixtures.Bytes("PizFloatSmooth");
        var h = ExrHeader.Read(new MemoryStream(bytes));
        Assert.Equal((4, 32), (h.Compression, h.BitsPerChannel));
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)h.OffsetTablePosition)) + 4));
        Assert.True(size < 11 * 32 * 12, "chunk 0 is PIZ-compressed");
        var img = ExrDecoder.Decode(new MemoryStream(bytes), default);
        for (var y = 0; y < 35; y++)
            for (var x = 0; x < 11; x++)
                for (var c = 0; c < 3; c++)
                    Assert.Equal(ExrOracleFixtures.FormulaS(x, y, c), img.Rgb[(y * 11 + x) * 3 + c]);
    }

    [OracleFixtureFact] // [EDGE-exr-support-38]
    public void OfficialWdec16Fixture_DecodesExactly()
    {
        var w = ExrOracleFixtures.Wdec16Width;
        var bytes = ExrOracleFixtures.Bytes("Wdec16Piz");
        var h = ExrHeader.Read(new MemoryStream(bytes));
        Assert.Equal((4, w, 32, 1), (h.Compression, h.Width, h.Height, h.ChunkCount));
        var trace = new ExrDecodeTrace();
        var img = ExrDecoder.Decode(new MemoryStream(bytes), default, trace);
        var words = new HashSet<ushort>();
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < w; x++)
            {
                var v = img.Rgb[(y * w + x) * 3];
                Assert.Equal(v, img.Rgb[(y * w + x) * 3 + 1]);
                var bits = BitConverter.HalfToUInt16Bits((Half)v);
                Assert.Equal(ExrOracleFixtures.GeneratorB(x, y, w), bits);
                words.Add(bits);
            }
        Assert.True(words.Count >= 16_385, $"{words.Count} distinct words");
        Assert.True(trace.UsedWdec16);
        Assert.Equal(1, trace.Chunks);
    }

    [Fact] // [EDGE-exr-support-38]
    public void SyntheticWdec16_TakesBranch()
    {
        byte[] Block(int w)
        {
            var s = new ExrSpec { Width = w, Height = 32, Channels = [new("Y")], Bits = (x, y, _) => (uint)(0x0400 + y * w + x) };
            return SyntheticExr.Unpacked(s, 0, 0, w, 32);
        }

        var big = Block(520);                     // 16,640 distinct words
        var trace = new ExrDecodeTrace();
        Assert.Equal(big, Decompress(SyntheticExr.PizCompress(big, 520, 32, [1]), 520, 32, [1], big.Length, trace));
        Assert.True(trace.UsedWdec16);

        var small = Block(500);                   // 16,000 distinct words: maxValue < 2^14
        var t2 = new ExrDecodeTrace();
        Assert.Equal(small, Decompress(SyntheticExr.PizCompress(small, 500, 32, [1]), 500, 32, [1], small.Length, t2));
        Assert.False(t2.UsedWdec16);
    }

    [ReferenceFileFact(ReferenceFiles.ExrSamplesDir)] // [EDGE-exr-support-16]
    public void RealFiles_WhenSourcePresent()
    {
        var folder = ReferenceFiles.Path(ReferenceFiles.ExrSamplesDir);
        var decoded = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*.exr"))
        {
            var name = Path.GetFileName(path);
            try
            {
                var probe = ImageLoader.Probe(path);
                int compression;
                using (var s = File.OpenRead(path)) compression = ExrHeader.Read(s).Compression;
                var img = ImageLoader.Load(path, probe.Kind, default);
                decoded[name] = img.Hdr!;
                output.WriteLine($"{name}: {probe.Width}x{probe.Height}, compression {compression}, decoded");
            }
            catch (MilieuException e) when (e.Status.Key is MessageKey.UnsupportedExrCompression or MessageKey.UnsupportedExrLayout or MessageKey.TooLargeHdr)
            {
                output.WriteLine($"{name}: {e.Status.Text}");
            }
        }
        foreach (var piz in decoded.Keys.Where(k => k.EndsWith(".piz.exr", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var zip = piz[..^".piz.exr".Length] + ".zip.exr";
            if (!decoded.TryGetValue(zip, out var z)) continue;
            var p = decoded[piz];
            Assert.Equal(z.Length, p.Length);
            for (var i = 0; i < p.Length; i++)
                Assert.True(float.IsNaN(p[i]) ? float.IsNaN(z[i]) : BitConverter.SingleToInt32Bits(p[i]) == BitConverter.SingleToInt32Bits(z[i]),
                            $"{piz} vs {zip} differ at {i}");
            output.WriteLine($"{piz} == {zip}");
        }
    }
}
