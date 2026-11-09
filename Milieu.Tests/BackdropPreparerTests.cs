using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Imaging;
using Milieu.Imaging.Exr;
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

    [Fact] // [AC-exr-support-2]
    public async Task Exr_ProducesValidAtex()
    {
        var (p, _) = Make();
        var exr = SyntheticExr.Write(dir.Path, "a.exr", new ExrSpec { Width = 64, Height = 32, Compression = 3 });
        var t = await p.PrepareTextureAsync(Req(exr), null, default);
        Assert.Equal((1024, 512), (t.Width, t.Height));
        AssertAtex(t.DiskPath, 1024, 512);
        Assert.Equal(ImageKind.Exr, t.Kind);
        Assert.Equal(1, p.DecodeCount);
    }

    [Fact] // [AC-exr-support-3]
    public async Task Exr_ToneMappedLikeHdr()
    {
        var (p, _) = Make();
        var s = new ExrSpec
        {
            Width = 1024, Height = 512, Compression = 4,
            Channels = [new("B", 2), new("G", 2), new("R", 2)],
            Value = (x, y, c) => x * 0.01f + y * 0.003f + c * 0.5f,
        };
        var exr = SyntheticExr.Write(dir.Path, "tm.exr", s);
        var floats = new float[1024 * 512 * 3];
        for (var y = 0; y < 512; y++)
            for (var x = 0; x < 1024; x++)
                for (var k = 0; k < 3; k++)
                    floats[(y * 1024 + x) * 3 + k] = s.Value(x, y, 2 - k);   // R, G, B are stored as channels 2, 1, 0
        var expected = ToneMapper.ToSrgb8(floats, 1024, 512, 1.5f, ToneMapOperator.Reinhard);

        byte[]? mip0 = null;
        p.OnMip0 = m => mip0 = m;
        await p.PrepareTextureAsync(Req(exr) with { ExposureEv = 1.5f, ToneMap = ToneMapOperator.Reinhard }, null, default);
        Assert.NotNull(mip0);
        for (var i = 0; i < 1024 * 512; i++)
        {
            Assert.Equal(expected[i * 3], mip0![i * 4]);
            Assert.Equal(expected[i * 3 + 1], mip0[i * 4 + 1]);
            Assert.Equal(expected[i * 3 + 2], mip0[i * 4 + 2]);
            Assert.Equal(255, mip0[i * 4 + 3]);
        }
    }

    [Fact] // [EDGE-exr-support-29]
    public async Task Exr_CancelledDuringDecode()
    {
        var (p, _) = Make();
        var exr = SyntheticExr.Write(dir.Path, "c.exr", new ExrSpec { Width = 64, Height = 32, Compression = 1 });
        using var cts = new CancellationTokenSource();
        var seen = -1;
        ExrDecoder.OnChunk.Value = i => { seen = i; if (i == 1) cts.Cancel(); };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.PrepareTextureAsync(Req(exr), null, cts.Token));
        }
        finally { ExrDecoder.OnChunk.Value = null; }
        Assert.Equal(1, seen);   // stopped at the chunk where it was cancelled
        Assert.Equal(1, p.DecodeCount);
        Assert.True(!Directory.Exists(cache.Root) || Directory.GetFiles(cache.Root, "sky_*.atex").Length == 0);
    }

    [Fact] // [EDGE-exr-support-31]
    public async Task UnsupportedOrOversizeExr_RefusedBeforeDecode()
    {
        var (p, e) = Make();
        var dwaa = SyntheticExr.Write(dir.Path, "d.exr", new ExrSpec { Compression = 8 });
        var big = SyntheticExr.WriteHeaderOnly(dir.Path, "big.exr", new ExrSpec { Width = 8193, Height = 4096, Compression = 3 });
        Assert.Equal(MessageKey.UnsupportedExrCompression,
                     (await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(Req(dwaa), null, default))).Status.Key);
        Assert.Equal(MessageKey.TooLargeHdr,
                     (await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(Req(big), null, default))).Status.Key);
        Assert.Equal(0, p.DecodeCount);
        Assert.Equal(0, e.Calls);
        Assert.True(!Directory.Exists(cache.Root) || Directory.GetFiles(cache.Root, "sky_*.atex").Length == 0);
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

    /// <summary>A real-sized fake bundled texture (correct header, zero blocks) and a pin that matches it.</summary>
    private (BackdropPreparer P, StubEncoder E, FakeBundled B, BackdropPin Pin) Bundled(string stem = "moon")
    {
        var dirB = dir.File("bundled");
        Directory.CreateDirectory(dirB);
        var path = Path.Combine(dirB, stem + ".atex");
        using (var fs = File.Create(path))
        {
            fs.Write(AtexWriter.ExpectedHeader(8192, 4096));
            fs.SetLength(AtexWriter.Layout(8192, 4096).TotalLength);
        }
        string sha;
        using (var fs = File.OpenRead(path)) sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        var pin = new BackdropPin(stem, "Moon", new FileInfo(path).Length, sha);
        var e = new StubEncoder();
        var b = new FakeBundled(dirB);
        var p = new BackdropPreparer(cache, e, b) { PinLookup = s => s == stem ? pin : null };
        return (p, e, b, pin);
    }

    [Fact] // [AC-hdri-backdrop-13]
    public async Task Bundled_UsesDownloadedAtex_NoDecodeNoEncode()
    {
        var (p, e, b, pin) = Bundled();
        var t = await p.PrepareTextureAsync(new BackdropRequest("bundled:moon", 8192, 0, ToneMapOperator.Aces), null, default);
        Assert.True(t.Preconverted);
        Assert.Equal(b.PathOf("moon"), t.DiskPath);
        Assert.Equal(pin.Sha256[..16], t.Key);
        Assert.Equal($"vfx/milieu/{pin.Sha256[..16]}/sky.atex", t.GamePath);
        Assert.Equal((8192, 4096), (t.Width, t.Height));
        Assert.Equal(0, p.DecodeCount);
        Assert.Equal(0, e.Calls);
        Assert.False(Directory.Exists(cache.Root) && Directory.EnumerateFiles(cache.Root).Any());   // no copy into the cache
    }

    [Fact] // [AC-hdri-backdrop-13] [EDGE-hdri-backdrop-67]
    public async Task Bundled_IgnoresMaxWidthAndHdrSettings()
    {
        var (p, _, b, _) = Bundled();
        var t = await p.PrepareTextureAsync(new BackdropRequest("bundled:moon", 4096, 5, ToneMapOperator.None), null, default);
        Assert.Equal((8192, 4096), (t.Width, t.Height));
        Assert.Equal(b.PathOf("moon"), t.DiskPath);
    }

    [Fact] // [EDGE-hdri-backdrop-61]
    public async Task Bundled_NotDownloaded_RefusedWithMessage()
    {
        var (p, _, b, _) = Bundled();
        File.Delete(b.PathOf("moon"));
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(new BackdropRequest("bundled:moon", 8192, 0, ToneMapOperator.Aces), null, default));
        Assert.Equal(MessageKey.BundledNotDownloaded, ex.Status.Key);
        Assert.Equal("Moon isn't downloaded yet.", ex.Status.Text);
    }

    [Theory] // [EDGE-hdri-backdrop-62]
    [InlineData("length")]
    [InlineData("header")]
    [InlineData("sha")]
    public async Task Bundled_BadHeaderOrSha_MarksCorrupt_NoRegistration(string damage)
    {
        var (p, _, b, _) = Bundled();
        var path = b.PathOf("moon");
        switch (damage)
        {
            case "length": using (var fs = File.OpenWrite(path)) fs.SetLength(1000); break;
            case "header": using (var fs = File.OpenWrite(path)) { fs.Position = 8; fs.WriteByte(0x55); } break;
            case "sha": using (var fs = File.OpenWrite(path)) { fs.Position = 5000; fs.WriteByte(1); } break;
        }
        // Each damage is caught by the preparer's own checks (the service would already treat a wrong length as missing).
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(new BackdropRequest("bundled:moon", 8192, 0, ToneMapOperator.Aces), null, default));
        Assert.Equal(MessageKey.BundledCorrupt, ex.Status.Key);
        Assert.Equal(new[] { "moon" }, b.Corrupt);
        Assert.False(File.Exists(path));
    }

    [Fact] // [EDGE-hdri-backdrop-62]
    public async Task Bundled_ShaVerifiedOncePerSession()
    {
        var (p, _, b, _) = Bundled();
        var req = new BackdropRequest("bundled:moon", 8192, 0, ToneMapOperator.Aces);
        await p.PrepareTextureAsync(req, null, default);
        var path = b.PathOf("moon");
        var mtime = File.GetLastWriteTimeUtc(path);
        // Same length, same mtime, different bytes: not re-hashed this session, so still accepted…
        using (var fs = File.OpenWrite(path)) { fs.Position = 5000; fs.WriteByte(1); }
        File.SetLastWriteTimeUtc(path, mtime);
        await p.PrepareTextureAsync(req, null, default);
        Assert.Empty(b.Corrupt);
        // …but a new file version (mtime) is hashed again and the damage is caught.
        File.SetLastWriteTimeUtc(path, mtime.AddMinutes(1));
        await Assert.ThrowsAsync<MilieuException>(() => p.PrepareTextureAsync(req, null, default));
        Assert.Equal(new[] { "moon" }, b.Corrupt);
    }

    [Fact] // [AC-hdri-backdrop-14]
    public async Task Conversion_WritesThumbFrom256x128Mip()
    {
        var thumbs = new ThumbStore(dir.File("thumbs"));
        var p = new BackdropPreparer(cache, new StubEncoder(), null, thumbs);
        var png = SyntheticImages.WritePng(dir.Path, "mine.png", 512, 256);
        Assert.Null(thumbs.TryLoad(png));
        await p.PrepareTextureAsync(Req(png), null, default);
        var thumb = thumbs.TryLoad(png);
        Assert.NotNull(thumb);
        Assert.Equal(256 * 128 * 4, thumb!.Length);
    }

    // ---------------------------------------------------------------- light rigs (scene lighting)

    private static float[] SunImage(int w, int h) =>
        SyntheticImages.SunDisc(w, h, 60, 25, 4, (40f, 36f, 30f), (_, el) => el > 0 ? (0.2f, 0.3f, 0.6f) : (0.1f, 0.08f, 0.05f));

    [Fact] // [AC-scene-lighting-8]
    public async Task HdrConversion_StoresRig()
    {
        var (p, _) = Make();
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        await p.PrepareTextureAsync(Req(hdr), null, default);
        var info = new FileInfo(hdr);
        var key = CacheStore.LightRigKey(hdr, info.Length, info.LastWriteTimeUtc.Ticks);
        var rig = new Milieu.Lighting.LightRigStore(cache).Load(key);
        Assert.NotNull(rig);
        Assert.NotNull(rig!.Key);
        Assert.Equal(Milieu.Lighting.LightRig.RegionCount, rig.Regions.Length);

        // An EXR conversion stores one too; an LDR conversion doesn't.
        var exr = SyntheticExr.Write(dir.Path, "a.exr", new ExrSpec { Width = 64, Height = 32, Compression = 3 });
        await p.PrepareTextureAsync(Req(exr), null, default);
        var ei = new FileInfo(exr);
        Assert.NotNull(new Milieu.Lighting.LightRigStore(cache).Load(CacheStore.LightRigKey(exr, ei.Length, ei.LastWriteTimeUtc.Ticks)));
        var png = SyntheticImages.WritePng(dir.Path, "b.png", 64, 32);
        await p.PrepareTextureAsync(Req(png), null, default);
        Assert.Equal(2, Directory.GetFiles(cache.LightRigDir, "lights_*.json").Length);
    }

    [Fact] // [AC-scene-lighting-8]
    public async Task PrepareLights_StoreHit_NoDecode()
    {
        var (p, _) = Make();
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        await p.PrepareTextureAsync(Req(hdr), null, default);
        Assert.Equal(1, p.DecodeCount);
        var rig = await p.PrepareLightsAsync(hdr, default);
        Assert.NotNull(rig?.Key);
        Assert.Equal(1, p.DecodeCount);   // from the store: no decode
        // A new session (new preparer) reads the store too.
        var fresh = new BackdropPreparer(cache, new StubEncoder());
        Assert.Equal(rig, await fresh.PrepareLightsAsync(hdr, default), new RigComparer());
        Assert.Equal(0, fresh.DecodeCount);
    }

    [Fact] // [EDGE-scene-lighting-25]
    public async Task TextureCachedNoRig_PrepareLightsDecodesOnceThenHits()
    {
        var (p, _) = Make();
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        await p.PrepareTextureAsync(Req(hdr), null, default);
        Directory.Delete(cache.LightRigDir, recursive: true);   // texture cached, rig gone
        var fresh = new BackdropPreparer(cache, new StubEncoder());
        await fresh.PrepareTextureAsync(Req(hdr), null, default);
        Assert.Equal(0, fresh.DecodeCount);                      // texture cache hit: no decode, so no rig either
        var rig = await fresh.PrepareLightsAsync(hdr, default);
        Assert.NotNull(rig?.Key);
        Assert.Equal(1, fresh.DecodeCount);
        await fresh.PrepareLightsAsync(hdr, default);
        Assert.Equal(1, fresh.DecodeCount);
    }

    [Fact] // [EDGE-scene-lighting-26]
    public async Task PrepareLights_BundledOrLdr_NullWithoutOpening()
    {
        var (p, _) = Make();
        Assert.Null(await p.PrepareLightsAsync("bundled:moon", default));
        // Neither file exists: a null (not a FileMissing error) proves it was never opened.
        Assert.Null(await p.PrepareLightsAsync(dir.File("absent.png"), default));
        Assert.Null(await p.PrepareLightsAsync(dir.File("absent.jpg"), default));
        Assert.Equal(0, p.DecodeCount);
        // An HDR that's missing is an error the caller latches (ReadFailed).
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareLightsAsync(dir.File("absent.hdr"), default));
        Assert.Equal(MessageKey.FileMissing, ex.Status.Key);
        var huge = SyntheticImages.WriteHdrHeaderOnly(dir.Path, "huge.hdr", 16384, 8192);
        Assert.Equal(MessageKey.TooLargeHdr, (await Assert.ThrowsAsync<MilieuException>(() => p.PrepareLightsAsync(huge, default))).Status.Key);
    }

    [Fact] // [EDGE-scene-lighting-30]
    public async Task RigStoreUnwritable_ConversionSucceeds()
    {
        var (p, _) = Make();
        Directory.CreateDirectory(cache.Root);
        File.WriteAllText(cache.LightRigDir, "a file where the rig folder should be");
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        var t = await p.PrepareTextureAsync(Req(hdr), null, default);
        AssertAtex(t.DiskPath, 1024, 512);
        Assert.True(File.Exists(cache.LightRigDir));
        // Lights still come (decoded again, as nothing could be stored).
        Assert.NotNull((await p.PrepareLightsAsync(hdr, default))?.Key);
    }

    private sealed class RigComparer : System.Collections.Generic.IEqualityComparer<Milieu.Lighting.LightRig?>
    {
        public bool Equals(Milieu.Lighting.LightRig? a, Milieu.Lighting.LightRig? b) =>
            a != null && b != null && a.Version == b.Version && Equals(a.Key, b.Key) && a.Regions.SequenceEqual(b.Regions) && a.TotalEnergy == b.TotalEnergy;
        public int GetHashCode(Milieu.Lighting.LightRig? r) => 0;
    }

    [Fact] // [EDGE-scene-lighting-30]
    public async Task RigExtractionThrows_ConversionSucceeds()
    {
        var log = new FakeLog();
        var p = new BackdropPreparer(cache, new StubEncoder(), log: log)
        {
            RigExtractor = (_, _, _, _) => throw new InvalidOperationException("a lighting bug"),
        };
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        var t = await p.PrepareTextureAsync(Req(hdr), null, default);
        AssertAtex(t.DiskPath, 1024, 512);
        Assert.False(Directory.Exists(cache.LightRigDir) && Directory.GetFiles(cache.LightRigDir).Length > 0);
        Assert.Contains(log.Lines, l => l.Level == "Debug" && l.Text.Contains("Light rig"));
        // Cancellation is not swallowed.
        var cancelling = new BackdropPreparer(cache, new StubEncoder())
        {
            RigExtractor = (_, _, _, _) => throw new OperationCanceledException(),
        };
        var other = SyntheticImages.WriteHdrRgb(dir.Path, "other.hdr", 256, 128, SunImage(256, 128));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.PrepareTextureAsync(Req(other), null, default));
    }

    [Fact] // [AC-scene-lighting-10] [EDGE-scene-lighting-28]
    public async Task KillSwitchOff_NoRigExtractedOrStored()
    {
        var extracted = 0;
        var p = new BackdropPreparer(cache, new StubEncoder(), storeRigs: false)
        {
            RigExtractor = (rgb, w, h, ct) => { extracted++; return Milieu.Lighting.LightExtractor.Extract(Milieu.Lighting.RadianceProbe.Build(rgb, w, h, ct)); },
        };
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        var t = await p.PrepareTextureAsync(Req(hdr), null, default);
        AssertAtex(t.DiskPath, 1024, 512);
        Assert.Equal(0, extracted);
        Assert.False(Directory.Exists(cache.LightRigDir));
    }

    [Theory] // [EDGE-scene-lighting-26] [EDGE-scene-lighting-14]
    [InlineData("x.hdr")]
    [InlineData("x.exr")]
    public async Task PrepareLights_HdrNamedButLdrContent_FailsAtProbe_NoDecode(string name)
    {
        // A PNG renamed to an HDR extension is refused by the HDR header probe (DecodeFailed, which scene lighting latches
        // as ReadFailed); it is never decoded, so no rig comes from LDR pixels.
        var (p, _) = Make();
        var png = SyntheticImages.WritePng(dir.Path, "real.png", 64, 32);
        var fake = dir.File(name);
        File.Copy(png, fake);
        var ex = await Assert.ThrowsAsync<MilieuException>(() => p.PrepareLightsAsync(fake, default));
        Assert.Equal(MessageKey.DecodeFailed, ex.Status.Key);
        Assert.Equal(0, p.DecodeCount);
        Assert.False(Directory.Exists(cache.LightRigDir));
    }

    [Fact] // [AC-scene-lighting-8] [EDGE-scene-lighting-25]
    public async Task PrepareLights_WaitingOnConversion_UsesTheRigItStored()
    {
        var gate = new TaskCompletionSource();
        var encoder = new StubEncoder { Before = _ => gate.Task };
        var p = new BackdropPreparer(cache, encoder);
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        var conversion = Task.Run(() => p.PrepareTextureAsync(Req(hdr), null, default));
        Assert.True(Eventually.Holds(() => encoder.Calls > 0), "conversion holds the decode slot");   // rig already stored
        Directory.Delete(cache.LightRigDir, recursive: true);    // force the first Load to miss…
        var lights = Task.Run(() => p.PrepareLightsAsync(hdr, default));
        Assert.True(Eventually.Holds(() => p.RigWaits == 1), "the rig load missed and is waiting for the decode slot");
        Assert.False(lights.IsCompleted);
        // …then put the rig back as if the conversion had just stored it: the second Load (after the wait) finds it.
        await new BackdropPreparer(cache, new StubEncoder()).PrepareLightsAsync(hdr, default);
        gate.SetResult();
        await conversion;
        var rig = await lights;
        Assert.NotNull(rig?.Key);
        Assert.Equal(1, p.DecodeCount);                          // the waiting call didn't decode again
    }

    [Fact] // [EDGE-scene-lighting-14]
    public async Task PrepareLights_ExtractorThrows_PassesThrough()
    {
        var p = new BackdropPreparer(cache, new StubEncoder())
        {
            RigExtractor = (_, _, _, _) => throw new InvalidOperationException("a lighting bug"),
        };
        var hdr = SyntheticImages.WriteHdrRgb(dir.Path, "sun.hdr", 256, 128, SunImage(256, 128));
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.PrepareLightsAsync(hdr, default));
    }
}
