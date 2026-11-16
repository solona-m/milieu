namespace Milieu.Lighting;

/// <summary>
/// Tuning for synced VFX lights (docs/adr/0011). Every value is an UNVERIFIED starting point to tune in game: the
/// effector's range (DstS) and brightness (Bri) semantics aren't documented, so they are kept here in one place. Changing
/// them needs neither a rig version bump nor rig regeneration (they apply at plan time, like LightRigLayout's brightness).
/// </summary>
public static class VfxLightLayout
{
    /// <summary>Key distance from the anchor (sky); in room mode it is clamped inside the dome.</summary>
    public const float KeyDistance = 30f;

    /// <summary>Fill distance from the anchor.</summary>
    public const float FillDistance = 8f;

    /// <summary>The closest a light may sit to the anchor.</summary>
    public const float MinDistance = 1.5f;

    /// <summary>Room mode keeps lights inside the dome: distance ≤ this fraction of the radius.</summary>
    public const float RoomDistanceFraction = 0.9f;

    /// <summary>Key Bri at strength 1, meant to roughly match the sun (Bri 5 cast a faint shadow next to it in game).</summary>
    public const float KeyBrightness = 40f;

    /// <summary>
    /// The stand-in key's share of <see cref="KeyBrightness"/>, for an image with no sun (ADR 0011). 1: full key brightness,
    /// so it casts a visible shadow. UNVERIFIED (ADR 0011 item 11): whether that looks like midday sun on a sunset.
    /// </summary>
    public const float StandInKeyScale = 1f;

    /// <summary>Key range (DstS).</summary>
    public const float KeyRange = 60f;

    /// <summary>Key shadow range (SCDN/SCDF); the game's recipe used 0.5/15 at short range, both near/far variants cast shadows.</summary>
    public const float KeyShadowNear = 0.5f;
    public const float KeyShadowFar = 45f;

    /// <summary>The strongest fill's Bri at strength 1; weaker fills scale by their share.</summary>
    public const float FillBrightness = 4f;

    /// <summary>Fill range (DstS).</summary>
    public const float FillRange = 16f;

    /// <summary>Fills cast no shadows; their (unused) shadow range must still be valid.</summary>
    public const float FillShadowNear = 0.5f;
    public const float FillShadowFar = 15f;

    /// <summary>
    /// Fills ride on particle-less emitters of their own, a structure not yet loaded in game (UNVERIFIED item 1). Off: only
    /// the key ships, on the dome emitter (the link proven in game).
    /// </summary>
    public const bool FillsOnOwnEmitters = false;

    /// <summary>
    /// Brightness against distance (UNVERIFIED tuning item 11): Bri ×= (d / nominal)^exponent, which only matters when the
    /// room clamp shortens d. 0 = no compensation.
    /// </summary>
    public const float DistanceCompensationExponent = 0f;
}
