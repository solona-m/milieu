using System;
using System.Linq;
using Milieu.Interop;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>Sun off's pure core (docs/adr/0013): the post-step on a fake manager, the layout, the scan pick and the check.</summary>
public sealed unsafe class SunOffPatchTests : IDisposable
{
    private readonly EnvBuffer buf = new();

    public void Dispose() => buf.Dispose();

    private static SunOffShared PassedShared(EnvBuffer b)
    {
        var s = new SunOffShared();
        s.SetWriting(true);
        b.SetColours(0.8f, 0.7f, 0.6f, 0.1f, 0.1f, 0.2f);
        Assert.Equal(PostResult.Probed, b.Post(s));
        s.Approve();
        Assert.Equal(SunOffProbe.Passed, s.Probe);
        return s;
    }

    [Fact] // [AC-sun-off-1]
    public void Written_ZeroesSunAndMoonOnly()
    {
        var s = PassedShared(buf);
        var before = buf.Snapshot();
        Assert.Equal(PostResult.Written, buf.Post(s));
        var after = buf.Snapshot();
        const int from = SunOffLayout.EnvManagerStateOffset + 0x20, to = SunOffLayout.EnvManagerStateOffset + 0x38;
        for (var i = 0; i < EnvBuffer.Size; i++)
        {
            if (i >= from && i < to) Assert.Equal(0, after[i]);
            else Assert.Equal(before[i], after[i]);
        }
        Assert.All(after.Skip(to).Take(12), b => Assert.Equal(0xAA, b));   // ambient untouched
    }

    [Theory] // [AC-sun-off-5] [EDGE-sun-off-1]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PickAddress(int count)
    {
        var matches = Enumerable.Range(1, count).Select(i => (nint)(0x1000 * i));
        Assert.Equal(count == 1 ? (nint?)0x1000 : null, SunOffScan.PickAddress(matches));
    }

    [Theory] // [AC-sun-off-5] [EDGE-sun-off-26] [EDGE-sun-off-29]
    [InlineData(float.NaN, PlausibilityVerdict.Fail)]
    [InlineData(float.PositiveInfinity, PlausibilityVerdict.Fail)]
    [InlineData(-0.5f, PlausibilityVerdict.Fail)]
    [InlineData(101f, PlausibilityVerdict.Fail)]
    [InlineData(1e-40f, PlausibilityVerdict.Fail)]
    [InlineData(100f, PlausibilityVerdict.Pass)]
    public void PlausibilityCheck(float first, PlausibilityVerdict expected)
    {
        Assert.Equal(expected, SunOffPlausibility.Check([first, 0.5f, 0.5f, 0f, 0f, 0f]));
    }

    [Fact] // [AC-sun-off-5] [EDGE-sun-off-29]
    public void PlausibilityCheck_AllZeroInconclusive_ColoursPass()
    {
        Assert.Equal(PlausibilityVerdict.Inconclusive, SunOffPlausibility.Check([0f, 0f, 0f, 0f, 0f, 0f]));
        Assert.Equal(PlausibilityVerdict.Pass, SunOffPlausibility.Check([0.8f, 0.7f, 0.6f, 0f, 0f, 0f]));
    }

    [Theory] // [EDGE-sun-off-12]
    [InlineData("FlagOff", PostResult.NotWriting)]
    [InlineData("A1Null", PostResult.NoState)]
    [InlineData("EnvNull", PostResult.NotOwnState)]
    [InlineData("OtherState", PostResult.NotOwnState)]
    public void NoWrite_Cases(string how, PostResult expected)
    {
        var s = PassedShared(buf);
        if (how == "FlagOff") s.SetWriting(false);
        var before = buf.Snapshot();
        var result = how switch
        {
            "A1Null" => SunOffPostStep.Run(null, buf.Env, s),
            "EnvNull" => SunOffPostStep.Run(buf.State, null, s),
            "OtherState" => SunOffPostStep.Run(buf.Env + 0x28, buf.Env, s),
            _ => buf.Post(s),
        };
        Assert.Equal(expected, result);
        Assert.Equal(before, buf.Snapshot());
    }

