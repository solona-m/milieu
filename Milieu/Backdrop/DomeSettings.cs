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

    public static DomeSettings SkyDefaults() => new() { Radius = 20000f, VerticalScale = 1f, HeightOffset = 0f, YawDegrees = 0f };

    /// <summary>A room-sized dome around the camera, its centre at roughly eye height.</summary>
    public static DomeSettings RoomDefaults() => new() { Radius = 10f, VerticalScale = 1f, HeightOffset = 1.6f, YawDegrees = 0f };

    public static (float Min, float Max) RadiusRange(DomeMode mode) => mode == DomeMode.Sky ? (200f, 20000f) : (1f, 100f);
    public static (float Min, float Max) HeightRange(DomeMode mode) => mode == DomeMode.Sky ? (-2000f, 2000f) : (-10f, 10f);
    public static readonly (float Min, float Max) VerticalScaleRange = (0.25f, 2f);
    public static readonly (float Min, float Max) YawRange = (-180f, 180f);

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
