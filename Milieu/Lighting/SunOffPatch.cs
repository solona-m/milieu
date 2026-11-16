using System;
using System.Collections.Generic;
using System.Linq;
using Milieu.Interop;

namespace Milieu.Lighting;

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
