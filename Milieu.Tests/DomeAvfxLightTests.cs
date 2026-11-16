using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Milieu.GameFiles;
using Xunit;

namespace Milieu.Tests;

/// <summary>Synced VFX point lights in the dome builder (docs/adr/0011).</summary>
public class DomeAvfxLightTests
{
    private static readonly string[] EffectorHeader = ["EfVT", "RoOT", "CCOT", "bAOV", "bAGm", "LpSt", "LpEd", "Data"];
    private static readonly string[] PointData = ["Col", "DstS", "Rot", "Pos", "Attn", "bSdw", "bChS", "bMpS", "bMvS", "SCDN", "SCDF"];

    private static readonly DomeLight Key = new(new Vector3(3, 20, -4), new Vector3(1, 0.8f, 0.6f), 40, 60, true, 0.5f, 45);

    private static DomeLight Fill(float x) => new(new Vector3(x, 2, 1), new Vector3(0.4f, 0.6f, 1), 4, 16, false, 0.5f, 15);

    private static DomeAvfxParams Synced(IReadOnlyList<DomeLight>? lights, float baseY = 18.25f) =>
        new("vfx/milieu/0011223344556677/sky.atex", 10, 1, 2, 1, false, 1, 0.16f, BakedHeight: 1.6f, AttachToOwner: true,
            EmitterBaseHeight: baseY, Lights: lights);

    private static ChunkNode Build(DomeAvfxParams p) => AvfxChunkReader.Parse(DomeAvfxBuilder.Build(p));

    private static string[] Tags(ChunkNode n) => n.Children.Select(c => c.Tag).ToArray();

    private static (float, float, float) Rgb(ChunkNode curve)
    {
        var k = curve.Need("Keys").Data;
        return (BitConverter.ToSingle(k, 4), BitConverter.ToSingle(k, 8), BitConverter.ToSingle(k, 12));
    }

    [Fact] // [AC-synced-lighting-1]
    public void Key_UsesGameShadowRecipe_InVfxEditorChunkOrder()
    {
        var root = Build(Synced([Key]));
        Assert.Equal(1, root.Need("EfCn").AsInt());
        var e = Assert.Single(root.All("Efct"));
        Assert.Equal(EffectorHeader, Tags(e));
        Assert.Equal((0, 2, 0, 0, 1), (e.Need("EfVT").AsInt(), e.Need("RoOT").AsInt(), e.Need("CCOT").AsInt(), e.Need("bAOV").AsInt(), e.Need("bAGm").AsInt()));
        var d = e.Need("Data");
        Assert.Equal(PointData, Tags(d));
        Assert.Equal(["RGB", "Bri"], Tags(d.Need("Col")));
        Assert.Equal((1f, 0.8f, 0.6f), Rgb(d.Need("Col").Need("RGB")));
        Assert.Equal(40f, d.Need("Col").Need("Bri").CurveValue());
        Assert.Equal(60f, d.Need("DstS").CurveValue());
        Assert.Equal((3f, 20f, -4f), (d.Need("Pos").Need("X").CurveValue(), d.Need("Pos").Need("Y").CurveValue(), d.Need("Pos").Need("Z").CurveValue()));
        Assert.Equal((2, 1, 1, 1, 0), (d.Need("Attn").AsInt(), d.Need("bSdw").AsInt(), d.Need("bChS").AsInt(), d.Need("bMpS").AsInt(), d.Need("bMvS").AsInt()));
        Assert.Equal((0.5f, 45f), (d.Need("SCDN").AsFloat(), d.Need("SCDF").AsFloat()));
        // One emitter: the dome's, linked to effector 0.
        Assert.Equal(0, Assert.Single(root.All("Emit")).Need("EfNo").AsInt());
    }

