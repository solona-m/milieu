using System;
using System.Buffers.Binary;

namespace Milieu.Imaging;

/// <summary>Explicit block fields for <see cref="Bc7Block.PackFields"/> (a test seam).</summary>
internal sealed record Bc7BlockFields(int Mode, int Partition, int[] Endpoints, int[] PBits, byte[] Indices);

/// <summary>Which BC7 modes <see cref="Bc7Block.Encode"/> may use.</summary>
[Flags]
internal enum Bc7Modes
{
    Mode6 = 1,
    Mode1 = 2,
    Solid = 4,
    All = 7,
}

/// <summary>
/// One 4x4 block of an opaque image → 16 bytes of BC7 (docs/adr/0015). Written from the BC7 format specification
/// (Khronos Data Format Specification, BC7 section): mode 6 for every block, mode 1 on the best-ranked two-subset
/// partitions when mode 6's error is high, and exact solid colours through mode-6 / mode-5 tables. Input alpha is ignored;
/// every block decodes with alpha 255. Pure, allocation-free, no <c>unsafe</c>; fixed iteration counts, so the same input
/// gives the same bytes on the same machine.
/// </summary>
internal static class Bc7Block
{
    /// <summary>The 64 two-subset partitions: bit i set = pixel i (row-major) is in subset 1. Format data.</summary>
    internal static readonly ushort[] Partitions2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
        0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
        0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
        0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
        0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    /// <summary>The anchor pixel of subset 1 for each two-subset partition. Format data.</summary>
    internal static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15,
        2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15,
        2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2,
        15, 15, 15, 15, 15, 2, 2, 15,
    ];

    internal static readonly int[] W4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    internal static readonly int[] W3 = [0, 9, 18, 27, 37, 46, 55, 64];
    internal static readonly int[] W2 = [0, 21, 43, 64];

    private const int PowerSteps = 8;
    private const int RefinePasses = 2;
    private const int SearchRounds = 2;

    /// <summary>Mode 6 with both p-bits 1: the 7-bit endpoint whose odd value 2q+1 is nearest to v (ties: lower).</summary>
    private static readonly byte[] Q6 = BuildQ6();

    /// <summary>Mode 1: [p * 256 + v] → the 6-bit endpoint nearest to v under shared p-bit p (ties: lower).</summary>
    private static readonly byte[] Q1 = BuildQ1();

    /// <summary>Mode 5 solid: [i * 256 + v] for shared colour index i ∈ 0..3: endpoints a (bits 0-7), b (8-15), error (16+).</summary>
    private static readonly int[] Solid5 = BuildSolid(W2, Expand7);

    /// <summary>Mode 6 solid (p = 1): [i * 256 + v] for shared index i ∈ 0..15: 7-bit endpoints a, b and the error, packed as above.</summary>
    private static readonly int[] Solid6 = BuildSolid(W4, Expand6);

    internal static int Interp(int e0, int e1, int w) => (e0 * (64 - w) + e1 * w + 32) >> 6;

    internal static int Expand6(int q) => 2 * q + 1;

    internal static int Expand1(int q, int p)
    {
        var v7 = (q << 1) | p;
        return (v7 << 1) | (v7 >> 6);
    }

    internal static int Expand7(int v7) => (v7 << 1) | (v7 >> 6);

    private static byte[] BuildQ6()
    {
        var t = new byte[256];
        for (var v = 0; v < 256; v++) t[v] = (byte)(v == 0 ? 0 : (v - 1) / 2);
        return t;
    }

    private static byte[] BuildQ1()
    {
        var t = new byte[512];
        for (var p = 0; p < 2; p++)
            for (var v = 0; v < 256; v++)
            {
                int best = 0, bestErr = int.MaxValue;
                for (var q = 0; q < 64; q++)
                {
                    var err = Math.Abs(Expand1(q, p) - v);
                    if (err < bestErr) { bestErr = err; best = q; }
                }
                t[p * 256 + v] = (byte)best;
            }
        return t;
    }

    /// <summary>
    /// For every 8-bit value and every shared index, the 7-bit endpoint pair (expanded by <paramref name="expand"/>) whose
    /// interpolation is nearest (ties: lower error, then lower a, then lower b).
    /// </summary>
    private static int[] BuildSolid(int[] weights, Func<int, int> expand)
    {
        var t = new int[weights.Length * 256];
        Span<int> ex = stackalloc int[128];
        for (var q = 0; q < 128; q++) ex[q] = expand(q);
        for (var i = 0; i < weights.Length; i++)
        {
            var w = weights[i];
            for (var v = 0; v < 256; v++)
            {
                int bestA = 0, bestB = 0, bestErr = int.MaxValue;
                for (var a = 0; a < 128 && bestErr > 0; a++)
                    for (var b = 0; b < 128; b++)
                    {
                        var err = Math.Abs(Interp(ex[a], ex[b], w) - v);
                        if (err < bestErr) { bestErr = err; bestA = a; bestB = b; if (err == 0) break; }
                    }
                t[i * 256 + v] = bestA | (bestB << 8) | (bestErr << 16);
            }
        }
        return t;
    }

    /// <summary>
    /// Encodes one block (<paramref name="rgba16"/>: 64 bytes, row-major 4x4 RGBA) into <paramref name="block16"/> and
    /// returns the RGB sum of squared errors of the block as the encoder reconstructs it.
    /// </summary>
    internal static long Encode(ReadOnlySpan<byte> rgba16, Span<byte> block16, Bc7Modes allowed, int mode1Threshold, int mode1Partitions)
    {
        if (rgba16.Length < 64) throw new ArgumentException("A block is 64 bytes.", nameof(rgba16));
        if (block16.Length < 16) throw new ArgumentException("A BC7 block is 16 bytes.", nameof(block16));
        if ((allowed & (Bc7Modes.Mode6 | Bc7Modes.Mode1)) == 0) throw new ArgumentException("Mode 6 or mode 1 must be allowed.", nameof(allowed));
        if ((allowed & Bc7Modes.Mode6) == 0 && mode1Partitions <= 0)
            throw new ArgumentException("Mode 1 alone needs at least one partition.", nameof(mode1Partitions));

        if ((allowed & Bc7Modes.Solid) != 0 && IsSolid(rgba16)) return EncodeSolid(rgba16[0], rgba16[1], rgba16[2], block16);

        long best = long.MaxValue;
        if ((allowed & Bc7Modes.Mode6) != 0) best = EncodeMode6(rgba16, block16);
        if ((allowed & Bc7Modes.Mode1) != 0 && best > mode1Threshold && mode1Partitions > 0)
        {
            Span<int> top = stackalloc int[Math.Min(64, mode1Partitions)];
            var n = RankPartitions(rgba16, top);
            Span<byte> candidate = stackalloc byte[16];
            for (var i = 0; i < n; i++)
            {
                var sse = EncodeMode1Core(rgba16, top[i], candidate);
                if (sse < best) { best = sse; candidate.CopyTo(block16); }
            }
        }
        return best;
    }

    private static bool IsSolid(ReadOnlySpan<byte> px)
    {
        for (var i = 4; i < 64; i += 4)
            if (px[i] != px[0] || px[i + 1] != px[1] || px[i + 2] != px[2]) return false;
        return true;
    }

    // ---------------------------------------------------------------- solid

    /// <summary>
    /// A solid colour: the better of mode 6 and mode 5, each searched jointly under its one shared index (R, G and B share
    /// it). Pixel 0's index MSB is implied 0, so an upper index is stored as its mirror with the endpoints swapped.
    /// </summary>
    internal static long EncodeSolid(int r, int g, int b, Span<byte> block16)
    {
        Span<int> rgb = [r, g, b];
        var (i6, sse6) = BestShared(Solid6, 16, rgb);
        var (i5, sse5) = BestShared(Solid5, 4, rgb);
        Span<int> q = stackalloc int[6];
        Span<byte> idx = stackalloc byte[16];
        var useMode5 = sse5 < sse6;
        var table = useMode5 ? Solid5 : Solid6;
        var i = useMode5 ? i5 : i6;
        var n = useMode5 ? 4 : 16;
        for (var ch = 0; ch < 3; ch++)
        {
            var e = table[i * 256 + rgb[ch]];
            q[ch * 2] = e & 0xFF;
            q[ch * 2 + 1] = (e >> 8) & 0xFF;
        }
        if (i >= n / 2)
        {
            for (var ch = 0; ch < 3; ch++) (q[ch * 2], q[ch * 2 + 1]) = (q[ch * 2 + 1], q[ch * 2]);
            i = n - 1 - i;
        }
        idx.Fill((byte)i);
        if (useMode5) PackMode5(q, idx, block16);
        else PackMode6(q, idx, block16);
        return useMode5 ? sse5 : sse6;
    }

    private static (int Index, long Sse) BestShared(int[] table, int n, ReadOnlySpan<int> rgb)
    {
        int bestI = 0;
        long best = long.MaxValue;
        for (var i = 0; i < n; i++)
        {
            long e = 0;
            for (var ch = 0; ch < 3; ch++)
            {
                long err = table[i * 256 + rgb[ch]] >> 16;
                e += err * err;
            }
            if (e < best) { best = e; bestI = i; }
        }
        return (bestI, best * 16);
    }

    private static void PackMode5(ReadOnlySpan<int> q, ReadOnlySpan<byte> idx, Span<byte> block16)
    {
        var bw = new BitWriter();
        bw.Write(1u << 5, 6);                                  // mode 5
        bw.Write(0, 2);                                        // rotation 0
        for (var ch = 0; ch < 3; ch++) { bw.Write((uint)q[ch * 2], 7); bw.Write((uint)q[ch * 2 + 1], 7); }
        bw.Write(255, 8); bw.Write(255, 8);                    // alpha endpoints
        bw.Write(idx[0], 1);                                   // colour indices; the anchor's MSB is implied 0
        for (var i = 1; i < 16; i++) bw.Write(idx[i], 2);
        bw.Write(0, 1);                                        // alpha indices, all 0
        for (var i = 1; i < 16; i++) bw.Write(0, 2);
        bw.CopyTo(block16);
    }

    // ---------------------------------------------------------------- mode 6

    private static long EncodeMode6(ReadOnlySpan<byte> px, Span<byte> block16)
    {
        Span<float> lo = stackalloc float[3];
        Span<float> hi = stackalloc float[3];
        Fit(px, 0xFFFF, lo, hi);

        Span<int> q = stackalloc int[6];
        Span<int> bestQ = stackalloc int[6];
        Span<byte> idx = stackalloc byte[16];
        Span<byte> bestIdx = stackalloc byte[16];
        Span<int> e0 = stackalloc int[3];
        Span<int> e1 = stackalloc int[3];

        Quantise6(lo, hi, q);
        long best = Pick6(px, q, idx, e0, e1);
        q.CopyTo(bestQ); idx.CopyTo(bestIdx);
        for (var pass = 0; pass < RefinePasses; pass++)
        {
            if (!LeastSquares(px, 0xFFFF, idx, W4, lo, hi)) break;
            Quantise6(lo, hi, q);
            var sse = Pick6(px, q, idx, e0, e1);
            if (sse < best) { best = sse; q.CopyTo(bestQ); idx.CopyTo(bestIdx); }
        }

        // Local search: nudge each endpoint by ±1 and keep any change that lowers the error (fixed round count).
        for (var round = 0; round < SearchRounds && best > 0; round++)
        {
            var improved = false;
            for (var k = 0; k < 6; k++)
                for (var d = -1; d <= 1; d += 2)
                {
                    var v = bestQ[k] + d;
                    if (v < 0 || v > 127) continue;
                    bestQ.CopyTo(q);
                    q[k] = v;
                    var sse = Pick6(px, q, idx, e0, e1);
                    if (sse < best) { best = sse; q.CopyTo(bestQ); idx.CopyTo(bestIdx); improved = true; }
                }
            if (!improved) break;
        }

        if (bestIdx[0] >= 8)
        {
            for (var ch = 0; ch < 3; ch++) (bestQ[ch * 2], bestQ[ch * 2 + 1]) = (bestQ[ch * 2 + 1], bestQ[ch * 2]);
            for (var i = 0; i < 16; i++) bestIdx[i] = (byte)(15 - bestIdx[i]);
        }
        PackMode6(bestQ, bestIdx, block16);
        return best;
    }

    private static void Quantise6(ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, Span<int> q)
    {
        for (var ch = 0; ch < 3; ch++)
        {
            q[ch * 2] = Q6[ToByte(lo[ch])];
            q[ch * 2 + 1] = Q6[ToByte(hi[ch])];
        }
    }

    private static long Pick6(ReadOnlySpan<byte> px, ReadOnlySpan<int> q, Span<byte> idx, Span<int> e0, Span<int> e1)
    {
        for (var ch = 0; ch < 3; ch++) { e0[ch] = Expand6(q[ch * 2]); e1[ch] = Expand6(q[ch * 2 + 1]); }
        return PickIndices(px, 0xFFFF, e0, e1, W4, idx);
    }

    private static void PackMode6(ReadOnlySpan<int> q, ReadOnlySpan<byte> idx, Span<byte> block16) => PackMode6(q, 1, 1, idx, block16);

    private static void PackMode6(ReadOnlySpan<int> q, int p0, int p1, ReadOnlySpan<byte> idx, Span<byte> block16)
    {
        var bw = new BitWriter();
        bw.Write(1u << 6, 7);                                  // mode 6
        for (var ch = 0; ch < 3; ch++) { bw.Write((uint)q[ch * 2], 7); bw.Write((uint)q[ch * 2 + 1], 7); }
        bw.Write(127, 7); bw.Write(127, 7);                    // alpha endpoints: with p = 1 → 255
        bw.Write((uint)p0, 1); bw.Write((uint)p1, 1);          // p-bits
        bw.Write(idx[0], 3);
        for (var i = 1; i < 16; i++) bw.Write(idx[i], 4);
        bw.CopyTo(block16);
    }

    // ---------------------------------------------------------------- mode 1

    /// <summary>Test seam: mode 1 on a forced partition. Returns the encoder's SSE.</summary>
    internal static long EncodeMode1(ReadOnlySpan<byte> rgba16, int partition, Span<byte> block16) =>
        EncodeMode1Core(rgba16, partition, block16);

    /// <summary>
    /// Ranks the 64 partitions by the summed squared distance of each subset's pixels to its mean (ties: lower index) and
    /// writes the best <c>top.Length</c> into <paramref name="top"/>. Returns how many were written.
    /// </summary>
    internal static int RankPartitions(ReadOnlySpan<byte> px, Span<int> top)
    {
        long tr = 0, tg = 0, tb = 0, ss = 0;
        for (var i = 0; i < 16; i++)
        {
            int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            tr += r; tg += g; tb += b; ss += r * r + g * g + b * b;
        }
        var k = Math.Min(top.Length, 64);
        Span<double> scores = stackalloc double[k];
        var filled = 0;
        for (var p = 0; p < 64; p++)
        {
            var mask = Partitions2[p];
            long r1 = 0, g1 = 0, b1 = 0;
            var n1 = 0;
            for (var i = 0; i < 16; i++)
                if ((mask >> i & 1) != 0) { r1 += px[i * 4]; g1 += px[i * 4 + 1]; b1 += px[i * 4 + 2]; n1++; }
            var n0 = 16 - n1;
            long r0 = tr - r1, g0 = tg - g1, b0 = tb - b1;
            var score = ss - (double)(r0 * r0 + g0 * g0 + b0 * b0) / n0 - (double)(r1 * r1 + g1 * g1 + b1 * b1) / n1;
            // Insert into the sorted top list; equal scores keep the earlier (lower) partition first.
            var pos = filled;
            while (pos > 0 && score < scores[pos - 1]) pos--;
            if (pos >= k) continue;
            var end = Math.Min(filled, k - 1);
            for (var j = end; j > pos; j--) { scores[j] = scores[j - 1]; top[j] = top[j - 1]; }
            scores[pos] = score; top[pos] = p;
            if (filled < k) filled++;
        }
        return filled;
    }

    private static long EncodeMode1Core(ReadOnlySpan<byte> px, int partition, Span<byte> block16)
    {
        var mask1 = Partitions2[partition];
        var mask0 = (ushort)(~mask1 & 0xFFFF);
        Span<int> q = stackalloc int[12];   // q[subset * 6 + ch * 2 + end]
        Span<int> pbits = stackalloc int[2];
        Span<byte> idx = stackalloc byte[16];
        long total = 0;
        for (var s = 0; s < 2; s++)
            total += FitSubset1(px, s == 0 ? mask0 : mask1, q.Slice(s * 6, 6), out pbits[s], idx);

        // Anchor fixes: pixel 0 for subset 0, the table anchor for subset 1.
        int anchor1 = Anchor2[partition];
        for (var s = 0; s < 2; s++)
        {
            var anchor = s == 0 ? 0 : anchor1;
            if (idx[anchor] < 4) continue;
            var sq = q.Slice(s * 6, 6);
            for (var ch = 0; ch < 3; ch++) (sq[ch * 2], sq[ch * 2 + 1]) = (sq[ch * 2 + 1], sq[ch * 2]);
            var mask = s == 0 ? mask0 : mask1;
            for (var i = 0; i < 16; i++) if ((mask >> i & 1) != 0) idx[i] = (byte)(7 - idx[i]);
        }

        Span<int> e = stackalloc int[12];
        for (var ch = 0; ch < 3; ch++)
            for (var s = 0; s < 2; s++)
            {
                e[ch * 4 + s * 2] = q[s * 6 + ch * 2];
                e[ch * 4 + s * 2 + 1] = q[s * 6 + ch * 2 + 1];
            }
        PackMode1(partition, e, pbits[0], pbits[1], idx, block16);
        return total;
    }

    /// <summary>One mode-1 subset: fit, both shared p-bits, one least-squares refinement. Writes q (ch*2+end), the p-bit and the subset's indices.</summary>
    private static long FitSubset1(ReadOnlySpan<byte> px, ushort set, Span<int> q, out int pbit, Span<byte> idx)
    {
        Span<float> lo = stackalloc float[3];
        Span<float> hi = stackalloc float[3];
        Span<int> tq = stackalloc int[6];
        Span<byte> tidx = stackalloc byte[16];
        Span<int> e0 = stackalloc int[3];
        Span<int> e1 = stackalloc int[3];
        Fit(px, set, lo, hi);

        long best = long.MaxValue;
        pbit = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                // Refine from the best indices so far.
                if (!LeastSquares(px, set, idx, W3, lo, hi)) break;
            }
            for (var p = 0; p < 2; p++)
            {
                for (var ch = 0; ch < 3; ch++)
                {
                    tq[ch * 2] = Q1[p * 256 + ToByte(lo[ch])];
                    tq[ch * 2 + 1] = Q1[p * 256 + ToByte(hi[ch])];
                    e0[ch] = Expand1(tq[ch * 2], p);
                    e1[ch] = Expand1(tq[ch * 2 + 1], p);
                }
                var sse = PickIndices(px, set, e0, e1, W3, tidx);
                if (sse < best)
                {
                    best = sse; pbit = p; tq.CopyTo(q);
                    for (var i = 0; i < 16; i++) if ((set >> i & 1) != 0) idx[i] = tidx[i];
                }
            }
        }
        return best;
    }

    /// <summary>Mode 1 fields in bit order: endpoints R0..R3 G0..G3 B0..B3 (6 bits; 0-1 subset 0, 2-3 subset 1), p-bits, indices.</summary>
    private static void PackMode1(int partition, ReadOnlySpan<int> e, int p0, int p1, ReadOnlySpan<byte> idx, Span<byte> block16)
    {
        var anchor1 = Anchor2[partition];
        var bw = new BitWriter();
        bw.Write(2, 2);                                        // mode 1
        bw.Write((uint)partition, 6);
        for (var k = 0; k < 12; k++) bw.Write((uint)e[k], 6);
        bw.Write((uint)p0, 1); bw.Write((uint)p1, 1);
        for (var i = 0; i < 16; i++) bw.Write(idx[i], i == 0 || i == anchor1 ? 2 : 3);
        bw.CopyTo(block16);
    }

    /// <summary>
    /// Test seam: packs explicit fields exactly as <see cref="Encode"/> does. Endpoints in bit order (mode 6 and 5:
    /// R0 R1 G0 G1 B0 B1, 7 bits; mode 1: R0..R3 G0..G3 B0..B3, 6 bits); p-bits (mode 6 and 1: two); 16 indices (anchors'
    /// MSB must already be 0). Mode 5 is the opaque form: alpha endpoints 255, rotation 0, alpha indices 0.
    /// </summary>
    internal static void PackFields(Bc7BlockFields f, Span<byte> block16)
    {
        switch (f.Mode)
        {
            case 6: PackMode6(f.Endpoints, f.PBits[0], f.PBits[1], f.Indices, block16); break;
            case 5: PackMode5(f.Endpoints, f.Indices, block16); break;
            case 1: PackMode1(f.Partition, f.Endpoints, f.PBits[0], f.PBits[1], f.Indices, block16); break;
            default: throw new ArgumentException($"Mode {f.Mode} is not packed by this encoder.", nameof(f));
        }
    }

    // ---------------------------------------------------------------- shared maths

    private static int ToByte(float v)
    {
        var r = (int)MathF.Round(v);
        return r < 0 ? 0 : r > 255 ? 255 : r;
    }

    /// <summary>The principal-axis line through a pixel set, clipped to the pixels' projections: endpoints lo/hi (floats).</summary>
    private static void Fit(ReadOnlySpan<byte> px, ushort set, Span<float> lo, Span<float> hi)
    {
        float mr = 0, mg = 0, mb = 0;
        var n = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((set >> i & 1) == 0) continue;
            mr += px[i * 4]; mg += px[i * 4 + 1]; mb += px[i * 4 + 2]; n++;
        }
        if (n == 0) { lo.Clear(); hi.Clear(); return; }
        mr /= n; mg /= n; mb /= n;

        float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((set >> i & 1) == 0) continue;
            float r = px[i * 4] - mr, g = px[i * 4 + 1] - mg, b = px[i * 4 + 2] - mb;
            crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
        }

        // Power iteration seeded from the largest-variance channel.
        float ar, ag, ab;
        if (crr >= cgg && crr >= cbb) { ar = 1; ag = 0; ab = 0; }
        else if (cgg >= cbb) { ar = 0; ag = 1; ab = 0; }
        else { ar = 0; ag = 0; ab = 1; }
        if (crr + cgg + cbb <= 0)
        {
            lo[0] = hi[0] = mr; lo[1] = hi[1] = mg; lo[2] = hi[2] = mb;
            return;
        }
        for (var s = 0; s < PowerSteps; s++)
        {
            var nr = crr * ar + crg * ag + crb * ab;
            var ng = crg * ar + cgg * ag + cgb * ab;
            var nb = crb * ar + cgb * ag + cbb * ab;
            var len = MathF.Sqrt(nr * nr + ng * ng + nb * nb);
            if (!(len > 1e-12f)) break;
            ar = nr / len; ag = ng / len; ab = nb / len;
        }

        float tmin = float.MaxValue, tmax = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            if ((set >> i & 1) == 0) continue;
            var t = (px[i * 4] - mr) * ar + (px[i * 4 + 1] - mg) * ag + (px[i * 4 + 2] - mb) * ab;
            if (t < tmin) tmin = t;
            if (t > tmax) tmax = t;
        }
        lo[0] = mr + tmin * ar; lo[1] = mg + tmin * ag; lo[2] = mb + tmin * ab;
        hi[0] = mr + tmax * ar; hi[1] = mg + tmax * ag; hi[2] = mb + tmax * ab;
    }

    /// <summary>Endpoints that minimise the squared error for the given indices (closed form per channel). False if degenerate.</summary>
    private static bool LeastSquares(ReadOnlySpan<byte> px, ushort set, ReadOnlySpan<byte> idx, int[] weights, Span<float> lo, Span<float> hi)
    {
        float aa = 0, ab = 0, bb = 0;
        float x0r = 0, x0g = 0, x0b = 0, x1r = 0, x1g = 0, x1b = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((set >> i & 1) == 0) continue;
            var t = weights[idx[i]] / 64f;
            var s = 1 - t;
            aa += s * s; ab += s * t; bb += t * t;
            float r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            x0r += s * r; x0g += s * g; x0b += s * b;
            x1r += t * r; x1g += t * g; x1b += t * b;
        }
        var det = aa * bb - ab * ab;
        if (!(det > 1e-6f)) return false;
        var inv = 1 / det;
        lo[0] = (bb * x0r - ab * x1r) * inv; hi[0] = (aa * x1r - ab * x0r) * inv;
        lo[1] = (bb * x0g - ab * x1g) * inv; hi[1] = (aa * x1g - ab * x0g) * inv;
        lo[2] = (bb * x0b - ab * x1b) * inv; hi[2] = (aa * x1b - ab * x0b) * inv;
        return true;
    }

    /// <summary>[n * 65 + w]: the index whose weight is nearest to w (0..64) in the n-entry weight table (ties: lower).</summary>
    private static readonly byte[] NearestWeight = BuildNearestWeight();

    private static byte[] BuildNearestWeight()
    {
        var t = new byte[17 * 65];
        foreach (var weights in new[] { W2, W3, W4 })
            for (var w = 0; w <= 64; w++)
            {
                int best = 0, bestD = int.MaxValue;
                for (var k = 0; k < weights.Length; k++)
                    if (Math.Abs(weights[k] - w) < bestD) { bestD = Math.Abs(weights[k] - w); best = k; }
                t[weights.Length * 65 + w] = (byte)best;
            }
        return t;
    }

    /// <summary>
    /// For each pixel in the set, a nearest palette entry: the pixel is projected onto the endpoint line and the entry at
    /// that weight and its two neighbours are compared exactly (ties: lower index). Returns the set's SSE.
    /// </summary>
    private static long PickIndices(ReadOnlySpan<byte> px, ushort set, ReadOnlySpan<int> e0, ReadOnlySpan<int> e1, int[] weights, Span<byte> idx)
    {
        var n = weights.Length;
        Span<int> pal = stackalloc int[48];
        for (var k = 0; k < n; k++)
        {
            var w = weights[k];
            pal[k * 3] = Interp(e0[0], e1[0], w);
            pal[k * 3 + 1] = Interp(e0[1], e1[1], w);
            pal[k * 3 + 2] = Interp(e0[2], e1[2], w);
        }
        int dr0 = e1[0] - e0[0], dg0 = e1[1] - e0[1], db0 = e1[2] - e0[2];
        var len2 = dr0 * dr0 + dg0 * dg0 + db0 * db0;
        long total = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((set >> i & 1) == 0) continue;
            int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            var guess = 0;
            if (len2 > 0)
            {
                var dot = (r - e0[0]) * dr0 + (g - e0[1]) * dg0 + (b - e0[2]) * db0;
                var w = dot <= 0 ? 0 : dot >= len2 ? 64 : (int)((dot * 64L + len2 / 2) / len2);
                guess = NearestWeight[n * 65 + w];
            }
            int best = int.MaxValue, bi = 0;
            var lo = Math.Max(0, guess - 1);
            var hi = Math.Min(n - 1, guess + 1);
            for (var k = lo; k <= hi; k++)
            {
                int dr = r - pal[k * 3], dg = g - pal[k * 3 + 1], db = b - pal[k * 3 + 2];
                var d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; bi = k; }
            }
            idx[i] = (byte)bi;
            total += best;
        }
        return total;
    }

    /// <summary>Little-endian bit writer over 128 bits, least significant bit first (the BC7 layout).</summary>
    private struct BitWriter
    {
        private ulong lo, hi;
        private int pos;

        public void Write(uint value, int bits)
        {
            for (var i = 0; i < bits; i++, pos++)
            {
                if ((value >> i & 1) == 0) continue;
                if (pos < 64) lo |= 1UL << pos;
                else hi |= 1UL << (pos - 64);
            }
        }

        public readonly void CopyTo(Span<byte> dest)
        {
            if (pos != 128) throw new InvalidOperationException($"BC7 block packed to {pos} bits, not 128.");
            BinaryPrimitives.WriteUInt64LittleEndian(dest, lo);
            BinaryPrimitives.WriteUInt64LittleEndian(dest[8..], hi);
        }
    }
}
