using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Milieu.Tests.Support;

/// <summary>A channel for <see cref="SyntheticExr"/>: Type 0 = UINT, 1 = HALF, 2 = FLOAT.</summary>
public sealed record ExrCh(string Name, int Type = 1, int XSampling = 1, int YSampling = 1)
{
    public int Size => Type == 1 ? 2 : 4;
}

/// <summary>What to write. Defaults: 16x8 RGB HALF, NONE, increasing lines, formula A values.</summary>
public sealed class ExrSpec
{
    public List<ExrCh> Channels { get; set; } = [new("B"), new("G"), new("R")];
    public int Width { get; set; } = 16;
    public int Height { get; set; } = 8;
    public int XMin { get; set; }
    public int YMin { get; set; }
    public (int XMin, int YMin, int XMax, int YMax)? DisplayWindow { get; set; }
    public int Compression { get; set; }
    public int LineOrder { get; set; }
    public int? TileWidth { get; set; }
    public int? TileHeight { get; set; }
    public int LevelMode { get; set; }
    public int? Envmap { get; set; }
    public int Version { get; set; } = 2;
    public int ExtraFlags { get; set; }
    public bool OmitChannels { get; set; }
    public bool OmitCompression { get; set; }
    public bool OmitDataWindow { get; set; }
    public bool OmitTiles { get; set; }
    public byte[]? RawChlist { get; set; }
    public List<(string Name, string Type, byte[] Value)> ExtraAttributes { get; } = [];
    /// <summary>Sample value at (x, y) relative to the data window, for stored channel index c.</summary>
    public Func<int, int, int, float> Value { get; set; } = SyntheticExr.FormulaA;
    /// <summary>Raw sample bits (overrides <see cref="Value"/>): the low 16 bits for HALF.</summary>
    public Func<int, int, int, uint>? Bits { get; set; }
    /// <summary>Store every chunk uncompressed (packed size == unpacked size).</summary>
    public bool ForceRaw { get; set; }
    /// <summary>Replaces a chunk's packed bytes: (chunk index, unpacked bytes) → packed bytes.</summary>
    public Func<int, byte[], byte[]>? PackedOverride { get; set; }
    public int RandomSeed { get; set; } = 1234;

    public bool Tiled => TileWidth != null;
}

public sealed class ExrBuild
{
    public required byte[] Bytes { get; init; }
    public required long OffsetTablePos { get; init; }
    /// <summary>File offsets of the level-0 chunks, by table index.</summary>
    public required long[] ChunkOffsets { get; init; }
    /// <summary>How many level-0 chunks were stored raw because compression didn't shrink them.</summary>
    public required int RawChunks { get; init; }
}

/// <summary>
/// Writes OpenEXR files in code (test only). The encoders follow the same OpenEXR reference as the decoder
/// (ImfRle, ImfZip, ImfPizCompressor, ImfHuf, ImfWav), so correctness against real files rests on ExrOracleFixtures.
/// </summary>
public static class SyntheticExr
{
    public static float FormulaA(int x, int y, int c) => ((x * 7 + y * 13 + c * 5) % 61 - 20) / 8f;

    public static int LinesPerBlock(int compression) => compression switch { 3 => 16, 4 => 32, _ => 1 };

    public static uint SampleBits(ExrSpec s, int x, int y, int c)
    {
        if (s.Bits != null) return s.Bits(x, y, c);
        var v = s.Value(x, y, c);
        return s.Channels[c].Type switch
        {
            1 => BitConverter.HalfToUInt16Bits((Half)v),
            2 => BitConverter.SingleToUInt32Bits(v),
            _ => (uint)v,
        };
    }

    /// <summary>The float the decoder must produce for a sample.</summary>
    public static float Expected(ExrSpec s, int x, int y, int c)
    {
        var bits = SampleBits(s, x, y, c);
        return s.Channels[c].Type switch
        {
            1 => (float)BitConverter.UInt16BitsToHalf((ushort)bits),
            2 => BitConverter.UInt32BitsToSingle(bits),
            _ => bits,
        };
    }