    [Theory] // [AC-synced-lighting-1]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NLights_ChainParsesBack_SelfCheckPasses(int n)
    {
        var lights = new List<DomeLight> { Key };
        for (var i = 1; i < n; i++) lights.Add(Fill(i));
        var root = Build(Synced(lights));   // Build runs SelfCheck

        Assert.Equal(n, root.Need("EmCn").AsInt());
        Assert.Equal(n, root.Need("EfCn").AsInt());
        var emits = root.All("Emit").ToArray();
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(i, emits[i].Need("EfNo").AsInt());
            Assert.Equal(18.25f, emits[i].Need("Pos").Need("Y").CurveValue());   // every emitter at the player's height
        }
        for (var i = 1; i < n; i++)
        {
            Assert.Equal(0, emits[i].Need("PrCn").AsInt());
            Assert.Null(emits[i].Child("ItPr"));
        }
        var effs = root.All("Efct").ToArray();
        Assert.Equal(1, effs[0].Need("Data").Need("bSdw").AsInt());
        for (var i = 1; i < n; i++)
            foreach (var f in new[] { "bSdw", "bChS", "bMpS", "bMvS" }) Assert.Equal(0, effs[i].Need("Data").Need(f).AsInt());   // fills: no shadows
        var items = root.Need("TmLn").All("Item").ToArray();
        Assert.Equal(n, root.Need("TmLn").Need("TICn").AsInt());
        Assert.Equal(Enumerable.Range(0, n), items[^1].Children.Where(c => c.Tag == "EmNo").Select(c => c.AsInt()));
        Assert.All(items[^1].Children.Where(c => c.Tag == "EfNo"), c => Assert.Equal(-1, c.AsInt()));
        Assert.Equal(1.6f, root.Need("Ptcl").Need("Pos").Need("Y").CurveValue());   // the particle's baked height is unchanged
    }

    [Fact] // [EDGE-synced-lighting-2]
    public void NoKey_FirstFillOnDomeEmitter()
    {
        var root = Build(Synced([Fill(1)]));
        Assert.Equal(0, Assert.Single(root.All("Emit")).Need("EfNo").AsInt());
        Assert.Equal(0, Assert.Single(root.All("Efct")).Need("Data").Need("bSdw").AsInt());
    }

    [Fact] // [AC-synced-lighting-7]
    public void EffectorPos_IndependentOfPlayerHeight()
    {
        var low = Build(Synced([Key], baseY: 0)).Need("Efct").Need("Data").Need("Pos");
        var high = Build(Synced([Key], baseY: 18.25f)).Need("Efct").Need("Data").Need("Pos");
        Assert.Equal(low.Data, high.Data);
    }

    [Fact] // [EDGE-synced-lighting-3]
    public void EmptyLights_EqualsNoLights()
    {
        Assert.Equal(DomeAvfxBuilder.Build(Synced(null)), DomeAvfxBuilder.Build(Synced([])));
    }

    [Fact] // [EDGE-synced-lighting-7] [AC-synced-lighting-3]
    public void LightsWithoutAttachToOwner_Throws()
    {
        var local = Synced([Key]) with { AttachToOwner = false, EmitterBaseHeight = 0, BakedHeight = 0 };
        Assert.Throws<ArgumentException>(() => DomeAvfxBuilder.Build(local));
    }

    private static DomeLight[] InvalidCase(int i) => i switch
    {
        0 => Enumerable.Repeat(Key, 5).ToArray(),
        1 => [Key with { Position = new Vector3(float.NaN, 0, 0) }],
        2 => [Key with { Position = new Vector3(0, 1e6f, 0) }],
        3 => [Key with { Rgb = new Vector3(1.5f, 0, 0) }],
        4 => [Key with { Rgb = new Vector3(-0.1f, 0, 0) }],
        5 => [Key with { Brightness = -1 }],
        6 => [Key with { Brightness = float.PositiveInfinity }],
        7 => [Key with { Range = 0 }],
        8 => [Key with { ShadowNear = 5, ShadowFar = 5 }],
        _ => [Key with { ShadowNear = -1 }],
    };

    [Theory] // [EDGE-synced-lighting-33]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void InvalidLights_Throw(int c)
    {
        Assert.ThrowsAny<ArgumentException>(() => DomeAvfxBuilder.Build(Synced(InvalidCase(c))));
    }
}
