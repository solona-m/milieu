using Milieu.GameFiles;
using Xunit;

namespace Milieu.Tests;

/// <summary>SPIKE (spike/vfx-light): every test-light mode builds and passes the builder's self-check.</summary>
public class SpikeVfxLightTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TestLightModesBuild(int mode)
    {
        var bytes = DomeAvfxBuilder.Build(new DomeAvfxParams("bgcommon/milieu/t.atex", 1000, 1, 2, 1, false, 1, 0.1f, TestLight: mode));
        var root = AvfxChunkReader.Parse(bytes);
        Assert.Equal(1, root.Need("EfCn").AsInt());
        Assert.Equal(2, root.Need("TmLn").Need("TICn").AsInt());
    }
}
