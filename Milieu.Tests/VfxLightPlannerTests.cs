using System;
using System.Linq;
using System.Numerics;
using Milieu.GameFiles;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

public class VfxLightPlannerTests
{
    private static readonly DomeView Default = new(0, false, 1, 2, 1);
    private const float Sky = 20000f;

    private static LightRig Rig(RigLight? key = null, bool noKey = false) =>
        new(LightExtractor.Version, noKey ? null : key ?? new RigLight(0.5f, 0.25f, new Vector3(3, 2, 1)),
            [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
             new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
            new Vector3(10, 10, 10));

    private static Vector3 Anchor => new(0, LightRigLayout.AnchorHeight, 0);

    [Fact] // [AC-synced-lighting-1]
    public void Key_SunMatchingAtStrength1_InKeyDirection_Far()
    {
        var plan = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 2, fillsOnOwnEmitters: true);
        Assert.True(plan.HasKey);
        var key = plan.Lights[0];
        var dir = DomeProjection.WorldDirection(0.5f, 0.25f, Default)!.Value;
        Assert.True(Vector3.Distance(Anchor + dir * VfxLightLayout.KeyDistance, key.Position) < 1e-3f);
        Assert.Equal(VfxLightLayout.KeyBrightness, key.Brightness);
        Assert.True(key.Shadows);
        Assert.Equal((VfxLightLayout.KeyRange, VfxLightLayout.KeyShadowNear, VfxLightLayout.KeyShadowFar), (key.Range, key.ShadowNear, key.ShadowFar));
        Assert.True(Vector3.Distance(new Vector3(1, 2f / 3, 1f / 3), key.Rgb) < 1e-5f);   // hue, largest channel 1
    }

