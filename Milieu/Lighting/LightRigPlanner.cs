using System;
using System.Collections.Generic;
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
/// exceeds <see cref="LightRigLayout.MaxChannel"/>.
/// </summary>
public static class LightRigPlanner
{
    public const string KeySlot = "key";

    public static string FillSlot(int i) => "fill" + i;

    public static PlanResult Plan(LightRig rig, DomeView view, Vector3 anchor, float strength, int fillCount)
    {
        var lights = new List<(string, LightParams)>();
        var rigHasKey = rig.Key != null;
        Vector3? keyDir = rig.Key is { } k ? DomeProjection.WorldDirection(k.S, k.T, view) : null;
        var keyDrawn = keyDir != null;
        var totalY = LightRig.Luminance(rig.TotalEnergy);
        if (!(strength > 0) || !(totalY > 0) || !float.IsFinite(totalY)) return new PlanResult(lights, rigHasKey, keyDrawn);

        var d = LightRigLayout.LightDistance;
        var compensation = DistanceCompensation(d, LightRigLayout.Falloff);
        if (rig.Key is { } key && keyDir is { } kd)
        {
            var colour = Clamp(key.Energy / totalY * LightRigLayout.KeyPower * strength * compensation);
            lights.Add((KeySlot, new LightParams(LightShapeKind.Spot, anchor + kd * d, RotationFacing(-kd), colour, LightRigLayout.KeyRange,
                                                 LightRigLayout.KeyConeDegrees, LightRigLayout.KeyAngularFalloffDegrees, true)));
        }

        var fills = Math.Clamp(fillCount, 0, Math.Min(LightRigLayout.MaxFills, LightRigLayout.MaxLights - lights.Count));
        var used = 0;
        foreach (var region in rig.Regions)
        {
            if (used >= fills) break;
            if (DomeProjection.WorldDirection(region.S, region.T, view) is not { } dir) continue;   // not on the dome: skipped
            var colour = Clamp(region.Energy / totalY * LightRigLayout.FillPower * strength * compensation);
            lights.Add((FillSlot(used), new LightParams(LightShapeKind.Point, anchor + dir * d, Quaternion.Identity, colour,
                                                        LightRigLayout.FillRange, LightRigLayout.KeyConeDegrees,
                                                        LightRigLayout.KeyAngularFalloffDegrees, false)));
            used++;
        }
        return new PlanResult(lights, rigHasKey, keyDrawn);
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
