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
