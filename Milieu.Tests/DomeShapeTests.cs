using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Milieu.GameFiles;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>The half-dome geometry and meshes (docs/adr/0015).</summary>
public class DomeShapeTests
{
    private const double Deg = Math.PI / 180;
    private const double Az0 = 90 * Deg, Az1 = (90 + 360.0 / 96) * Deg;

    private static (double X, double Y, double Z) Dir(double el, double az) =>
        (Math.Cos(el) * Math.Cos(az), Math.Sin(el), Math.Cos(el) * Math.Sin(az));

    private static (double X, double Y, double Z) Cast(double el, double az, double f)
    {
        var (x, y, z) = Dir(el, az);
        return DomeShape.CastImageFloor(x, y, z, f);
    }

    private static double Ring(double x, double z) => Math.Sqrt(x * x + z * z);

    // ---------------------------------------------------------------- AC-1: the shape

    [Theory] // [AC-half-dome-1]
    [InlineData(0.145)]
    [InlineData(0.16)]
    [InlineData(0.5)]
    public void ImageFloor_PointsOnHemisphereOrFloor(double f)
    {
        var els = DomeShape.RingElevations(f);
        foreach (var el in els)
        {
            var (x, y, z) = Cast(el, Az0, f);
            Assert.True(y >= -f - 1e-12, $"el {el / Deg}: below the floor");
            var onBall = Math.Abs(Math.Sqrt(x * x + (y + f) * (y + f) + z * z) - 1) < 1e-9;
            var onFloor = Math.Abs(y + f) < 1e-12 && Ring(x, z) <= 1 + 1e-9;
            Assert.True(onBall || onFloor, $"el {el / Deg}: neither on the hemisphere nor on the floor");
        }
        // The crease ring lies exactly on the rim: radius 1, at the floor.
        var crease = els.Single(e => Math.Abs(e + Math.Atan(f)) < 1e-12);
        var (cx, cy, cz) = Cast(crease, Az0, f);
        Assert.Equal(1, Ring(cx, cz), 9);
        Assert.Equal(-f, cy, 9);
        // The horizon hits the wall at camera height above the floor.
        var (_, hy, _) = Cast(0, Az0, f);
        Assert.Equal(0, hy, 12);
    }

    [Fact] // [AC-half-dome-1]
    public void Projected_FloorFlat_RimOnFloor()
    {
        const float f = 0.16f;
        var mesh = SphereMesh.BuildProjected(f);
        var els = DomeShape.RingElevations(f);
        var crease = Array.FindIndex(els, e => Math.Abs(e + Math.Atan(f)) < 1e-12);
        for (var r = crease; r < els.Length; r++)
            for (var c = 0; c < 97; c++)
                Assert.Equal(-f, (double)mesh.Vertices[r * 97 + c].Y, 2);
        for (var c = 0; c < 97; c++)
        {
            var v = mesh.Vertices[crease * 97 + c];
            Assert.Equal(1, Ring((double)v.X, (double)v.Z), 2);
        }
    }

    // ---------------------------------------------------------------- AC-2: the open bottom

    [Fact] // [AC-half-dome-2]
    public void OpenBottom_IsUpperHalfOfReference()
    {
        var open = SphereMesh.BuildOpenBottom();
        var full = SphereMesh.Build();
        Assert.Equal(2425, open.Vertices.Length);
        Assert.Equal(13824, open.Indices.Length);
        Assert.All(open.Vertices, v => Assert.True((double)v.Y >= 0));
        Assert.Equal(0, open.Vertices.Max(v => (double)v.V));
        Assert.Equal(full.VertexBytes()[..open.VertexBytes().Length], open.VertexBytes());
        Assert.Equal(full.IndexBytes()[..open.IndexBytes().Length], open.IndexBytes());
    }

