using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using StbImageSharp;
using Xunit;
using Xunit.Abstractions;

namespace Milieu.Tests;

/// <summary>
/// The in-repo encoder against the reference (BCnEncoder Fast): quality gates (PSNR within 0.25 dB, p99.9 block MSE within
/// 1.5x + 1) and speed gates (at least 10x; an 8K Show in 10 s). The JPEG test reads the committed product images.
/// </summary>
public class Bc7QualityTests(ITestOutputHelper output)
{
    private const double PsnrSlackDb = 0.25;

    private async Task<(double Psnr, double P999)> Measure(IBlockEncoder encoder, byte[] rgba, int w, int h)
    {
        var blocks = await encoder.EncodeMipAsync(rgba, w, h, null, default);
        var decoded = Bc7Metrics.Decode(blocks, w, h);
        return (Bc7Metrics.Psnr(rgba, decoded, w, h), Bc7Metrics.Percentile(Bc7Metrics.BlockMses(rgba, decoded, w, h), 0.999));
    }

    private async Task AssertWithinGate(string name, byte[] rgba, int w, int h)
    {
        var (refPsnr, refP) = await Measure(new Bc7(), rgba, w, h);
        var (newPsnr, newP) = await Measure(new OpaqueBc7(), rgba, w, h);
        output.WriteLine($"{name}: PSNR ref {refPsnr:0.000} new {newPsnr:0.000} (Δ {newPsnr - refPsnr:+0.000;-0.000}); " +
                         $"p99.9 block MSE ref {refP:0.00} new {newP:0.00}");
        Assert.True(newPsnr >= refPsnr - PsnrSlackDb, $"{name}: PSNR {newPsnr:0.000} < reference {refPsnr:0.000} - {PsnrSlackDb}");
        Assert.True(newP <= 1.5 * refP + 1, $"{name}: p99.9 block MSE {newP:0.00} > 1.5 x {refP:0.00} + 1");
    }

    [Theory] // [AC-fast-bc7-2] [EDGE-fast-bc7-13]
    [InlineData("sky")]
    [InlineData("night")]
    [InlineData("foliage")]
    [InlineData("shapes")]
    public Task Quality_SyntheticPanoramas_WithinGate(string kind) =>
        AssertWithinGate(kind, Bc7Metrics.Synthetic(kind, 512, 256), 512, 256);

    public static IEnumerable<object[]> BundledJpegs()
    {
        foreach (var f in Directory.GetFiles(RepoPaths.DefaultBackdrops(), "*.jpeg")) yield return [Path.GetFileName(f)];
    }

    [Theory, Trait("Speed", "Slow")] // [AC-fast-bc7-2]
    [MemberData(nameof(BundledJpegs))]
    public async Task Quality_BundledJpegTiles_WithinGate(string file)
    {
        ImageResult img;
        using (var s = File.OpenRead(Path.Combine(RepoPaths.DefaultBackdrops(), file))) img = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
        // Three 256x256 tiles (sky, horizon, ground), side by side in one 768x256 image so the gate pools them.
        const int t = 256;
        var rgba = new byte[3 * t * t * 4];
        var x0 = img.Width / 3;
        double[] rows = [0.12, 0.47, 0.8];
        for (var k = 0; k < 3; k++)
        {
            var y0 = Math.Min(img.Height - t, (int)(img.Height * rows[k]));
            for (var y = 0; y < t; y++)
                for (var x = 0; x < t; x++)
                {
                    var src = ((y0 + y) * img.Width + x0 + x) * 3;
                    var dst = (y * 3 * t + k * t + x) * 4;
                    rgba[dst] = img.Data[src]; rgba[dst + 1] = img.Data[src + 1]; rgba[dst + 2] = img.Data[src + 2]; rgba[dst + 3] = 255;
                }
        }
        await AssertWithinGate(file, rgba, 3 * t, t);
    }

    [Theory, Trait("Speed", "Slow")] // [AC-fast-bc7-1] [EDGE-fast-bc7-10]
    [InlineData("foliage")]
    [InlineData("noise")]
    public async Task Speed_TenTimesReference(string kind)
    {
        const int w = 1024, h = 512;
        byte[] rgba;
        if (kind == "noise")
        {
            rgba = new byte[w * h * 4];
            new Random(9).NextBytes(rgba);
        }
        else rgba = Bc7Metrics.Synthetic(kind, w, h);
        var reference = await Bc7Metrics.BestOf(3, () => new Bc7().EncodeMipAsync(rgba, w, h, null, default));
        var fast = await Bc7Metrics.BestOf(3, () => new OpaqueBc7().EncodeMipAsync(rgba, w, h, null, default));
        var ratio = reference.TotalMilliseconds / fast.TotalMilliseconds;
        output.WriteLine($"{kind}: reference {reference.TotalMilliseconds:0} ms, in-repo {fast.TotalMilliseconds:0} ms, {ratio:0.0}x");
        Assert.True(ratio >= 10, $"{kind}: only {ratio:0.0}x faster");
    }

    [Fact, Trait("Speed", "Slow")] // [AC-fast-bc7-1]
    public async Task Speed_Full8kForestShowPath_WithinBudget()
    {
        using var dir = new TempDir();
        var forest = Path.Combine(RepoPaths.DefaultBackdrops(), "forest.jpeg");
        var run = 0;
        long length = 0;
        // Warm-up on a small image, then the best of two full Show-path conversions, each into a fresh cache (a miss).
        var small = SyntheticImages.WriteJpg(dir.Path, "small.jpg", 1024, 512, SyntheticImages.Gradient(1024, 512), 90);
        await Convert(small, 1024);
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t = await Convert(forest, 8192);
            sw.Stop();
            length = new FileInfo(t.DiskPath).Length;
            if (sw.Elapsed < best) best = sw.Elapsed;
        }
        var budget = TimeSpan.FromSeconds(10 * Math.Max(1.0, 14.0 / ToneMapper.Workers));
        output.WriteLine($"8K forest Show path (decode, light estimate, thumbnail, encode): best {best.TotalSeconds:0.00} s, budget {budget.TotalSeconds:0} s");
        Assert.Equal(44_739_312, length);
        Assert.True(best <= budget, $"8K conversion took {best.TotalSeconds:0.00} s");

        async Task<PreparedTexture> Convert(string path, int maxWidth)
        {
            var root = Path.Combine(dir.Path, "run" + run++);
            var preparer = new BackdropPreparer(new CacheStore(Path.Combine(root, "cache")), new OpaqueBc7(),
                                                thumbs: new ThumbStore(Path.Combine(root, "thumbs")), storeRigs: true);
            return await preparer.PrepareTextureAsync(new BackdropRequest(path, maxWidth, 0, ToneMapOperator.Aces), null, default);
        }
    }
}
