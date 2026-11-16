namespace Milieu.Interop;

/// <summary>
/// The game's environment-state layout that sun off relies on (docs/adr/0013). Dalamud's bundled ClientStructs names
/// <c>EnvManager.EnvState</c> at 0x58 but has no lighting fields, so the lighting offsets are mirrored from Brio
/// (Game/World/BrioEnvManager.cs: <c>EnvState.EnvironmentLighting</c> at 0x20, <c>EnvLighting.SunlightColor</c> +0x00,
/// <c>MoonlightColor</c> +0x0C, <c>AmbientColor</c> +0x18; its header credits Ktisis). A scoped exception to ADR 0009's
/// "no local mirrors" rule, guarded by the plausibility check and the exact own-state address match.
/// </summary>
public static class SunOffLayout
{
    /// <summary>EnvManager's own (rendered) state: ClientStructs' <c>EnvManager.EnvState</c>, Brio's <c>BrioEnvManager.EnvState</c>.</summary>
    public const int EnvManagerStateOffset = 0x58;

    /// <summary>Brio's <c>BrioEnvManager.GPoseBrightness</c>, another EnvState (diagnostics only; never written).</summary>
    public const int GPoseStateOffset = 0x448;

    /// <summary>EnvState + EnvLighting (0x20) + SunlightColor (0x00), a Vector3 (Brio, Ktisis).</summary>
    public const int SunlightColorOffset = 0x20;

    /// <summary>EnvState + EnvLighting (0x20) + MoonlightColor (0x0C), right after the sun colour (Brio, Ktisis).</summary>
    public const int MoonlightColorOffset = 0x2C;

    public const int ColourBytes = 12;

    /// <summary>Sun and moon colour, contiguous: EnvState 0x20..0x37. Ambient (0x38..) is not touched.</summary>
    public const int PatchLength = 24;

    public const int SampleFloats = 6;

    /// <summary>Write only into EnvManager's own state, never into the other EnvState copies this function updates.</summary>
    public const bool OwnStateOnly = true;

    /// <summary>The largest value accepted as a colour channel by the plausibility check.</summary>
    public const float ColourMax = 100f;

    /// <summary>Fewer own-state calls than this in the first 600 episode ticks logs the low call rate warning.</summary>
    public const long MinOwnStateCallsPerWindow = 300;

    public const int DeltaSlots = 4;

    /// <summary>An inconclusive (all-zero) sample is re-armed at most once per this many ticks.</summary>
    public const int RearmIntervalTicks = 60;
}
