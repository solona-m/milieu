using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class EquirectLayoutTests
{
    [Fact] // [AC-hdri-backdrop-12] [EDGE-hdri-backdrop-43] [EDGE-hdri-backdrop-51]
    public void Place_FullBandStretched()
    {
        Assert.Equal(new EquirectPlacement(PanoramaShape.Full, 0, 4096), EquirectLayout.Place(8192, 4096, 8192, 4096));
        Assert.Equal(PanoramaShape.Full, EquirectLayout.Place(9000, 4500, 8192, 4096).Shape);
        Assert.Equal(PanoramaShape.Full, EquirectLayout.Place(11801, 5900, 8192, 4096).Shape);   // 2.0002: near 2:1
        Assert.Equal(PanoramaShape.Full, EquirectLayout.Place(2020, 1000, 1024, 512).Shape);     // within 1%

        var band = EquirectLayout.Place(21184, 3848, 8192, 4096);
        Assert.Equal(PanoramaShape.Band, band.Shape);
        Assert.Equal(1488, band.BandHeight);
        Assert.Equal((4096 - 1488) / 2, band.BandTop);

        var tall = EquirectLayout.Place(1000, 1000, 1024, 512);
        Assert.Equal(new EquirectPlacement(PanoramaShape.Stretched, 0, 512), tall);
    }
}
