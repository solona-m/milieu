using System;
using System.Globalization;
using Milieu.GameFiles;

namespace Milieu.Backdrop;

public enum DomeMode { Sky = 0, Room = 1 }

/// <summary>The dome a set of settings builds (docs/adr/0015): the mesh, its floor (unit space) and the centre's height above the feet.</summary>
internal readonly record struct DomeShapeChoice(DomeMeshKind Kind, float? FloorY, float CentreHeight);

/// <summary>Per-mode dome geometry. Radius and vertical scale are baked into the VFX; height and yaw are live.</summary>
public sealed class DomeSettings
{
    public float Radius { get; set; }
    public float VerticalScale { get; set; } = 1f;
    public float HeightOffset { get; set; }
    public float YawDegrees { get; set; }

    /// <summary>The half dome standing on the floor (docs/adr/0015); <see cref="HeightOffset"/> is then the camera height.</summary>
    public bool GroundProjection { get; set; }

    /// <summary>What lies under the half dome with ground projection on (docs/adr/0015). Absent in older configs: the image floor.</summary>
    public DomeBottom Bottom { get; set; }

    /// <summary>The lowest camera height the image floor accepts (docs/adr/0015): below it the image's floor squashes to a dot.</summary>
    public const float MinCameraHeight = 0.25f;

    /// <summary>
    /// The sky dome: ground projection with the camera at eye height (1.6), so its flat floor lies at your feet, the horizon
    /// sits at eye level and the panorama never sinks into the terrain (chosen in game).
    /// </summary>
    public static DomeSettings SkyDefaults() => new() { Radius = 20000f, VerticalScale = 1f, HeightOffset = 1.6f, YawDegrees = 0f, GroundProjection = true };

    /// <summary>
    /// A room-sized half dome with an open bottom: the picture's upper half on the hemisphere, its horizon at your feet, and
    /// the game's own floor left in place (chosen in game, docs/adr/0015 amendment).
    /// </summary>
    public static DomeSettings RoomDefaults() => new()
    {
        Radius = 10f, VerticalScale = 1f, HeightOffset = 1.6f, YawDegrees = 0f, GroundProjection = true, Bottom = DomeBottom.Open,
    };

    public static (float Min, float Max) RadiusRange(DomeMode mode) => mode == DomeMode.Sky ? (200f, 20000f) : (1f, 100f);
    public static (float Min, float Max) HeightRange(DomeMode mode) => mode == DomeMode.Sky ? (-2000f, 2000f) : (-10f, 10f);
    public static readonly (float Min, float Max) VerticalScaleRange = (0.25f, 2f);
    public static readonly (float Min, float Max) YawRange = (-180f, 180f);

    /// <summary>The height slider's minimum: <see cref="MinCameraHeight"/> while ground projection is on, else the mode's range.</summary>
    public float HeightMin(DomeMode mode) =>
        GroundProjection ? MathF.Max(MinCameraHeight, HeightRange(mode).Min) : HeightRange(mode).Min;

    /// <summary>The camera height does nothing (an open bottom puts the image's horizon at the feet), so the slider is disabled.</summary>
    public bool CameraHeightDisabled(bool halfDomeAvailable) => GroundProjection && Bottom == DomeBottom.Open && halfDomeAvailable;

    /// <summary>
    /// The dome these settings build (docs/adr/0015). Projection off, or the image floor inactive: the reference sphere centred
    /// at the height. Kill switch off: the pre-0015 floor (an open entry too). Open: the reference's upper half centred at the feet.
    /// </summary>
    internal DomeShapeChoice Shape(bool halfDomeAvailable)
    {
        if (!GroundProjection) return new(DomeMeshKind.Sphere, null, HeightOffset);
        if (halfDomeAvailable && Bottom == DomeBottom.Open) return new(DomeMeshKind.OpenBottom, null, 0f);
        if (FloorY() is not { } f) return new(DomeMeshKind.Sphere, null, HeightOffset);
        return new(halfDomeAvailable ? DomeMeshKind.ImageFloor : DomeMeshKind.LegacyFloor, f, HeightOffset);
    }

    public const string BottomLabel = "Bottom:";
    public static readonly string[] BottomLabels = ["Image floor", "Open (horizon at your feet)"];

    public static string BottomTooltip(DomeBottom bottom) => bottom == DomeBottom.Open
        ? "Only the image's upper half is drawn, its horizon at your feet, and the game's own ground shows below. Best for skies and open scenes: in a room the horizon sits below eye level and the image's floor is gone."
        : "The image's lower half lies on a flat floor at your feet; its horizon sits at camera height.";

