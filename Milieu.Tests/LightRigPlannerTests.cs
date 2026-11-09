using System;
using System.Linq;
using System.Numerics;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

public class LightRigPlannerTests
{
    private static readonly DomeView Default = new(0, false, 1, 2, 1);
    private static readonly Vector3 Anchor = new(10, 21, 30);

    /// <summary>A key in the upper sky plus four regions spread around the image (all drawn at default repeats).</summary>
    private static LightRig Rig(RigLight? key = null, RigLight[]? regions = null) =>
        new(LightExtractor.Version, key ?? new RigLight(0.5f, 0.25f, new Vector3(3, 2, 1)),
            regions ?? [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
                        new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
            new Vector3(10, 10, 10));

    private static Vector3 Forward(Quaternion q) => Vector3.Transform(LightRigLayout.SpotForward, q);

    [Fact] // [AC-scene-lighting-1]
    public void KeyIsSpotAimedAtAnchor()
    {
        var rig = Rig();
        var plan = LightRigPlanner.Plan(rig, Default, Anchor, 1f, 2);
        Assert.True(plan.RigHasKey && plan.KeyDrawn);
        var (slot, key) = plan.Lights[0];
        Assert.Equal("key", slot);
        Assert.Equal(LightShapeKind.Spot, key.Shape);
        var dir = DomeProjection.WorldDirection(0.5f, 0.25f, Default)!.Value;
        Assert.True(Vector3.Distance(Anchor + dir * LightRigLayout.LightDistance, key.Position) < 1e-4f);
        Assert.True(Vector3.Distance(-dir, Forward(key.Rotation)) < 1e-4f, "aimed at the anchor");
        Assert.True(key.CharacterShadows);
        Assert.Equal(LightRigLayout.KeyConeDegrees, key.SpotAngleDegrees);
        // colour = E / Y_total × KeyPower × strength × 1
        Assert.True(Vector3.Distance(new Vector3(3, 2, 1) / 10f * LightRigLayout.KeyPower, key.Colour) < 1e-3f);

        var fills = plan.Lights.Skip(1).ToList();
        Assert.Equal(["fill0", "fill1"], fills.Select(f => f.Slot));
        Assert.All(fills, f =>
        {
            Assert.Equal(LightShapeKind.Point, f.P.Shape);
            Assert.False(f.P.CharacterShadows);
            Assert.Equal(LightRigLayout.LightDistance, Vector3.Distance(Anchor, f.P.Position), 1e-3f);
        });
        // Fills follow the region ranking: region 0 first, coloured by its energy.
        var r0 = DomeProjection.WorldDirection(0.3f, 0.3f, Default)!.Value;
        Assert.True(Vector3.Distance(Anchor + r0 * LightRigLayout.LightDistance, fills[0].P.Position) < 1e-4f);
        Assert.True(Vector3.Distance(new Vector3(0.5f, 0.6f, 1f) / 10f * LightRigLayout.FillPower, fills[0].P.Colour) < 1e-3f);
        Assert.Equal(2, plan.FillCount);
    }

    [Fact] // [EDGE-scene-lighting-2]
    public void RotationFacing_StraightDownAndUp_Stable()
    {
        foreach (var f in new[]
                 {
                     -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX,
                     Vector3.Normalize(new Vector3(1e-7f, -1, 0)), Vector3.Normalize(new Vector3(0, 1, -1e-7f)),
                     Vector3.Normalize(new Vector3(1e-7f, 0, -1)), Vector3.Normalize(new Vector3(0.3f, -0.9f, 0.2f)),
                 })
        {
            var q = LightRigPlanner.RotationFacing(f);
            Assert.True(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W));
            Assert.Equal(1f, q.Length(), 1e-4f);
            Assert.True(Vector3.Distance(f, Forward(q)) < 1e-3f, $"facing {f}");
        }
    }

    [Fact] // [EDGE-scene-lighting-4]
    public void ZeroTotal_NoLights()
    {
        var black = new LightRig(LightExtractor.Version, null, Enumerable.Repeat(new RigLight(0.5f, 0.5f, Vector3.Zero), 4).ToArray(), Vector3.Zero);
        var plan = LightRigPlanner.Plan(black, Default, Anchor, 1f, 3);
        Assert.Empty(plan.Lights);
        Assert.False(plan.RigHasKey);
    }

    [Fact] // [EDGE-scene-lighting-9]
    public void FillCountZero_KeyOnly()
    {
        var plan = LightRigPlanner.Plan(Rig(), Default, Anchor, 1f, 0);
        Assert.Equal(["key"], plan.Lights.Select(l => l.Slot));
    }

    [Fact] // [EDGE-scene-lighting-9]
    public void FillCountThree()
    {
        var plan = LightRigPlanner.Plan(Rig(), Default, Anchor, 1f, 3);
        Assert.Equal(["key", "fill0", "fill1", "fill2"], plan.Lights.Select(l => l.Slot));
        // The fourth (lowest-ranked) region is discarded.
        var r3 = DomeProjection.WorldDirection(0.8f, 0.75f, Default)!.Value;
        Assert.DoesNotContain(plan.Lights, l => Vector3.Distance(l.P.Position, Anchor + r3 * LightRigLayout.LightDistance) < 1e-3f);
    }

