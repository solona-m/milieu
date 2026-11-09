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

    /// <summary>The game's light emitter (hash 2965227F157E314F, emitter 6): no ItPr, no particles.</summary>
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
        Assert.Equal((0f, 0f, 0f), Rgb(data.Need("Amb").Need("RGB")));
        Assert.Equal(["RGB", "Bri"], Tags(data.Need("Col")));   // Bri, not the point lights' A
        Assert.Equal(mode == 3 ? (1f, 182f / 255f, 99f / 255f) : (1f, 0.15f, 0.15f), Rgb(data.Need("Col").Need("RGB")));
        Assert.Equal(mode == 3 ? 3f : 10f, data.Need("Col").Need("Bri").CurveValue());
        Assert.Equal(1f, data.Need("Pow").CurveValue());
        Assert.Equal(mode == 3 ? ["ACT", "ACTR", "X"] : ["ACT", "ACTR", "X", "Y"], Tags(data.Need("Rot")));
        Assert.Equal(mode == 3 ? 1.5707964f : 0.8f, data.Need("Rot").Need("X").CurveValue());
        if (mode != 3) Assert.Equal(0.6f, data.Need("Rot").Need("Y").CurveValue());

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
        Assert.Equal([-1], Ints(items[0], "EfNo"));
        Assert.Equal([0, 1], Ints(items[1], "EmNo"));
        Assert.Equal([-1, -1], Ints(items[1], "EfNo"));
        Assert.Equal([-1, -1], Ints(items[1], "EdTm"));   // both run for the VFX's whole life
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mode5_LinksMode4sLightFromTheDomeEmitter(bool synced)
    {
        var root = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params(5, synced)));
        var mode4 = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params(4, synced)));

        // The same directional effector bytes as mode 4 ...
        Assert.Equal(mode4.Need("Efct").Data, Assert.Single(root.All("Efct")).Data);
        // ... but no light emitter: one emitter (the dome, linked) and one timeline item.
        Assert.Equal(1, root.Need("EmCn").AsInt());
        var dome = Assert.Single(root.All("Emit"));
        Assert.Equal(0, dome.Need("EfNo").AsInt());
        Assert.Equal(1, dome.Need("PrCn").AsInt());
        Assert.Equal(1, root.Need("TmLn").Need("TICn").AsInt());
        Assert.Equal(-1, Assert.Single(root.Need("TmLn").All("Item")).Need("EfNo").AsInt());
    }

    private static (float, float, float) Rgb(ChunkNode curve)
    {
        var keys = curve.Need("Keys").Data;
        return (BitConverter.ToSingle(keys, 4), BitConverter.ToSingle(keys, 8), BitConverter.ToSingle(keys, 12));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
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
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    public void SelfCheck_RejectsLightMismatch(int built, int checkedAs)
    {
        var mesh = SphereMesh.Build(floorY: 0.1f);
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(DomeAvfxBuilder.Build(Params(built)), Params(checkedAs), mesh));
    }

    /// <summary>
    /// Patches the first 4-byte leaf <paramref name="tag"/> after the <paramref name="occurrence"/>-th <paramref name="parent"/>
    /// tag (4-character tags, stored reversed).
    /// </summary>
    private static byte[] Patch(byte[] bytes, string parent, string tag, int value, int occurrence = 0, int leafOccurrence = 0)
    {
        var at = Find(bytes, parent, occurrence, 0);
        var leaf = Find(bytes, tag, leafOccurrence, at);
        var copy = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(copy.AsSpan(leaf + 8, 4), value);
        return copy;
    }

    /// <summary>Renames the first <paramref name="tag"/> after the <paramref name="occurrence"/>-th <paramref name="parent"/> tag.</summary>
    private static byte[] Rename(byte[] bytes, string parent, int occurrence, string tag, string newTag)
    {
        var leaf = Find(bytes, tag, 0, Find(bytes, parent, occurrence, 0));
        var copy = (byte[])bytes.Clone();
        Stored(newTag).CopyTo(copy, leaf);
        return copy;
    }

    private static byte[] Stored(string t) => System.Text.Encoding.ASCII.GetBytes(t).Reverse().ToArray();

    /// <summary>Offset of the <paramref name="occurrence"/>-th stored 4-character <paramref name="tag"/> at or after <paramref name="from"/>.</summary>
    private static int Find(byte[] bytes, string tag, int occurrence, int from)
    {
        var at = from - 1;
        for (var i = 0; i <= occurrence; i++)
        {
            var next = bytes.AsSpan(at + 1).IndexOf(Stored(tag));
            Assert.True(next >= 0, tag);
            at += 1 + next;
        }
        return at;
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
    [InlineData(5, "Emit", 0, "EfNo", -1, "emitter → effector link")]                // mode 5's dome emitter unlinked
    public void SelfCheck_RejectsBadLinks(int mode, string parent, int occurrence, string tag, int value, string check)
    {
        var mesh = SphereMesh.Build(floorY: 0.1f);
        var bad = Patch(DomeAvfxBuilder.Build(Params(mode)), parent, tag, value, occurrence);
        var e = Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bad, Params(mode), mesh));
        Assert.Contains(check, e.Message);
    }

    // Timeline item chunks, in directional modes 3/4: "Item" occurrence 0 is the scheduler's, 1 is timeline chunk 0
    // ([dome]), 2 is timeline chunk 1 ([dome, light]); leafOccurrence picks the item within the chunk.
    [Theory]
    [InlineData(2, "EfNo", 1, 0)]    // the light item also links the effector from the timeline
    [InlineData(2, "EfNo", 0, 0)]    // the dome item in chunk 1 links it
    [InlineData(2, "EmNo", 1, 0)]    // chunk 1's emitters out of order: [0, 0]
    [InlineData(2, "EmNo", 0, 1)]    // chunk 1's emitters out of order: [1, 1]
    [InlineData(1, "EfNo", 0, 0)]    // chunk 0's item links the effector
    public void SelfCheck_RejectsBadTimelineChunks(int itemOccurrence, string tag, int leafOccurrence, int value)
    {
        var mesh = SphereMesh.Build(floorY: 0.1f);
        var bad = Patch(DomeAvfxBuilder.Build(Params(3)), "Item", tag, value, itemOccurrence, leafOccurrence);
        var e = Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bad, Params(3), mesh));
        Assert.Contains("timeline items", e.Message);
    }

    [Fact]
    public void SelfCheck_RejectsLightEmitterWithItPr()
    {
        // Renaming the light emitter's Life chunk to ItPr gives it an ItPr child (the self-check doesn't look at its Life).
        var mesh = SphereMesh.Build(floorY: 0.1f);
        var bad = Rename(DomeAvfxBuilder.Build(Params(3)), "Emit", 1, "Life", "ItPr");
        Assert.NotNull(AvfxChunkReader.Parse(bad).All("Emit").ElementAt(1).Child("ItPr"));
        var e = Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bad, Params(3), mesh));
        Assert.Contains("light emitter → effector 0, no particles", e.Message);
    }
}
