using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Milieu.GameFiles;

/// <summary>What lies under the half dome with ground projection on (docs/adr/0015). Persisted per dome set.</summary>
public enum DomeBottom
{
    /// <summary>The image's lower half on a flat floor disc at your feet; its horizon at camera height.</summary>
    ImageFloor = 0,

    /// <summary>Only the image's upper half, its horizon on the rim at your feet; the game's own ground shows below.</summary>
    Open = 1,
}

/// <summary>The mesh a dome is built with (docs/adr/0015).</summary>
internal enum DomeMeshKind
{
    /// <summary>The reference unit sphere (model 7), ground projection off or inactive.</summary>
    Sphere = 0,

    /// <summary>The pre-0015 ground projection (the reference sphere with its lower vertices pulled onto the floor): the kill-switch path.</summary>
    LegacyFloor = 1,

    /// <summary>A half dome standing on the floor, closed by the image's floor, cast from the capture point.</summary>
    ImageFloor = 2,

    /// <summary>The reference sphere's upper half, centred at the feet.</summary>
    OpenBottom = 3,
}

/// <summary>
/// The half-dome geometry (pure; docs/adr/0015). Mesh space is unit radius horizontally; the particle scales it by
/// (R, R·vs, R). For the image floor the origin is the capture point and f = h / (R·vs) is the floor's depth below it:
/// the solid is the unit ball centred at (0, −f, 0) cut at y ≥ −f, so in the world a hemisphere centred at the feet,
/// its rim on the floor at radius R and its top R·vs above the floor. Every vertex stays on its own ray from the origin.
/// </summary>
internal static class DomeShape
{
    public const double UpperStepDegrees = 1.875;
    public const int FloorRings = 48;
    public const double TaperRatio = 1.25;
    public const double TaperLimitDegrees = 20;
    public const int MaxIterations = 256;

    private const double Deg = Math.PI / 180.0;

    /// <summary>
    /// The image-floor ring elevations in radians, strictly decreasing from +π/2 to exactly −π/2: 1.875° steps above
    /// the horizon; uniform rings down to the exact floor/wall crease at −atan(f); then square-root-spaced floor rings,
    /// each at most a 1.25× step in tan (floor distance) while within 20° of the horizon. The floor sequence ends at the
    /// nadir once the next ring would fall within half a step of it, so no ring is left a sliver away from the pole.
    /// </summary>
    public static double[] RingElevations(double floorY, int maxIterations = MaxIterations)
    {
        CheckFloor(floorY);
        var rings = new List<double>();
        var upper = (int)Math.Round(90.0 / UpperStepDegrees);
        for (var k = 0; k <= upper; k++) rings.Add((90.0 - UpperStepDegrees * k) * Deg);

        var crease = Math.Atan(floorY);
        var n = Math.Max(1, (int)Math.Ceiling(crease / (UpperStepDegrees * Deg)));
        for (var k = 1; k <= n; k++) rings.Add(-crease * k / n);

        var half = Math.PI / 2;
        var s = (Math.Sqrt(half) - Math.Sqrt(crease)) / FloorRings;
        var theta = crease;
        for (var i = 0; ; i++)
        {
            if (i >= maxIterations) throw new InvalidDataException($"Dome ring layout did not reach the nadir within {maxIterations} rings.");
            var next = Math.Pow(Math.Sqrt(theta) + s, 2);
            if (theta < TaperLimitDegrees * Deg) next = Math.Min(next, Math.Atan(TaperRatio * Math.Tan(theta)));
            if (next >= half - s / 2)
            {
                rings.Add(-half);
                break;
            }
            rings.Add(-next);
            theta = next;
        }
        return rings.ToArray();
    }

    /// <summary>The image-floor exit point along unit direction d from the capture point (min of the wall and the floor).</summary>
    public static (double X, double Y, double Z) CastImageFloor(double dx, double dy, double dz, double floorY)
    {
        CheckFloor(floorY);
        var t = -floorY * dy + Math.Sqrt(1 - floorY * floorY * (1 - dy * dy));
        if (dy < 0) t = Math.Min(t, floorY / -dy);
        return (dx * t, dy * t, dz * t);
    }

    /// <summary>
    /// How far a ray from <paramref name="from"/> (feet frame, world units) along unit <paramref name="dir"/> travels before
    /// it leaves the ellipsoid (x² + z²)/R² + y²/(R·vs)² = 1 centred at the feet: the half dome's wall, continued below the
    /// floor (no floor plane, so below-horizon lights keep about their old distance). 0 when <paramref name="from"/> is not inside.
    /// </summary>
    public static float ExitDistance(Vector3 from, Vector3 dir, float radius, float verticalScale)
    {
        if (!(radius > 0) || !(verticalScale > 0) || !float.IsFinite(radius) || !float.IsFinite(verticalScale)) return 0f;
        double ox = from.X, oy = from.Y / (double)verticalScale, oz = from.Z;
        double dx = dir.X, dy = dir.Y / (double)verticalScale, dz = dir.Z;
        var a = dx * dx + dy * dy + dz * dz;
        var b = 2 * (ox * dx + oy * dy + oz * dz);
        var c = ox * ox + oy * oy + oz * oz - (double)radius * radius;
        if (!(a > 0) || !(c < 0)) return 0f;
        var t = (-b + Math.Sqrt(b * b - 4 * a * c)) / (2 * a);
        return double.IsFinite(t) && t > 0 ? (float)t : 0f;
    }

    private static void CheckFloor(double floorY)
    {
        if (!double.IsFinite(floorY) || !(floorY > 0 && floorY < 1))
            throw new ArgumentOutOfRangeException(nameof(floorY), "floorY must be finite and in (0, 1).");
    }
}
