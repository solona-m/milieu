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
    // ---- Estimated rigs (docs/adr/0010) ----

    private static readonly RigLight[] FlatRegions =
        [new(0.3f, 0.3f, new Vector3(1, 1, 1)), new(0.7f, 0.35f, new Vector3(1, 1, 1)), new(0.4f, 0.7f, new Vector3(1, 1, 1)), new(0.8f, 0.75f, new Vector3(1, 1, 1))];

    private static readonly Vector3 WarmKey = new Vector3(1.2f, 1f, 0.6f) / LightRig.Luminance(new Vector3(1.2f, 1f, 0.6f));

    /// <summary>An estimated rig: key colour (luminance 1), two lamps at weights 0.5 and 0.25, regions summing to Y 4.</summary>
    private static LightRig Estimated(RigLight[]? lamps = null, RigLight? key = null, RigLight[]? regions = null)
    {
        regions ??= FlatRegions;
        return new LightRig(LightExtractor.Version, key ?? new RigLight(0.5f, 0.25f, WarmKey),
                            regions, regions.Aggregate(Vector3.Zero, (a, r) => a + r.Energy),
                            lamps ?? [new(0.45f, 0.4f, new Vector3(0.5f, 0.5f, 0.5f)), new(0.55f, 0.45f, new Vector3(0.25f, 0.25f, 0.25f))],
                            LdrLightEstimator.Version);
    }

    [Fact] // [AC-ldr-light-estimate-3]
    public void LampsTakeFillSlotsFirst_WithLampPower()
    {
        var rig = Estimated();
        var resolved = LightRigPlanner.Resolve(rig);
        var plan = LightRigPlanner.Plan(rig, Default, Anchor, 1f, 3);
        Assert.Equal(["key", "fill0", "fill1", "fill2"], plan.Lights.Select(l => l.Slot));
        var totalY = LightRig.Luminance(resolved.TotalEnergy);
        for (var i = 0; i < 2; i++)
        {
            var lamp = rig.Lamps[i];
            var dir = DomeProjection.WorldDirection(lamp.S, lamp.T, Default)!.Value;
            var (_, p) = plan.Lights[1 + i];
            Assert.Equal(LightShapeKind.Point, p.Shape);
            Assert.False(p.CharacterShadows);
            Assert.True(Vector3.Distance(Anchor + dir * LightRigLayout.LightDistance, p.Position) < 1e-4f, $"lamp {i} in its direction");
            Assert.True(Vector3.Distance(resolved.Lamps[i].Energy / totalY * LightRigLayout.LampPower, p.Colour) < 1e-3f, $"lamp {i} colour");
        }
        // The third fill slot is the top region, at FillPower.
        var r0 = DomeProjection.WorldDirection(0.3f, 0.3f, Default)!.Value;
        Assert.True(Vector3.Distance(Anchor + r0 * LightRigLayout.LightDistance, plan.Lights[3].P.Position) < 1e-4f);
        Assert.True(Vector3.Distance(resolved.Regions[0].Energy / totalY * LightRigLayout.FillPower, plan.Lights[3].P.Colour) < 1e-3f);
        Assert.True(Vector3.Distance(resolved.Key!.Energy / totalY * LightRigLayout.KeyPower, plan.Lights[0].P.Colour) < 1e-3f);
    }

    [Fact] // [AC-ldr-light-estimate-7]
    public void HdrRigWithoutLamps_PlanUnchanged()
    {
        var rig = Rig();
        Assert.False(rig.IsEstimated);
        Assert.Empty(rig.Lamps);
        Assert.Same(rig, LightRigPlanner.Resolve(rig));
        var plan = LightRigPlanner.Plan(rig, Default, Anchor, 1f, 3);
        Assert.Equal(["key", "fill0", "fill1", "fill2"], plan.Lights.Select(l => l.Slot));
        Assert.True(Vector3.Distance(new Vector3(3, 2, 1) / 10f * LightRigLayout.KeyPower, plan.Lights[0].P.Colour) < 1e-4f);
        Assert.True(Vector3.Distance(new Vector3(0.5f, 0.6f, 1f) / 10f * LightRigLayout.FillPower, plan.Lights[1].P.Colour) < 1e-4f);
        Assert.Equal(plan.Lights, LightRigPlanner.Plan(rig, Default, Anchor, 1f, 3).Lights);
    }

    [Fact] // [EDGE-ldr-light-estimate-17]
    public void Lamps_FillCountZero_KeyOnly()
    {
        Assert.Equal(["key"], LightRigPlanner.Plan(Estimated(), Default, Anchor, 1f, 0).Lights.Select(l => l.Slot));
    }

    [Fact] // [EDGE-ldr-light-estimate-17]
    public void FillCountOne_FirstLampOnly()
    {
        var rig = Estimated();
        var plan = LightRigPlanner.Plan(rig, Default, Anchor, 1f, 1);
        Assert.Equal(["key", "fill0"], plan.Lights.Select(l => l.Slot));
        var dir = DomeProjection.WorldDirection(rig.Lamps[0].S, rig.Lamps[0].T, Default)!.Value;
        Assert.True(Vector3.Distance(Anchor + dir * LightRigLayout.LightDistance, plan.Lights[1].P.Position) < 1e-4f);
    }

    [Fact] // [EDGE-ldr-light-estimate-17]
    public void WithLamps_NeverExceedsMaxLights()
    {
        var three = Estimated(lamps: [new(0.45f, 0.4f, Vector3.One), new(0.55f, 0.45f, Vector3.One), new(0.5f, 0.3f, Vector3.One)]);
        foreach (var n in new[] { -5, 0, 1, 2, 3, 4, 10, int.MaxValue })
        {
            var plan = LightRigPlanner.Plan(three, Default, Anchor, 2f, n);
            Assert.InRange(plan.Lights.Count, 0, LightRigLayout.MaxLights);
            Assert.Equal(plan.Lights.Count, plan.Lights.Select(l => l.Slot).Distinct().Count());
        }
    }

    [Fact] // [EDGE-ldr-light-estimate-18]
    public void LampNotDrawn_NextCandidateTakesSlot()
    {
        // U repeat 0.5 draws only s in [0.375, 0.625]: the first lamp (s = 0.1) has no drawn copy.
        var view = Default with { URepeat = 0.5f };
        var rig = Estimated(lamps: [new(0.1f, 0.4f, Vector3.One), new(0.5f, 0.45f, Vector3.One * 0.5f)],
                            regions: [new(0.45f, 0.3f, Vector3.One), new(0.9f, 0.3f, Vector3.One), new(0.55f, 0.6f, Vector3.One), new(0.5f, 0.7f, Vector3.One)]);
        var plan = LightRigPlanner.Plan(rig, view, Anchor, 1f, 2);
        Assert.Equal(["key", "fill0", "fill1"], plan.Lights.Select(l => l.Slot));
        var lamp1 = DomeProjection.WorldDirection(0.5f, 0.45f, view)!.Value;
        var region0 = DomeProjection.WorldDirection(0.45f, 0.3f, view)!.Value;
        Assert.True(Vector3.Distance(Anchor + lamp1 * LightRigLayout.LightDistance, plan.Lights[1].P.Position) < 1e-4f, "the second lamp");
        Assert.True(Vector3.Distance(Anchor + region0 * LightRigLayout.LightDistance, plan.Lights[2].P.Position) < 1e-4f, "then the top region");
    }

    [Fact] // [EDGE-ldr-light-estimate-32]
    public void Resolve_AppliesBrightnessAtPlanTime_RigUnchanged()
    {
        var rig = Estimated();
        var json = LightRigStore.Serialise(rig);
        var resolved = LightRigPlanner.Resolve(rig);
        var k = LightRigLayout.EstimatedKeyOverSky * Math.Max(4f, LightRigLayout.EstimatedSkyFloorY);
        Assert.True(Vector3.Distance(rig.Key!.Energy * k, resolved.Key!.Energy) < 1e-4f);
        Assert.True(Vector3.Distance(rig.Lamps[1].Energy * k, resolved.Lamps[1].Energy) < 1e-4f);
        Assert.Equal(rig.Regions, resolved.Regions);
        var all = resolved.Regions.Concat(resolved.Lamps).Aggregate(resolved.Key.Energy, (a, r) => a + r.Energy);
        Assert.True(Vector3.Distance(all, resolved.TotalEnergy) <= 1e-5f * resolved.TotalEnergy.Length());
        // The persisted rig is untouched: the brightness lives only in the plan.
        Assert.Equal(json, LightRigStore.Serialise(rig));
        Assert.Equal(rig.Key, Estimated().Key);
    }

    [Fact] // [EDGE-ldr-light-estimate-40]
    public void BlackBackgroundOneLamp_FloorKeepsKey()
    {
        var zero = Enumerable.Repeat(new RigLight(0.5f, 0.5f, Vector3.Zero), 4).ToArray();
        var rig = Estimated(lamps: [], regions: zero);
        Assert.Equal(Vector3.Zero, rig.TotalEnergy);
        var plan = LightRigPlanner.Plan(rig, Default, Anchor, 1f, 2);
        var (slot, key) = plan.Lights[0];
        Assert.Equal("key", slot);
        Assert.True(key.Colour.Length() > 0, "the floor keeps a non-zero key");
        Assert.True(plan.KeyDrawn);
    }
}
