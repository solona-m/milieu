using System;

namespace Milieu.Backdrop;

public enum DomeMode { Sky = 0, Room = 1 }

/// <summary>Per-mode dome geometry. Radius and vertical scale are baked into the VFX; height and yaw are live.</summary>
public sealed class DomeSettings
{
    public float Radius { get; set; }
    public float VerticalScale { get; set; } = 1f;
    public float HeightOffset { get; set; }
    public float YawDegrees { get; set; }

    /// <summary>Flatten the dome's lower half onto a floor at <see cref="HeightOffset"/> below the centre.</summary>
    public bool GroundProjection { get; set; }

    /// <summary>
    /// The sky dome: ground projection with the camera at eye height (1.6), so its flat floor lies at your feet, the horizon
    /// sits at eye level and the panorama never sinks into the terrain (chosen in game).
    /// </summary>
    public static DomeSettings SkyDefaults() => new() { Radius = 20000f, VerticalScale = 1f, HeightOffset = 1.6f, YawDegrees = 0f, GroundProjection = true };

    /// <summary>A room-sized dome around the camera, its centre at roughly eye height.</summary>
    public static DomeSettings RoomDefaults() => new() { Radius = 10f, VerticalScale = 1f, HeightOffset = 1.6f, YawDegrees = 0f, GroundProjection = true };

    public static (float Min, float Max) RadiusRange(DomeMode mode) => mode == DomeMode.Sky ? (200f, 20000f) : (1f, 100f);
    public static (float Min, float Max) HeightRange(DomeMode mode) => mode == DomeMode.Sky ? (-2000f, 2000f) : (-10f, 10f);
    public static readonly (float Min, float Max) VerticalScaleRange = (0.25f, 2f);
    public static readonly (float Min, float Max) YawRange = (-180f, 180f);

    /// <summary>
    /// The ground-projection floor in unit-radius mesh space: height / (radius · vertical scale), so the floor lands at
    /// world y = −height below the centre. Null when projection is off or the floor would not cut the sphere.
    /// </summary>
    public float? FloorY()
    {
        if (!GroundProjection || !(HeightOffset > 0)) return null;
        var extent = Radius * VerticalScale;
        if (!(extent > 0) || HeightOffset >= extent) return null;
        return HeightOffset / extent;
    }

    /// <summary>What ground projection is doing, in words, so a silently inactive floor is visible in the window.</summary>
    public string ProjectionStatus()
    {
        var h = HeightOffset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var extent = (Radius * VerticalScale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (!GroundProjection) return "Off: the dome is a full sphere centred at the height above your feet.";
        if (FloorY() != null) return $"Floor at your feet; horizon {h} above it.";
        return HeightOffset <= 0
            ? $"Inactive: the camera height ({h}) must be above 0."
            : $"Inactive: the camera height ({h}) must be below the dome's height ({extent}). Lower it or raise the radius.";
    }

    public bool ProjectionInactive => GroundProjection && FloorY() == null;

    public void Clamp(DomeMode mode)
    {
        var d = mode == DomeMode.Sky ? SkyDefaults() : RoomDefaults();
        Radius = Fix(Radius, RadiusRange(mode), d.Radius);
        VerticalScale = Fix(VerticalScale, VerticalScaleRange, d.VerticalScale);
        HeightOffset = Fix(HeightOffset, HeightRange(mode), d.HeightOffset);
        YawDegrees = Fix(YawDegrees, YawRange, d.YawDegrees);
    }

    internal static float Fix(float v, (float Min, float Max) range, float fallback) =>
        float.IsFinite(v) ? Math.Clamp(v, range.Min, range.Max) : fallback;
}
