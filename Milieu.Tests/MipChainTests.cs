using System.Linq;
using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class MipChainTests
{
    [Fact] // [AC-hdri-backdrop-6]
    public void StopsAtHeight4_BoxFilter()
    {
        var sizes = MipChain.Sizes(8192, 4096);
        Assert.Equal(11, sizes.Count);
        Assert.Equal((8, 4), sizes[^1]);
        for (var i = 1; i < sizes.Count; i++) Assert.Equal((sizes[i - 1].Width / 2, sizes[i - 1].Height / 2), sizes[i]);

        var px = new byte[16 * 8 * 4];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)((i * 37) % 251);
        var mips = MipChain.Build(px, 16, 8);
        Assert.Equal(2, mips.Count);
        Assert.Equal(8 * 4 * 4, mips[1].Length);
        // Level 1 pixel (1,1) channel 0 = rounded average of its 2x2 block.
        int P(int x, int y, int c) => px[(y * 16 + x) * 4 + c];
        var expected = (P(2, 2, 0) + P(3, 2, 0) + P(2, 3, 0) + P(3, 3, 0) + 2) / 4;
        Assert.Equal(expected, mips[1][(1 * 8 + 1) * 4]);
        Assert.Same(px, mips[0]);
        Assert.True(MipChain.Sizes(1024, 512).Count == 8);
        Assert.Equal(MipChain.Sizes(4096, 2048).Select(s => s.Width).ToArray(),
                     Milieu.GameFiles.AtexWriter.Layout(4096, 2048).MipWidths);
    }
}
