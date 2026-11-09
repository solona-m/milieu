using System.Numerics;

namespace Milieu.Lighting;

/// <summary>How a light's brightness falls off with distance (values match the game's LightFalloffType).</summary>
public enum LightFalloff { Linear = 0, Quadratic = 1, Cubic = 2 }

/// <summary>
/// The light rig's constants. The powers, distance and ranges are starting values, UNVERIFIED in game (docs/adr/0009):
/// tune them there. <see cref="SpotForward"/> is the axis a spot light emits along, per the FFXIVClientStructs docs
/// (Render/Light.cs: "a cone along its positive Z axis"), to be confirmed in game.
/// </summary>
public static class LightRigLayout
{
    /// <summary>Key and fill lights all sit this far from the anchor.</summary>
    public const float LightDistance = 8f;

    public const float KeyPower = 30f;
    public const float FillPower = 10f;

    /// <summary>No colour channel exceeds this; a brighter light is scaled down uniformly (chroma kept).</summary>
    public const float MaxChannel = 120f;

    /// <summary>A key plus up to three fills.</summary>
    public const int MaxLights = 4;
    public const int MaxFills = 3;

    public const float KeyConeDegrees = 60f;
    public const float KeyAngularFalloffDegrees = 15f;
    public const float KeyRange = 30f;
    public const float FillRange = 20f;

    public const LightFalloff Falloff = LightFalloff.Quadratic;
    public const float FalloffFactor = 1f;
    public const float ShadowPlaneNear = 0.01f;
    public const float ShadowPlaneFar = 30f;
    public const float CharacterShadowRange = 110f;

    /// <summary>The anchor is the local player's position raised by this much (roughly chest height).</summary>
    public const float AnchorHeight = 1f;

    /// <summary>Light positions are rewritten only once the anchor moved more than this.</summary>
    public const float AnchorMoveThreshold = 0.05f;

    /// <summary>The axis a spot light emits along, in its local space.</summary>
    public static readonly Vector3 SpotForward = Vector3.UnitZ;

    /// <summary>Consecutive ticks with everything in place (GPose included) before lights are created.</summary>
    public const int GposeSettleTicks = 30;

    /// <summary>Consecutive ticks with lights alive in GPose after which the crash sentinel is cleared.</summary>
    public const int StableTicks = 600;
}
