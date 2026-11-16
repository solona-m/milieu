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

    /// <summary>
    /// Key Bri at strength 1, tuned in game (2026-11-16, Mad Bird Ring, sky dome, key 30 units out, Attn 1): Bri 4000 was far too
    /// bright and Bri 200 looked right.
    /// </summary>
    public const float KeyBrightness = 200f;

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
    /// Fills ride on particle-less emitters of their own, a structure not yet loaded in game (UNVERIFIED, ADR 0011 item 1). Off: only
    /// the key ships, on the dome emitter (the link proven in game).
    /// </summary>
    public const bool FillsOnOwnEmitters = false;

    /// <summary>
    /// Brightness against distance (ADR 0011 item 7): Bri ×= (d / nominal)^exponent, which only matters when the room clamp
    /// shortens d. Tuned in game (2026-11-16): a room-dome key 8.4 units out looked right at 40% of the sky dome's brightness,
    /// and (8.4 / 30)^0.72 ≈ 0.40 (≈ 0.42 at the default room radius's 9 units). 0 = no compensation.
    /// </summary>
    public const float DistanceCompensationExponent = 0.72f;
}