    public static string Write(string dir, string name, ExrSpec s)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Build(s).Bytes);
        return path;
    }

    public static string WriteHeaderOnly(string dir, string name, ExrSpec s)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Header(s));
        return path;
    }

    public static byte[] Header(ExrSpec s)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(20000630);
        w.Write(s.Version | (s.Tiled ? 0x200 : 0) | s.ExtraFlags);
        if (!s.OmitChannels) Attr(w, "channels", "chlist", s.RawChlist ?? Chlist(s.Channels));
        if (!s.OmitCompression) Attr(w, "compression", "compression", [(byte)s.Compression]);
        int x1 = s.XMin + s.Width - 1, y1 = s.YMin + s.Height - 1;
        if (!s.OmitDataWindow) Attr(w, "dataWindow", "box2i", Box(s.XMin, s.YMin, x1, y1));
        var dw = s.DisplayWindow ?? (s.XMin, s.YMin, x1, y1);
        Attr(w, "displayWindow", "box2i", Box(dw.XMin, dw.YMin, dw.XMax, dw.YMax));
        Attr(w, "lineOrder", "lineOrder", [(byte)s.LineOrder]);
        Attr(w, "pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attr(w, "screenWindowCenter", "v2f", new byte[8]);
        Attr(w, "screenWindowWidth", "float", BitConverter.GetBytes(1f));
        if (s.Tiled && !s.OmitTiles)
        {
            var td = new byte[9];
            BinaryPrimitives.WriteUInt32LittleEndian(td, (uint)s.TileWidth!.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(td.AsSpan(4), (uint)s.TileHeight!.Value);
            td[8] = (byte)s.LevelMode;
            Attr(w, "tiles", "tiledesc", td);
        }
        if (s.Envmap is { } env) Attr(w, "envmap", "envmap", [(byte)env]);
        foreach (var (n, t, v) in s.ExtraAttributes) Attr(w, n, t, v);
        w.Write((byte)0);
        return ms.ToArray();
    }

    public static byte[] Chlist(IEnumerable<ExrCh> channels)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        foreach (var c in channels)
        {
            w.Write(Encoding.Latin1.GetBytes(c.Name));
            w.Write((byte)0);
            w.Write(c.Type);
            w.Write(0);
            w.Write(c.XSampling);
            w.Write(c.YSampling);
        }
        w.Write((byte)0);
        return ms.ToArray();
    }

    public static void Attr(BinaryWriter w, string name, string type, byte[] value)
    {
        w.Write(Encoding.Latin1.GetBytes(name));
        w.Write((byte)0);
        w.Write(Encoding.Latin1.GetBytes(type));
        w.Write((byte)0);
        w.Write(value.Length);
        w.Write(value);
    }

    private static byte[] Box(int a, int b, int c, int d)
    {
        var r = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(r, a);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), b);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(8), c);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(12), d);
        return r;
    }

    private sealed record Chunk(int Index, int Level, int Tx, int Ty, int X0, int Y0, int W, int H);

    public static ExrBuild Build(ExrSpec s)
    {
        var header = Header(s);
        var chunks = new List<Chunk>();
        if (s.Tiled)
        {
            int tw = s.TileWidth!.Value, th = s.TileHeight!.Value;
            var levels = s.LevelMode == 1 ? (int)Math.Floor(Math.Log2(Math.Max(s.Width, s.Height))) + 1 : 1;
            for (var l = 0; l < levels; l++)
            {
                int lw = Math.Max(1, s.Width >> l), lh = Math.Max(1, s.Height >> l);
                int nx = (lw + tw - 1) / tw, ny = (lh + th - 1) / th;
                for (var ty = 0; ty < ny; ty++)
                for (var tx = 0; tx < nx; tx++)
                    chunks.Add(new Chunk(chunks.Count, l, tx, ty, tx * tw, ty * th, Math.Min(tw, lw - tx * tw), Math.Min(th, lh - ty * th)));
            }
        }
        else
        {
            var lpb = LinesPerBlock(s.Compression);
            for (int y = 0, i = 0; y < s.Height; y += lpb, i++)
                chunks.Add(new Chunk(i, 0, 0, 0, 0, y, s.Width, Math.Min(lpb, s.Height - y)));
        }

        var level0 = chunks.Count(c => c.Level == 0);
        var tablePos = header.Length;
        var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(new byte[8 * chunks.Count]);
        var offsets = new long[chunks.Count];
        var order = Enumerable.Range(0, chunks.Count).ToArray();
        if (s.LineOrder == 1) Array.Reverse(order);
        if (s.LineOrder == 2) new Random(s.RandomSeed).Shuffle(order);
        var raw = 0;
        var w = new BinaryWriter(ms);
        foreach (var i in order)
        {
            var c = chunks[i];
            offsets[i] = ms.Position;
            var unpacked = c.Level == 0 ? Unpacked(s, c.X0, c.Y0, c.W, c.H) : new byte[c.W * c.H * s.Channels.Sum(ch => ch.Size)];
            byte[] packed;
            if (c.Level == 0 && s.PackedOverride != null) packed = s.PackedOverride(i, unpacked);
            else
            {
                packed = s.ForceRaw || c.Level != 0 ? unpacked : Compress(s, unpacked, c.W, c.H);
                if (packed.Length >= unpacked.Length) packed = unpacked;
                if (c.Level == 0 && packed == unpacked && s.Compression != 0) raw++;
            }
            if (s.Tiled)
            {
                w.Write(c.Tx); w.Write(c.Ty); w.Write(c.Level); w.Write(c.Level);
            }
            else w.Write(s.YMin + c.Y0);
            w.Write(packed.Length);
            w.Write(packed);
        }
        var bytes = ms.ToArray();
        for (var i = 0; i < chunks.Count; i++) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(tablePos + 8 * i), offsets[i]);
        return new ExrBuild { Bytes = bytes, OffsetTablePos = tablePos, ChunkOffsets = offsets[..level0], RawChunks = raw };
    }

    /// <summary>The uncompressed chunk layout: per line, per stored channel, w little-endian samples.</summary>
    public static byte[] Unpacked(ExrSpec s, int x0, int y0, int w, int h)
    {
        var bpp = s.Channels.Sum(c => c.Size);
        var b = new byte[w * h * bpp];
        var o = 0;
        for (var y = 0; y < h; y++)
            for (var c = 0; c < s.Channels.Count; c++)
                for (var x = 0; x < w; x++)
                {
                    var bits = SampleBits(s, x0 + x, y0 + y, c);
                    if (s.Channels[c].Size == 2) { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)bits); o += 2; }
                    else { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), bits); o += 4; }
                }
        return b;
    }

    public static byte[] Compress(ExrSpec s, byte[] unpacked, int w, int h) => s.Compression switch
    {
        0 => unpacked,
        1 => RleCompress(Predict(unpacked)),
        2 or 3 => Zlib(Predict(unpacked)),
        4 => PizCompress(unpacked, w, h, s.Channels.Select(c => c.Size / 2).ToArray()),
        _ => unpacked,   // unsupported ids: the header is what the tests exercise
    };

    /// <summary>The ZIP/RLE pre-step: interleave the two halves, then the byte predictor.</summary>
    public static byte[] Predict(byte[] src)
    {
        var t = new byte[src.Length];
        int t1 = 0, t2 = (src.Length + 1) / 2, i = 0;
        while (true)
        {
            if (i < src.Length) t[t1++] = src[i++]; else break;
            if (i < src.Length) t[t2++] = src[i++]; else break;
        }
        var p = t.Length > 0 ? t[0] : 0;
        for (var k = 1; k < t.Length; k++)
        {
            var d = t[k] - p + 128 + 256;
            p = t[k];
            t[k] = (byte)d;
        }
        return t;
    }

    public static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    public static byte[] RleCompress(byte[] input)
    {
        var o = new List<byte>();
        int runStart = 0, runEnd = 1, end = input.Length;
        while (runStart < end)
        {
            while (runEnd < end && input[runStart] == input[runEnd] && runEnd - runStart - 1 < 127) ++runEnd;
            if (runEnd - runStart >= 3)
            {
                o.Add((byte)(runEnd - runStart - 1));
                o.Add(input[runStart]);
                runStart = runEnd;
            }
            else
            {
                while (runEnd < end
                       && ((runEnd + 1 >= end || input[runEnd] != input[runEnd + 1])
                           || (runEnd + 2 >= end || input[runEnd + 1] != input[runEnd + 2]))
                       && runEnd - runStart < 127)
                    ++runEnd;
                o.Add((byte)(sbyte)(runStart - runEnd));
                while (runStart < runEnd) o.Add(input[runStart++]);
            }
            ++runEnd;
        }
        return o.ToArray();
    }

    // ---- PIZ (forward) ----

    /// <summary>PIZ-compresses one block in the chunk layout. sizes are per channel in 16-bit words.</summary>
    public static byte[] PizCompress(byte[] unpacked, int nx, int ny, int[] sizes)
    {
        var words = unpacked.Length / 2;
        var tmp = new ushort[words];
        var planes = new int[sizes.Length];
        for (int c = 0, at = 0; c < sizes.Length; c++) { planes[c] = at; at += nx * ny * sizes[c]; }
        var o = 0;
        for (var y = 0; y < ny; y++)
            for (var c = 0; c < sizes.Length; c++)
                for (var i = 0; i < nx * sizes[c]; i++, o += 2)
                    tmp[planes[c] + y * nx * sizes[c] + i] = BinaryPrimitives.ReadUInt16LittleEndian(unpacked.AsSpan(o));

        var bitmap = new byte[8192];
        foreach (var v in tmp) bitmap[v >> 3] |= (byte)(1 << (v & 7));
        bitmap[0] &= 0xFE;
        int minNz = 8191, maxNz = 0;
        for (var i = 0; i < 8192; i++)
            if (bitmap[i] != 0) { minNz = Math.Min(minNz, i); maxNz = Math.Max(maxNz, i); }
        var lut = new ushort[65536];
        var k = 0;
        for (var i = 0; i < 65536; i++) lut[i] = i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0 ? (ushort)k++ : (ushort)0;
        var maxValue = (ushort)(k - 1);
        for (var i = 0; i < tmp.Length; i++) tmp[i] = lut[tmp[i]];

        for (var c = 0; c < sizes.Length; c++)
            for (var j = 0; j < sizes[c]; j++) Wav2Encode(tmp, planes[c] + j, nx, sizes[c], ny, nx * sizes[c], maxValue);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)minNz);
        w.Write((ushort)maxNz);
        if (minNz <= maxNz) w.Write(bitmap, minNz, maxNz - minNz + 1);
        var huf = HufCompress(tmp);
        w.Write(huf.Length);
        w.Write(huf);
        return ms.ToArray();
    }

    public static void Wav2Encode(ushort[] a, int start, int nx, int ox, int ny, int oy, ushort mx)
    {
        var w14 = mx < 1 << 14;
        var n = Math.Min(nx, ny);
        int p = 1, p2 = 2;
        while (p2 <= n)
        {
            var py = start;
            var ey = start + oy * (ny - p2);
            int oy1 = oy * p, oy2 = oy * p2, ox1 = ox * p, ox2 = ox * p2;
            for (; py <= ey; py += oy2)
            {
                var px = py;
                var ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                    Enc(w14, a[px], a[p01], out var i00, out var i01);
                    Enc(w14, a[p10], a[p11], out var i10, out var i11);
                    Enc(w14, i00, i10, out a[px], out a[p10]);
                    Enc(w14, i01, i11, out a[p01], out a[p11]);
                }
                if ((nx & p) != 0)
                {
                    var p10 = px + oy1;
                    Enc(w14, a[px], a[p10], out var i00, out a[p10]);
                    a[px] = i00;
                }
            }
            if ((ny & p) != 0)
            {
                var px = py;
                var ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    var p01 = px + ox1;
                    Enc(w14, a[px], a[p01], out var i00, out a[p01]);
                    a[px] = i00;
                }
            }
            p = p2;
            p2 <<= 1;
        }
    }

    private static void Enc(bool w14, ushort a, ushort b, out ushort l, out ushort h)
    {
        if (w14)
        {
            int @as = (short)a, bs = (short)b;
            l = (ushort)(short)((@as + bs) >> 1);
            h = (ushort)(short)(@as - bs);
        }
        else
        {
            var ao = (a + 0x8000) & 0xFFFF;
            var m = (ao + b) >> 1;
            var d = ao - b;
            if (d < 0) m = (m + 0x8000) & 0xFFFF;
            d &= 0xFFFF;
            l = (ushort)m;
            h = (ushort)d;
        }
    }

    /// <summary>
    /// A simple valid Huffman encoder: every used symbol (plus the run-length symbol, max + 1) gets the same code length,
    /// so blocks with more than 16,383 distinct words exercise the decoder's long-code (&gt; 14 bit) path. Runs of a
    /// repeated word use the run-length symbol, as ImfHuf's sendCode does.
    /// </summary>
    public static byte[] HufCompress(ushort[] input)
    {
        var used = new bool[65537];
        foreach (var v in input) used[v] = true;
        int im = Array.IndexOf(used, true), iMax = Array.LastIndexOf(used, true);
        var rlc = iMax + 1;
        used[rlc] = true;
        var count = used.Count(u => u);
        var len = Math.Max(1, (int)Math.Ceiling(Math.Log2(count)));
        var hcode = new long[65537];
        for (var i = 0; i < hcode.Length; i++) hcode[i] = used[i] ? len : 0;
        Canonical(hcode);

        var bits = new BitWriter();
        // Packed code-length table (hufPackEncTable).
        for (var i = im; i <= rlc; i++)
        {
            var l = (int)(hcode[i] & 63);
            if (l == 0)
            {
                var zerun = 1;
                while (i < rlc && zerun < 255 + 6)
                {
                    if ((hcode[i + 1] & 63) > 0) break;
                    i++;
                    zerun++;
                }
                if (zerun >= 2)
                {
                    if (zerun >= 6) { bits.Put(6, 63); bits.Put(8, zerun - 6); }
                    else bits.Put(6, 59 + zerun - 2);
                    continue;
                }
            }
            bits.Put(6, l);
        }
        var table = bits.Finish(out _);

        var data = new BitWriter();
        var s = input[0];
        var cs = 0;
        for (var i = 1; i < input.Length; i++)
        {
            if (s == input[i] && cs < 255) cs++;
            else { SendCode(data, hcode[s], cs, hcode[rlc]); cs = 0; }
            s = input[i];
        }
        SendCode(data, hcode[s], cs, hcode[rlc]);
        var payload = data.Finish(out var nBits);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(im); w.Write(rlc); w.Write(table.Length); w.Write(nBits); w.Write(0);
        w.Write(table);
        w.Write(payload);
        return ms.ToArray();
    }

    private static void SendCode(BitWriter o, long sCode, int runCount, long runCode)
    {
        int sl = (int)(sCode & 63), rl = (int)(runCode & 63);
        if (sl + rl + 8 < sl * runCount)
        {
            o.Put(sl, sCode >> 6);
            o.Put(rl, runCode >> 6);
            o.Put(8, runCount);
        }
        else
            while (runCount-- >= 0) o.Put(sl, sCode >> 6);
    }

    /// <summary>OpenEXR's canonical code assignment (hufCanonicalCodeTable), written independently of the decoder's copy.</summary>
    public static void Canonical(long[] hcode)
    {
        var n = new long[59];
        foreach (var l in hcode) n[l]++;
        long c = 0;
        for (var i = 58; i > 0; --i)
        {
            var nc = (c + n[i]) >> 1;
            n[i] = c;
            c = nc;
        }
        for (var i = 0; i < hcode.Length; i++)
        {
            var l = (int)hcode[i];
            if (l > 0) hcode[i] = (n[l]++ << 6) | (uint)l;
        }
    }

    public sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private ulong c;
        private int lc;
        private long total;

        public void Put(int nBits, long value)
        {
            c = (c << nBits) | ((ulong)value & ((1UL << nBits) - 1));
            lc += nBits;
            total += nBits;
            while (lc >= 8) bytes.Add((byte)(c >> (lc -= 8)));
        }

        public byte[] Finish(out int nBits)
        {
            if (lc > 0) bytes.Add((byte)(c << (8 - lc)));
            lc = 0;
            nBits = (int)total;
            return bytes.ToArray();
        }
    }
}
