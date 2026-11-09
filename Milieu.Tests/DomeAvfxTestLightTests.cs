using System;
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

    /// <summary>The game's light emitter (hash 2965227F157E314F, emitter 1): no ItPr, no particles.</summary>
    private static readonly string[] LightEmitterTags =
        ["SdNo", "LpSt", "LpEd", "ClCn", "EfNo", "bAD", "EVT", "RBDT", "CCOT", "ROT", "PrCn", "EmCn", "Life", "CrC", "CrI", "ARs", "Col", "Pos", "Rot", "Scl"];

    private static DomeAvfxParams Params(int mode, bool synced = false) =>
        new("bgcommon/milieu/t.atex", 1000, 1, 2, 1, false, 1, 0.1f, AttachToOwner: synced, TestLight: mode);

    private static string[] Tags(ChunkNode node) => node.Children.Select(c => c.Tag).ToArray();

    private static int[] Ints(ChunkNode node, string tag) => node.Children.Where(c => c.Tag == tag).Select(c => c.AsInt()).ToArray();

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void PointModes_LinkOneVenueStyleLightFromTheDomeEmitter(int mode, bool synced)
    {
        var root = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params(mode, synced)));

        Assert.Equal(1, root.Need("EfCn").AsInt());
        var effector = Assert.Single(root.All("Efct"));
        Assert.Equal(EffectorHeader, Tags(effector));
        Assert.Equal(0, effector.Need("EfVT").AsInt());
        Assert.Equal(2, effector.Need("RoOT").AsInt());
        Assert.Equal(1, effector.Need("bAOV").AsInt());
        Assert.Equal(1, effector.Need("bAGm").AsInt());
        var data = effector.Need("Data");
        Assert.Equal(PointData, Tags(data));
        foreach (var flag in new[] { "bSdw", "bChS", "bMpS", "bMvS" }) Assert.Equal(1, data.Need(flag).AsInt());
        Assert.Equal(["ACT", "ACTR", "X", "Y", "Z"], Tags(data.Need("Pos")));

        // Venue values: colour as RGB + A (no Bri), distance scale 1 (10 in mode 2), shadow range 0.0001..20.
        Assert.Equal(["RGB", "A"], Tags(data.Need("Col")));
        Assert.Equal(mode == 2 ? 10f : 1f, data.Need("DstS").CurveValue());
        Assert.Equal(mode == 2 ? 3f : 1f, data.Need("Col").Need("A").CurveValue());
        Assert.Equal(0.0001f, data.Need("SCDN").AsFloat());
        Assert.Equal(20f, data.Need("SCDF").AsFloat());
        Assert.Equal(1.5f, data.Need("Pos").Need("Y").CurveValue());
        Assert.Equal(2.5f, data.Need("Pos").Need("Z").CurveValue());

        // Linked from the dome emitter, as the venue's lights are; one emitter, one timeline item with no effector.
        Assert.Equal(1, root.Need("EmCn").AsInt());
        Assert.Equal(0, Assert.Single(root.All("Emit")).Need("EfNo").AsInt());
        var timeline = root.Need("TmLn");
        Assert.Equal(1, timeline.Need("TICn").AsInt());
        Assert.Equal(-1, Assert.Single(timeline.All("Item")).Need("EfNo").AsInt());
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void DirectionalModes_PutTheGamesKindOfLightOnItsOwnEmitter(int mode, bool synced)
    {
        var root = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params(mode, synced)));

        Assert.Equal(1, root.Need("EfCn").AsInt());
        var effector = Assert.Single(root.All("Efct"));
        Assert.Equal(EffectorHeader, Tags(effector));
        Assert.Equal(1, effector.Need("EfVT").AsInt());
        Assert.Equal(2, effector.Need("RoOT").AsInt());
        Assert.Equal(0, effector.Need("bAOV").AsInt());   // as the game's world-lighting directional lights
        Assert.Equal(1, effector.Need("bAGm").AsInt());
        var data = effector.Need("Data");
        Assert.Equal(DirectionalData, Tags(data));
        Assert.Equal(["RGB"], Tags(data.Need("Amb")));
        Assert.Equal(["RGB", "Bri"], Tags(data.Need("Col")));   // Bri, not the point lights' A
        Assert.Equal(mode == 3 ? 3f : 10f, data.Need("Col").Need("Bri").CurveValue());
        Assert.Equal(1f, data.Need("Pow").CurveValue());
        Assert.Equal(mode == 3 ? ["ACT", "ACTR", "X"] : ["ACT", "ACTR", "X", "Y"], Tags(data.Need("Rot")));
        Assert.Equal(mode == 3 ? 1.5707964f : 0.8f, data.Need("Rot").Need("X").CurveValue());

        // Two emitters: the dome (unlinked) and the light's own emitter (linked, no particles, no ItPr).
        Assert.Equal(2, root.Need("EmCn").AsInt());
        var emits = root.All("Emit").ToArray();
        Assert.Equal(2, emits.Length);
        Assert.Equal(-1, emits[0].Need("EfNo").AsInt());
        Assert.NotNull(emits[0].Child("ItPr"));
        Assert.Equal(LightEmitterTags, Tags(emits[1]));
        Assert.Equal(0, emits[1].Need("EfNo").AsInt());
        Assert.Equal(0, emits[1].Need("PrCn").AsInt());

        // Timeline: chunk 0 = [dome item], chunk 1 = [dome item, light item]; no item links an effector.
        var timeline = root.Need("TmLn");
        Assert.Equal(2, timeline.Need("TICn").AsInt());
        var items = timeline.All("Item").ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal([0], Ints(items[0], "EmNo"));
        Assert.Equal([0, 1], Ints(items[1], "EmNo"));
        Assert.Equal([-1, -1], Ints(items[1], "EfNo"));
        Assert.Equal([-1, -1], Ints(items[1], "EdTm"));   // both run for the VFX's whole life
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void OffOrUnknownMode_IsByteIdenticalToNoLight(int mode)
    {
        var plain = DomeAvfxBuilder.Build(new DomeAvfxParams("bgcommon/milieu/t.atex", 1000, 1, 2, 1, false, 1, 0.1f));
        Assert.Equal(plain, DomeAvfxBuilder.Build(Params(mode)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(3, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public void SelfCheck_RejectsLightMismatch(int built, int checkedAs)
    {
        var mesh = SphereMesh.Build(floorY: 0.1f);
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(DomeAvfxBuilder.Build(Params(built)), Params(checkedAs), mesh));
    }

    /// <summary>
    /// Patches the first 4-byte leaf <paramref name="tag"/> after the <paramref name="occurrence"/>-th <paramref name="parent"/>
    /// tag (4-character tags, stored reversed).
    /// </summary>
    private static byte[] Patch(byte[] bytes, string parent, string tag, int value, int occurrence = 0)
    {
        static byte[] Stored(string t) => System.Text.Encoding.ASCII.GetBytes(t).Reverse().ToArray();
        var at = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            var next = bytes.AsSpan(at + 1).IndexOf(Stored(parent));
            Assert.True(next >= 0, parent);
            at += 1 + next;
        }
        var leaf = bytes.AsSpan(at).IndexOf(Stored(tag));
        Assert.True(leaf >= 0, tag);
        var copy = (byte[])bytes.Clone();
        System.BitConverter.TryWriteBytes(copy.AsSpan(at + leaf + 8, 4), value);
        return copy;
    }

    [Theory]
    [InlineData(1, "Emit", 0, "EfNo", 1, "emitter → effector link")]                 // out of range for one effector
    [InlineData(1, "Emit", 0, "EfNo", -1, "emitter → effector link")]                // light written but not linked
    [InlineData(0, "Emit", 0, "EfNo", 0, "emitter → effector link")]                 // link with no effector
    [InlineData(3, "Emit", 0, "EfNo", 0, "emitter → effector link")]                 // directional also on the dome emitter
    [InlineData(3, "Emit", 1, "EfNo", -1, "light emitter → effector 0, no particles")] // light emitter unlinked
    [InlineData(3, "Emit", 1, "PrCn", 1, "light emitter → effector 0, no particles")]  // light emitter with particles
    [InlineData(1, "TmLn", 0, "EfNo", 0, "timeline items")]                          // a stray timeline link
    [InlineData(3, "TmLn", 0, "EmNo", 1, "timeline items")]                          // dome item points at the light emitter
    public void SelfCheck_RejectsBadLinks(int mode, string parent, int occurrence, string tag, int value, string check)
    {
        var mesh = SphereMesh.Build(floorY: 0.1f);
        var bad = Patch(DomeAvfxBuilder.Build(Params(mode)), parent, tag, value, occurrence);
        var e = Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bad, Params(mode), mesh));
        Assert.Contains(check, e.Message);
    }
}
