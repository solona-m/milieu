using System;
using System.IO;
using System.Linq;
using Milieu.GameFiles;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class DomeAvfxBuilderTests
{
    private const string TexPath = "vfx/milieu/0123456789abcdef/sky.atex";

    private static DomeAvfxParams Params(float radius = 20000, float vscale = 1, float u = 2, float v = 1, bool flip = false, float bri = 1) =>
        new(TexPath, radius, vscale, u, v, flip, bri);

    private static ChunkNode Build(DomeAvfxParams p) => AvfxChunkReader.Parse(DomeAvfxBuilder.Build(p));

    [Fact] // [AC-hdri-backdrop-1]
    public void Structure_ParsesBack_WithConsistentChain()
    {
        var bytes = DomeAvfxBuilder.Build(Params());
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(0, bytes.Length % 4);
        var root = AvfxChunkReader.Parse(bytes);

        Assert.Equal(DomeAvfxBuilder.Version, root.Need("Ver").AsInt());
        foreach (var (tag, n) in new[] { ("ScCn", 1), ("TlCn", 1), ("EmCn", 1), ("PrCn", 1), ("EfCn", 0), ("BdCn", 0), ("TxCn", 1), ("MdCn", 1) })
            Assert.Equal(n, root.Need(tag).AsInt());
        Assert.Equal(0, root.Need("bCBC").AsInt());
        Assert.Equal(0, root.Need("bCul").AsInt());

        Assert.Equal(new[] { "Schd", "TmLn", "Emit", "Ptcl", "Tex", "Modl" },
                     root.Children.Where(c => c.IsContainer || c.Tag == "Tex").Select(c => c.Tag).ToArray());

        Assert.Equal(0, root.Need("Schd").Need("Item").Need("TlNo").AsInt());
        Assert.Equal(0, root.Need("TmLn").Need("Item").Need("EmNo").AsInt());
        Assert.Equal(0, root.Need("Emit").Need("ItPr").Need("TgtB").AsInt());

        var ptcl = root.Need("Ptcl");
        Assert.Equal(5, ptcl.Need("PrVT").AsInt());
        Assert.Equal(8, ptcl.Need("RMT").AsInt());
        Assert.Equal(-1f, ptcl.Need("Life").Need("Val").AsFloat());
        var mdNo = ptcl.Need("Data").Need("MdNo");
        Assert.Equal(1, mdNo.Size);
        Assert.Equal(0, mdNo.AsInt());
        var tc1 = ptcl.Need("TC1");
        Assert.Equal(1, tc1.Need("bEna").AsInt());
        Assert.Equal(1, tc1.Need("TLst").Size);
        Assert.Equal(0, tc1.Need("TLst").AsInt());
        Assert.Null(tc1.Child("TxNo"));
        foreach (var slot in new[] { "TC2", "TC3", "TN", "TR", "TD", "TP" })
        {
            Assert.Equal(0, ptcl.Need(slot).Need("bEna").AsInt());
            Assert.Equal(-1, ptcl.Need(slot).Need("TxNo").AsInt());
        }

        Assert.Equal(TexPath + "\0", System.Text.Encoding.ASCII.GetString(root.Need("Tex").Data));
        var modl = root.Need("Modl");
        Assert.Equal(4753 * 36, modl.Need("VDrw").Size);
        Assert.Equal(27648 * 2, modl.Need("VIdx").Size);
        var idx = modl.Need("VIdx").Data;
        for (var i = 0; i < idx.Length; i += 2) Assert.True(BitConverter.ToUInt16(idx, i) < 4753);
    }

    [Fact] // [AC-hdri-backdrop-1]
    public void Schd_LayoutMatchesReferenceRules()
    {
        var schd = Build(Params()).Need("Schd");
        Assert.Equal(1, schd.Need("ItCn").AsInt());
        Assert.Equal(12, schd.Need("TrCn").AsInt());
        var item = schd.Need("Item");
        Assert.Equal(36, item.Size);
        Assert.Single(schd.All("Item"));
        var triggers = schd.All("Trgr").ToList();
        Assert.Equal(12, triggers.Count);
        for (var j = 0; j < 12; j++)
        {
            var t = triggers[j];
            Assert.Equal(36 + 36 * (j + 1), t.Size);
            var tlNos = t.All("TlNo").Select(c => c.AsInt()).ToArray();
            Assert.Equal(0, tlNos[0]);                                  // the item first
            Assert.All(tlNos.Skip(1), n => Assert.Equal(-1, n));       // then triggers[0..j]
            Assert.Equal(j + 2, tlNos.Length);
            Assert.All(t.All("bEna"), c => Assert.Equal(1, c.AsInt()));
            Assert.All(t.All("StTm"), c => Assert.Equal(0, c.AsInt()));
        }
    }

    [Fact] // [AC-hdri-backdrop-2]
    public void ParticleScale_EncodesRadiusAndVerticalScale()
    {
        var scl = Build(Params(radius: 8, vscale: 0.5f)).Need("Ptcl").Need("Scl");
        Assert.Equal(3, scl.Need("ACT").AsInt());
        Assert.Equal(8f, scl.Need("X").CurveValue());
        Assert.Equal(4f, scl.Need("Y").CurveValue());
    }

    [Fact] // [AC-hdri-backdrop-4] [EDGE-hdri-backdrop-30]
    public void UvRepeatFlipBrightness_Written()
    {
        var ptcl = Build(Params(u: 2, v: 1)).Need("Ptcl");
        Assert.Equal(2, ptcl.Need("UvSN").AsInt());
        Assert.Equal(2, ptcl.All("UvSt").Count());
        var uv0 = ptcl.Need("UvSt").Need("Scl");
        Assert.Equal(2f, uv0.Need("X").CurveValue());
        Assert.Equal(1f, uv0.Need("Y").CurveValue());

        var flipped = Build(Params(u: 2, v: 1.5f, flip: true, bri: 0.6f)).Need("Ptcl");
        Assert.Equal(-2f, flipped.Need("UvSt").Need("Scl").Need("X").CurveValue());
        Assert.Equal(1.5f, flipped.Need("UvSt").Need("Scl").Need("Y").CurveValue());
        Assert.Equal(0.6f, flipped.Need("Col").Need("Bri").CurveValue());
    }

    [Fact]
    public void InvalidParams_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DomeAvfxBuilder.Build(Params(radius: float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DomeAvfxBuilder.Build(Params(radius: 0)));
        Assert.Throws<ArgumentException>(() => DomeAvfxBuilder.Build(Params() with { TextureGamePath = "" }));
        Assert.Throws<ArgumentException>(() => DomeAvfxBuilder.Build(Params() with { TextureGamePath = "vfx/é.atex" }));
    }

    [ReferenceFileFact(ReferenceFiles.Avfx)] // [AC-hdri-backdrop-1] (reference)
    public void Reference_ElementChunkSequencesAreOrderedSubsetsOfReference()
    {
        var reference = AvfxChunkReader.Parse(File.ReadAllBytes(ReferenceFiles.Path(ReferenceFiles.Avfx)));
        var ours = Build(Params());

        // Root scalars: an ordered subset, each the same size.
        AssertOrderedSubset(ours.Children.Where(c => !c.IsContainer && c.Tag != "Tex").ToList(),
                            reference.Children.Where(c => !c.IsContainer && c.Tag != "Tex").ToList(), "root");
        AssertSubtree(ours.Need("Schd"), reference.Need("Schd"), "Schd");
        AssertSubtree(ours.Need("TmLn"), reference.Need("TmLn", 2), "TmLn");
        AssertSubtree(ours.Need("Emit"), reference.Need("Emit", 5), "Emit");
        AssertSubtree(ours.Need("Ptcl"), reference.Need("Ptcl", 11), "Ptcl");
        AssertSubtree(ours.Need("Modl"), reference.Need("Modl", 7), "Modl");
    }

    private static void AssertSubtree(ChunkNode ours, ChunkNode reference, string path) =>
        AssertOrderedSubset(ours.Children, reference.Children, path);

    private static void AssertOrderedSubset(System.Collections.Generic.IList<ChunkNode> ours,
                                            System.Collections.Generic.IList<ChunkNode> reference, string path)
    {
        var j = 0;
        foreach (var o in ours)
        {
            while (j < reference.Count && reference[j].Tag != o.Tag) j++;
            Assert.True(j < reference.Count, $"{path}/{o.Tag} is not in the reference at this position.");
            var r = reference[j];
            Assert.Equal(r.IsContainer, o.IsContainer);
            if (o.IsContainer) AssertOrderedSubset(o.Children, r.Children, $"{path}/{o.Tag}");
            else if (o.Tag is not ("VDrw" or "VIdx" or "Keys") || o.Size == r.Size)
                Assert.True(o.Size == r.Size, $"{path}/{o.Tag} is {o.Size} bytes, the reference's is {r.Size}.");
            j++;
        }
    }


    [Fact] // [AC-hdri-backdrop-15]
    public void GroundProjection_ChangesOnlyVertexPositions()
    {
        var plain = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params()));
        var flat = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(Params() with { FloorY = 0.2f }));
        var a = plain.Need("Modl").Need("VDrw").Data;
        var b = flat.Need("Modl").Need("VDrw").Data;
        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(a, b);
        for (var o = 0; o < a.Length; o += 36) Assert.Equal(a[(o + 8)..(o + 36)], b[(o + 8)..(o + 36)]);
        Assert.Equal(plain.Need("Modl").Need("VIdx").Data, flat.Need("Modl").Need("VIdx").Data);
        Assert.Equal(plain.Need("Ptcl").Data, flat.Need("Ptcl").Data);
    }

    [Fact] // [AC-hdri-backdrop-15]
    public void PosAndRotYAreZero_SeamForSyncedFollowUp()
    {
        // The reference raises its particle 400 and turns it 45°; Milieu places the dome at spawn instead. The synced
        // follow-up will bake these (the game, not Milieu, places the cushion's VFX), so they must stay present at 0.
        var ptcl = Build(Params()).Need("Ptcl");
        Assert.Equal(0, ptcl.Need("Pos").Need("ACT").AsInt());
        Assert.Equal(0, ptcl.Need("Rot").Need("ACT").AsInt());
        Assert.Null(ptcl.Need("Pos").Child("Y"));
        Assert.Null(ptcl.Need("Rot").Child("Y"));
    }
}
