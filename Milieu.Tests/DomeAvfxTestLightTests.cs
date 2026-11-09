using System.IO;
using System.Linq;
using Milieu.GameFiles;
using Xunit;

namespace Milieu.Tests;

/// <summary>The VFX light experiment: every test-light mode builds, passes the self-check, and links the effector.</summary>
public class DomeAvfxTestLightTests
{
    private static readonly string[] PointData = ["Col", "DstS", "Rot", "Pos", "Attn", "bSdw", "bChS", "bMpS", "bMvS", "SCDN", "SCDF"];
    private static readonly string[] DirectionalData = ["Amb", "Col", "Pow", "Rot"];
    private static readonly string[] EffectorHeader = ["EfVT", "RoOT", "CCOT", "bAOV", "bAGm", "LpSt", "LpEd", "Data"];

    private static DomeAvfxParams Params(int mode, bool synced = false) =>
        new("bgcommon/milieu/t.atex", 1000, 1, 2, 1, false, 1, 0.1f, AttachToOwner: synced, TestLight: mode);

    private static string[] Tags(ChunkNode node) => node.Children.Select(c => c.Tag).ToArray();

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void TestLightModes_AddOneLinkedEffectorWithItsData(int mode, bool synced)
    {
        var root = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params(mode, synced)));

        Assert.Equal(1, root.Need("EfCn").AsInt());
        var effector = Assert.Single(root.All("Efct"));
        Assert.Equal(EffectorHeader, Tags(effector));
        Assert.Equal(mode == 3 ? 1 : 0, effector.Need("EfVT").AsInt());
        Assert.Equal(1, effector.Need("bAGm").AsInt());
        var data = effector.Need("Data");
        Assert.Equal(mode == 3 ? DirectionalData : PointData, Tags(data));
        if (mode != 3)
        {
            var shadow = mode == 2 ? 1 : 0;
            foreach (var flag in new[] { "bSdw", "bChS", "bMpS", "bMvS" }) Assert.Equal(shadow, data.Need(flag).AsInt());
            Assert.Equal(["ACT", "ACTR", "X", "Y", "Z"], Tags(data.Need("Pos")));
        }

        // Timeline: chunk 0 holds item 0 (the dome emitter); chunk 1 holds item 0 and item 1 (the light).
        var timeline = root.Need("TmLn");
        Assert.Equal(2, timeline.Need("TICn").AsInt());
        var items = timeline.All("Item").ToArray();
        Assert.Equal(2, items.Length);
        Assert.Single(items[0].Children, c => c.Tag == "bEna");
        Assert.Equal(new[] { -1, 0 }, items[1].Children.Where(c => c.Tag == "EfNo").Select(c => c.AsInt()).ToArray());
        Assert.Equal(new[] { 0, -1 }, items[1].Children.Where(c => c.Tag == "EmNo").Select(c => c.AsInt()).ToArray());
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

    [Fact]
    public void SelfCheck_RejectsLightMismatch()
    {
        var mesh = SphereMesh.Build(floorY: 0.1f);
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(DomeAvfxBuilder.Build(Params(1)), Params(0), mesh));
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(DomeAvfxBuilder.Build(Params(0)), Params(1), mesh));
    }
}