    public const string CameraHeightTooltip = "How high the image was taken above the floor: its horizon sits this far above your feet.";
    public const string CameraHeightDisabledTooltip = "Not used: with an open bottom the image's horizon is at your feet.";

    /// <summary>The Bottom choice is offered while ground projection is on, unless the half dome's kill switch is off.</summary>
    public bool ShowsBottomChoice(bool halfDomeAvailable) => GroundProjection && halfDomeAvailable;

    /// <summary>The Ground projection checkbox's tooltip; with the kill switch off, today's text (the legacy floor).</summary>
    public static string ProjectionTooltip(bool halfDomeAvailable) => halfDomeAvailable
        ? "Stands the dome on the floor at your feet as a half dome."
        : "Flattens the bottom of the dome into a floor, so the image's ground lies at your feet.";

    /// <summary>The camera height slider's tooltip with projection on; with the kill switch off, today's text.</summary>
    public static string HeightTooltip(bool halfDomeAvailable) =>
        halfDomeAvailable ? CameraHeightTooltip : "Distance from the floor to the dome centre.";

    /// <summary>
    /// The ground-projection floor in unit-radius mesh space: height / (radius · vertical scale), so the floor lands at
    /// world y = −height below the centre. Null when projection is off, the camera height is below <see cref="MinCameraHeight"/>
    /// (or not finite), or the floor would not cut the dome.
    /// </summary>
    public float? FloorY()
    {
        if (!GroundProjection || !(HeightOffset >= MinCameraHeight)) return null;
        var extent = Radius * VerticalScale;
        if (!(extent > 0) || HeightOffset >= extent) return null;
        return HeightOffset / extent;
    }

    /// <summary>What ground projection is doing, in words, so a silently inactive floor is visible in the window.</summary>
    public string ProjectionStatus(bool halfDomeAvailable)
    {
        var h = HeightOffset.ToString("0.##", CultureInfo.InvariantCulture);
        var extent = (Radius * VerticalScale).ToString("0.##", CultureInfo.InvariantCulture);
        if (!GroundProjection) return "Off: the dome is a full sphere centred at the height above your feet.";
        if (halfDomeAvailable && Bottom == DomeBottom.Open)
            return "Half dome on the floor, the image's horizon at your feet; the game's ground shows below.";
        if (FloorY() != null)
            return halfDomeAvailable
                ? $"Half dome on the floor with the image's floor; its horizon {h} above your feet."
                : $"Floor at your feet; horizon {h} above it.";
        return !(HeightOffset >= MinCameraHeight)
            ? $"Inactive: the camera height ({h}) must be at least {MinCameraHeight.ToString("0.##", CultureInfo.InvariantCulture)}."
            : $"Inactive: the camera height ({h}) must be below the dome's height ({extent}). Lower it or raise the radius.";
    }

    /// <summary>Projection is on but draws the plain sphere (the camera height is out of range).</summary>
    public bool IsProjectionInactive(bool halfDomeAvailable) =>
        GroundProjection && !(halfDomeAvailable && Bottom == DomeBottom.Open) && FloorY() == null;

    public bool ProjectionInactive => IsProjectionInactive(true);

    /// <summary>A copy with all six values (per-backdrop settings, docs/adr/0014; the bottom, docs/adr/0015).</summary>
    public DomeSettings Copy() => new()
    {
        Radius = Radius, VerticalScale = VerticalScale, HeightOffset = HeightOffset, YawDegrees = YawDegrees, GroundProjection = GroundProjection,
        Bottom = Bottom,
    };

    /// <summary>Takes all six values from <paramref name="other"/>, keeping this instance (a reference held elsewhere follows).</summary>
    public void CopyFrom(DomeSettings other)
    {
        Radius = other.Radius;
        VerticalScale = other.VerticalScale;
        HeightOffset = other.HeightOffset;
        YawDegrees = other.YawDegrees;
        GroundProjection = other.GroundProjection;
        Bottom = other.Bottom;
    }

    public void Clamp(DomeMode mode)
    {
        var d = mode == DomeMode.Sky ? SkyDefaults() : RoomDefaults();
        Radius = Fix(Radius, RadiusRange(mode), d.Radius);
        VerticalScale = Fix(VerticalScale, VerticalScaleRange, d.VerticalScale);
        HeightOffset = Fix(HeightOffset, HeightRange(mode), d.HeightOffset);
        YawDegrees = Fix(YawDegrees, YawRange, d.YawDegrees);
        if (!Enum.IsDefined(Bottom)) Bottom = DomeBottom.ImageFloor;
    }

    internal static float Fix(float v, (float Min, float Max) range, float fallback) =>
        float.IsFinite(v) ? Math.Clamp(v, range.Min, range.Max) : fallback;
}