    [Theory] // [AC-half-dome-12] [EDGE-half-dome-23]
    [InlineData(0.16f, 28)]          // the room default: rows to −7.5°, then the cut at −9.2°
    [InlineData(0.0654031f, 26)]     // sin 3.75°: the cut lands on row 25, which it replaces
    [InlineData(0.001f, 26)]         // just below the horizon: row 24 kept, a sliver band to the cut
    [InlineData(0.999f, 49)]         // nearly the whole sphere
    public void OpenBottom_Cut_IsReferencePrefixPlusRimOnTheFloor(float f, int rows)
    {
        var open = SphereMesh.BuildOpenBottom(f);
        var full = SphereMesh.Build();
        Assert.Equal(rows * 97, open.Vertices.Length);
        Assert.Equal(6 * 96 * (rows - 1), open.Indices.Length);
        var prefix = (rows - 1) * 97 * DomeVertex.Stride;
        Assert.Equal(full.VertexBytes()[..prefix], open.VertexBytes()[..prefix]);
        Assert.Equal(full.IndexBytes()[..open.IndexBytes().Length], open.IndexBytes());
        var rim = open.Vertices[^97..];
        Assert.All(rim, v => Assert.Equal(-f, (double)v.Y, 2));   // world y = h − R·vs·f = 0
        Assert.All(rim, v => Assert.Equal(Math.Asin(f) / Math.PI, (double)v.V, 3));
        Assert.True(rim[0].V > open.Vertices[^98].V);   // v strictly increasing to the rim
        Assert.True(open.Vertices.Length <= DomeAvfxBuilder.MaxVertices);
    }

