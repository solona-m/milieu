using System;
using System.Linq;
using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class BandFillTests
{
    [Fact] // [AC-hdri-backdrop-12] [EDGE-hdri-backdrop-43]
    public void Band_FadesToLowVarianceAwayFromBand()
    {
        const int w = 256, h = 32, top = 8, bandH = 16, fade = 6;
        var px = new byte[w * h * 3];
        for (var y = top; y < top + bandH; y++)
        for (var x = 0; x < w; x++)
        {
            var v = (byte)(x % 2 == 0 ? 0 : 255);
            var o = (y * w + x) * 3;
            px[o] = v; px[o + 1] = v; px[o + 2] = (byte)(255 - v);
        }
        BandFill.Fill(px, w, h, 3, top, bandH, fade);

        var blurred = BandFill.BlurRow(px, w, 3, 3, top, w / 64);
        double[] Row(int y, int c) => Enumerable.Range(0, w).Select(x => (double)px[(y * w + x) * 3 + c]).ToArray();
        var edgeMean = Row(top, 0).Average();

        // The row next to the band ≈ the blurred edge row.
        for (var x = 0; x < w; x++) Assert.InRange(Math.Abs(px[((top - 1) * w + x) * 3] - blurred[x * 3]), 0, 2);

        // Rows at least F away are flat, at the edge row's mean.
        for (var y = 0; y <= top - 1 - fade; y++)
        {
            var row = Row(y, 0);
            var mean = row.Average();
            var std = Math.Sqrt(row.Select(v => (v - mean) * (v - mean)).Average());
            Assert.True(std < 2, $"row {y} std {std}");
            Assert.InRange(Math.Abs(mean - edgeMean), 0, 2);
        }

        // Distance to the mean shrinks monotonically going away from the band (below it).
        var bottomEdge = top + bandH - 1;
        var prevDev = double.MaxValue;
        for (var y = bottomEdge + 1; y < h; y++)
        {
            var dev = Row(y, 0).Select(v => Math.Abs(v - edgeMean)).Average();
            Assert.True(dev <= prevDev + 1e-9);
            prevDev = dev;
        }

        // Nothing went black.
        for (var y = 0; y < h; y++) Assert.True(Row(y, 0).Average() > 50 || Row(y, 2).Average() > 50);
    }

    [Fact]
    public void Fill_SetsAlphaOpaque_AndRejectsBadBand()
    {
        var px = new byte[4 * 4 * 4];
        BandFill.Fill(px, 4, 4, 4, 1, 2, 1);
        Assert.Equal(255, px[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => BandFill.Fill(px, 4, 4, 4, 3, 2, 1));
    }
}
