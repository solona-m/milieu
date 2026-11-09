using System;
using System.Numerics;
using Milieu.GameFiles;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

public class DomeProjectionTests
{
    private static DomeView View(float yawDeg = 0, bool flip = false, float vs = 1, float u = 2, float v = 1) =>
        new(yawDeg * MathF.PI / 180f, flip, vs, u, v);

    /// <summary>The world direction of a mesh vertex, computed independently: Y × vertical scale, then the VFX yaw.</summary>
    private static Vector3 World(Vector3 mesh, DomeView view) =>
        Vector3.Normalize(Vector3.Transform(new Vector3(mesh.X, mesh.Y * view.VerticalScale, mesh.Z),
                                            Quaternion.CreateFromYawPitchRoll(view.YawRadians, 0, 0)));

    public static TheoryData<float, bool, float> MeshViews => new()
    {
        { 0f, false, 1f }, { 90f, false, 1f }, { -180f, false, 1f }, { 0f, true, 1f }, { 0f, false, 0.5f }, { 37f, true, 2f },
    };

    [Theory] // [AC-scene-lighting-1]
    [MemberData(nameof(MeshViews))]
    public void MatchesSphereMeshVertices(float yawDeg, bool flip, float vs)
    {
        var view = View(yawDeg, flip, vs);
        var mesh = SphereMesh.Build();
        foreach (var vert in mesh.Vertices)
        {
            var (s, t) = DomeProjection.TexelOf((float)vert.U, (float)vert.V, view);
            var dir = DomeProjection.WorldDirection(s, t, view);
            Assert.True(dir.HasValue, $"vertex uv ({(float)vert.U}, {(float)vert.V}) has a drawn copy");
            var expected = World(new Vector3((float)vert.X, (float)vert.Y, (float)vert.Z), view);
            Assert.True(Vector3.Distance(expected, dir!.Value) < 1e-3f,
                        $"uv ({(float)vert.U}, {(float)vert.V}) → {dir.Value}, mesh {expected}");
        }
    }

    public static TheoryData<float, bool, float> Views => new()
    {
        { 180f, false, 1f }, { -180f, false, 1f }, { 0f, true, 1f }, { 0f, false, 0.25f }, { 0f, false, 2f }, { -180f, true, 0.25f }, { 180f, true, 2f },
    };

    [Theory] // [EDGE-scene-lighting-11]
    [MemberData(nameof(Views))]
    public void ViewTheory(float yawDeg, bool flip, float vs)
    {
        var view = View(yawDeg, flip, vs);
        var unyaw = Quaternion.CreateFromYawPitchRoll(-view.YawRadians, 0, 0);
        for (var s = 0.02f; s < 1f; s += 0.07f)
        for (var t = 0.03f; t < 1f; t += 0.09f)
        {
            var dir = DomeProjection.WorldDirection(s, t, view)!.Value;
            // Undo the yaw and the vertical scale, read the mesh azimuth/elevation, then the forward UV map.
            var m = Vector3.Transform(dir, unyaw);
            m = Vector3.Normalize(new Vector3(m.X, m.Y / vs, m.Z));
            var el = Math.Asin(Math.Clamp(m.Y, -1f, 1f)) * 180 / Math.PI;
            var az = Math.Atan2(m.Z, m.X) * 180 / Math.PI;
            var u = (float)(((az - 90) / 360 % 1 + 1) % 1) - 0.5f;
            var v = (float)((90 - el) / 180) - 0.5f;
            var (s2, t2) = DomeProjection.TexelOf(u, v, view);
            var ds = Math.Abs(s2 - s);
            Assert.True(Math.Min(ds, 1 - ds) < 1e-3, $"s {s} → {s2} at {view}");
            Assert.Equal(t, t2, 1e-3f);
        }
        // Flip mirrors the image left-right: the same texel lands mirrored about the dome's seam meridian.
        var a = DomeProjection.WorldDirection(0.3f, 0.4f, View())!.Value;
        var b = DomeProjection.WorldDirection(0.3f, 0.4f, View(flip: true))!.Value;
        Assert.Equal(a.Y, b.Y, 1e-4f);
        Assert.NotEqual(a, b);
    }

    public static TheoryData<bool, float, float, float, bool> DrawnCases => new()
    {
        // horizontal (U repeat): drawn s range is 0.5 ± U·0.5/2
        { true, 0.5f, 0.5f, 0.5f, true },     // centre of a 0.25-wide window
        { true, 0.5f, 0.1f, 0.5f, false },    // outside it: no drawn copy
        { true, 0.5f, 0.62f, 0.5f, true },
        { true, 1.5f, 0.05f, 0.5f, false },   // window 0.75 wide: [0.125, 0.875]
        { true, 1.5f, 0.2f, 0.5f, true },
        { true, 4f, 0.05f, 0.5f, true },      // ≥ one full wrap: every texel drawn (smallest-|u| copy)
        { true, 4f, 0.95f, 0.5f, true },
        // vertical (V repeat): only the direct copy |v| ≤ 0.5
        { false, 0.5f, 0.5f, 0.1f, false },   // t window [0.25, 0.75]
        { false, 0.5f, 0.5f, 0.3f, true },
        { false, 1.5f, 0.5f, 0.0f, true },    // direct copy at v = −1/3
        { false, 1.5f, 0.5f, 1.0f, true },
        { false, 4f, 0.5f, 0.0f, true },      // direct copy at v = −1/8
    };

    [Theory] // [EDGE-scene-lighting-36]
    [MemberData(nameof(DrawnCases))]
    public void DrawnCopyRule(bool horizontal, float repeat, float s, float t, bool drawn)
    {
        var view = horizontal ? View(u: repeat) : View(v: repeat);
        var dir = DomeProjection.WorldDirection(s, t, view);
        Assert.Equal(drawn, dir.HasValue);
        if (!drawn) return;
        // The copy used: horizontally the smallest |u| = (s − 0.5)/(U·0.5); vertically v = (t − 0.5)/V.
        var u = horizontal ? (s - 0.5f) / (repeat * 0.5f) : (s - 0.5f) / (2 * 0.5f);
        var v = horizontal ? (t - 0.5f) / 1f : (t - 0.5f) / repeat;
        Assert.InRange(u, -0.5f, 0.5f);
        var expected = World(DomeProjection.MeshDirection(u, v), view);
        Assert.True(Vector3.Distance(expected, dir!.Value) < 1e-4f);
        if (!horizontal && repeat == 4f) Assert.Equal(Math.Sin(22.5 * Math.PI / 180), dir.Value.Y, 1e-4);   // v = −1/8 → 22.5° up
    }

    [Fact] // [AC-scene-lighting-1]
    public void DefaultView_ImageTopIsZenith_CentreColumnIsSeamOpposite()
    {
        Assert.Equal(1f, DomeProjection.WorldDirection(0.5f, 0f, View())!.Value.Y, 1e-5f);
        Assert.Equal(-1f, DomeProjection.WorldDirection(0.5f, 1f, View())!.Value.Y, 1e-5f);
        // s = 0.5 is u = 0 → azimuth 270° on the mesh: (0, 0, −1) at the horizon.
        var c = DomeProjection.WorldDirection(0.5f, 0.5f, View())!.Value;
        Assert.True(Vector3.Distance(new Vector3(0, 0, -1), c) < 1e-5f);
    }
}
