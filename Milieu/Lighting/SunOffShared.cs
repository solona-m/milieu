using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Milieu.Lighting;

/// <summary>The one-time plausibility probe (docs/adr/0013): which side may move it is fixed, so the two never overlap.</summary>
public enum SunOffProbe
{
    /// <summary>Nothing captured yet. The detour may claim it (CAS to Capturing).</summary>
    NotProbed = 0,
    /// <summary>The detour won the claim and is copying the six floats.</summary>
    Capturing = 1,
    /// <summary>The sample is ready for the framework thread.</summary>
    Captured = 2,
    /// <summary>The framework thread approved the sample: zeroing may run (for the rest of the session).</summary>
    Passed = 3,
    /// <summary>The sample didn't look like colours: never zero this session.</summary>
    Failed = 4,
}

/// <summary>Per-call diagnostic counters (docs/adr/0013), totals since the hook was installed.</summary>
public readonly record struct SunOffCounts(long Calls, long AtState, long AtGPoseState, long Other, long NoManager)
{
    public SunOffCounts Minus(SunOffCounts baseline) =>
        new(Calls - baseline.Calls, AtState - baseline.AtState, AtGPoseState - baseline.AtGPoseState, Other - baseline.Other,
            NoManager - baseline.NoManager);
}

/// <summary>
/// Everything the environment-update detour reads or writes (docs/adr/0013), in one object allocated once, so the detour
/// touches a single reference and allocates nothing. The detour (a game thread) uses only <see cref="Volatile"/> and
/// <see cref="Interlocked"/> operations on preallocated fields; <see cref="SunOff"/> (the framework thread) sets the flag
/// and moves the probe from Captured on; <see cref="Close"/> (dispose) ends writing for good.
/// </summary>
public sealed unsafe class SunOffShared
{
    private int writing;
    private int closed;
    private int probe;
    private readonly int[] sample = new int[SunOffLayout.SampleFloats];
    private readonly long[] deltas = new long[SunOffLayout.DeltaSlots];
    private long calls, atState, atGPose, other, noManager;

    private const long EmptySlot = long.MinValue;

    public SunOffShared() => Array.Fill(deltas, EmptySlot);

    /// <summary>The detour zeroes (once approved) only while this is set.</summary>
    public bool Writing => Volatile.Read(ref writing) != 0;

    public bool Closed => Volatile.Read(ref closed) != 0;

    public SunOffProbe Probe => (SunOffProbe)Volatile.Read(ref probe);

    /// <summary>Framework thread. Ignored once <see cref="Close"/> has run.</summary>
    public void SetWriting(bool on)
    {
        if (!on) { Volatile.Write(ref writing, 0); return; }
        if (Closed) return;
        Volatile.Write(ref writing, 1);
        if (Closed) Volatile.Write(ref writing, 0);   // a Close racing this call wins
    }

    /// <summary>Dispose: the flag drops and no later <see cref="SetWriting"/> can raise it.</summary>
    public void Close()
    {
        Volatile.Write(ref closed, 1);
        Volatile.Write(ref writing, 0);
    }

    /// <summary>Copies the captured sample into <paramref name="into"/> (six floats); false unless the probe is Captured.</summary>
    public bool TryReadSample(Span<float> into)
    {
        if (Probe != SunOffProbe.Captured) return false;   // the acquire read orders the sample reads below
        for (var i = 0; i < SunOffLayout.SampleFloats; i++) into[i] = BitConverter.Int32BitsToSingle(Volatile.Read(ref sample[i]));
        return true;
    }

    /// <summary>Captured → Passed: zeroing starts on the next own-state call.</summary>
    public void Approve() => Interlocked.CompareExchange(ref probe, (int)SunOffProbe.Passed, (int)SunOffProbe.Captured);

    /// <summary>Failed, and the flag drops: nothing is ever zeroed this session.</summary>
    public void Reject()
    {
        Volatile.Write(ref probe, (int)SunOffProbe.Failed);
        Volatile.Write(ref writing, 0);
    }

    /// <summary>Captured → NotProbed (an inconclusive, all-zero sample); nothing from any other state.</summary>
    public bool Rearm() =>
        Interlocked.CompareExchange(ref probe, (int)SunOffProbe.NotProbed, (int)SunOffProbe.Captured) == (int)SunOffProbe.Captured;

    /// <summary>Framework thread: the counters now.</summary>
    public SunOffCounts Counts => new(Interlocked.Read(ref calls), Interlocked.Read(ref atState), Interlocked.Read(ref atGPose),
                                      Interlocked.Read(ref other), Interlocked.Read(ref noManager));

    /// <summary>Framework thread: the recorded raw deltas (a copy; may allocate).</summary>
    public IReadOnlyList<long> Deltas() =>
        Enumerable.Range(0, deltas.Length).Select(i => Volatile.Read(ref deltas[i])).Where(d => d != EmptySlot).ToArray();

    /// <summary>
    /// Detour: counts the call in exactly one bucket (own state, Brio's GPose state, no manager, other) and records its raw
    /// delta from the manager in the first free of the fixed slots (a null a1 counts as other, with no delta).
    /// </summary>
    internal void Classify(byte* a1, byte* env)
    {
        Interlocked.Increment(ref calls);
        if (env == null) { Interlocked.Increment(ref noManager); return; }
        if (a1 == null) { Interlocked.Increment(ref other); return; }
        var delta = (long)a1 - (long)env;
        if (delta == SunOffLayout.EnvManagerStateOffset) Interlocked.Increment(ref atState);
        else if (delta == SunOffLayout.GPoseStateOffset) Interlocked.Increment(ref atGPose);
        else Interlocked.Increment(ref other);
        RecordDelta(delta);
    }

    /// <summary>The first distinct deltas, one slot each (CAS); a repeat isn't stored twice and a delta past the last slot is dropped.</summary>
    internal void RecordDelta(long delta)
    {
        for (var i = 0; i < deltas.Length; i++)
        {
            var current = Volatile.Read(ref deltas[i]);
            if (current == delta) return;
            if (current != EmptySlot) continue;
            var previous = Interlocked.CompareExchange(ref deltas[i], delta, EmptySlot);
            if (previous == EmptySlot || previous == delta) return;   // ours, or another call stored the same delta
        }
    }

    /// <summary>Detour: NotProbed → Capturing; only the call that wins captures.</summary>
    internal bool TryBeginCapture() =>
        Interlocked.CompareExchange(ref probe, (int)SunOffProbe.Capturing, (int)SunOffProbe.NotProbed) == (int)SunOffProbe.NotProbed;

    /// <summary>Detour, after winning <see cref="TryBeginCapture"/>: copies the six floats' bits, then publishes Captured.</summary>
    internal void FinishCapture(byte* source)
    {
        var floats = (int*)source;
        for (var i = 0; i < SunOffLayout.SampleFloats; i++) Volatile.Write(ref sample[i], floats[i]);
        Volatile.Write(ref probe, (int)SunOffProbe.Captured);
    }
}
