using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class BackdropPreparerTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly CacheStore cache;

    public BackdropPreparerTests() => cache = new CacheStore(dir.File("cache"));

    public void Dispose() => dir.Dispose();

    /// <summary>Returns correctly sized zero blocks: the pipeline test does not need real BC7.</summary>
    private sealed class StubEncoder : IBlockEncoder
    {
        public int Calls;
        public Func<CancellationToken, Task>? Before;

        public async Task<byte[]> EncodeMipAsync(byte[] rgba, int width, int height, IProgress<float>? progress, CancellationToken ct)
        {
            Calls++;
            if (Before != null) await Before(ct);
            ct.ThrowIfCancellationRequested();
            return new byte[AtexWriter.BlockBytes(width, height)];
        }
    }

    private (BackdropPreparer P, StubEncoder E) Make() { var e = new StubEncoder(); return (new BackdropPreparer(cache, e), e); }

    private static BackdropRequest Req(string path, int max = 8192) => new(path, max, 0, ToneMapOperator.Aces);

    private static void AssertAtex(string path, int w, int h)
    {
        var b = File.ReadAllBytes(path);
        Assert.Equal(AtexWriter.Layout(w, h).TotalLength, b.Length);
        Assert.Equal(0x6432u, BitConverter.ToUInt32(b, 4));
        Assert.Equal(w, BitConverter.ToUInt16(b, 8));
        Assert.Equal(h, BitConverter.ToUInt16(b, 10));
        Assert.Equal(AtexWriter.Layout(w, h).MipCount, b[14]);
    }

    [Fact] // [AC-hdri-backdrop-1]
    public async Task Hdr_Png_Jpg_AllProduceValidAtex()
    {
        var (p, _) = Make();
        foreach (var path in new[]
                 {
                     SyntheticImages.WriteHdr(dir.Path, "a.hdr", 64, 32),
                     SyntheticImages.WritePng(dir.Path, "b.png", 64, 32),
                     SyntheticImages.WriteJpg(dir.Path, "c.jpg", 64, 32),
                 })
        {
            var t = await p.PrepareTextureAsync(Req(path), null, default);
            Assert.Equal((1024, 512), (t.Width, t.Height));
            AssertAtex(t.DiskPath, 1024, 512);
            Assert.Equal(8, AtexWriter.Layout(1024, 512).MipCount);
            Assert.Matches("sky_[0-9a-f]{16}\\.atex$", t.DiskPath);
        }
    }

    [Fact] // [AC-hdri-backdrop-5]
    public async Task Ldr_PassesThroughUnchanged()
    {
        var (p, _) = Make();
        var rgb = SyntheticImages.Rgb(1024, 512, (x, y) => ((byte)(x % 251), (byte)(y % 241), (byte)((x + y) % 239)));
        var png = SyntheticImages.WritePng(dir.Path, "same.png", 1024, 512, rgb);
        byte[]? mip0 = null;
        p.OnMip0 = m => mip0 = m;
        await p.PrepareTextureAsync(Req(png) with { ExposureEv = 5, ToneMap = ToneMapOperator.None }, null, default);
        Assert.NotNull(mip0);
        for (var i = 0; i < 1024 * 512; i++)
        {
            Assert.Equal(rgb[i * 3], mip0![i * 4]);
            Assert.Equal(rgb[i * 3 + 1], mip0[i * 4 + 1]);
            Assert.Equal(rgb[i * 3 + 2], mip0[i * 4 + 2]);
            Assert.Equal(255, mip0[i * 4 + 3]);
        }
    }

    [Fact] // [AC-hdri-backdrop-6] [EDGE-hdri-backdrop-25]
    public async Task CacheHit_SkipsDecodeAndEncode()
    {
        var (p, e) = Make();
        var jpg = SyntheticImages.WriteJpg(dir.Path, "c.jpg", 64, 32);
        var first = await p.PrepareTextureAsync(Req(jpg), null, default);
        var (decodes, encodes) = (p.DecodeCount, e.Calls);
        var second = await p.PrepareTextureAsync(Req(jpg), null, default);
        Assert.Equal(first.DiskPath, second.DiskPath);
        Assert.Equal(decodes, p.DecodeCount);
        Assert.Equal(encodes, e.Calls);
    }

    [Fact] // [AC-hdri-backdrop-6]
    public async Task MaxWidth4096_Option()
    {
        // The size rule through the pipeline with a stub encoder (fast), plus the pure rule.
        var (p, _) = Make();
        var png = SyntheticImages.WritePngHeaderOnly(dir.Path, "big.png", 4096, 2048);
        var probe = ImageLoader.Probe(png);
        Assert.Equal((4096, 2048), BackdropSizing.OutputSize(probe.Width, 4096));
        var small = SyntheticImages.WritePng(dir.Path, "s.png", 1500, 750);
        var t = await p.PrepareTextureAsync(Req(small, 4096), null, default);
        Assert.Equal((2048, 1024), (t.Width, t.Height));
    }

    [Fact, Trait("Speed", "Slow")] // [AC-hdri-backdrop-6]
    public async Task Full8k_EndToEnd()
    {
        var p = new BackdropPreparer(cache, new Bc7());
        var jpg = SyntheticImages.WriteJpg(dir.Path, "big.jpg", 8192, 4096, SyntheticImages.Gradient(8192, 4096), 90);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = await p.PrepareTextureAsync(Req(jpg), null, default);
        Assert.Equal(44739312, new FileInfo(t.DiskPath).Length);
        Console.WriteLine($"8K conversion: {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact] // [AC-hdri-backdrop-6]
    public async Task LargeLdr_DownscaledToOutput()
    {
        var (p, _) = Make();
        var png = SyntheticImages.WritePng(dir.Path, "wide.png", 2400, 1200);
        var t = await p.PrepareTextureAsync(Req(png, 4096) with { MaxWidth = 4096 }, null, default);
        Assert.Equal((4096, 2048), (t.Width, t.Height));
        var tiny = await p.PrepareTextureAsync(Req(SyntheticImages.WritePng(dir.Path, "w2.png", 2400, 1200), 8192), null, default);
        Assert.Equal((4096, 2048), (tiny.Width, tiny.Height));
    }

    [Fact] // [AC-hdri-backdrop-6] [EDGE-hdri-backdrop-55]
    public async Task OverBudget_RejectedBeforeDecode()
    {
        var (p, e) = Make();
        var path = dir.File("prog.jpg");
        File.WriteAllBytes(path, SyntheticImages.JpegHeader(0xC2, 16384, 8192, padToBytes: 20_000_000));
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(Req(path), null, default));
        Assert.Equal(MessageKey.TooLargeLdr, ex.Status.Key);
        Assert.Contains("about 1.6 GB", ex.Status.Text);
        Assert.Equal(0, p.DecodeCount);
        Assert.Equal(0, e.Calls);
    }

    [Fact] // [AC-hdri-backdrop-12] [EDGE-hdri-backdrop-9]
    public async Task WiderThan2to1_NoStretchWarning_TallerThan2to1_Warns()
    {
        var (p, _) = Make();
        var wide = SyntheticImages.WritePng(dir.Path, "wide.png", 256, 32, SyntheticImages.Rgb(256, 32, (x, _) =>
        {
            var v = (byte)(128 + 100 * Math.Sin(2 * Math.PI * x / 256)); return (v, v, v);
        }));
        var w = await p.PrepareTextureAsync(Req(wide), null, default);
        Assert.DoesNotContain(w.Warnings, m => m.Key == MessageKey.NotTwoToOne);
        Assert.DoesNotContain(w.Warnings, m => m.Key == MessageKey.NoWrap);

        var tall = SyntheticImages.WritePng(dir.Path, "tall.png", 64, 64);
        var t = await p.PrepareTextureAsync(Req(tall), null, default);
        Assert.Equal("tall.png is taller than 2:1; it will be stretched.", t.Warnings.Single(m => m.Key == MessageKey.NotTwoToOne).Text);
    }

    [Fact] // [AC-hdri-backdrop-12] [EDGE-hdri-backdrop-54]
    public async Task NonWrappingImage_ReturnsNoWrapWarning()
    {
        var (p, _) = Make();
        var step = SyntheticImages.WritePng(dir.Path, "step.png", 128, 64,
            SyntheticImages.Rgb(128, 64, (x, _) => x < 64 ? ((byte)30, (byte)30, (byte)30) : ((byte)220, (byte)220, (byte)220)));
        var t = await p.PrepareTextureAsync(Req(step), null, default);
        Assert.Contains(t.Warnings, m => m.Key == MessageKey.NoWrap);
    }

    [Fact] // [EDGE-hdri-backdrop-7]
    public async Task DecodeFailure_LeavesNoCacheFile()
    {
        var (p, _) = Make();
        var empty = SyntheticImages.WritePngHeaderOnly(dir.Path, "empty.png", 64, 32);
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(Req(empty), null, default));
        Assert.Equal(MessageKey.DecodeFailed, ex.Status.Key);
        Assert.False(Directory.Exists(cache.Root) && Directory.EnumerateFiles(cache.Root).Any());
    }

    [Fact] // [EDGE-hdri-backdrop-11]
    public async Task MissingFile_FileMissing()
    {
        var (p, _) = Make();
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(Req(dir.File("gone.jpg")), null, default));
        Assert.Equal(MessageKey.FileMissing, ex.Status.Key);
    }

    [Fact] // [EDGE-hdri-backdrop-16]
    public async Task CancelledMidEncode_DeletesTmp_NoOutput()
    {
        var (p, e) = Make();
        using var cts = new CancellationTokenSource();
        e.Before = _ => { cts.Cancel(); return Task.CompletedTask; };
        var jpg = SyntheticImages.WriteJpg(dir.Path, "c.jpg", 64, 32);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.PrepareTextureAsync(Req(jpg), null, cts.Token));
        Assert.Equal(1, e.Calls);
        Assert.False(Directory.Exists(cache.Root) && Directory.EnumerateFiles(cache.Root).Any());
    }

    [Fact]
    public async Task Progress_ReachesOne()
    {
        var (p, _) = Make();
        var values = new System.Collections.Generic.List<float>();
        await p.PrepareTextureAsync(Req(SyntheticImages.WriteJpg(dir.Path, "c.jpg", 64, 32)), new Bc7.SyncProgress(values.Add), default);
        Assert.Equal(1f, values[^1], 3);
        for (var i = 1; i < values.Count; i++) Assert.True(values[i] >= values[i - 1] - 1e-4);
    }


    [Fact] // [AC-hdri-backdrop-6] [EDGE-hdri-backdrop-15]
    public async Task Conversions_NeverOverlap()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var e = new StubEncoder { Before = async _ => { started.TrySetResult(); await release.Task; } };
        var p = new BackdropPreparer(cache, e);
        var a = SyntheticImages.WriteJpg(dir.Path, "a.jpg", 64, 32);
        var b = SyntheticImages.WriteJpg(dir.Path, "b.jpg", 64, 32);
        var first = Task.Run(() => p.PrepareTextureAsync(Req(a), null, default));
        await started.Task;
        var second = Task.Run(() => p.PrepareTextureAsync(Req(b), null, default));
        await Task.Delay(100);
        Assert.Equal(1, p.DecodeCount);            // the second has not started decoding
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, p.DecodeCount);
    }
}
