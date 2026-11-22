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

    /// <summary>
    /// Golden SHA-256 values captured from `test` (da8171b) before synced lighting touched the builder: the local dome over
    /// a parameter matrix, and a non-default synced dome without lights (equals EmitterBaseHeight 0). Since the half dome
    /// (docs/adr/0015), floor0.16_r10 (no explicit mesh) pins the legacy floor, the kill-switch path; the half-dome entries
    /// imageFloor0.16_r10, open_r10 and synced_open_h0 were captured on the half-dome branch.
    /// </summary>
    [Theory] // [AC-synced-lighting-3] [AC-half-dome-4]
    [InlineData("default", "5A2E7FE433FFEA4D71952FA2177C162D0D0EEF8A27DC477336A7E9FB07539D81")]
    [InlineData("flip", "E1C0B9302FD4BB626C46A854384F820A706DA92AD873A0BD6407AE02067E92CF")]
    [InlineData("repeats3x1.5", "A8E1FD1E5D6D93277A531A8AD4EDCBDC0AC772467AE8D401D476EF21DE5F85D8")]
    [InlineData("vscale0.7", "D1B57018BC415B5429EC86A7A9329B882F512187B3DCC6FFA0359FA1020F8112")]
    [InlineData("bri1.5", "DF73353954BC784A1792917672246E6E52CAE7B02C0023F6340AC25A0DBF04E1")]
    [InlineData("floorNull_r10", "516DF977470DC1E59C809C09307B207C06163A0CB98711104F6F87259E3916FB")]
    [InlineData("floor0.16_r10", "C1D206A17F008FDD7775A9383C0C9A065BC905A3D050EED8327D8B5F08A3C9E7")]
    [InlineData("synced_h1.6_yaw0.7_rep3x1.5", "2DCAB3079C2BB5016E0719D3D9DFE8173815D936B9D466DBEE756275FBC8D03F")]
    [InlineData("imageFloor0.16_r10", "DC62FDB32324C37BF5F919251C17D517ACFB9FF3D6FE9BF225229875F13C9E5A")]
    [InlineData("open_r10", "1388DE02C1B8CBD14BDF5794D6C8165993478FA1E2300C827AE2D2315E7C30A0")]
    [InlineData("synced_open_h0", "BFF7F8376ED8B692ED43B01AC68D9D7CD6C84D03AC091E33F787FF7018A7E54F")]
    public void GoldenMatrix_ByteIdenticalToPreFeature(string name, string sha)
    {
        var b = new DomeAvfxParams("bgcommon/milieu/aabbccddeeff0011/sky.atex", 20000, 1, 2, 1, false, 1);
        var p = name switch
        {
            "default" => b,
            "flip" => b with { Flip = true },
            "repeats3x1.5" => b with { URepeat = 3, VRepeat = 1.5f },
            "vscale0.7" => b with { VerticalScale = 0.7f },
            "bri1.5" => b with { Brightness = 1.5f },
            "floorNull_r10" => b with { Radius = 10 },
            "floor0.16_r10" => b with { Radius = 10, FloorY = 0.16f },
            "synced_h1.6_yaw0.7_rep3x1.5" => b with { AttachToOwner = true, BakedHeight = 1.6f, BakedYawRadians = 0.7f, URepeat = 3, VRepeat = 1.5f },
            "imageFloor0.16_r10" => b with { Radius = 10, FloorY = 0.16f, Mesh = DomeMeshKind.ImageFloor },
            "open_r10" => b with { Radius = 10, Mesh = DomeMeshKind.OpenBottom },
            "synced_open_h0" => b with { Radius = 10, Mesh = DomeMeshKind.OpenBottom, AttachToOwner = true, BakedYawRadians = 0.7f },
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        Assert.Equal(sha, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(DomeAvfxBuilder.Build(p))));
    }

    [Fact] // [EDGE-synced-delivery-27]
    public void DefaultParams_ByteIdenticalToLegacy()
    {
        // Golden SHA-256 of the local dome for default params, captured before synced delivery changed the builder.
        var bytes = DomeAvfxBuilder.Build(Params());
        Assert.Equal("6FABACD8C01AA63616DB60AD36B6C5806C68B15F9E871B351DFEA9A0744AC24F", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        var itpr = AvfxChunkReader.Parse(bytes).Need("Emit").Need("ItPr");
        Assert.Equal((3, 1, 1, 1), (itpr.Need("PICd").AsInt(), itpr.Need("ICbS").AsInt(), itpr.Need("ICbR").AsInt(), itpr.Need("ICbP").AsInt()));
    }

    [Fact] // [AC-synced-delivery-3]
    public void AttachToOwner_SetsItPrFlags_SelfChecked()
    {
        Assert.Equal((1, 0, 0, 1), DomeAvfxBuilder.SyncedAttach);
        var p = Params() with { AttachToOwner = true };
        var bytes = DomeAvfxBuilder.Build(p);
        var itpr = AvfxChunkReader.Parse(bytes).Need("Emit").Need("ItPr");
        Assert.Equal(DomeAvfxBuilder.SyncedAttach, (itpr.Need("PICd").AsInt(), itpr.Need("ICbS").AsInt(), itpr.Need("ICbR").AsInt(), itpr.Need("ICbP").AsInt()));
        // SelfCheck rejects bytes that don't match the params (local bytes checked as synced, and the other way round).
        var mesh = SphereMesh.For(p.MeshKind, p.FloorY);
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(DomeAvfxBuilder.Build(Params()), p, mesh));
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bytes, Params(), mesh));
    }

    [Fact] // [AC-synced-delivery-6]
    public void BakedPlacement_WritesPosAndRotYCurves()
    {
        var p = Params() with { BakedHeight = 1.6f, BakedYawRadians = 0.5f };
        var ptcl = Build(p).Need("Ptcl");
        Assert.Equal(1.6f, ptcl.Need("Pos").Need("Y").CurveValue());
        Assert.Equal(0.5f, ptcl.Need("Rot").Need("Y").CurveValue());
        Assert.Equal(["ACT", "ACTR", "Y"], ptcl.Need("Pos").Children.Select(c => c.Tag).ToArray());
        Assert.Equal(["ACT", "ACTR", "Y"], ptcl.Need("Rot").Children.Select(c => c.Tag).ToArray());
        var mesh = SphereMesh.For(p.MeshKind, p.FloorY);
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(DomeAvfxBuilder.Build(Params()), p, mesh));
    }

    [Fact] // [AC-synced-lighting-7]
    public void EmitterBaseHeight_RaisesTheEmitter_ParticleBakedHeightUnchanged()
    {
        var p = Params() with { AttachToOwner = true, BakedHeight = 1.6f, EmitterBaseHeight = 18.25f };
        var root = Build(p);
        Assert.Equal(18.25f, root.Need("Emit").Need("Pos").Need("Y").CurveValue());
        Assert.Equal(1.6f, root.Need("Ptcl").Need("Pos").Need("Y").CurveValue());
        Assert.Null(Build(Params()).Need("Emit").Need("Pos").Child("Y"));   // 0: no key, byte-identical to before
    }

    [Theory] // [EDGE-synced-lighting-9]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(10000.5f)]
    [InlineData(-10001f)]
    public void EmitterBaseHeight_NonFiniteOrOutOfRange_Rejected(float y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DomeAvfxBuilder.Build(Params() with { AttachToOwner = true, EmitterBaseHeight = y }));
    }

    // ---------------------------------------------------------------- half dome (docs/adr/0015)

    private static (int Vertices, ushort[] Indices, byte[] Vdrw) Model(byte[] avfx)
    {
        var modl = AvfxChunkReader.Parse(avfx).Need("Modl");
        var vdrw = modl.Need("VDrw").Data;
        var vidx = modl.Need("VIdx").Data;
        var idx = new ushort[vidx.Length / 2];
        Buffer.BlockCopy(vidx, 0, idx, 0, vidx.Length);
        return (vdrw.Length / 36, idx, vdrw);
    }

    [Theory] // [AC-half-dome-6]
    [InlineData(true)]
    [InlineData(false)]
    public void Synced_SameMeshAsLocal(bool imageFloor)
    {
        var kind = imageFloor ? DomeMeshKind.ImageFloor : DomeMeshKind.OpenBottom;
        var floor = kind == DomeMeshKind.ImageFloor ? 0.16f : (float?)null;
        var local = Params(radius: 10) with { FloorY = floor, Mesh = kind };
        var synced = local with { AttachToOwner = true, BakedHeight = kind == DomeMeshKind.ImageFloor ? 1.6f : 0f, BakedYawRadians = 0.4f, EmitterBaseHeight = 12f };
        var a = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(local)).Need("Modl");
        var b = AvfxChunkReader.Parse(DomeAvfxBuilder.Build(synced)).Need("Modl");
        Assert.Equal(a.Need("VDrw").Data, b.Need("VDrw").Data);
        Assert.Equal(a.Need("VIdx").Data, b.Need("VIdx").Data);
    }

    [Fact] // [EDGE-half-dome-16]
    public void Synced_OpenBottom_FlagsAndNoPosKey()
    {
        var p = Params(radius: 10) with { Mesh = DomeMeshKind.OpenBottom, AttachToOwner = true, BakedHeight = 0f, BakedYawRadians = 0.4f };
        var root = Build(p);
        Assert.Null(root.Need("Ptcl").Need("Pos").Child("Y"));
        var itpr = root.Need("Emit").Need("ItPr");
        Assert.Equal(DomeAvfxBuilder.SyncedAttach, (itpr.Need("PICd").AsInt(), itpr.Need("ICbS").AsInt(), itpr.Need("ICbR").AsInt(), itpr.Need("ICbP").AsInt()));
        Assert.Equal(2425, Model(DomeAvfxBuilder.Build(p)).Vertices);
    }

    [Fact] // [EDGE-half-dome-16]
    public void Synced_ImageFloor_BakesHeight()
    {
        var p = Params(radius: 10) with { FloorY = 0.16f, Mesh = DomeMeshKind.ImageFloor, AttachToOwner = true, BakedHeight = 1.6f };
        Assert.Equal(1.6f, Build(p).Need("Ptcl").Need("Pos").Need("Y").CurveValue());
        Assert.Equal(9894, Model(DomeAvfxBuilder.Build(p)).Vertices);
    }

    public static TheoryData<float, float, float, int, bool, bool> AllowedSettings()
    {
        var data = new TheoryData<float, float, float, int, bool, bool>();
        foreach (var (r, mode) in new[] { (200f, 0), (20000f, 0), (1f, 1), (100f, 1) })
        foreach (var vs in new[] { 0.25f, 1f, 2f })
        foreach (var h in new[] { 0.25f, 1.6f, -1f })   // -1: just below the dome's height
        foreach (var kind in new[] { 0, 1, 2, 3 })
        foreach (var lights in new[] { false, true })
        foreach (var synced in new[] { false, true })
            data.Add(r, vs, h, kind, lights, synced);
        return data;
    }

    [Theory] // [AC-half-dome-10] [EDGE-half-dome-1] [EDGE-half-dome-2]
    [MemberData(nameof(AllowedSettings))]
    public void AllowedSettings_SelfCheckPasses(float r, float vs, float h, int kindIndex, bool lights, bool synced)
    {
        var kind = (DomeMeshKind)kindIndex;
        var height = h < 0 ? r * vs * 0.999f : h;
        float? floor = kind is DomeMeshKind.ImageFloor or DomeMeshKind.LegacyFloor ? height / (r * vs) : null;
        if (floor is { } f && !(f > 0 && f < 1)) return;   // camera at or above the dome: inactive, built as a sphere
        var p = Params(radius: r, vscale: vs) with
        {
            FloorY = floor, Mesh = kind,
            Lights = lights ? [new DomeLight(new System.Numerics.Vector3(0, 30, 0), System.Numerics.Vector3.One, 1, 50, true, 0.1f, 30)] : null,
            AttachToOwner = synced, BakedHeight = synced && kind != DomeMeshKind.OpenBottom ? height : 0f,
        };
        var (vertices, indices, vdrw) = Model(DomeAvfxBuilder.Build(p));
        Assert.True(vertices <= DomeAvfxBuilder.MaxVertices);
        Assert.All(indices, i => Assert.True(i < vertices));
        Assert.Equal(6 * 96 * (vertices / 97 - 1), indices.Length);
        for (var o = 0; o < vdrw.Length; o += 36)
            for (var k = 0; k < 6; k += 2)
                Assert.True(Half.IsFinite(BitConverter.ToHalf(vdrw, o + k)));
    }

    [Fact] // [AC-half-dome-10]
    public void SelfCheck_RejectsBadMesh()
    {
        var p = Params(radius: 10) with { FloorY = 0.16f, Mesh = DomeMeshKind.ImageFloor };
        var bytes = DomeAvfxBuilder.Build(p);
        var good = SphereMesh.BuildProjected(0.16f);
        var nan = (DomeVertex[])good.Vertices.Clone();
        nan[5] = nan[5] with { Y = Half.NaN };
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bytes, p, new SphereMesh { Vertices = nan, Indices = good.Indices }));
        Assert.Throws<InvalidDataException>(() => DomeAvfxBuilder.SelfCheck(bytes, p, new SphereMesh { Vertices = good.Vertices, Indices = good.Indices[..^3] }));
    }

    public static TheoryData<int, float> BadKindOrFloor() => new()
    {
        { 2, float.NaN }, { 2, 0f }, { 2, 1f }, { 2, -0.1f }, { 1, 1.5f }, { 2, float.PositiveInfinity },
        { 0, 0.16f }, { 3, 0f }, { 3, 1f }, { 3, float.NaN }, { 7, 0.16f }, { 7, float.NegativeZero },
    };

    [Theory] // [EDGE-half-dome-17] [EDGE-half-dome-21]
    [MemberData(nameof(BadKindOrFloor))]
    public void Validate_RejectsBadKindOrFloor(int kind, float floor)
    {
        float? f = kind is 7 && floor == 0 ? null : floor;
        var p = Params(radius: 10) with { FloorY = f, Mesh = (DomeMeshKind)kind };
        Assert.Throws<ArgumentOutOfRangeException>(() => DomeAvfxBuilder.Build(p));
    }

    [Fact] // [EDGE-half-dome-21]
    public void GuardExceptionTypes()
    {
        static void IsCaught(Action a)
        {
            var e = Record.Exception(a);
            Assert.NotNull(e);
            Assert.True(e is ArgumentException or InvalidDataException, e.GetType().Name);
        }
        IsCaught(() => DomeAvfxBuilder.Build(Params(radius: 10) with { Mesh = DomeMeshKind.ImageFloor }));
        IsCaught(() => DomeAvfxBuilder.Build(Params(radius: 10) with { Mesh = (DomeMeshKind)9 }));
        IsCaught(() => DomeShape.RingElevations(0.16, maxIterations: 2));
        IsCaught(() => DomeShape.CastImageFloor(0, -1, 0, 2));
        IsCaught(() => SphereMesh.BuildProjected(0.16f, segments: 2));
        IsCaught(() => SphereMesh.BuildProjected(0.16f, segments: 1000));   // beyond 16-bit indices
    }
}