    [Fact] // [AC-synced-lighting-1]
    public void Fills_SoftColouredNoShadows_StrongestAtFillBrightness()
    {
        var plan = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 3, fillsOnOwnEmitters: true);
        Assert.Equal(3, plan.Fills);
        var fills = plan.Lights.Skip(1).ToList();
        Assert.All(fills, f => Assert.False(f.Shadows));
        Assert.Equal(VfxLightLayout.FillBrightness, fills.Max(f => f.Brightness), 4);
        Assert.All(fills, f => Assert.True(f.Brightness <= VfxLightLayout.FillBrightness + 1e-4f));
        Assert.All(fills, f => Assert.Equal(VfxLightLayout.FillDistance, Vector3.Distance(Anchor, f.Position), 3));
    }

    [Fact] // [AC-synced-lighting-1]
    public void SameSelectionAsNativePlanner()
    {
        var rig = Rig();
        var native = LightRigPlanner.Plan(rig, Default, Vector3.Zero, 1f, 3);
        var vfx = VfxLightPlanner.Plan(rig, Default, Sky, 1f, 3, fillsOnOwnEmitters: true);
        Assert.Equal(native.Lights.Count, vfx.Lights.Count);
        for (var i = 0; i < vfx.Lights.Count; i++)
        {
            var nativeDir = Vector3.Normalize(native.Lights[i].P.Position);
            var vfxDir = Vector3.Normalize(vfx.Lights[i].Position - Anchor);
            Assert.True(Vector3.Distance(nativeDir, vfxDir) < 1e-4f, $"light {i} direction");
        }
    }

    [Fact] // [AC-synced-lighting-2]
    public void Strength_ScalesBrightnessLinearly()
    {
        var one = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 2, fillsOnOwnEmitters: true);
        var two = VfxLightPlanner.Plan(Rig(), Default, Sky, 2f, 2, fillsOnOwnEmitters: true);
        for (var i = 0; i < one.Lights.Count; i++) Assert.Equal(one.Lights[i].Brightness * 2, two.Lights[i].Brightness, 4);
    }

    [Fact] // [EDGE-synced-lighting-3]
    public void StrengthZero_NoLights()
    {
        Assert.Empty(VfxLightPlanner.Plan(Rig(), Default, Sky, 0f, 3, fillsOnOwnEmitters: true).Lights);
    }

    [Fact] // [EDGE-synced-lighting-4]
    public void FillCountZero_KeyOnly()
    {
        var plan = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 0, fillsOnOwnEmitters: true);
        Assert.Single(plan.Lights);
        Assert.True(plan.HasKey);
    }

    [Fact] // [EDGE-synced-lighting-10]
    public void FillsOnOwnEmittersDefaultOff_KeyOnly()
    {
        Assert.False(VfxLightLayout.FillsOnOwnEmitters);
        var plan = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 3);
        Assert.Single(plan.Lights);
        Assert.Equal(0, plan.Fills);
    }

    [Fact] // [EDGE-synced-lighting-2]
    public void NoKeyInRig_FillsOn_OneStandInKeyWithShadows_RestSoft()
    {
        var plan = VfxLightPlanner.Plan(Rig(noKey: true), Default, Sky, 1f, 2, fillsOnOwnEmitters: true);
        Assert.True(plan.HasKey);
        Assert.True(plan.Lights[0].Shadows);
        Assert.All(plan.Lights.Skip(1), l => Assert.False(l.Shadows));
    }

    [Fact] // [EDGE-synced-lighting-2]
    public void NoKeyInRig_FillsOff_StandInKeyOnly()
    {
        var plan = VfxLightPlanner.Plan(Rig(noKey: true), Default, Sky, 1f, 2);
        Assert.True(Assert.Single(plan.Lights).Shadows);
    }

    [Theory] // [EDGE-synced-lighting-39]
    [InlineData(10f, 9f, 8f)]          // room default: key inside the dome at 0.9 × R
    [InlineData(1f, 1.5f, 1.5f)]       // tiny room: never closer than MinDistance
    [InlineData(20000f, 30f, 8f)]      // sky: nominal distances
    public void Distance_RoomAndSkyRadii(float radius, float key, float fill)
    {
        Assert.Equal(key, VfxLightPlanner.Distance(VfxLightLayout.KeyDistance, radius), 4);
        Assert.Equal(fill, VfxLightPlanner.Distance(VfxLightLayout.FillDistance, radius), 4);
    }

    [Fact] // [EDGE-synced-lighting-50]
    public void DistanceExponent_ZeroUnchanged_TwoScalesBySquare()
    {
        var zero = VfxLightPlanner.Plan(Rig(), Default, 10f, 1f, 0, distanceExponent: 0);
        var two = VfxLightPlanner.Plan(Rig(), Default, 10f, 1f, 0, distanceExponent: 2);
        Assert.Equal(VfxLightLayout.KeyBrightness, zero.Lights[0].Brightness, 4);
        Assert.Equal(VfxLightLayout.KeyBrightness * MathF.Pow(9f / 30f, 2), two.Lights[0].Brightness, 3);
    }

    [Fact] // [AC-synced-lighting-7]
    public void Positions_AreEmitterRelative_AroundTheAnchorHeight()
    {
        var key = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 0).Lights[0];
        Assert.Equal(VfxLightLayout.KeyDistance, Vector3.Distance(Anchor, key.Position), 3);
    }

    [Fact] // [EDGE-synced-lighting-48]
    public void SameLights_NullEqualsEmpty_FieldDifference_NotSame()
    {
        Assert.True(VfxLightPlan.SameLights(null, []));
        var a = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 0).Lights;
        var b = VfxLightPlanner.Plan(Rig(), Default, Sky, 1f, 0).Lights;
        Assert.True(VfxLightPlan.SameLights(a, b));   // values, not references
        var c = VfxLightPlanner.Plan(Rig(), Default, Sky, 1.5f, 0).Lights;
        Assert.False(VfxLightPlan.SameLights(a, c));
    }

    [Fact]
    public void BuilderAcceptsEveryPlannedLight()
    {
        var plan = VfxLightPlanner.Plan(Rig(), Default, Sky, 2f, 3, fillsOnOwnEmitters: true);
        var p = new DomeAvfxParams("vfx/milieu/0011223344556677/sky.atex", Sky, 1, 2, 1, false, 1, AttachToOwner: true, Lights: plan.Lights);
        Assert.NotEmpty(DomeAvfxBuilder.Build(p));
    }

    [Fact] // [AC-synced-lighting-1]
    public void FillWeights_LuminanceTimesTypePower_OverTheStrongest()
    {
        // One estimated lamp (LampPower) before the regions (FillPower): an HDR rig with lamps set directly.
        var lamp = new RigLight(0.6f, 0.4f, new Vector3(0.2f, 0.2f, 0.2f));
        var rig = Rig() with { Lamps = [lamp] };
        var plan = VfxLightPlanner.Plan(rig, Default, Sky, 1f, 2, fillsOnOwnEmitters: true);
        var resolved = LightRigPlanner.Resolve(rig);
        var w0 = LightRig.Luminance(resolved.Lamps[0].Energy) * LightRigLayout.LampPower;
        var w1 = LightRig.Luminance(resolved.Regions[0].Energy) * LightRigLayout.FillPower;
        var strongest = MathF.Max(w0, w1);
        Assert.Equal(VfxLightLayout.FillBrightness * w0 / strongest, plan.Lights[1].Brightness, 4);
        Assert.Equal(VfxLightLayout.FillBrightness * w1 / strongest, plan.Lights[2].Brightness, 4);
    }

    [Fact] // [EDGE-synced-lighting-2]
    public void KeyWithNoPositiveChannel_Dropped_FirstFillLeads()
    {
        var plan = VfxLightPlanner.Plan(Rig(new RigLight(0.5f, 0.25f, Vector3.Zero)), Default, Sky, 1f, 2, fillsOnOwnEmitters: true);
        Assert.False(plan.HasKey);
        Assert.All(plan.Lights, l => Assert.False(l.Shadows));
    }

    [Fact] // [EDGE-synced-lighting-2]
    public void KeyExistsButNotDrawn_NoKeyLight()
    {
        // A small horizontal repeat draws only the middle of the image: a key near its edge isn't on the dome.
        var view = Default with { URepeat = 0.5f };
        const float keyS = 0.95f;
        Assert.Null(DomeProjection.WorldDirection(keyS, 0.25f, view));
        var plan = VfxLightPlanner.Plan(Rig(new RigLight(keyS, 0.25f, new Vector3(3, 2, 1))), view, Sky, 1f, 0);
        Assert.False(plan.HasKey);
        Assert.Empty(plan.Lights);
    }

    [Fact] // [AC-synced-lighting-1]
    public void NoSunInTheImage_StrongestDrawnLightStandsInAsTheKey()
    {
        // The bundled Sunset rig has no key, only regions: it must still ship one shadowed light.
        var plan = VfxLightPlanner.Plan(Rig(noKey: true), Default, Sky, 1f, 0);
        Assert.True(plan.HasKey);
        var key = Assert.Single(plan.Lights);
        Assert.True(key.Shadows);
        Assert.Equal(VfxLightLayout.KeyBrightness, key.Brightness);
        var dir = DomeProjection.WorldDirection(0.3f, 0.3f, Default)!.Value;   // the first lamp
        Assert.True(Vector3.Distance(Anchor + dir * VfxLightLayout.KeyDistance, key.Position) < 1e-3f);

        var withFills = VfxLightPlanner.Plan(Rig(noKey: true), Default, Sky, 1f, 3, fillsOnOwnEmitters: true);
        Assert.Single(withFills.Lights, l => l.Shadows);
        Assert.Equal(withFills.Lights.Count, withFills.Lights.Select(l => l.Position).Distinct().Count());   // not used twice
    }

    [Fact] // [AC-synced-lighting-1]
    public void BundledSunset_GetsAKeyLight()
    {
        var rig = Milieu.Assets.BundledLightRigs.Find("sunset");
        Assert.NotNull(rig);
        Assert.Null(rig!.Key);
        Assert.True(VfxLightPlanner.Plan(rig, Default, Sky, 1f, 0).HasKey);
    }
}
