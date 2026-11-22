using System;
using System.Collections.Generic;
using System.Numerics;
using Milieu.GameFiles;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

/// <summary>Light directions and containment for the half dome (docs/adr/0015, docs/adr/0011 distance rule).</summary>
public class HalfDomeLightTests
{
    private static readonly DomeView Default = new(0, false, 1, 2, 1);
    private static Vector3 Anchor => new(0, LightRigLayout.AnchorHeight, 0);

    /// <summary>A key in the upper sky plus regions at t 0.3, 0.35 (above the horizon) and 0.7, 0.75 (below).</summary>
    private static LightRig Rig(RigLight? key = null) =>
        new(LightExtractor.Version, key ?? new RigLight(0.5f, 0.25f, new Vector3(3, 2, 1)),
            [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
             new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
            new Vector3(10, 10, 10));

    private static Vector3 World(Vector3 mesh, DomeView view) =>
        Vector3.Normalize(Vector3.Transform(new Vector3(mesh.X, mesh.Y * view.VerticalScale, mesh.Z),
                                            Quaternion.CreateFromYawPitchRoll(view.YawRadians, 0, 0)));

    private static IEnumerable<Vector3> Directions(bool belowOnly = false)
    {
        var step = belowOnly ? 0.5 : 2.5;
        for (var el = -89.5; el <= (belowOnly ? -0.01 : 89.5); el += step)
            for (var az = 0.0; az < 360; az += 30)
            {
                double e = el * Math.PI / 180, a = az * Math.PI / 180;
                yield return new Vector3((float)(Math.Cos(e) * Math.Cos(a)), (float)Math.Sin(e), (float)(Math.Cos(e) * Math.Sin(a)));
            }
    }

    // ---------------------------------------------------------------- AC-5: directions

    public static TheoryData<int, float, bool, float> ProjectedViews()
    {
        var data = new TheoryData<int, float, bool, float>();
        foreach (var mesh in new[] { 0, 1, 2 })   // image floor at 0.16, image floor at 8e-5, open bottom
            foreach (var (yaw, flip, vs) in new[] { (0f, false, 1f), (90f, false, 1f), (0f, true, 1f), (0f, false, 0.5f), (37f, true, 2f) })
                data.Add(mesh, yaw, flip, vs);
        return data;
    }

    [Theory] // [AC-half-dome-5] [EDGE-half-dome-5]
    [MemberData(nameof(ProjectedViews))]
    public void MatchesProjectedMeshVertices(int meshIndex, float yawDeg, bool flip, float vs)
    {
        var open = meshIndex == 2;
        var view = new DomeView(yawDeg * MathF.PI / 180f, flip, vs, 2, 1, open);
        var mesh = meshIndex switch { 0 => SphereMesh.BuildProjected(0.16f), 1 => SphereMesh.BuildProjected(8e-5f), _ => SphereMesh.BuildOpenBottom() };
        foreach (var vert in mesh.Vertices)
        {
            var p = new Vector3((float)vert.X, (float)vert.Y, (float)vert.Z);
            if (p.Length() < 1e-3f) continue;   // under the feet: no usable direction at Half precision
            var (s, t) = DomeProjection.TexelOf((float)vert.U, (float)vert.V, view);
            var dir = DomeProjection.WorldDirection(s, t, view);
            Assert.True(dir.HasValue, $"vertex uv ({(float)vert.U}, {(float)vert.V}) has a drawn copy");
            var expected = World(Vector3.Normalize(p), view);
            Assert.True(Vector3.Distance(expected, dir!.Value) < 2e-3f, $"uv ({(float)vert.U}, {(float)vert.V}) → {dir.Value}, mesh {expected}");
        }
    }

    // ---------------------------------------------------------------- AC-5: containment

    [Theory] // [AC-half-dome-5] [EDGE-half-dome-5]
    [InlineData(3f, 0.5f)] [InlineData(3f, 1f)] [InlineData(3f, 2f)]
    [InlineData(5f, 0.5f)] [InlineData(5f, 1f)] [InlineData(5f, 2f)]
    [InlineData(10f, 0.5f)] [InlineData(10f, 1f)] [InlineData(10f, 2f)]
    [InlineData(11f, 1f)] [InlineData(33f, 1f)] [InlineData(33f, 2f)]
    public void HalfDome_LightsInsideWall(float r, float vs)
    {
        var bounds = new DomeBounds(r, vs);
        foreach (var nominal in new[] { VfxLightLayout.KeyDistance, VfxLightLayout.FillDistance })
            foreach (var dir in Directions())
            {
                var d = VfxLightPlanner.Distance(nominal, r, Anchor, dir, bounds);
                var exit = DomeShape.ExitDistance(Anchor, dir, r, vs);
                if (VfxLightLayout.RoomDistanceFraction * exit >= VfxLightLayout.MinDistance)
                    Assert.True(d <= VfxLightLayout.RoomDistanceFraction * exit + 1e-4f, $"r {r} vs {vs} dir {dir}: {d} beyond the wall ({exit})");
                Assert.InRange(d, VfxLightLayout.MinDistance, nominal);
            }
        // Directions stay those of the image; only distances change.
        var view = new DomeView(0, false, vs, 2, 1);
        var bounded = VfxLightPlanner.Plan(Rig(), view, r, 1f, 3, fillsOnOwnEmitters: true, bounds: bounds);
        var free = VfxLightPlanner.Plan(Rig(), view, r, 1f, 3, fillsOnOwnEmitters: true);
        Assert.Equal(free.Lights.Count, bounded.Lights.Count);
        for (var i = 0; i < free.Lights.Count; i++)
            Assert.True(Vector3.Distance(Vector3.Normalize(free.Lights[i].Position - Anchor), Vector3.Normalize(bounded.Lights[i].Position - Anchor)) < 1e-4f);
    }

    [Theory] // [EDGE-half-dome-14]
    [InlineData(3f)] [InlineData(5f)] [InlineData(10f)] [InlineData(11f)] [InlineData(33f)]
    public void HalfDome_BelowHorizonFarFromWall(float r)
    {
        var bounds = new DomeBounds(r, 1f);
        foreach (var nominal in new[] { VfxLightLayout.KeyDistance, VfxLightLayout.FillDistance })
            foreach (var dir in Directions(belowOnly: true))
            {
                var d = VfxLightPlanner.Distance(nominal, r, Anchor, dir, bounds);
                Assert.True(d >= MathF.Min(nominal, 0.9f * MathF.Sqrt(r * r - 1)) - 1e-3f, $"r {r} dir {dir}: {d}");
                if (-dir.Y >= 1 / (2 * r)) Assert.True(d >= VfxLightPlanner.Distance(nominal, r) - 1e-3f, $"r {r} dir {dir}: {d} below today's");
            }
    }

    [Theory] // [EDGE-half-dome-14]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void HalfDome_SmallDomeUsesMinimum(float r)
    {
        var bounds = new DomeBounds(r, 1f);
        Assert.Equal(VfxLightLayout.MinDistance, VfxLightPlanner.Distance(VfxLightLayout.KeyDistance, r, Anchor, Vector3.UnitY, bounds));
        Assert.Equal(VfxLightLayout.MinDistance, VfxLightPlanner.Distance(VfxLightLayout.FillDistance, r, Anchor, Vector3.UnitY, bounds));
    }

    [Fact] // [EDGE-half-dome-14]
    public void NoBounds_Unchanged()
    {
        foreach (var r in new[] { 3f, 10f, 200f, 20000f })
            foreach (var dir in Directions())
                Assert.Equal(VfxLightPlanner.Distance(VfxLightLayout.KeyDistance, r), VfxLightPlanner.Distance(VfxLightLayout.KeyDistance, r, Anchor, dir, null));
        var a = VfxLightPlanner.Plan(Rig(), Default, 10f, 1f, 3, fillsOnOwnEmitters: true);
        var b = VfxLightPlanner.Plan(Rig(), Default, 10f, 1f, 3, fillsOnOwnEmitters: true, bounds: null);
        Assert.Equal(a.Lights, b.Lights);
    }

    [Theory] // [EDGE-half-dome-14]
    [InlineData(200f, 0.25f)] [InlineData(200f, 1f)] [InlineData(200f, 2f)]
    [InlineData(20000f, 0.25f)] [InlineData(20000f, 1f)] [InlineData(20000f, 2f)]
    public void Sky_BoundsDontChangeDistances(float r, float vs)
    {
        var bounds = new DomeBounds(r, vs);
        foreach (var nominal in new[] { VfxLightLayout.KeyDistance, VfxLightLayout.FillDistance })
            foreach (var dir in Directions())
                Assert.Equal(VfxLightPlanner.Distance(nominal, r), VfxLightPlanner.Distance(nominal, r, Anchor, dir, bounds));
        var view = new DomeView(0, false, vs, 2, 1);
        Assert.Equal(VfxLightPlanner.Plan(Rig(), view, r, 1f, 3, fillsOnOwnEmitters: true).Lights,
                     VfxLightPlanner.Plan(Rig(), view, r, 1f, 3, fillsOnOwnEmitters: true, bounds: bounds).Lights);
    }

    [Theory] // [EDGE-half-dome-14]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalLights_EqualSyncedShiftedByCentreHeight(bool open)
    {
        var bounds = new DomeBounds(10f, 1f);
        var centre = open ? 0f : 1.6f;
        var synced = VfxLightPlanner.Plan(Rig(), new DomeView(0, false, 1, 2, 1, open), 10f, 1f, 3, fillsOnOwnEmitters: true, bounds: bounds);
        var local = VfxLightPlanner.PlanLocal(Rig(), false, 1, 2, 1, 10f, centre, 1f, 3, fillsOnOwnEmitters: true, bounds: bounds, lowerHalfHidden: open);
        Assert.NotEmpty(local.Lights);
        Assert.Equal(synced.Lights.Count, local.Lights.Count);
        for (var i = 0; i < synced.Lights.Count; i++)
            Assert.True(Vector3.Distance(synced.Lights[i].Position - new Vector3(0, centre, 0), local.Lights[i].Position) < 1e-5f);
    }

    // ---------------------------------------------------------------- EDGE-18: the open bottom's undrawn lower half

    [Fact] // [EDGE-half-dome-18]
    public void OpenBottom_LowerHalfNotDrawn()
    {
        var open = Default with { LowerHalfHidden = true };
        for (var s = 0.3f; s < 0.7f; s += 0.05f)
        {
            Assert.Null(DomeProjection.WorldDirection(s, 0.75f, open));
            Assert.NotNull(DomeProjection.WorldDirection(s, 0.75f, Default));
            Assert.Equal(DomeProjection.WorldDirection(s, 0.25f, Default), DomeProjection.WorldDirection(s, 0.25f, open));
            Assert.Equal(DomeProjection.WorldDirection(s, 0.5f, Default), DomeProjection.WorldDirection(s, 0.5f, open));   // the horizon is drawn
        }
    }

    [Fact] // [EDGE-half-dome-18]
    public void OpenBottom_SkipsLightsBelowHorizon()
    {
        var open = Default with { LowerHalfHidden = true };
        var anchor = new Vector3(10, 21, 30);
        var below = LightRigPlanner.Plan(Rig(new RigLight(0.5f, 0.75f, new Vector3(3, 2, 1))), open, anchor, 1f, 4);
        Assert.False(below.KeyDrawn);   // a sun below the open bottom's horizon isn't drawn, so it isn't lit
        var upper = LightRigPlanner.Plan(Rig(), open, anchor, 1f, 4);
        Assert.True(upper.KeyDrawn);
        Assert.True(upper.FillCount < LightRigPlanner.Plan(Rig(), Default, anchor, 1f, 4).FillCount);   // t 0.7 and 0.75 are undrawn
        var vfx = VfxLightPlanner.Plan(Rig(new RigLight(0.5f, 0.75f, new Vector3(3, 2, 1))), open, 10f, 1f, 0);
        Assert.False(vfx.HasKey);
    }

    // ---------------------------------------------------------------- the open bottom at camera height (amendment 2)

    [Fact] // [AC-half-dome-12] [EDGE-half-dome-18]
    public void OpenBottom_Cut_DrawsDownToTheFloor()
    {
        var cutV = DomeShape.OpenCutV(0.16f);
        Assert.Equal(Math.Asin(0.16) / Math.PI, cutV, 5);
        Assert.Equal(0f, DomeShape.OpenCutV(null));
        var open = Default with { LowerHalfHidden = true, CutV = cutV };
        var (s, above) = DomeProjection.TexelOf(0f, cutV - 0.01f, open);
        var (_, below) = DomeProjection.TexelOf(0f, cutV + 0.01f, open);
        Assert.NotNull(DomeProjection.WorldDirection(s, above, open));   // just below the horizon, above the floor: drawn
        Assert.Null(DomeProjection.WorldDirection(s, above, Default with { LowerHalfHidden = true }));   // uncut: not drawn
        Assert.Null(DomeProjection.WorldDirection(s, below, open));   // below the floor: not drawn
    }

    [Fact] // [AC-half-dome-12] [EDGE-half-dome-14]
    public void OpenBottom_WallCentredAtCameraHeight()
    {
        var anchor = new Vector3(0, 1.6f, 0);
        var dir = new Vector3(1, 0, 0);
        Assert.Equal(9f, VfxLightPlanner.Distance(100f, 10f, anchor, dir, new DomeBounds(10f, 1f, 1.6f)), 4);   // level with the centre: 0.9 × R
        Assert.Equal(0.9f * MathF.Sqrt(100f - 1.6f * 1.6f), VfxLightPlanner.Distance(100f, 10f, anchor, dir, new DomeBounds(10f, 1f)), 3);
    }
}
