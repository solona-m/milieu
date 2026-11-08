using System;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class SeamDetectorTests
{
    [Fact] // [AC-hdri-backdrop-12] [EDGE-hdri-backdrop-54]
    public void WrapContinuous_NoWarning_Step_Warns()
    {
        const int w = 512, h = 64;
        var sine = SyntheticImages.Rgb(w, h, (x, _) =>
        {
            var v = (byte)(128 + 100 * Math.Sin(2 * Math.PI * x / w));
            return (v, v, v);
        });
        Assert.False(SeamDetector.Measure(sine, w, h).NoWrap);

        var step = SyntheticImages.Rgb(w, h, (x, _) => x < w / 2 ? ((byte)40, (byte)40, (byte)40) : ((byte)200, (byte)200, (byte)200));
        var m = SeamDetector.Measure(step, w, h);
        Assert.Equal(160, m.Seam, 3);
        Assert.True(m.NoWrap);

        // Under the absolute threshold: seam 15, adjacent ≈ 0.
        var small = SyntheticImages.Rgb(w, h, (x, _) => x < w / 2 ? ((byte)100, (byte)100, (byte)100) : ((byte)115, (byte)115, (byte)115));
        Assert.False(SeamDetector.Measure(small, w, h).NoWrap);

        // Under the ratio threshold: seam 30 against adjacent 10.
        Assert.False(new SeamMeasure(30, 10).NoWrap);
        Assert.True(new SeamMeasure(41, 10).NoWrap);
    }
}
