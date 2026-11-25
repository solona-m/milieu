using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.GameFiles;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class OpaqueBc7Tests
{
    private static long DecodedSse(byte[] block, byte[] src) => Bc7Metrics.BlockSse(Bc7Metrics.Decode(block, 4, 4), src);

    private static int ModeOf(byte first) =>
        (first & 1) != 0 ? 0 : (first & 2) != 0 ? 1 : (first & 4) != 0 ? 2 : (first & 8) != 0 ? 3
        : (first & 16) != 0 ? 4 : (first & 32) != 0 ? 5 : (first & 64) != 0 ? 6 : (first & 128) != 0 ? 7 : -1;

    private static void AssertOpaque(byte[] decoded)
    {
        for (var i = 3; i < decoded.Length; i += 4) Assert.Equal(255, decoded[i]);
    }

    /// <summary>A change to either value changes every user texture: re-run Bc7QualityTests (Slow) and OpaqueBc7Tests and
    /// amend docs/adr/0016.</summary>
    [Fact] // [AC-more-bundled-scenes-9]
    public void Defaults_ThreeMode1Partitions_CacheIdV2()
    {
        Assert.Equal(3, OpaqueBc7.DefaultMode1Partitions);
        var encoder = new OpaqueBc7();
        Assert.Equal(3, encoder.Mode1Partitions);
        Assert.Equal("opaque-bc7-2", encoder.CacheId);
    }

    [Fact] // [AC-fast-bc7-3]
    public async Task EncodesExpectedBlockCount()
    {
        var px = new byte[16 * 8 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 100; px[i + 2] = 50; px[i + 3] = 255; }
        var blocks = await new OpaqueBc7().EncodeMipAsync(px, 16, 8, null, default);
        Assert.Equal(128, blocks.Length);
        var decoded = Bc7Metrics.Decode(blocks, 16, 8);
        for (var i = 0; i < decoded.Length; i += 4)
        {
            Assert.InRange(decoded[i], 198, 202);
            Assert.InRange(decoded[i + 1], 98, 102);
            Assert.InRange(decoded[i + 2], 48, 52);
            Assert.Equal(255, decoded[i + 3]);
        }
    }

    [Theory] // [AC-fast-bc7-3] [EDGE-fast-bc7-14]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void RandomBlocks_EncoderSseEqualsDecodedSse_PerMode(int modeBits)
    {
        var rnd = new Random(11);
        var block = new byte[16];
        for (var n = 0; n < 10_000; n++)
        {
            var src = Bc7Metrics.RandomBlock(rnd);
            var sse = Bc7Block.Encode(src, block, (Bc7Modes)modeBits, 0, 2);
            Assert.NotEqual(0, block[0]);
            var decoded = Bc7Metrics.Decode(block, 4, 4);
            AssertOpaque(decoded);
            Assert.Equal(sse, Bc7Metrics.BlockSse(decoded, src));
        }
    }

    [Theory] // [EDGE-fast-bc7-16] [EDGE-fast-bc7-13]
    [InlineData(7)]
    [InlineData(2)]
    [InlineData(1)]
    public void StructuredBlocks_EncoderSseEqualsDecodedSse(int modeBits)
    {
        var rnd = new Random(23);
        var block = new byte[16];
        for (var n = 0; n < 20_000; n++)
        {
            var src = Bc7Metrics.StructuredBlock(rnd);
            var sse = Bc7Block.Encode(src, block, (Bc7Modes)modeBits, 0, 4);
            Assert.NotEqual(0, block[0]);
            var decoded = Bc7Metrics.Decode(block, 4, 4);
            AssertOpaque(decoded);
            Assert.Equal(sse, Bc7Metrics.BlockSse(decoded, src));
        }
    }

    [Fact] // [AC-fast-bc7-3] [EDGE-fast-bc7-3] [EDGE-fast-bc7-1]
    public void GoldenBlocks_ThreeReadingsAgree()
    {
        foreach (var g in Bc7GoldenBlocks.All)
        {
            // Reading 2: BcDecoder decodes the spec literal to the spec texels.
            Assert.Equal(g.Texels, Bc7Metrics.Decode(g.Bytes, 4, 4));
            // Reading 3: the encoder's packing reproduces the literal from its fields.
            var packed = new byte[16];
            Bc7Block.PackFields(new Bc7BlockFields(g.Mode, g.Partition, g.Endpoints, g.PBits, g.Indices), packed);
            Assert.Equal(g.Bytes, packed);
        }
    }

    // ------------------------------------------------------------ solid blocks

    private static readonly int[] W4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    private static readonly int[] W2 = [0, 21, 43, 64];

    /// <summary>Brute force, independent of the encoder's tables: [index * 256 + v] → best (error, a, b) over all 7-bit pairs.</summary>
    private static (int Err, int A, int B)[] BruteTable(int[] weights, Func<int, int> expand)
    {
        var t = new (int, int, int)[weights.Length * 256];
        for (var i = 0; i < weights.Length; i++)
            for (var v = 0; v < 256; v++)
            {
                var best = (int.MaxValue, 0, 0);
                for (var a = 0; a < 128; a++)
                    for (var b = 0; b < 128; b++)
                    {
                        var d = Math.Abs(((expand(a) * (64 - weights[i]) + expand(b) * weights[i] + 32) >> 6) - v);
                        if (d < best.Item1) best = (d, a, b);
                    }
                t[i * 256 + v] = best;
            }
        return t;
    }

    private static readonly Lazy<(int, int, int)[]> Brute6 = new(() => BruteTable(W4, q => 2 * q + 1));
    private static readonly Lazy<(int, int, int)[]> Brute5 = new(() => BruteTable(W2, q => (q << 1) | (q >> 6)));

    /// <summary>The optimum over both modes and every shared index, and its block (anchor rule applied), packed and decoded.</summary>
    private static (long Sse, byte[] Decoded) BruteForceSolid(int r, int g, int b)
    {
        int[] rgb = [r, g, b];
        long best = long.MaxValue;
        int bestMode = 6, bestI = 0;
        foreach (var (mode, table, n) in new[] { (6, Brute6.Value, 16), (5, Brute5.Value, 4) })
            for (var i = 0; i < n; i++)
            {
                long e = 0;
                foreach (var v in rgb) { long d = table[i * 256 + v].Item1; e += d * d; }
                if (e * 16 < best) { best = e * 16; bestMode = mode; bestI = i; }
            }
        var tbl = bestMode == 6 ? Brute6.Value : Brute5.Value;
        var count = bestMode == 6 ? 16 : 4;
        var ends = new int[6];
        for (var ch = 0; ch < 3; ch++) { var t = tbl[bestI * 256 + rgb[ch]]; ends[ch * 2] = t.Item2; ends[ch * 2 + 1] = t.Item3; }
        var idx = bestI;
        if (idx >= count / 2)
        {
            for (var ch = 0; ch < 3; ch++) (ends[ch * 2], ends[ch * 2 + 1]) = (ends[ch * 2 + 1], ends[ch * 2]);
            idx = count - 1 - idx;
        }
        var indices = new byte[16];
        Array.Fill(indices, (byte)idx);
        var block = new byte[16];
        Bc7Block.PackFields(new Bc7BlockFields(bestMode, 0, ends, bestMode == 6 ? [1, 1] : [], indices), block);
        return (best, Bc7Metrics.Decode(block, 4, 4));
    }

    private static byte[] SolidBlock(int r, int g, int b)
    {
        var px = new byte[64];
        for (var i = 0; i < 16; i++) { px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)b; px[i * 4 + 3] = 255; }
        return px;
    }

    [Fact] // [EDGE-fast-bc7-1]
    public void SolidBlocks_MatchBruteForceOptimum()
    {
        var rnd = new Random(5);
        var triples = new List<(int, int, int)>();
        for (var v = 0; v < 256; v++)
        {
            triples.Add((v, rnd.Next(256), rnd.Next(256)));
            triples.Add((rnd.Next(256), v, rnd.Next(256)));
            triples.Add((rnd.Next(256), rnd.Next(256), v));
        }
        int[] specials = [0, 1, 2, 127, 128, 254, 255];
        foreach (var a in specials) foreach (var b in specials) foreach (var c in specials) triples.Add((a, b, c));
        for (var i = 0; i < 1000; i++) triples.Add((rnd.Next(256), rnd.Next(256), rnd.Next(256)));

        var block = new byte[16];
        foreach (var (r, g, b) in triples)
        {
            var src = SolidBlock(r, g, b);
            var (optimum, expectedDecoded) = BruteForceSolid(r, g, b);
            // The brute force's own block decodes to its optimum (checks the brute force and the anchor rule).
            Assert.Equal(optimum, Bc7Metrics.BlockSse(expectedDecoded, src));

            var sse = Bc7Block.Encode(src, block, Bc7Modes.All, 0, 2);
            var decoded = Bc7Metrics.Decode(block, 4, 4);
            AssertOpaque(decoded);
            Assert.Equal(optimum, Bc7Metrics.BlockSse(decoded, src));
            Assert.Equal(optimum, sse);
        }
        // Black, and colours with a 0 channel, are exact.
        Assert.Equal(0, DecodedSse(Encode(SolidBlock(0, 0, 0)), SolidBlock(0, 0, 0)));
        Assert.Equal(0, DecodedSse(Encode(SolidBlock(0, 128, 254)), SolidBlock(0, 128, 254)));
    }

    private static byte[] Encode(byte[] src, Bc7Modes modes = Bc7Modes.All)
    {
        var block = new byte[16];
        Bc7Block.Encode(src, block, modes, 0, 2);
        return block;
    }

    // ------------------------------------------------------------ mode 1 and partitions

    [Fact] // [EDGE-fast-bc7-2] [EDGE-fast-bc7-3]
    public void Mode1_EveryPartition_TwoColoursExact()
    {
        // Values exactly representable in mode 1 with p-bit 0: expand((q << 1) | 0).
        static int E(int q) { var v7 = q << 1; return (v7 << 1) | (v7 >> 6); }
        int[] c0 = [E(5), E(40), E(63)];
        int[] c1 = [E(60), E(2), E(20)];
        var block = new byte[16];
        for (var p = 0; p < 64; p++)
        {
            var src = new byte[64];
            for (var i = 0; i < 16; i++)
            {
                var c = ((Bc7Block.Partitions2[p] >> i) & 1) == 0 ? c0 : c1;
                src[i * 4] = (byte)c[0]; src[i * 4 + 1] = (byte)c[1]; src[i * 4 + 2] = (byte)c[2]; src[i * 4 + 3] = 255;
            }
            var sse = Bc7Block.EncodeMode1(src, p, block);
            Assert.Equal(1, ModeOf(block[0]));
            Assert.Equal(p, (block[0] >> 2) & 63);
            Assert.Equal(0, sse);
            Assert.Equal(src, Bc7Metrics.Decode(block, 4, 4));
        }
    }

    [Fact] // [EDGE-fast-bc7-2]
    public void HardEdgeBlock_PrefersMode1()
    {
        var src = new byte[64];
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                // A hard diagonal edge between a red ramp and a blue ramp: no single line fits both sides.
                var i = (y * 4 + x) * 4;
                var k = (byte)(60 + 40 * ((x + 2 * y) % 4));
                if (x + y >= 3) { src[i] = 20; src[i + 1] = 30; src[i + 2] = k; }
                else { src[i] = k; src[i + 1] = 30; src[i + 2] = 20; }
                src[i + 3] = 255;
            }
        var all = new byte[16];
        var only6 = new byte[16];
        var sseAll = Bc7Block.Encode(src, all, Bc7Modes.All, 0, 2);
        var sse6 = Bc7Block.Encode(src, only6, Bc7Modes.Mode6, 0, 2);
        Assert.Equal(1, ModeOf(all[0]));
        Assert.True(sseAll < sse6, $"mode 1 {sseAll} vs mode 6 {sse6}");
    }

    [Fact] // [EDGE-fast-bc7-17]
    public void PartitionTies_LowestIndexWins_Deterministic()
    {
        // A block whose rows are all identical: every partition that splits only by column scores the same as its
        // vertical mirror; a solid-grey-with-one-column block gives exact ties among several partitions.
        var src = new byte[64];
        for (var i = 0; i < 16; i++) { var v = (byte)((i % 4) == 3 ? 200 : 50); src[i * 4] = v; src[i * 4 + 1] = v; src[i * 4 + 2] = v; src[i * 4 + 3] = 255; }
        Span<int> top = stackalloc int[64];
        var n = Bc7Block.RankPartitions(src, top);
        Assert.Equal(64, n);
        // Verify the order is non-decreasing in score and ties are in ascending index order.
        double Score(int p)
        {
            double s0 = 0, s1 = 0, q0 = 0, q1 = 0; int n0 = 0, n1 = 0;
            for (var i = 0; i < 16; i++)
            {
                double v = src[i * 4];
                if (((Bc7Block.Partitions2[p] >> i) & 1) == 0) { s0 += v; q0 += v * v; n0++; } else { s1 += v; q1 += v * v; n1++; }
            }
            return 3 * ((q0 - s0 * s0 / n0) + (q1 - s1 * s1 / n1));
        }
        var ties = 0;
        for (var k = 1; k < 64; k++)
        {
            var a = Score(top[k - 1]);
            var b = Score(top[k]);
            Assert.True(a <= b + 1e-6, $"rank {k}: {a} > {b}");
            if (Math.Abs(a - b) < 1e-6) { ties++; Assert.True(top[k - 1] < top[k], $"tie at rank {k}: {top[k - 1]} before {top[k]}"); }
        }
        Assert.True(ties > 0, "the fixture must produce at least one exact tie");
        var first = Encode(src);
        for (var r = 0; r < 5; r++) Assert.Equal(first, Encode(src));
        Assert.Equal(Bc7Block.Encode(src, new byte[16], Bc7Modes.All, 0, 2), DecodedSse(first, src));
    }

    [Fact] // [EDGE-fast-bc7-4]
    public void DegenerateAxes_NoNaN()
    {
        var cases = new List<byte[]>();
        for (var ch = 0; ch < 3; ch++)
        {
            var b = new byte[64];
            for (var i = 0; i < 16; i++) { b[i * 4 + ch] = (byte)(i * 16); b[i * 4 + 3] = 255; }
            cases.Add(b);
        }
        var grey = new byte[64];
        for (var i = 0; i < 16; i++) { grey[i * 4] = grey[i * 4 + 1] = grey[i * 4 + 2] = (byte)(i * 17); grey[i * 4 + 3] = 255; }
        cases.Add(grey);
        var two = new byte[64];
        for (var i = 0; i < 16; i++) { var v = (byte)(i < 8 ? 10 : 245); two[i * 4] = two[i * 4 + 1] = two[i * 4 + 2] = v; two[i * 4 + 3] = 255; }
        cases.Add(two);
        foreach (var src in cases)
            foreach (var modes in new[] { Bc7Modes.Mode6, Bc7Modes.Mode1, Bc7Modes.All, Bc7Modes.Mode6 | Bc7Modes.Mode1 })
            {
                var block = new byte[16];
                var sse = Bc7Block.Encode(src, block, modes, 0, 2);
                Assert.Equal(sse, DecodedSse(block, src));
                Assert.True(sse <= 16 * 3 * 16, $"{modes}: SSE {sse}");
            }
    }

    [Fact] // [EDGE-fast-bc7-5]
    public async Task InputAlphaIgnored_OutputOpaque()
    {
        var rnd = new Random(3);
        var px = new byte[32 * 16 * 4];
        rnd.NextBytes(px);
        var opaque = (byte[])px.Clone();
        var zero = (byte[])px.Clone();
        for (var i = 3; i < px.Length; i += 4) { opaque[i] = 255; zero[i] = 0; }
        var enc = new OpaqueBc7();
        var a = await enc.EncodeMipAsync(opaque, 32, 16, null, default);
        Assert.Equal(a, await enc.EncodeMipAsync(zero, 32, 16, null, default));
        Assert.Equal(a, await enc.EncodeMipAsync(px, 32, 16, null, default));
        AssertOpaque(Bc7Metrics.Decode(a, 32, 16));
    }

    [Fact] // [EDGE-fast-bc7-6]
    public async Task RejectsBadInput()
    {
        var enc = new OpaqueBc7();
        await Assert.ThrowsAsync<ArgumentException>(() => enc.EncodeMipAsync(new byte[8 * 4 * 4 - 1], 8, 4, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => enc.EncodeMipAsync(new byte[6 * 4 * 4], 6, 4, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => enc.EncodeMipAsync(new byte[4 * 6 * 4], 4, 6, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => enc.EncodeMipAsync([], 0, 0, null, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpaqueBc7(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpaqueBc7(-1));
        Assert.Throws<ArgumentException>(() => Bc7Block.Encode(new byte[64], new byte[16], Bc7Modes.Mode1, 0, 0));
        // Neither mode 6 nor mode 1 allowed: a non-solid block would otherwise be left unwritten (the reserved mode 0).
        Assert.Throws<ArgumentException>(() => Bc7Block.Encode(Bc7Metrics.RandomBlock(new Random(1)), new byte[16], Bc7Modes.Solid, 0, 2));
        Assert.Throws<ArgumentException>(() => Bc7Block.Encode(new byte[64], new byte[16], 0, 0, 2));
        Assert.Throws<ArgumentException>(() => Bc7Block.Encode(new byte[63], new byte[16], Bc7Modes.All, 0, 2));
        Assert.Throws<ArgumentException>(() => Bc7Block.Encode(new byte[64], new byte[15], Bc7Modes.All, 0, 2));
        await Assert.ThrowsAsync<ArgumentNullException>(() => enc.EncodeMipAsync(null!, 8, 4, null, default));
        Assert.Throws<ArgumentException>(() => Bc7Block.PackFields(new Bc7BlockFields(7, 0, new int[6], [1, 1], new byte[16]), new byte[16]));
    }

    // ------------------------------------------------------------ cancellation, progress, concurrency

    [Fact] // [AC-fast-bc7-4] [EDGE-fast-bc7-7]
    public async Task CancelInsideMip0_ReturnsPromptly()
    {
        const int w = 8192, h = 4096;
        var px = new byte[w * h * 4];
        new Random(1).NextBytes(px);
        using var cts = new CancellationTokenSource();
        var firstProgress = new TaskCompletionSource();
        var progress = new Bc7.SyncProgress(_ => { firstProgress.TrySetResult(); cts.Cancel(); });

        var sw = Stopwatch.StartNew();
        var encode = new OpaqueBc7().EncodeMipAsync(px, w, h, progress, cts.Token);
        var winner = await Task.WhenAny(firstProgress.Task, Task.Delay(50));
        if (winner != firstProgress.Task) cts.Cancel();
        var cancelledAt = sw.Elapsed;

        Assert.False(encode.IsCompletedSuccessfully, "the encode finished before it could be cancelled; enlarge the input");
        await Assert.ThrowsAsync<OperationCanceledException>(() => encode);
        Assert.True(sw.Elapsed - cancelledAt < TimeSpan.FromSeconds(2), $"cancel took {(sw.Elapsed - cancelledAt).TotalMilliseconds} ms");
    }

    [Fact] // [EDGE-fast-bc7-7]
    public async Task CancelledBeforeStart_ThrowsExactOce()
    {
        var started = 0;
        var enc = new OpaqueBc7 { RowStarted = _ => Interlocked.Increment(ref started) };
        await Assert.ThrowsAsync<OperationCanceledException>(() => enc.EncodeMipAsync(new byte[64 * 64 * 4], 64, 64, null, new CancellationToken(true)));
        Assert.Equal(0, started);
    }

    /// <summary>Delegates to <see cref="OpaqueBc7"/> and cancels once the first mip is done.</summary>
    private sealed class CancelAfterFirst(CancellationTokenSource cts) : IBlockEncoder
    {
        private readonly OpaqueBc7 inner = new();
        public int Calls;
        public string CacheId => inner.CacheId;

        public async Task<byte[]> EncodeMipAsync(byte[] rgba, int width, int height, IProgress<float>? progress, CancellationToken ct)
        {
            Calls++;
            var r = await inner.EncodeMipAsync(rgba, width, height, progress, ct);
            cts.Cancel();
            return r;
        }
    }

    [Fact] // [EDGE-fast-bc7-7]
    public async Task EncodeChain_CancelBetweenMips()
    {
        using var cts = new CancellationTokenSource();
        var sizes = MipChain.Sizes(64, 32);
        var mips = MipChain.Build(new byte[64 * 32 * 4], 64, 32);
        var enc = new CancelAfterFirst(cts);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Bc7.EncodeChainAsync(enc, mips, sizes, null, cts.Token));
        Assert.Equal(1, enc.Calls);
    }

    [Fact] // [AC-fast-bc7-4] [EDGE-fast-bc7-8]
    public async Task Progress_MonotonicManyReportsEndsAtOne()
    {
        var px = Bc7Metrics.Synthetic("foliage", 2048, 1024);
        var reports = new List<float>();
        var progress = new Bc7.SyncProgress(f => { lock (reports) reports.Add(f); });
        await new OpaqueBc7().EncodeMipAsync(px, 2048, 1024, progress, default);
        Assert.True(reports.Count >= 20, $"{reports.Count} reports");
        for (var i = 1; i < reports.Count; i++) Assert.True(reports[i] >= reports[i - 1], $"report {i}: {reports[i]} < {reports[i - 1]}");
        Assert.Equal(1f, reports[^1]);
    }

    [Fact] // [AC-fast-bc7-4] [EDGE-fast-bc7-8]
    public async Task Progress_RowsNotAMultipleOfTheStep_StillEndsAtOne()
    {
        // 257 block rows, a report every 2: the last row's count is odd, so the closing report is what reaches 1.
        var px = Bc7Metrics.Synthetic("sky", 4, 1028);
        var reports = new List<float>();
        await new OpaqueBc7().EncodeMipAsync(px, 4, 1028, new Bc7.SyncProgress(f => { lock (reports) reports.Add(f); }), default);
        for (var i = 1; i < reports.Count; i++) Assert.True(reports[i] >= reports[i - 1]);
        Assert.Equal(1f, reports[^1]);
        Assert.True(reports[^2] < 1f, "the step reports alone must not have reached 1");
    }

    [Fact] // [AC-fast-bc7-5] [EDGE-fast-bc7-15]
    public async Task Concurrency_NeverExceedsWorkers()
    {
        var px = Bc7Metrics.Synthetic("foliage", 4096, 512);
        foreach (var (workers, limit) in new[] { ((int?)null, ToneMapper.Workers), (2, 2) })
        {
            int current = 0, peak = 0;
            var enc = new OpaqueBc7(workers)
            {
                RowStarted = _ =>
                {
                    var c = Interlocked.Increment(ref current);
                    int p;
                    while (c > (p = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, c, p) != p) { }
                },
                RowFinished = _ => Interlocked.Decrement(ref current),
            };
            await enc.EncodeMipAsync(px, 4096, 512, null, default);
            Assert.InRange(peak, 1, limit);
        }
    }

    [Fact] // [EDGE-fast-bc7-9]
    public async Task ChainOf64x32_AllMipLengthsAcceptedByAtexWriter()
    {
        var sizes = MipChain.Sizes(64, 32);
        var mips = MipChain.Build(Bc7Metrics.Synthetic("sky", 64, 32), 64, 32);
        var blocks = await Bc7.EncodeChainAsync(new OpaqueBc7(), mips, sizes, null, default);
        for (var i = 0; i < sizes.Count; i++) Assert.Equal(AtexWriter.BlockBytes(sizes[i].Width, sizes[i].Height), blocks[i].Length);
        using var ms = new System.IO.MemoryStream();
        AtexWriter.Write(ms, 64, 32, blocks);
        Assert.Equal(AtexWriter.Layout(64, 32).TotalLength, ms.Length);
    }
}
