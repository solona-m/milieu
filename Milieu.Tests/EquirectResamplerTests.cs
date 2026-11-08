using System;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class EquirectResamplerTests
{
    [Fact] // [EDGE-hdri-backdrop-14]
    public void WrapsHorizontally_ClampsVertically()
    {
        // 4x2 source: column 3 is white, everything else black; top row distinct from bottom.
        var src = SyntheticImages.Rgb(4, 2, (x, y) => x == 3 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)(y == 0 ? 0 : 100), (byte)0));
        var up = EquirectResampler.Resample(src, 4, 2, 8, 4);
        // Output column 0 sits between source column 3 (wrapped) and column 0: it blends white in, not black.
        Assert.True(up[0] > 0, "column 0 blends with the last column");
        // The top output row only uses source row 0 (clamped), so its green is 0 where red is 0.
        Assert.Equal(0, up[(0 * 8 + 2) * 4 + 1]);
        Assert.Equal(100, up[(3 * 8 + 2) * 4 + 1]);
        Assert.Equal(255, up[3]);
    }

    [Fact] // [EDGE-hdri-backdrop-14]
    public void DownsampleAveragesArea_UpsampleBilinear()
    {
        var src = SyntheticImages.Rgb(4, 2, (x, _) => x % 2 == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)200, (byte)200, (byte)200));
        var down = EquirectResampler.Resample(src, 4, 2, 2, 1);
        Assert.Equal(100, down[0]);
        Assert.Equal(100, down[4]);

        var two = SyntheticImages.Rgb(2, 2, (x, _) => x == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)200, (byte)200, (byte)200));
        var up = EquirectResampler.Resample(two, 2, 2, 4, 2);
        // Output centres fall at source -0.25, 0.25, 0.75, 1.25: bilinear with wrap gives 50, 50, 150, 150.
        Assert.Equal(new byte[] { 50, 50, 150, 150 }, new[] { up[0], up[4], up[8], up[12] });
    }

    [Fact] // [AC-hdri-backdrop-12]
    public void Band_UsesBandFill()
    {
        const int sw = 64, sh = 8, ow = 32, oh = 16;  // 8:1 → band of 4 rows
        var src = SyntheticImages.Rgb(sw, sh, (x, y) => ((byte)(x * 4), (byte)(y * 30), 60));
        var output = EquirectResampler.ResampleLayout(src, sw, sh, ow, oh);
        var place = EquirectLayout.Place(sw, sh, ow, oh);
        Assert.Equal(PanoramaShape.Band, place.Shape);

        var expected = new byte[ow * oh * 4];
        EquirectResampler.ResampleInto(src, sw, sh, expected, ow, oh, place.BandTop, place.BandHeight, default);
        BandFill.Fill(expected, ow, oh, 4, place.BandTop, place.BandHeight, Math.Max(1, oh / 8));
        Assert.Equal(expected, output);
        // Band rows are drawn, others filled (no zero rows).
        for (var y = 0; y < oh; y++) Assert.Equal(255, output[(y * ow) * 4 + 3]);
    }

    [Fact]
    public void LargeShrink_KeepsMeanColour()
    {
        var src = SyntheticImages.Rgb(2000, 1000, (x, y) => ((byte)((x + y) % 256), 77, 200));
        var dst = EquirectResampler.Resample(src, 2000, 1000, 64, 32);
        double sum = 0;
        for (var i = 1; i < dst.Length; i += 4) sum += dst[i];
        Assert.InRange(sum / (64 * 32), 76.5, 77.5);
    }
}
