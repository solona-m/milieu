using System;
using System.Collections.Generic;
using System.Linq;

namespace Milieu.Lighting;

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

/// <summary>Picks the hook address from a signature scan.</summary>
public static class SunOffScan
{
    /// <summary>The address when the signature matched exactly once; null for no match or several.</summary>
    public static nint? PickAddress(IEnumerable<nint> matches)
    {
        var first = matches.Take(2).ToList();
        return first.Count == 1 ? first[0] : null;
    }
}

public enum PlausibilityVerdict { Pass, Fail, Inconclusive }

/// <summary>The one-time check that the six target floats look like the game's sun and moon colours (docs/adr/0013).</summary>
public static class SunOffPlausibility
{
    /// <summary>
    /// Fail: any value non-finite, subnormal (what small integers or pointer halves look like as floats), negative or above
    /// <see cref="SunOffLayout.ColourMax"/>. Inconclusive: all exactly 0 (zeros say nothing about the offsets). Pass: otherwise.
    /// A heuristic that catches garbage, not a proof.
    /// </summary>
    public static PlausibilityVerdict Check(ReadOnlySpan<float> values)
    {
        var nonZero = false;
        foreach (var v in values)
        {
            if (!float.IsFinite(v) || v < 0 || v > SunOffLayout.ColourMax) return PlausibilityVerdict.Fail;
            if (v == 0) continue;
            if (!float.IsNormal(v)) return PlausibilityVerdict.Fail;
            nonZero = true;
        }
        return nonZero ? PlausibilityVerdict.Pass : PlausibilityVerdict.Inconclusive;
    }
}

public enum PostResult { NotWriting, NoState, NotOwnState, Probed, AwaitingCheck, Written }

/// <summary>
/// What the environment-update detour does after the game's own code has run (docs/adr/0013). Static, closure-free and
/// allocation-free: it runs on the game's thread for every call. The address checks are the real guard (a native access
/// violation isn't catchable): it writes only into EnvManager's own state, and only after the sample was approved.
/// </summary>
public static class SunOffPostStep
{
    public static unsafe PostResult Run(byte* a1, byte* env, SunOffShared s)
    {
        s.Classify(a1, env);
        if (!s.Writing) return PostResult.NotWriting;
        if (a1 == null) return PostResult.NoState;
        if (SunOffLayout.OwnStateOnly && (env == null || a1 != env + SunOffLayout.EnvManagerStateOffset)) return PostResult.NotOwnState;
        switch (s.Probe)
        {
            case SunOffProbe.Passed:
                new Span<byte>(a1 + SunOffLayout.SunlightColorOffset, SunOffLayout.PatchLength).Clear();
                return PostResult.Written;
            case SunOffProbe.NotProbed when s.TryBeginCapture():
                s.FinishCapture(a1 + SunOffLayout.SunlightColorOffset);
                return PostResult.Probed;
            default:
                return PostResult.AwaitingCheck;
        }
    }
}
