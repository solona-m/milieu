using System;
using System.Linq;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

public class RadianceProbeTests
{
    private const int W = RadianceProbe.Width;
    private const int H = RadianceProbe.Height;

    private static float[] Fill(int w, int h, Func<int, int, float> v)
    {
        var d = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 3;
            d[o] = d[o + 1] = d[o + 2] = v(x, y);
        }
        return d;
    }

    private static float At(RadianceProbe p, int x, int y, int c = 0) => p.Rgb[(y * W + x) * 3 + c];

    [Fact] // [EDGE-scene-lighting-5]
    public void NanInfNegative_Sanitised()
    {
        var src = Fill(W, H, (_, _) => 1f);
        src[0] = float.NaN;                       // (0,0) R
        src[3] = -5f;                             // (1,0) R
        src[6] = float.PositiveInfinity;          // (2,0) R
        src[9] = 1e9f;                            // (3,0) R
        src[12] = float.NegativeInfinity;         // (4,0) R
        var p = RadianceProbe.Build(src, W, H);
        Assert.All(p.Rgb, v => Assert.True(float.IsFinite(v) && v >= 0 && v <= RadianceProbe.MaxValue));
        Assert.Equal(0f, At(p, 0, 0));
        Assert.Equal(0f, At(p, 1, 0));
        Assert.Equal(65504f, At(p, 2, 0));
        Assert.Equal(65504f, At(p, 3, 0));
        Assert.Equal(0f, At(p, 4, 0));
        Assert.Equal(1f, At(p, 5, 0));

        // A downsampled texel mixing a NaN, an Inf and normal pixels stays finite.
        var big = Fill(W * 2, H * 2, (_, _) => 2f);
        big[0] = float.NaN; big[3] = float.PositiveInfinity;
        var q = RadianceProbe.Build(big, W * 2, H * 2);
        Assert.All(q.Rgb, v => Assert.True(float.IsFinite(v)));
        Assert.Equal((0f + 65504f + 2f + 2f) / 4f, At(q, 0, 0), 3);
    }

    [Fact] // [EDGE-scene-lighting-6]
    public void BandPanorama_OnlyBandRowsLit_CoveredRowsRecorded()
    {
        // 8:1 → a horizon band of 256·128/1024 = 32 rows, centred.
        var p = RadianceProbe.Build(Fill(1024, 128, (_, _) => 1f), 1024, 128);
        Assert.Equal(48, p.CoveredTop);
        Assert.Equal(32, p.CoveredRows);
        for (var y = 0; y < H; y++)
        {
            var inside = y >= 48 && y < 80;
            for (var x = 0; x < W; x += 17) Assert.Equal(inside ? 1f : 0f, At(p, x, y), 4);
        }
    }

    [Fact] // [EDGE-scene-lighting-6]
    public void Stretched_FillsFrame()
    {
        // 1:1 is taller than 2:1: stretched over the whole frame, every row covered.
        var p = RadianceProbe.Build(Fill(200, 200, (_, y) => y < 100 ? 3f : 1f), 200, 200);
        Assert.Equal(0, p.CoveredTop);
        Assert.Equal(H, p.CoveredRows);
        Assert.Equal(3f, At(p, 10, 0), 4);
        Assert.Equal(1f, At(p, 10, H - 1), 4);
        Assert.All(p.Rgb, v => Assert.True(v > 0));
    }

    [Fact] // [EDGE-scene-lighting-7]
    public void SmallSource_Upsamples()
    {
        // 64x32 → each source pixel covers a 4x4 block of probe texels.
        var src = Fill(64, 32, (x, y) => x + 100f * y);
        var p = RadianceProbe.Build(src, 64, 32);
        Assert.Equal(H, p.CoveredRows);
        for (var y = 0; y < H; y += 5)
        for (var x = 0; x < W; x += 7)
            Assert.Equal(x / 4 + 100f * (y / 4), At(p, x, y), 3);
    }

    private static double Linear(int v)
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    [Fact] // [AC-ldr-light-estimate-1]
    public void BuildSrgb8_LinearisesAndBuildsClipMask()
    {
        // 1:1 onto the probe: each texel is its pixel, linearised; the mask is 8x finer, so each pixel is 8x8 cells.
        var px = new byte[W * H * 3];
        for (var i = 0; i < W * H; i++) { px[i * 3] = 128; px[i * 3 + 1] = 64; px[i * 3 + 2] = 200; }
        void Set(int x, int y, byte r, byte g, byte b) { var o = (y * W + x) * 3; px[o] = r; px[o + 1] = g; px[o + 2] = b; }
        Set(10, 20, 255, 255, 255);   // clipped
        Set(11, 20, 245, 250, 245);   // clipped: min(R,G,B) = 245
        Set(12, 20, 255, 160, 40);    // an orange signage core: not clipped (min 40 < 160)
        Set(13, 20, 244, 244, 244);   // just under
        var p = RadianceProbe.BuildSrgb8(px, W, H);
        Assert.Equal(Linear(128), At(p, 0, 0, 0), 6);
        Assert.Equal(Linear(64), At(p, 0, 0, 1), 6);
        Assert.Equal(Linear(200), At(p, 0, 0, 2), 6);
        Assert.Equal(1.0, At(p, 10, 20, 1), 6);
        Assert.Equal((0, H), (p.CoveredTop, p.CoveredRows));

        var m = Assert.IsType<ClipMask>(p.Clip);
        Assert.Equal(ClipMask.Width * ClipMask.Height, m.Fraction.Length);
        Assert.Equal((0, ClipMask.Height), (m.CoveredTop, m.CoveredRows));
        float Cell(int x, int y) => m.Fraction[y * ClipMask.Width + x];
        for (var dy = 0; dy < 8; dy++)
        for (var dx = 0; dx < 8; dx++)
        {
            Assert.Equal(1f, Cell(80 + dx, 160 + dy));
            Assert.Equal(1f, Cell(88 + dx, 160 + dy));
            Assert.Equal(0f, Cell(96 + dx, 160 + dy));
            Assert.Equal(0f, Cell(104 + dx, 160 + dy));
        }
        Assert.Equal(2 * 64, m.Fraction.Count(f => f > 0));

        // The probe is exactly Build on the linearised pixels (the shared accumulation), at a non-integer scale too.
        const int w = 700, h = 350;
        var src = new byte[w * h * 3];
        var lin = new float[w * h * 3];
        for (var i = 0; i < src.Length; i++)
        {
            src[i] = (byte)((i * 7919 + i / 3 * 104729) % 256);
            lin[i] = RadianceProbe.SrgbToLinear[src[i]];
        }
        Assert.Equal(RadianceProbe.Build(lin, w, h).Rgb, RadianceProbe.BuildSrgb8(src, w, h).Rgb);

        // A band panorama: the mask fills only its band rows, like the probe.
        var band = RadianceProbe.BuildSrgb8(Enumerable.Repeat((byte)255, 4096 * 1024 * 3).ToArray(), 4096, 1024);
        Assert.Equal((32, 64), (band.CoveredTop, band.CoveredRows));
        Assert.Equal((256, 512), (band.Clip!.CoveredTop, band.Clip.CoveredRows));
        Assert.Equal(512 * ClipMask.Width, band.Clip.Fraction.Count(f => f == 1f));

        // A small source is box-upsampled: one clipped pixel of 100x50 covers 20.48 x 20.48 cells (partial at the edges).
        var small = new byte[100 * 50 * 3];
        small[(25 * 100 + 50) * 3] = small[(25 * 100 + 50) * 3 + 1] = small[(25 * 100 + 50) * 3 + 2] = 255;
        var sm = RadianceProbe.BuildSrgb8(small, 100, 50).Clip!;
        Assert.Equal(20.48 * 20.48, sm.Fraction.Sum(f => (double)f), 1e-2);
        Assert.Null(RadianceProbe.Build(new float[W * H * 3], W, H).Clip);
    }

    [Fact] // [EDGE-scene-lighting-6]
    public void Downsample_AreaAverages_PerChannel()
    {
        var src = new float[W * 2 * H * 2 * 3];
        for (var i = 0; i < src.Length / 3; i++) { src[i * 3] = i % 2; src[i * 3 + 1] = 4f; src[i * 3 + 2] = 0f; }
        var p = RadianceProbe.Build(src, W * 2, H * 2);
        Assert.Equal(0.5f, At(p, 3, 3, 0), 4);
        Assert.Equal(4f, At(p, 3, 3, 1), 4);
        Assert.Equal(0f, p.Rgb.Where((_, i) => i % 3 == 2).Max());
    }
}
