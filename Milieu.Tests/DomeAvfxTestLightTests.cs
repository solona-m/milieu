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

        // Linked from the dome emitter, as the venue's lights are; the timeline stays one item with no effector.
        Assert.Equal(0, root.Need("Emit").Need("EfNo").AsInt());
        var timeline = root.Need("TmLn");
        Assert.Equal(1, timeline.Need("TICn").AsInt());
        Assert.Equal(-1, Assert.Single(timeline.All("Item")).Need("EfNo").AsInt());

        // Venue values: colour as RGB + A (no Bri), distance scale 1, shadow range 0.0001..20.
        Assert.Equal(["RGB", "A"], Tags(data.Need("Col")));
        Assert.Equal(1, effector.Need("bAOV").AsInt());
        Assert.Equal(2, effector.Need("RoOT").AsInt());
        if (mode != 3)
        {
            Assert.Equal(1f, data.Need("DstS").CurveValue());
            Assert.Equal(0.0001f, data.Need("SCDN").AsFloat());
            Assert.Equal(20f, data.Need("SCDF").AsFloat());
            Assert.Equal(1.5f, data.Need("Pos").Need("Y").CurveValue());
            Assert.Equal(2.5f, data.Need("Pos").Need("Z").CurveValue());
        }
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
