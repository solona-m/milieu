using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Milieu.GameFiles;

namespace Milieu.Lighting;

/// <summary>
/// The VFX lights planned for a synced dome: the lights, whether a shadowed key leads the list (the rig's, or the stand-in
/// for an image with no sun), the number of fills, and whether the rig was estimated.
/// </summary>
internal sealed record VfxLightPlan(IReadOnlyList<DomeLight> Lights, bool HasKey, int Fills, bool Estimated)
{
    public static readonly VfxLightPlan Empty = new([], false, 0, false);

    /// <summary>The no-op rule for light rebuilds: sequence equality over DomeLight values, null equal to empty.</summary>
    public static bool SameLights(IReadOnlyList<DomeLight>? a, IReadOnlyList<DomeLight>? b) => (a ?? []).SequenceEqual(b ?? []);
}

/// <summary>
/// Turns a <see cref="LightRig"/> into VFX point lights for a synced dome (pure; docs/adr/0011). The selection is the native
/// planner's (<see cref="LightRigPlanner.Select"/>). Positions are emitter-relative: (0, LightRigLayout.AnchorHeight, 0) +
/// dir × distance, independent of the player's world height (the emitter carries that). The key is a shadow-casting light
/// far away in the key's direction at <see cref="VfxLightLayout.KeyBrightness"/> × strength ("match the sun"); fills are
/// soft coloured lights without shadows, the strongest at <see cref="VfxLightLayout.FillBrightness"/> × strength.
/// </summary>
internal static class VfxLightPlanner
{
    /// <summary>WIP test: the key no higher than MaxKeyElevationDegrees, same azimuth (a long, visible shadow).</summary>
    private static Vector3 LowerKey(Vector3 dir)
    {
        var max = VfxLightLayout.MaxKeyElevationDegrees * MathF.PI / 180f;
        var horizontal = new Vector2(dir.X, dir.Z);
        var len = horizontal.Length();
        if (MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) <= max) return dir;
        if (len < 1e-4f) horizontal = new Vector2(0, 1); else horizontal /= len;
        return new Vector3(horizontal.X * MathF.Cos(max), MathF.Sin(max), horizontal.Y * MathF.Cos(max));
    }

    public static VfxLightPlan Plan(LightRig rig, DomeView view, float radius, float strength, int fillCount,
                                    bool fillsOnOwnEmitters = VfxLightLayout.FillsOnOwnEmitters,
                                    float distanceExponent = VfxLightLayout.DistanceCompensationExponent,
                                    float standInKeyScale = VfxLightLayout.StandInKeyScale)
    {
        var estimated = rig.IsEstimated;
        rig = LightRigPlanner.Resolve(rig);
        var totalY = LightRig.Luminance(rig.TotalEnergy);
        if (!(strength > 0) || !float.IsFinite(strength) || !(totalY > 0) || !float.IsFinite(totalY))
            return VfxLightPlan.Empty with { Estimated = estimated };

        // Fills need emitters of their own (unproven): with that off, only the key ships, on the dome emitter.
        var (key, fills) = LightRigPlanner.Select(rig, view, fillsOnOwnEmitters ? fillCount : 0);
        var standIn = false;
        if (rig.Key == null)
        {
            // No sun in the image at all (an overcast or sunset image whose estimate found no key): the first drawn region
            // (radiance-ordered by both estimators; or a lamp, for a rig with lamps but no key) stands in as the key, so the scene gets
            // one directional light with shadows (EDGE-synced-lighting-52). A sun that exists but isn't drawn stays unlit
            // (EDGE-synced-lighting-2). It is the first entry of the same ordered walk, so it is fills[0] when fills exist.
            var (_, best) = LightRigPlanner.Select(rig, view, 1);
            if (best.Count > 0)
            {
                key = best[0];
                standIn = true;
                if (fills.Count > 0) fills.RemoveAt(0);
            }
        }
        var anchor = new Vector3(0, LightRigLayout.AnchorHeight, 0);
        var lights = new List<DomeLight>();

        if (key is { } k && Hue(k.Light.Energy) is { } keyHue)
        {
            var d = Distance(VfxLightLayout.KeyDistance, radius);
            var bri = VfxLightLayout.KeyBrightness * (standIn ? standInKeyScale : 1f) * strength
                      * Compensation(d, VfxLightLayout.KeyDistance, distanceExponent);
            lights.Add(new DomeLight(anchor + LowerKey(k.Dir) * d, keyHue, bri, d + VfxLightLayout.KeyRange, true,
                                     VfxLightLayout.KeyShadowNear, d + VfxLightLayout.KeyShadowFar));
        }
        var hasKey = lights.Count == 1;

        var weights = fills.Select(f => LightRig.Luminance(f.Light.Energy) * f.Power).ToList();
        var strongest = weights.Count > 0 ? weights.Max() : 0f;
        var fillCountOut = 0;
        for (var i = 0; i < fills.Count && strongest > 0 && float.IsFinite(strongest); i++)
        {
            if (Hue(fills[i].Light.Energy) is not { } hue || !(weights[i] > 0)) continue;
            var d = Distance(VfxLightLayout.FillDistance, radius);
            var bri = VfxLightLayout.FillBrightness * strength * (weights[i] / strongest)
                      * Compensation(d, VfxLightLayout.FillDistance, distanceExponent);
            lights.Add(new DomeLight(anchor + fills[i].Dir * d, hue, bri, VfxLightLayout.FillRange, false,
                                     VfxLightLayout.FillShadowNear, VfxLightLayout.FillShadowFar));
            fillCountOut++;
        }
        return new VfxLightPlan(lights, hasKey, fillCountOut, estimated);
    }

    /// <summary>
    /// clamp(min(nominal, RoomDistanceFraction × radius), MinDistance, nominal): the light stays inside a room dome, and
    /// never closer than MinDistance.
    /// </summary>
    public static float Distance(float nominal, float radius)
    {
        var inside = float.IsFinite(radius) && radius > 0 ? MathF.Min(nominal, VfxLightLayout.RoomDistanceFraction * radius) : nominal;
        return Math.Clamp(inside, VfxLightLayout.MinDistance, nominal);
    }

    /// <summary>(d / nominal)^exponent; 1 at exponent 0.</summary>
    public static float Compensation(float d, float nominal, float exponent) =>
        exponent == 0 ? 1f : MathF.Pow(d / nominal, exponent);

    /// <summary>The energy's hue with the largest channel at 1; null when it has no positive finite channel.</summary>
    public static Vector3? Hue(Vector3 energy)
    {
        static float Fix(float v) => float.IsFinite(v) && v > 0 ? v : 0;
        var c = new Vector3(Fix(energy.X), Fix(energy.Y), Fix(energy.Z));
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return max > 0 ? Vector3.Clamp(c / max, Vector3.Zero, Vector3.One) : null;
    }
}
