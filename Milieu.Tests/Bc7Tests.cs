using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class Bc7Tests
{
    [Fact] // [AC-hdri-backdrop-6]
    public async Task EncodesExpectedBlockCount()
    {
        var px = new byte[16 * 8 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 100; px[i + 2] = 50; px[i + 3] = 255; }
        var blocks = await new Bc7().EncodeMipAsync(px, 16, 8, null, default);
        Assert.Equal(128, blocks.Length);

        var decoded = new BcDecoder().DecodeRaw(blocks, 16, 8, CompressionFormat.Bc7);
        foreach (var c in decoded)
        {
            Assert.InRange(c.r, 198, 202);
            Assert.InRange(c.g, 98, 102);
            Assert.InRange(c.b, 48, 52);
        }
    }

    /// <summary>Counts mips and can cancel after a given one.</summary>
    private sealed class CountingEncoder(int cancelAfter, CancellationTokenSource cts) : IBlockEncoder
    {
        public int Calls;

        public Task<byte[]> EncodeMipAsync(byte[] rgba, int width, int height, IProgress<float>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++Calls == cancelAfter) cts.Cancel();
            return Task.FromResult(new byte[Math.Max(1, width / 4) * Math.Max(1, height / 4) * 16]);
        }
    }

    [Fact] // [EDGE-hdri-backdrop-16]
    public async Task CancelBetweenMips_Throws()
    {
        using var cts = new CancellationTokenSource();
        var sizes = MipChain.Sizes(64, 32);
        var mips = new List<byte[]>();
        foreach (var (w, h) in sizes) mips.Add(new byte[w * h * 4]);
        var enc = new CountingEncoder(1, cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bc7.EncodeChainAsync(enc, mips, sizes, null, cts.Token));
        Assert.Equal(1, enc.Calls);

        var reports = new List<float>();
        using var ok = new CancellationTokenSource();
        var all = await Bc7.EncodeChainAsync(new CountingEncoder(-1, ok), mips, sizes, new Bc7.SyncProgress(reports.Add), ok.Token);
        Assert.Equal(sizes.Count, all.Count);
        Assert.Equal(1f, reports[^1], 4);
    }

    [Fact] // [EDGE-hdri-backdrop-58]
    public async Task CancelInsideMip0_ReturnsPromptly()
    {
        const int w = 4096, h = 4096;
        var px = new byte[w * h * 4];
        new Random(1).NextBytes(px);
        using var cts = new CancellationTokenSource();
        var firstProgress = new TaskCompletionSource();
        var progress = new Bc7.SyncProgress(_ => { firstProgress.TrySetResult(); cts.Cancel(); });

        var sw = Stopwatch.StartNew();
        var encode = new Bc7().EncodeMipAsync(px, w, h, progress, cts.Token);
        // If the encoder reports no progress, cancel 50 ms in instead.
        var winner = await Task.WhenAny(firstProgress.Task, Task.Delay(50));
        if (winner != firstProgress.Task) cts.Cancel();
        var cancelledAt = sw.Elapsed;

        try
        {
            await encode;
            Assert.Fail("Setup error: the 4096x4096 encode finished before the cancellation was observed, so this run proves nothing. Use a larger image.");
        }
        catch (OperationCanceledException)
        {
            Assert.True(sw.Elapsed - cancelledAt < TimeSpan.FromSeconds(2), $"cancel took {(sw.Elapsed - cancelledAt).TotalMilliseconds} ms");
        }
    }
}