    [Fact] // [EDGE-sun-off-13]
    public void LayoutConstants()
    {
        Assert.Equal(SunOffLayout.MoonlightColorOffset, SunOffLayout.SunlightColorOffset + SunOffLayout.ColourBytes);
        Assert.Equal(24, SunOffLayout.PatchLength);
        Assert.Equal(2 * SunOffLayout.ColourBytes, SunOffLayout.PatchLength);
        Assert.Equal(0x58, SunOffLayout.EnvManagerStateOffset);
        Assert.Equal(0x448, SunOffLayout.GPoseStateOffset);
        Assert.Equal(0x20, SunOffLayout.SunlightColorOffset);
        Assert.True(SunOffLayout.OwnStateOnly);
    }

    [Fact] // [EDGE-sun-off-13]
    public void StateOffset_MatchesClientStructs()
    {
        var env = new FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager();
        var offset = (byte*)&env.EnvState - (byte*)&env;
        Assert.Equal(SunOffLayout.EnvManagerStateOffset, (int)offset);
    }

    [Fact] // [EDGE-sun-off-14]
    public void PostStep_TouchesOnlyTheTargetBytes_WhateverWasThere()
    {
        var s = PassedShared(buf);
        buf.SetColours(3.4f, 2.4f, 3.4f, 5.6f, 2f, 5.6f);   // another plugin's frozen colours
        var before = buf.Snapshot();
        Assert.Equal(PostResult.Written, buf.Post(s));
        var after = buf.Snapshot();
        var changed = Enumerable.Range(0, EnvBuffer.Size).Where(i => before[i] != after[i]).ToArray();
        Assert.All(changed, i => Assert.InRange(i, 0x58 + 0x20, 0x58 + 0x37));
    }

    [Fact] // [EDGE-sun-off-19]
    public void PostStep_AllocatesNothing()
    {
        var s = new SunOffShared();
        var waiting = new SunOffShared();   // stays Captured: AwaitingCheck on every call
        var rearming = new SunOffShared();  // re-armed every iteration: Probed on every call
        using var other = new EnvBuffer();
        void AllPaths()
        {
            buf.Post(waiting);
            rearming.Rearm();
            if (buf.Post(rearming) != PostResult.Probed) throw new InvalidOperationException("expected Probed");
            s.SetWriting(false); buf.Post(s);                          // NotWriting
            s.SetWriting(true);
            SunOffPostStep.Run(null, buf.Env, s);                       // NoState
            SunOffPostStep.Run(buf.Env + 0x28, buf.Env, s);             // NotOwnState (+ a delta claim)
            SunOffPostStep.Run(other.State, other.Env, s);              // the probe state decides below
            buf.Post(s);
        }
        // Warm-up: every PostResult path once (JIT, static initialization), including Probed and Written.
        buf.SetColours(0.8f, 0.7f, 0.6f, 0f, 0f, 0f);
        s.SetWriting(true);
        Assert.Equal(PostResult.Probed, buf.Post(s));
        Assert.Equal(PostResult.AwaitingCheck, buf.Post(s));
        s.Approve();
        Assert.Equal(PostResult.Written, buf.Post(s));
        waiting.SetWriting(true);
        buf.SetColours(0.8f, 0.7f, 0.6f, 0f, 0f, 0f);
        Assert.Equal(PostResult.Probed, buf.Post(waiting));
        Assert.Equal(PostResult.AwaitingCheck, buf.Post(waiting));
        rearming.SetWriting(true);
        Assert.Equal(PostResult.Probed, buf.Post(rearming));
        AllPaths();
        // Tiered JIT can re-compile these methods mid-run and charge a few bytes to this thread once, so the paths pass if
        // any of three rounds allocates nothing. Each round is at least twice as long as everything before it, so a buffer
        // that grows by up to about 3x resizes inside every round, and anything allocating at least every 1000 calls shows in every round.
        int[] rounds = [1000, 2000, 6000];
        var allocated = new long[rounds.Length];
        for (var round = 0; round < rounds.Length; round++)
        {
            var baseline = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < rounds[round]; i++) AllPaths();
            allocated[round] = GC.GetAllocatedBytesForCurrentThread() - baseline;
            if (allocated[round] == 0) return;
        }
        Assert.Fail($"every round allocated: {string.Join(", ", allocated)} bytes");
    }
}
