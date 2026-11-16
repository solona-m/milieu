using System;
using System.Collections.Generic;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>A game clock and time-of-day field to drive NightWhileShown.</summary>
public sealed class FakeEnvTime : IEnvTime
{
    public bool Available { get; set; } = true;
    public float? Day = 50000f;     // ~13:53 ET
    public float? Clock = 50000f;
    public bool WriteSucceeds = true;
    public int Reads;
    public readonly List<float> Writes = [];

    public float? ReadDayTimeSeconds() { Reads++; return Day; }
    public float? ReadEorzeaDaySeconds() { Reads++; return Clock; }

    public bool WriteDayTimeSeconds(float seconds)
    {
        if (!WriteSucceeds) return false;
        Writes.Add(seconds);
        Day = seconds;
        return true;
    }
}

public class NightWhileShownTests
{
    private readonly FakeEnvTime env = new();
    private readonly FakeGame game = new();
    private readonly Configuration config = new() { NightWhileShown = true };
    private readonly FakeSentinel sentinel = new();
    private readonly FakeLog log = new();
    private bool drawn = true;
    private bool unloading;
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private NightWhileShown n;

    public NightWhileShownTests() => n = Make();

    private NightWhileShown Make(FakeSentinel? s = null) =>
        new(env, game, () => drawn, config, s ?? sentinel, log, () => unloading, () => now);

    private void Tick(int ms = 16) { now += TimeSpan.FromMilliseconds(ms); n.Tick(); }

    [Fact] // [AC-synced-lighting-5]
    public void OffByDefault_NoWrites()
    {
        Assert.False(new Configuration().NightWhileShown);   // [EDGE-synced-lighting-32]
        config.NightWhileShown = false;
        n = Make();
        Tick(); Tick();
        Assert.Empty(env.Writes);
        Assert.Equal(NightStatus.Off, n.Status);
    }

    [Fact] // [AC-synced-lighting-5]
    public void OnWhileDrawn_HoldsNight_SentinelSet()
    {
        Tick(); Tick();
        Assert.Equal([NightTime.Seconds, NightTime.Seconds], env.Writes);
        Assert.Equal(NightStatus.On, n.Status);
        Assert.True(sentinel.IsSet);
    }

