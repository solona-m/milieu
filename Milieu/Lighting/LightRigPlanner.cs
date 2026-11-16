using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Milieu.Lighting;

/// <summary>The planned lights by slot ("key", "fill0".."fill2"), whether the image has a key, and whether it is drawn.</summary>
public sealed record PlanResult(IReadOnlyList<(string Slot, LightParams P)> Lights, bool RigHasKey, bool KeyDrawn)
{
    public int FillCount
    {
        get
        {
            var n = 0;
            foreach (var (slot, _) in Lights) if (slot != LightRigPlanner.KeySlot) n++;
            return n;
        }
    }
}

/// <summary>
/// Turns a <see cref="LightRig"/> (image space) into game lights around an anchor, for the dome as drawn (pure).
/// The key is a spot light at anchor + dir·<see cref="LightRigLayout.LightDistance"/>, aimed at the anchor, casting
/// character shadows; fills are point lights at the same distance, the top regions whose direction is drawn.
/// colour = E / TotalEnergy_Y × TypePower × Strength × DistanceCompensation, then scaled uniformly so no channel
/// exceeds <see cref="LightRigLayout.MaxChannel"/>. An estimated rig is resolved first (<see cref="Resolve"/>); its lamps
/// are fill candidates before the regions (<see cref="LightRigLayout.LampPower"/>).
/// </summary>
public static class LightRigPlanner
{
    public const string KeySlot = "key";

    public static string FillSlot(int i) => "fill" + i;

    /// <summary>
    /// An estimated rig's brightness, applied at plan time (docs/adr/0010): k = EstimatedKeyOverSky × max(TotalEnergy_Y,
    /// EstimatedSkyFloorY); the key and lamps (a colour times a relative weight) are scaled by k and added to the total, so
    /// regions + key + lamps = total. An HDR rig is returned as it is.
    /// </summary>
    public static LightRig Resolve(LightRig rig)
    {
        if (!rig.IsEstimated) return rig;
        var sky = LightRig.Luminance(rig.TotalEnergy);
        var k = LightRigLayout.EstimatedKeyOverSky * (float.IsFinite(sky) ? MathF.Max(sky, LightRigLayout.EstimatedSkyFloorY) : LightRigLayout.EstimatedSkyFloorY);
        var key = rig.Key is { } kl ? kl with { Energy = kl.Energy * k } : null;
        var lamps = rig.Lamps.Select(l => l with { Energy = l.Energy * k }).ToArray();
        var total = rig.TotalEnergy + (key?.Energy ?? Vector3.Zero);
        foreach (var l in lamps) total += l.Energy;
        return rig with { Key = key, Lamps = lamps, TotalEnergy = total };
    }

    public static PlanResult Plan(LightRig rig, DomeView view, Vector3 anchor, float strength, int fillCount)
    {
        rig = Resolve(rig);
        var lights = new List<(string, LightParams)>();
        var rigHasKey = rig.Key != null;
        var keyDrawn = rig.Key is { } k && DomeProjection.WorldDirection(k.S, k.T, view) != null;
        var totalY = LightRig.Luminance(rig.TotalEnergy);
        if (!(strength > 0) || !(totalY > 0) || !float.IsFinite(totalY)) return new PlanResult(lights, rigHasKey, keyDrawn);

        var d = LightRigLayout.LightDistance;
        var compensation = DistanceCompensation(d, LightRigLayout.Falloff);
        var (key, fills) = Select(rig, view, fillCount);
        if (key is { } kl)
        {
            var colour = Clamp(kl.Light.Energy / totalY * kl.Power * strength * compensation);
            lights.Add((KeySlot, new LightParams(LightShapeKind.Spot, anchor + kl.Dir * d, RotationFacing(-kl.Dir), colour, LightRigLayout.KeyRange,
                                                 LightRigLayout.KeyConeDegrees, LightRigLayout.KeyAngularFalloffDegrees, true)));
        }
        for (var i = 0; i < fills.Count; i++)
        {
            var f = fills[i];
            var colour = Clamp(f.Light.Energy / totalY * f.Power * strength * compensation);
            lights.Add((FillSlot(i), new LightParams(LightShapeKind.Point, anchor + f.Dir * d, Quaternion.Identity, colour,
                                                     LightRigLayout.FillRange, LightRigLayout.KeyConeDegrees,
                                                     LightRigLayout.KeyAngularFalloffDegrees, false)));
        }
        return new PlanResult(lights, rigHasKey, keyDrawn);
    }

