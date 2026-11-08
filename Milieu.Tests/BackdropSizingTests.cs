using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class BackdropSizingTests
{
    [Fact] // [EDGE-hdri-backdrop-51] [EDGE-hdri-backdrop-52]
    public void TargetSize_Rules()
    {
        Assert.Equal((8192, 4096, false), BackdropSizing.TargetSize(9000, 4500));    // forest
        Assert.Equal((8192, 4096, false), BackdropSizing.TargetSize(10000, 5000));   // galaxy
        Assert.Equal((8192, 4096, false), BackdropSizing.TargetSize(11801, 5900));   // warehouse (near 2:1)
        Assert.Equal((8192, 4096, true), BackdropSizing.TargetSize(8192, 4096));     // lodge: copied
        Assert.Equal((2048, 4096, false), BackdropSizing.TargetSize(4000, 8000));
        Assert.Equal((8192, 1488, false), BackdropSizing.TargetSize(21184, 3848));
    }

    [Fact] // [EDGE-hdri-backdrop-10]
    public void OutputSize_Rule()
    {
        Assert.Equal((2048, 1024), BackdropSizing.OutputSize(1500, 8192));
        Assert.Equal((1024, 512), BackdropSizing.OutputSize(600, 8192));
        Assert.Equal((4096, 2048), BackdropSizing.OutputSize(3000, 8192));
        Assert.Equal((8192, 4096), BackdropSizing.OutputSize(8192, 8192));
        Assert.Equal((4096, 2048), BackdropSizing.OutputSize(8192, 4096));
        Assert.Equal((8192, 4096), BackdropSizing.OutputSize(21184, 8192));
        Assert.Equal((8192, 4096), BackdropSizing.OutputSize(5000, 1234));          // unknown max → 8192

        // The game needs power-of-two sides, at most 8192.
        foreach (var max in new[] { 4096, 8192, 1234 })
            for (var w = 1; w <= 40000; w = w * 3 / 2 + 1)
            {
                var (ow, oh) = BackdropSizing.OutputSize(w, max);
                Assert.True(System.Numerics.BitOperations.IsPow2(ow) && System.Numerics.BitOperations.IsPow2(oh), $"{ow}x{oh}");
                Assert.InRange(ow, 1024, 8192);
                Assert.Equal(ow / 2, oh);
            }
    }

    [Fact] // [EDGE-hdri-backdrop-60]
    public void StripPlan_Sunset()
    {
        var p = BackdropSizing.PlanStrip(21184, 3848, 0.9);
        Assert.Equal((4096, 744), (p.ScaledWidth, p.ScaledHeight));
        Assert.Equal(670, p.HorizonRow);
        Assert.Equal(1378, p.Top);
        Assert.Equal((8192, 4096), (p.OutWidth, p.OutHeight));
    }

    [Fact] // [EDGE-hdri-backdrop-60]
    public void MirrorTile_IsSeamless()
    {
        const int w = 5, h = 2;
        var px = new byte[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(i * 10 + 1);
        var tiled = BackdropSizing.MirrorTile(px, w, h, 1);
        Assert.Equal(10 * h, tiled.Length);
        for (var y = 0; y < h; y++)
        {
            Assert.Equal(tiled[y * 10 + 0], tiled[y * 10 + 9]);
            Assert.Equal(tiled[y * 10 + 4], tiled[y * 10 + 5]);
            for (var x = 0; x < w; x++)
            {
                Assert.Equal(px[y * w + x], tiled[y * 10 + x]);
                Assert.Equal(px[y * w + x], tiled[y * 10 + 9 - x]);
            }
        }
    }
}