    [Theory] // [EDGE-half-dome-23]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(float.NaN)]
    public void OpenBottom_BadCut_Throws(float f) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SphereMesh.BuildOpenBottom(f));

    [ReferenceFileFact(ReferenceFiles.Avfx)] // [AC-half-dome-2] (reference)
    public void Reference_OpenBottomPrefixMatchesModel7()
    {
        var model = AvfxChunkReader.Parse(File.ReadAllBytes(ReferenceFiles.Path(ReferenceFiles.Avfx))).All("Modl").ElementAt(7);
        var open = SphereMesh.BuildOpenBottom();
        Assert.Equal(model.Need("VIdx").Data[..open.IndexBytes().Length], open.IndexBytes());
        var vdrw = model.Need("VDrw").Data;
        var ours = open.VertexBytes();
        for (var o = 0; o < ours.Length; o += 36)
            for (var k = 0; k < 8; k += 2)
                Assert.InRange(Math.Abs((double)BitConverter.ToHalf(vdrw, o + k) - (double)BitConverter.ToHalf(ours, o + k)), 0, 1e-3);
    }

    // ---------------------------------------------------------------- AC-3: no streaks

    /// <summary>
    /// The worst vertical and horizontal (× cos elevation) error of the drawn image along every ring, column and diagonal edge
    /// of one column pair: the GPU interpolates UV linearly along the surface, the image belongs at the point's direction
    /// from the capture point. Horizontal is not measured on edges touching a pole ring (no azimuth there).
    /// </summary>
    private static (double Vertical, double Horizontal) WorstError(double f)
    {
        var els = DomeShape.RingElevations(f);
        double worstV = 0, worstH = 0;
        void Edge(double elA, double azA, double elB, double azB)
        {
            var pa = Cast(elA, azA, f);
            var pb = Cast(elB, azB, f);
            var pole = Math.Abs(Math.Abs(elA) - Math.PI / 2) < 1e-12 || Math.Abs(Math.Abs(elB) - Math.PI / 2) < 1e-12;
            for (var i = 1; i < 40; i++)
            {
                var t = i / 40.0;
                double x = pa.X + (pb.X - pa.X) * t, y = pa.Y + (pb.Y - pa.Y) * t, z = pa.Z + (pb.Z - pa.Z) * t;
                var len = Math.Sqrt(x * x + y * y + z * z);
                var trueEl = Math.Asin(y / len);
                var drawnEl = elA + (elB - elA) * t;
                worstV = Math.Max(worstV, Math.Abs(drawnEl - trueEl) / Deg);
                if (pole) continue;
                var trueAz = Math.Atan2(z, x);
                var drawnAz = azA + (azB - azA) * t;
                worstH = Math.Max(worstH, Math.Abs(drawnAz - trueAz) * Math.Cos(trueEl) / Deg);
            }
        }
        for (var r = 0; r < els.Length; r++)
        {
            if (Math.Abs(Math.Abs(els[r]) - Math.PI / 2) > 1e-12) Edge(els[r], Az0, els[r], Az1);   // ring edge
            if (r + 1 >= els.Length) continue;
            Edge(els[r], Az0, els[r + 1], Az0);   // column edge
            Edge(els[r], Az0, els[r + 1], Az1);   // diagonal (a → a + cols + 1)
        }
        return (worstV, worstH);
    }

    [Theory] // [AC-half-dome-3] [EDGE-half-dome-1] [EDGE-half-dome-2]
    [InlineData(6.25e-6)]
    [InlineData(8e-5)]
    [InlineData(0.0064)]
    [InlineData(0.05)]
    [InlineData(0.145)]
    [InlineData(0.16)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(0.99)]
    public void InterpolationError_WithinBounds(double f)
    {
        var (v, h) = WorstError(f);
        Assert.True(v <= (f <= 0.9 ? 0.045 : 0.11), $"vertical error {v:0.0000}° at f {f}");
        Assert.True(h <= 0.25, $"horizontal error {h:0.0000}° at f {f}");
    }

    [Fact] // [AC-half-dome-3] [EDGE-half-dome-1] [EDGE-half-dome-2] [EDGE-half-dome-6]
    public void DenseSweep_RowsMonotonicHalfVAndBounds()
    {
        for (var i = 0; i < 200; i++)
        {
            var f = Math.Exp(Math.Log(6.25e-6) + (Math.Log(0.99) - Math.Log(6.25e-6)) * i / 199);
            var els = DomeShape.RingElevations(f);
            Assert.True(els.Length <= DomeAvfxBuilder.MaxVertices / 97, $"{els.Length} rows at f {f}");
            Assert.Equal(Math.PI / 2, els[0]);
            Assert.Equal(-Math.PI / 2, els[^1]);
            for (var r = 1; r < els.Length; r++)
            {
                Assert.True(els[r] < els[r - 1], $"rows not decreasing at f {f}, row {r}");
                Assert.True((Half)(-0.5 + (Math.PI / 2 - els[r]) / Math.PI) > (Half)(-0.5 + (Math.PI / 2 - els[r - 1]) / Math.PI),
                            $"Half v not increasing at f {f}, row {r}");
            }
            var (v, h) = WorstError(f);
            Assert.True(v <= (f <= 0.9 ? 0.045 : 0.11), $"vertical error {v:0.0000}° at f {f}");
            Assert.True(h <= 0.25, $"horizontal error {h:0.0000}° at f {f}");
        }
    }

    [Fact] // [AC-half-dome-3]
    public void KnownCounts()
    {
        Assert.Equal(102, DomeShape.RingElevations(0.16).Length);
        Assert.Equal(9894, SphereMesh.BuildProjected(0.16f).Vertices.Length);
        Assert.Equal(130, DomeShape.RingElevations(6.25e-6).Length);
        Assert.Equal(12610, SphereMesh.BuildProjected(6.25e-6f).Vertices.Length);
    }

    // ---------------------------------------------------------------- AC-4: the reference's seam, U, winding

    [Fact] // [AC-half-dome-4]
    public void Projected_KeepsSeamUDirectionWinding()
    {
        var mesh = SphereMesh.BuildProjected(0.16f);
        Assert.Equal(new ushort[] { 0, 1, 98, 98, 97, 0 }, mesh.Indices[..6]);
        var els = DomeShape.RingElevations(0.16);
        for (var r = 0; r < els.Length; r++)
            for (var c = 0; c < 97; c++)
            {
                var v = mesh.Vertices[r * 97 + c];
                Assert.Equal(-0.5 + c / 96.0, (double)v.U, 3);
                var az = (90 + 360.0 * c / 96) * Deg;
                var (dx, dy, dz) = Dir(els[r], az);
                Assert.InRange(Math.Abs(v.NormalX - 128 + dx * 128), 0, 1.01);   // normal = −direction
                Assert.InRange(Math.Abs(v.NormalY - 128 + dy * 128), 0, 1.01);
                Assert.InRange(Math.Abs(v.NormalZ - 128 + dz * 128), 0, 1.01);
                Assert.Equal(SphereMesh.EncodeBiased(Math.Abs(Math.Sin(az)) < 1e-9 ? 0 : -Math.Sin(az)), v.TangentX);
                Assert.Equal(SphereMesh.EncodeBiased(Math.Abs(Math.Cos(az)) < 1e-9 ? 0 : Math.Cos(az)), v.TangentZ);
            }
        // Every non-degenerate triangle faces the origin, the reference's winding sense.
        var reference = SphereMesh.Build();
        static double Facing(SphereMesh m, int i)
        {
            Vector3 P(int k) => new((float)m.Vertices[m.Indices[k]].X, (float)m.Vertices[m.Indices[k]].Y, (float)m.Vertices[m.Indices[k]].Z);
            var (a, b, c) = (P(i), P(i + 1), P(i + 2));
            var n = Vector3.Cross(b - a, c - a);
            return n.LengthSquared() < 1e-12 ? double.NaN : Vector3.Dot(n, (a + b + c) / 3);
        }
        var refSign = Math.Sign(Facing(reference, 6 * 96 * 10));   // a band away from the pole
        Assert.NotEqual(0, refSign);
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var s = Facing(mesh, i);
            if (!double.IsNaN(s)) Assert.Equal(refSign, Math.Sign(s));
        }
    }

    // ---------------------------------------------------------------- AC-5: the exit distance that bounds lights

    [Theory] // [AC-half-dome-5]
    [InlineData(10f, 1f)]
    [InlineData(3f, 1f)]
    [InlineData(5f, 0.5f)]
    [InlineData(11f, 2f)]
    public void ExitDistance_Analytic(float r, float vs)
    {
        var anchor = new Vector3(0, 1, 0);
        Assert.Equal(r * vs - 1, DomeShape.ExitDistance(anchor, Vector3.UnitY, r, vs), 3);
        Assert.Equal(r * MathF.Sqrt(1 - 1 / (r * vs * r * vs)), DomeShape.ExitDistance(anchor, Vector3.UnitX, r, vs), 3);
        Assert.Equal(r * vs + 1, DomeShape.ExitDistance(anchor, -Vector3.UnitY, r, vs), 3);
        Assert.Equal(0f, DomeShape.ExitDistance(anchor, Vector3.UnitX, 1f, 1f));      // anchor on the wall
        Assert.Equal(0f, DomeShape.ExitDistance(anchor, Vector3.UnitX, 0.5f, 1f));    // anchor outside
    }

    // ---------------------------------------------------------------- edges

    [Fact] // [EDGE-half-dome-1]
    public void Projected_TinyFloor_Finite()
    {
        const float f = 6.25e-6f;
        var mesh = SphereMesh.BuildProjected(f);
        Assert.All(mesh.Vertices, v => Assert.True(Half.IsFinite(v.X) && Half.IsFinite(v.Y) && Half.IsFinite(v.Z)));
        Assert.Equal((Half)(-f), mesh.Vertices[^1].Y);
    }

    [Fact] // [EDGE-half-dome-7]
    public void Projected_NadirIsAPole()
    {
        const float f = 0.16f;
        var mesh = SphereMesh.BuildProjected(f);
        var last = mesh.Vertices[^97..];
        Assert.All(last, v => Assert.Equal(((Half)0, (Half)(-f), (Half)0), (v.X, v.Y, v.Z)));
        Assert.Equal(0.5, (double)last[0].V, 3);
        for (var c = 0; c < 97; c++) Assert.Equal(-0.5 + c / 96.0, (double)last[c].U, 3);
    }

    [Theory] // [EDGE-half-dome-8]
    [InlineData(true)]
    [InlineData(false)]
    public void SeamColumnsShareAPosition(bool imageFloor)
    {
        var mesh = imageFloor ? SphereMesh.BuildProjected(0.16f) : SphereMesh.BuildOpenBottom();
        for (var r = 0; r < mesh.Vertices.Length / 97; r++)
        {
            var a = mesh.Vertices[r * 97];
            var b = mesh.Vertices[r * 97 + 96];
            Assert.InRange(Math.Abs((double)a.X - (double)b.X) + Math.Abs((double)a.Y - (double)b.Y) + Math.Abs((double)a.Z - (double)b.Z), 0, 2e-3);
            Assert.Equal(-0.5, (double)a.U, 3);
            Assert.Equal(0.5, (double)b.U, 3);
        }
    }

    [Fact] // [EDGE-half-dome-21]
    public void RingCap_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => DomeShape.RingElevations(0.16, maxIterations: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => DomeShape.RingElevations(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => DomeShape.RingElevations(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SphereMesh.For((DomeMeshKind)7, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => SphereMesh.For(DomeMeshKind.ImageFloor, null));
    }
}
