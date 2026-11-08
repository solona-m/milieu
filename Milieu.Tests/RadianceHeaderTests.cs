using System.IO;
using System.Text;
using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class RadianceHeaderTests
{
    private static (int, int)? Read(string text) => RadianceHeader.Read(new MemoryStream(Encoding.ASCII.GetBytes(text)));

    [Fact] // [EDGE-hdri-backdrop-8]
    public void ReadsSize_RejectsMalformed()
    {
        Assert.Equal((8193, 4096), Read("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 4096 +X 8193\n"));
        Assert.Equal((10, 5), Read("#?RGBE\nEXPOSURE=1.0\n\n-Y 5 +X 10\n"));
        Assert.Null(Read("#?NOTHDR\n\n-Y 5 +X 10\n"));                            // bad magic
        Assert.Null(Read("#?RADIANCE\nFORMAT=32-bit_rle_xyze\n\n-Y 5 +X 10\n"));  // other pixel format
        Assert.Null(Read("#?RADIANCE\n\n+Y 5 +X 10\n"));                          // flipped orientation not supported
        Assert.Null(Read("#?RADIANCE\n\n-Y 5 -X 10\n"));
        Assert.Null(Read("#?RADIANCE\n\n-Y five +X 10\n"));                       // non-numeric
        Assert.Null(Read("#?RADIANCE\n\n-Y 0 +X 10\n"));                          // zero size
        Assert.Null(Read("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n"));                // no resolution line
        Assert.Null(Read(""));
    }
}