    /// <summary>A chosen light: the rig light, its world direction for the dome as drawn, and its type power.</summary>
    internal readonly record struct SelectedLight(RigLight Light, Vector3 Dir, float Power);

    /// <summary>
    /// Which lights a <em>resolved</em> rig contributes for the dome as drawn, shared by the native and VFX planners: the key
    /// if drawn (KeyPower), then up to <paramref name="fillCount"/> fills (at most MaxFills, and MaxLights with the key),
    /// lamps before regions (LampPower, FillPower), each only if its direction is drawn.
    /// </summary>
    internal static (SelectedLight? Key, List<SelectedLight> Fills) Select(LightRig resolved, DomeView view, int fillCount)
    {
        SelectedLight? key = resolved.Key is { } k && DomeProjection.WorldDirection(k.S, k.T, view) is { } kd
            ? new SelectedLight(k, kd, LightRigLayout.KeyPower) : null;
        var max = Math.Clamp(fillCount, 0, Math.Min(LightRigLayout.MaxFills, LightRigLayout.MaxLights - (key != null ? 1 : 0)));
        var fills = new List<SelectedLight>();
        foreach (var (region, power) in resolved.Lamps.Select(l => (l, LightRigLayout.LampPower))
                                                     .Concat(resolved.Regions.Select(r => (r, LightRigLayout.FillPower))))
        {
            if (fills.Count >= max) break;
            if (DomeProjection.WorldDirection(region.S, region.T, view) is not { } dir) continue;   // not on the dome: skipped
            fills.Add(new SelectedLight(region, dir, power));
        }
        return (key, fills);
    }

    /// <summary>
    /// (d / LightDistance)^p for linear, quadratic or cubic falloff (p = 1, 2, 3): what keeps a light's brightness at the
    /// anchor independent of its distance. 1 today, as every light sits at LightDistance.
    /// </summary>
    public static float DistanceCompensation(float distance, LightFalloff falloff) =>
        MathF.Pow(distance / LightRigLayout.LightDistance, falloff switch
        {
            LightFalloff.Linear => 1,
            LightFalloff.Quadratic => 2,
            LightFalloff.Cubic => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(falloff)),
        });

    /// <summary>Scales uniformly so no channel exceeds MaxChannel (hue and saturation kept); non-finite or negative → 0.</summary>
    public static Vector3 Clamp(Vector3 c)
    {
        static float Fix(float v) => float.IsFinite(v) && v > 0 ? v : 0;
        c = new Vector3(Fix(c.X), Fix(c.Y), Fix(c.Z));
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return max > LightRigLayout.MaxChannel ? c * (LightRigLayout.MaxChannel / max) : c;
    }

    /// <summary>A rotation q with Transform(SpotForward, q) ≈ forward; stable for straight up/down and for the reverse axis.</summary>
    public static Quaternion RotationFacing(Vector3 forward)
    {
        var from = LightRigLayout.SpotForward;
        var to = Vector3.Normalize(forward);
        var dot = Vector3.Dot(from, to);
        if (dot > 1 - 1e-6f) return Quaternion.Identity;
        if (dot < -1 + 1e-6f)
        {
            // Half turn about any axis perpendicular to SpotForward.
            var axis = Vector3.Normalize(Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }
        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + dot));
    }
}
