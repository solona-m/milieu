using System.Linq;
using Milieu.GameFiles;
using Xunit;

namespace Milieu.Tests;

/// <summary>The VFX light experiment: every test-light mode builds, passes the self-check, and links the effector.</summary>
public class DomeAvfxTestLightTests
{
    private static DomeAvfxParams Params(int mode, bool synced = false) =>
        new("bgcommon/milieu/t.atex", 1000, 1, 2, 1, false, 1, 0.1f, AttachToOwner: synced, TestLight: mode);

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(3, 1, false)]
    [InlineData(1, 0, true)]
    public void TestLightModes_AddOneLinkedEffector(int mode, int effectorType, bool synced)
    {
        var root = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params(mode, synced)));

        Assert.Equal(1, root.Need("EfCn").AsInt());
        var effector = Assert.Single(root.All("Efct"));
        Assert.Equal(effectorType, effector.Need("EfVT").AsInt());
        Assert.NotNull(effector.Child("Data"));

        var timeline = root.Need("TmLn");
        Assert.Equal(2, timeline.Need("TICn").AsInt());
        var last = timeline.All("Item").Last();
        var efNo = last.Children.Where(c => c.Tag == "EfNo").Select(c => c.AsInt()).ToArray();
        var emNo = last.Children.Where(c => c.Tag == "EmNo").Select(c => c.AsInt()).ToArray();
        Assert.Equal(new[] { -1, 0 }, efNo);   // item 0 the dome emitter, item 1 the light
        Assert.Equal(new[] { 0, -1 }, emNo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void OffOrUnknownMode_IsByteIdenticalToNoLight(int mode)
    {
        var plain = DomeAvfxBuilder.Build(new DomeAvfxParams("bgcommon/milieu/t.atex", 1000, 1, 2, 1, false, 1, 0.1f));
        Assert.Equal(plain, DomeAvfxBuilder.Build(Params(mode)));
    }
}