    [Fact] // [EDGE-scene-lighting-10]
    public void StrengthZero_NoLights()
    {
        var plan = LightRigPlanner.Plan(Rig(), Default, Anchor, 0f, 2);
        Assert.Empty(plan.Lights);
        Assert.True(plan.RigHasKey);
    }

    [Fact] // [EDGE-scene-lighting-10]
    public void StrengthTwo_ClampsPreservingChroma()
    {
        // Key energy (40, 20, 10) of total Y 10 → (120, 60, 30)·2 = (240, ...) before the clamp.
        var rig = Rig(key: new RigLight(0.5f, 0.25f, new Vector3(4, 2, 1)));
        var one = LightRigPlanner.Plan(rig, Default, Anchor, 1f, 0).Lights[0].P.Colour;
        Assert.True(Vector3.Distance(new Vector3(12, 6, 3), one) < 1e-4f);   // under the cap: unchanged
        var hot = Rig(key: new RigLight(0.5f, 0.25f, new Vector3(40, 20, 10)));
        var c = LightRigPlanner.Plan(hot, Default, Anchor, 2f, 0).Lights[0].P.Colour;
        Assert.Equal(LightRigLayout.MaxChannel, Math.Max(c.X, Math.Max(c.Y, c.Z)), 1e-3f);
        Assert.Equal(2f, c.X / c.Y, 1e-4f);
        Assert.Equal(4f, c.X / c.Z, 1e-4f);
    }

    [Fact] // [EDGE-scene-lighting-36]
    public void KeyNotDrawn_DroppedAndFlagged()
    {
        // U repeat 0.5 draws only s ∈ [0.375, 0.625]: a key at s = 0.1 has no drawn copy.
        var view = Default with { URepeat = 0.5f };
        var rig = Rig(key: new RigLight(0.1f, 0.3f, new Vector3(3, 2, 1)),
                      regions: [new(0.5f, 0.3f, Vector3.One), new(0.45f, 0.6f, Vector3.One), new(0.55f, 0.4f, Vector3.One), new(0.5f, 0.8f, Vector3.One)]);
        var plan = LightRigPlanner.Plan(rig, view, Anchor, 1f, 2);
        Assert.True(plan.RigHasKey);
        Assert.False(plan.KeyDrawn);
        Assert.DoesNotContain(plan.Lights, l => l.Slot == "key");
        Assert.Equal(["fill0", "fill1"], plan.Lights.Select(l => l.Slot));
    }

    [Fact] // [EDGE-scene-lighting-36]
    public void FillNotDrawn_Skipped()
    {
        var view = Default with { URepeat = 0.5f };
        var rig = Rig(regions: [new(0.1f, 0.3f, Vector3.One * 5), new(0.5f, 0.3f, Vector3.One * 2), new(0.9f, 0.6f, Vector3.One), new(0.45f, 0.4f, Vector3.One)]);
        var plan = LightRigPlanner.Plan(rig, view, Anchor, 1f, 3);
        var fills = plan.Lights.Where(l => l.Slot != "key").ToList();
        Assert.Equal(2, fills.Count);   // regions 0 and 2 aren't on the dome
        var r1 = DomeProjection.WorldDirection(0.5f, 0.3f, view)!.Value;
        Assert.True(Vector3.Distance(Anchor + r1 * LightRigLayout.LightDistance, fills[0].P.Position) < 1e-4f);
        Assert.Equal(["fill0", "fill1"], fills.Select(f => f.Slot));
    }

    [Fact] // [EDGE-scene-lighting-40]
    public void NeverExceedsMaxLights()
    {
        foreach (var n in new[] { -5, 0, 1, 2, 3, 4, 10, int.MaxValue })
        {
            var plan = LightRigPlanner.Plan(Rig(), Default, Anchor, 2f, n);
            Assert.InRange(plan.Lights.Count, 0, LightRigLayout.MaxLights);
            Assert.Equal(plan.Lights.Count, plan.Lights.Select(l => l.Slot).Distinct().Count());
        }
        Assert.Equal(4, LightRigLayout.MaxLights);
    }

    [Fact] // [EDGE-scene-lighting-40]
    public void DistanceCompensation_OneAtSharedDistance_ScalesByFalloffPower()
    {
        foreach (var f in Enum.GetValues<LightFalloff>())
            Assert.Equal(1f, LightRigPlanner.DistanceCompensation(LightRigLayout.LightDistance, f), 1e-6f);
        var d = LightRigLayout.LightDistance * 2;
        Assert.Equal(2f, LightRigPlanner.DistanceCompensation(d, LightFalloff.Linear), 1e-5f);
        Assert.Equal(4f, LightRigPlanner.DistanceCompensation(d, LightFalloff.Quadratic), 1e-5f);
        Assert.Equal(8f, LightRigPlanner.DistanceCompensation(d, LightFalloff.Cubic), 1e-5f);
    }
}
