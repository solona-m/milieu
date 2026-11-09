using System;
using System.Buffers.Binary;
using System.Threading;

namespace Milieu.Imaging.Exr;

/// <summary>
/// PIZ chunk decoding: a port of OpenEXR's ImfPizCompressor (bitmap + reverse LUT), ImfHuf (Huffman with run-length
/// symbol) and ImfWav (2D Haar wavelet, 14- and 16-bit variants). Every read from the chunk is bounds-checked and any
/// inconsistency throws <see cref="ExrFormatException"/>. The fixed tables are allocated once per decode (≈ 0.8 MiB, plus up to
/// ≈ 0.25 MiB of long-code symbols), counted in <see cref="ExrLimits.WorkingSetBytes"/>. Codes longer than 14 bits are
/// resolved canonically by length (at most 44 steps per code), never by scanning the symbols that share a prefix, so a
/// hostile code table can't make decoding quadratic.
/// </summary>
internal sealed class ExrPiz
{
    private const int UShortRange = 1 << 16;
    private const int BitmapSize = UShortRange >> 3;
    internal const int HufEncSize = (1 << 16) + 1;
    private const int HufDecBits = 14;
    private const int HufDecSize = 1 << HufDecBits;
    private const int HufDecMask = HufDecSize - 1;
    private const int ShortZeroCodeRun = 59;
    private const int LongZeroCodeRun = 63;
    private const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;

    private readonly byte[] bitmap = new byte[BitmapSize];
    private readonly ushort[] lut = new ushort[UShortRange];
    private const int MaxCodeLength = 58;
    private const int CancelCheckMask = (1 << 16) - 1;

    // Only [im, iM] of hcode is ever written or read for a block, so it is never cleared.
    private readonly long[] hcode = new long[HufEncSize];
    // Indexed by the next 14 bits: (len << 24) | symbol for a code of up to 14 bits, LongPrefix where only longer codes
    // start, 0 where no code does.
    private readonly int[] dec = new int[HufDecSize];
    private const int LongPrefix = -1;
    // Long codes by length (canonical: the codes of one length are consecutive, in symbol order):
    // codes firstCode[l] .. firstCode[l] + lenCount[l] - 1 are symbols longSyms[lenBase[l] ..].
    private readonly long[] firstCode = new long[MaxCodeLength + 1];
    private readonly int[] lenCount = new int[MaxCodeLength + 1];
    private readonly int[] lenBase = new int[MaxCodeLength + 1];
    private int minLong, maxLong;
    private int[] longSyms = [];

    /// <summary>
    /// Decodes one PIZ block. <paramref name="sizes"/> are the stored channels' sample sizes in 16-bit words (1 for HALF,
    /// 2 for FLOAT/UINT); every channel is <paramref name="nx"/> x <paramref name="ny"/>. The output is the usual chunk
    /// layout: per line, per channel, nx*size little-endian 16-bit words. <paramref name="scratch"/> holds the words.
    /// </summary>
    public void Decompress(ReadOnlySpan<byte> src, Span<byte> dst, int nx, int ny, ReadOnlySpan<int> sizes, ushort[] scratch,
                           ExrDecodeTrace? trace, CancellationToken ct = default)
    {
        long words = 0;
        foreach (var sz in sizes) words += (long)nx * ny * sz;
        if (words * 2 != dst.Length || words > scratch.Length) throw new ExrFormatException("PIZ block size mismatch");
        var tmp = scratch.AsSpan(0, (int)words);

        if (src.Length < 4) throw new ExrFormatException("PIZ data too short");
        int minNonZero = BinaryPrimitives.ReadUInt16LittleEndian(src), maxNonZero = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
        var p = 4;
        Array.Clear(bitmap);
        if (minNonZero <= maxNonZero)
        {
            if (maxNonZero >= BitmapSize) throw new ExrFormatException("PIZ bitmap range invalid");
            var n = maxNonZero - minNonZero + 1;
            if (src.Length - p < n) throw new ExrFormatException("PIZ bitmap past the data");
            src.Slice(p, n).CopyTo(bitmap.AsSpan(minNonZero));
            p += n;
        }
        var maxValue = ReverseLutFromBitmap();

        if (src.Length - p < 4) throw new ExrFormatException("PIZ data too short");
        var length = BinaryPrimitives.ReadInt32LittleEndian(src[p..]);
        p += 4;
        if (length < 0 || length > src.Length - p) throw new ExrFormatException("PIZ Huffman length past the data");
        HufUncompress(src.Slice(p, length), tmp, ct);

        var start = 0;
        var w16 = maxValue >= 1 << 14;
        foreach (var sz in sizes)
        {
            for (var j = 0; j < sz; j++) Wav2Decode(tmp[(start + j)..], nx, sz, ny, nx * sz, maxValue);
            start += nx * ny * sz;
        }
        if (w16 && trace != null) trace.UsedWdec16 = true;

        for (var i = 0; i < tmp.Length; i++) tmp[i] = lut[tmp[i]];

        // Reassemble: per line, per channel, nx*size words from that channel's plane.
        var o = 0;
        for (var y = 0; y < ny; y++)
        {
            var plane = 0;
            foreach (var sz in sizes)
            {
                var n = nx * sz;
                var from = tmp.Slice(plane + y * n, n);
                for (var i = 0; i < n; i++, o += 2) BinaryPrimitives.WriteUInt16LittleEndian(dst[o..], from[i]);
                plane += nx * ny * sz;
            }
        }
    }