    [Fact] // [AC-synced-lighting-5]
    public void Release_WritesTheGameClockBack_WhenStillOurs()
    {
        Tick();
        env.Clock = 50010f;
        n.Release();
        Assert.Equal(50010f, env.Writes[^1]);
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [EDGE-synced-lighting-20]
    public void Release_SomethingElseChangedIt_NoWriteBack()
    {
        Tick();
        env.Day = 20000f;   // another plugin
        var writes = env.Writes.Count;
        n.Release();
        Assert.Equal(writes, env.Writes.Count);
    }

    [Theory] // [EDGE-synced-lighting-43]
    [InlineData(10f, true)]    // 500 ms → tolerance 2 + 20.57 × 0.5 × 1.5 ≈ 17.4
    [InlineData(30f, false)]
    public void Release500msLate_FieldAdvancedByTheGame_ScaledTolerance(float advanced, bool restores)
    {
        Tick();
        now += TimeSpan.FromMilliseconds(500);
        env.Day = NightTime.Seconds + advanced;
        var writes = env.Writes.Count;
        n.Release();
        Assert.Equal(restores, env.Writes.Count == writes + 1);
    }

    [Fact] // [EDGE-synced-lighting-19]
    public void NoDomeDrawn_Waits_TurnedOffWhileDrawn_RestoresSameTick()
    {
        drawn = false;
        Tick();
        Assert.Empty(env.Writes);
        Assert.Equal(NightStatus.Waiting, n.Status);
        drawn = true;
        Tick();
        config.NightWhileShown = false;
        Tick();
        Assert.Equal(env.Clock, env.Writes[^1]);
        Assert.Equal(NightStatus.Off, n.Status);
    }

    [Fact] // [AC-synced-lighting-6] [EDGE-synced-lighting-42]
    public void CrossCheckMismatch_NotInThisZone_NoWrite_RecheckedAfterTerritoryChange()
    {
        env.Day = 10000f;   // a zone with its own fixed time
        Tick(); Tick();
        Assert.Empty(env.Writes);
        Assert.Equal(NightStatus.NotInThisZone, n.Status);
        n.OnTerritoryChanged();
        env.Day = env.Clock;
        Tick();
        Assert.Equal(NightStatus.On, n.Status);
    }

    [Fact] // [EDGE-synced-lighting-42]
    public void CrossCheck_WrapsAroundMidnight()
    {
        env.Day = 86390f;
        env.Clock = 50f;   // 60 ET s apart across midnight
        Tick();
        Assert.Equal(NightStatus.On, n.Status);
    }

    [Theory] // [AC-synced-lighting-6] [EDGE-synced-lighting-24]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    [InlineData(86400f)]
    public void ImplausibleRead_LatchesUnavailable_UntilOffOn(float bad)
    {
        env.Day = bad;
        Tick(); Tick();
        Assert.Empty(env.Writes);
        Assert.Equal(NightStatus.Unavailable, n.Status);
        env.Day = env.Clock;
        Tick();
        Assert.Equal(NightStatus.Unavailable, n.Status);   // latched
        config.NightWhileShown = false; Tick();
        config.NightWhileShown = true; Tick();
        Assert.Equal(NightStatus.On, n.Status);
    }

    [Fact] // [EDGE-synced-lighting-24] [EDGE-synced-lighting-44]
    public void AddressNeverResolved_Unavailable_NotCreatedYet_WaitsNoLatch()
    {
        env.Day = null;   // the object isn't created yet
        Tick();
        Assert.Equal(NightStatus.Waiting, n.Status);
        env.Day = env.Clock;
        Tick();
        Assert.Equal(NightStatus.On, n.Status);

        var e2 = new FakeEnvTime { Available = false };
        var m = new NightWhileShown(e2, game, () => true, config, new FakeSentinel(), log, () => false, () => now);
        m.Tick();
        Assert.Equal(NightStatus.Unavailable, m.Status);
        Assert.Empty(e2.Writes);
    }

    [Fact] // [EDGE-synced-lighting-24]
    public void WriteFails_LatchesUnavailable()
    {
        env.WriteSucceeds = false;
        Tick();
        Assert.Equal(NightStatus.Unavailable, n.Status);
    }

    [Fact] // [EDGE-synced-lighting-22]
    public void Logout_NoWriteNow_FirstReadyTickRestoresIfOurs()
    {
        Tick();
        var writes = env.Writes.Count;
        game.IsLoggedIn = false;
        n.Forget();
        Assert.Equal(writes, env.Writes.Count);
        Assert.False(sentinel.IsSet);
        env.Clock = 51000f;
        Tick();                          // not ready: still pending
        Assert.Equal(writes, env.Writes.Count);
        game.IsLoggedIn = true;
        drawn = false;
        Tick();
        Assert.Equal(51000f, env.Writes[^1]);   // restored before anything else
    }

    [Fact] // [EDGE-synced-lighting-21]
    public void TerritoryChange_PendingRestoreThenNewEpisode()
    {
        Tick();
        n.OnTerritoryChanged();
        env.Clock = 52000f;
        Tick();
        Assert.Contains(52000f, env.Writes);     // restored first
        Assert.Equal(NightTime.Seconds, env.Writes[^1]);   // then a new episode (the field now equals the clock)
        Assert.Equal(NightStatus.On, n.Status);
    }

    [Fact] // [EDGE-synced-lighting-23]
    public void GameExiting_NoEnvAccessAtAll()
    {
        Tick();
        var reads = env.Reads;
        var writes = env.Writes.Count;
        unloading = true;
        Tick();
        n.Release();
        n.Teardown();
        Assert.Equal((reads, writes), (env.Reads, env.Writes.Count));
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [EDGE-synced-lighting-27]
    public void Teardown_RestoresIfOurs_ThenInert()
    {
        Tick();
        env.Clock = 50100f;
        n.Teardown();
        Assert.Equal(50100f, env.Writes[^1]);
        var writes = env.Writes.Count;
        Tick(); n.Release(); n.Forget();
        Assert.Equal(writes, env.Writes.Count);
    }

    [Fact] // [EDGE-synced-lighting-25]
    public void SentinelAtStartup_CrashGuard_UntilOffOn_ClearedAfterStableTicks()
    {
        var found = new FakeSentinel(setAtStartup: true);
        n = Make(found);
        Tick();
        Assert.Equal(NightStatus.CrashGuard, n.Status);
        Assert.Empty(env.Writes);
        config.NightWhileShown = false; Tick();
        config.NightWhileShown = true; Tick();
        Assert.Equal(NightStatus.On, n.Status);
        Assert.True(found.IsSet);
        for (var i = 0; i < NightWhileShown.StableTicks; i++) Tick();
        Assert.False(found.IsSet);
    }

    [Theory] // [EDGE-synced-lighting-28]
    [InlineData("Hidden")]
    [InlineData("PenumbraGone")]
    public void HiddenEvent_ReleasesWithinTheCall(string _)
    {
        Tick();
        n.Release();
        Assert.Equal(env.Clock, env.Writes[^1]);
    }

    [Fact]
    public void Helpers_DistanceAndTolerance()
    {
        Assert.Equal(100f, NightTime.Distance(86350f, 50f), 3);
        Assert.Equal(NightTime.OwnershipMargin, NightTime.OwnershipTolerance(TimeSpan.Zero), 4);
        Assert.Equal(NightTime.OwnershipCap, NightTime.OwnershipTolerance(TimeSpan.FromHours(1)), 4);
    }
}