    private ushort ReverseLutFromBitmap()
    {
        var k = 0;
        for (var i = 0; i < UShortRange; i++)
            if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0) lut[k++] = (ushort)i;
        var n = k - 1;
        while (k < UShortRange) lut[k++] = 0;
        return (ushort)n;
    }

    // ---- Wavelet (ImfWav) ----

    private static void Wdec14(ushort l, ushort h, out ushort a, out ushort b)
    {
        int ls = (short)l, hi = (short)h;
        var ai = ls + (hi & 1) + (hi >> 1);
        a = (ushort)ai;
        b = (ushort)(ai - hi);
    }

    private static void Wdec16(ushort l, ushort h, out ushort a, out ushort b)
    {
        int m = l, d = h;
        var bb = (m - (d >> 1)) & 0xFFFF;
        var aa = (d + bb - 0x8000) & 0xFFFF;
        b = (ushort)bb;
        a = (ushort)aa;
    }

    private static void Dec(bool w14, ushort l, ushort h, out ushort a, out ushort b)
    {
        if (w14) Wdec14(l, h, out a, out b);
        else Wdec16(l, h, out a, out b);
    }

    /// <summary>In-place inverse 2D wavelet of an nx x ny plane with x stride <paramref name="ox"/> and y stride <paramref name="oy"/>.</summary>
    internal static void Wav2Decode(Span<ushort> a, int nx, int ox, int ny, int oy, ushort mx)
    {
        var w14 = mx < 1 << 14;
        var n = nx > ny ? ny : nx;
        var p = 1;
        while (p <= n) p <<= 1;
        p >>= 1;
        var p2 = p;
        p >>= 1;
        while (p >= 1)
        {
            var py = 0;
            var ey = oy * (ny - p2);
            int oy1 = oy * p, oy2 = oy * p2, ox1 = ox * p, ox2 = ox * p2;
            for (; py <= ey; py += oy2)
            {
                var px = py;
                var ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    int p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                    Dec(w14, a[px], a[p10], out var i00, out var i10);
                    Dec(w14, a[p01], a[p11], out var i01, out var i11);
                    Dec(w14, i00, i01, out a[px], out a[p01]);
                    Dec(w14, i10, i11, out a[p10], out a[p11]);
                }
                if ((nx & p) != 0)
                {
                    var p10 = px + oy1;
                    Dec(w14, a[px], a[p10], out var i00, out a[p10]);
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
                    Dec(w14, a[px], a[p01], out var i00, out a[p01]);
                    a[px] = i00;
                }
            }
            p2 = p;
            p >>= 1;
        }
    }

    // ---- Huffman (ImfHuf) ----

    private static int HufLength(long code) => (int)(code & 63);

    private static long HufCode(long code) => code >> 6;

    /// <summary>Decodes a Huffman-compressed block (20-byte header, packed code table, bit stream) into exactly out.Length words.</summary>
    internal void HufUncompress(ReadOnlySpan<byte> c, Span<ushort> output, CancellationToken ct = default)
    {
        if (c.Length == 0)
        {
            if (output.Length != 0) throw new ExrFormatException("PIZ Huffman data missing");
            return;
        }
        if (c.Length < 20) throw new ExrFormatException("PIZ Huffman header truncated");
        var im = BinaryPrimitives.ReadInt32LittleEndian(c);
        var iM = BinaryPrimitives.ReadInt32LittleEndian(c[4..]);
        var nBits = BinaryPrimitives.ReadInt32LittleEndian(c[12..]);
        if (im < 0 || im >= HufEncSize || iM < 0 || iM >= HufEncSize || im > iM) throw new ExrFormatException("PIZ Huffman table size invalid");
        if (nBits < 0 || ((long)nBits + 7) / 8 > c.Length - 20) throw new ExrFormatException("PIZ Huffman bit count past the data");
        var table = c[20..];
        var used = UnpackEncTable(table, im, iM);
        var data = table[used..];
        if (nBits > 8L * data.Length) throw new ExrFormatException("PIZ Huffman bit count invalid");
        BuildDecTable(im, iM);
        Decode(data, nBits, iM, output, ct);
    }

    private int UnpackEncTable(ReadOnlySpan<byte> p, int im, int iM)
    {
        var first = im;
        var last = iM;
        ulong c = 0;
        int lc = 0, i = 0;
        for (; im <= iM; im++)
        {
            if (i >= p.Length) throw new ExrFormatException("PIZ Huffman table truncated");
            var l = hcode[im] = GetBits(6, ref c, ref lc, p, ref i);
            if (l == LongZeroCodeRun)
            {
                if (i > p.Length) throw new ExrFormatException("PIZ Huffman table truncated");
                var zerun = (int)GetBits(8, ref c, ref lc, p, ref i) + ShortestLongRun;
                if (im + zerun > iM + 1) throw new ExrFormatException("PIZ Huffman table overrun");
                while (zerun-- > 0) hcode[im++] = 0;
                im--;
            }
            else if (l >= ShortZeroCodeRun)
            {
                var zerun = (int)l - ShortZeroCodeRun + 2;
                if (im + zerun > iM + 1) throw new ExrFormatException("PIZ Huffman table overrun");
                while (zerun-- > 0) hcode[im++] = 0;
                im--;
            }
        }
        CanonicalCodeTable(hcode, first, last);
        return i;
    }

    private static long GetBits(int nBits, ref ulong c, ref int lc, ReadOnlySpan<byte> p, ref int i)
    {
        while (lc < nBits)
        {
            if (i >= p.Length) throw new ExrFormatException("PIZ Huffman table truncated");
            c = (c << 8) | p[i++];
            lc += 8;
        }
        lc -= nBits;
        return (long)((c >> lc) & ((1UL << nBits) - 1));
    }

    /// <summary>
    /// Turns the code lengths in hcode[im..iM] into canonical codes, stored as (code &lt;&lt; 6) | length. Entries outside the
    /// range have length 0 in OpenEXR's full-table version, and zero lengths don't affect the assignment.
    /// </summary>
    internal static void CanonicalCodeTable(long[] hcode, int im, int iM)
    {
        Span<long> n = stackalloc long[59];
        for (var i = im; i <= iM; i++) n[(int)hcode[i]] += 1;
        long c = 0;
        for (var i = 58; i > 0; --i)
        {
            var nc = (c + n[i]) >> 1;
            n[i] = c;
            c = nc;
        }
        for (var i = im; i <= iM; i++)
        {
            var l = (int)hcode[i];
            if (l > 0) hcode[i] = (n[l]++ << 6) | (long)(uint)l;
        }
    }

    private void BuildDecTable(int im, int iM)
    {
        Array.Clear(dec);
        Array.Clear(lenCount);
        var longCount = 0;
        for (var i = im; i <= iM; i++)
        {
            var c = HufCode(hcode[i]);
            var l = HufLength(hcode[i]);
            if (l > 0 && (c >> l) != 0) throw new ExrFormatException("PIZ Huffman table entry invalid");
            if (l > HufDecBits)
            {
                var pi = (int)(c >> (l - HufDecBits));
                if (dec[pi] > 0) throw new ExrFormatException("PIZ Huffman table entry invalid");
                dec[pi] = LongPrefix;
                lenCount[l]++;
                longCount++;
            }
            else if (l > 0)
            {
                var pi = (int)(c << (HufDecBits - l));
                var entry = (l << 24) | i;
                for (var k = 1 << (HufDecBits - l); k > 0; k--, pi++)
                {
                    if (dec[pi] != 0) throw new ExrFormatException("PIZ Huffman table entry invalid");
                    dec[pi] = entry;
                }
            }
        }
        minLong = MaxCodeLength + 1;
        maxLong = 0;
        if (longCount == 0) return;
        if (longSyms.Length < longCount) longSyms = new int[longCount];
        for (int l = HufDecBits + 1, at = 0; l <= MaxCodeLength; l++)
        {
            lenBase[l] = at;
            at += lenCount[l];
            if (lenCount[l] > 0) { minLong = Math.Min(minLong, l); maxLong = l; }
        }
        // Symbols in ascending order within each length: the canonical order, so the first one holds the length's first code.
        Span<int> fill = stackalloc int[MaxCodeLength + 1];
        lenBase.CopyTo(fill);
        for (var i = im; i <= iM; i++)
        {
            var l = HufLength(hcode[i]);
            if (l > HufDecBits) longSyms[fill[l]++] = i;
        }
        for (var l = minLong; l <= maxLong; l++)
            if (lenCount[l] > 0) firstCode[l] = HufCode(hcode[longSyms[lenBase[l]]]);
    }

    private void Decode(ReadOnlySpan<byte> input, int nBits, int rlc, Span<ushort> output, CancellationToken ct)
    {
        ulong c = 0;
        int lc = 0, i = 0, o = 0, steps = 0;
        var ie = (int)(((long)nBits + 7) / 8);
        while (i < ie)
        {
            c = (c << 8) | input[i++];
            lc += 8;
            while (lc >= HufDecBits)
            {
                if ((++steps & CancelCheckMask) == 0) ct.ThrowIfCancellationRequested();
                var pi = (int)((c >> (lc - HufDecBits)) & HufDecMask);
                var e = dec[pi];
                if (e > 0)
                {
                    lc -= e >> 24;
                    GetCode(e & 0xFFFFFF, rlc, ref c, ref lc, input, ref i, ie, output, ref o);
                }
                else if (e == LongPrefix)
                {
                    // Canonical decoding by length: the code of length l is the top l bits; it is a symbol if it falls in
                    // that length's consecutive range. Equivalent to OpenEXR's prefix-bucket scan for a valid table.
                    var sym = -1;
                    for (var l = minLong; l <= maxLong; l++)
                    {
                        while (lc < l && i < ie)
                        {
                            c = (c << 8) | input[i++];
                            lc += 8;
                        }
                        if (lc < l) break;
                        if (lenCount[l] == 0) continue;
                        var offset = (long)((c >> (lc - l)) & ((1UL << l) - 1)) - firstCode[l];
                        if (offset >= 0 && offset < lenCount[l])
                        {
                            sym = longSyms[lenBase[l] + (int)offset];
                            lc -= l;
                            break;
                        }
                    }
                    if (sym < 0) throw new ExrFormatException("PIZ Huffman code invalid");
                    GetCode(sym, rlc, ref c, ref lc, input, ref i, ie, output, ref o);
                }
                else throw new ExrFormatException("PIZ Huffman code invalid");
            }
        }

        var r = (8 - nBits) & 7;
        c >>= r;
        lc -= r;
        while (lc > 0)
        {
            if ((++steps & CancelCheckMask) == 0) ct.ThrowIfCancellationRequested();
            var e = dec[(int)((c << (HufDecBits - lc)) & HufDecMask)];
            if (e <= 0 || (e >> 24) > lc) throw new ExrFormatException("PIZ Huffman code invalid");
            lc -= e >> 24;
            GetCode(e & 0xFFFFFF, rlc, ref c, ref lc, input, ref i, ie, output, ref o);
        }
        if (o != output.Length) throw new ExrFormatException("PIZ Huffman output too short");
    }

    private static void GetCode(int po, int rlc, ref ulong c, ref int lc, ReadOnlySpan<byte> input, ref int i, int ie,
                                Span<ushort> output, ref int o)
    {
        if (po == rlc)
        {
            if (lc < 8)
            {
                if (i >= ie) throw new ExrFormatException("PIZ Huffman run past the data");
                c = (c << 8) | input[i++];
                lc += 8;
            }
            lc -= 8;
            var cs = (int)((c >> lc) & 0xFF);
            if (output.Length - o < cs) throw new ExrFormatException("PIZ Huffman output too long");
            if (o < 1) throw new ExrFormatException("PIZ Huffman run with nothing to repeat");
            var s = output[o - 1];
            while (cs-- > 0) output[o++] = s;
        }
        else if (o < output.Length) output[o++] = (ushort)po;
        else throw new ExrFormatException("PIZ Huffman output too long");
    }
}
