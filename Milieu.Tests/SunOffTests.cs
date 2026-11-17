using System;
using System.Linq;
using Milieu.Lighting;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>Sun off's lifecycle (docs/adr/0013), with a fake switch and the post-step on a fake manager.</summary>
public sealed class SunOffTests : IDisposable
{
    private readonly FakeSunSwitch sw = new();
    private readonly FakeGame game = new();
    private readonly FakeLog log = new();
    private readonly Configuration config = new();
    private readonly EnvBuffer buf = new();
    private FakeSentinel sentinel = new();
    private LitBackdrop? lit = new("pano.jpg", new DomeView(0, false, 1, 1, 1));
    private DeliveryMode delivery = DeliveryMode.Local;
    private bool unloading;
    private SunOff s;

    public SunOffTests() => s = Make();

    public void Dispose() => buf.Dispose();

    private SunOff Make() => new(sw, game, () => lit, () => delivery, config, sentinel, log, () => unloading);

    private static readonly float[] Colours = [0.8f, 0.7f, 0.6f, 0.1f, 0.1f, 0.2f];

    /// <summary>Starts an episode, captures a colour sample and approves it: the sun is off.</summary>
    private void TurnOn()
    {
        s.Tick();
        buf.SetColours(Colours);
        Assert.Equal(PostResult.Probed, buf.Post(sw.Shared));
        s.Tick();
        Assert.Equal(SunOffStatus.On, s.Status);
    }

    private int Lines(string level, string text) => log.Lines.Count(l => l.Level == level && l.Text.Contains(text));

    /// <summary>The probe and the flag at each sentinel Set, in order.</summary>
    private System.Collections.Generic.List<(SunOffProbe Probe, bool Writing)> RecordSets()
    {
        var at = new System.Collections.Generic.List<(SunOffProbe, bool)>();
        sentinel.OnSet = () => at.Add((sw.Shared.Probe, sw.Shared.Writing));
        return at;
    }

    [Fact] // [AC-sun-off-1]
    public void OnLocalShownReady_InstallsOnce_StartingThenOnAfterApproval()
    {
        s.Tick();
        Assert.Equal(1, sw.Installs);
        Assert.True(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.Starting, s.Status);
        Assert.True(sentinel.IsSet);
        buf.SetColours(Colours);
        Assert.Equal(PostResult.Probed, buf.Post(sw.Shared));
        s.Tick();
        Assert.Equal(SunOffStatus.On, s.Status);
        Assert.Equal(PostResult.Written, buf.Post(sw.Shared));
        Assert.Equal(1, sw.Installs);
    }

    [Fact] // [AC-sun-off-1] [EDGE-sun-off-17]
    public void InGPose_StillOn()
    {
        game.IsGPosing = true;
        TurnOn();
        Assert.True(sw.Shared.Writing);
    }

    [Fact] // [AC-sun-off-2]
    public void Release_DropsWritingWithinTheCall()
    {
        TurnOn();
        s.Release();
        Assert.False(sw.Shared.Writing);
        Assert.Equal(PostResult.NotWriting, buf.Post(sw.Shared));
        Assert.Equal(1, Lines("Information", "Sun back on."));
    }

    [Fact] // [AC-sun-off-2] [EDGE-sun-off-11]
    public void Teardown_DropsWriting_ThenInert()
    {
        TurnOn();
        s.Teardown();
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);
        var lines = log.Lines.Count;
        s.Tick();
        s.Release();
        Assert.False(sw.Shared.Writing);
        Assert.Equal(lines, log.Lines.Count);
    }

    [Theory] // [AC-sun-off-3] [EDGE-sun-off-6]
    [InlineData(DeliveryMode.Cushion, false)]
    [InlineData(DeliveryMode.Cushion, true)]
    [InlineData(DeliveryMode.MadBirdRing, false)]
    [InlineData(DeliveryMode.MadBirdRing, true)]
    public void PSyncDelivery_NeverWrites_NotOnlyMe(DeliveryMode mode, bool syncedLit)
    {
        delivery = mode;
        lit = lit! with { Synced = syncedLit };
        for (var i = 0; i < 5; i++) s.Tick();
        Assert.Equal(0, sw.Installs);
        Assert.False(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.NotOnlyMe, s.Status);

        // A switch to PSync in the middle of an episode drops the flag the same tick.
        delivery = DeliveryMode.Local;
        lit = lit with { Synced = false };
        TurnOn();
        delivery = mode;
        s.Tick();
        Assert.False(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.NotOnlyMe, s.Status);
    }

    [Fact] // [AC-sun-off-4] [EDGE-sun-off-15]
    public void UntickWhileShown_StopsNextTick_RetickRestartsWithoutReinstall()
    {
        TurnOn();
        config.SunOffWhileShown = false;
        s.Tick();
        Assert.False(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.Off, s.Status);
        config.SunOffWhileShown = true;
        s.Tick();
        Assert.True(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.On, s.Status);   // the probe passed once this session
        Assert.Equal(1, sw.Installs);
    }

    [Fact] // [AC-sun-off-5] [EDGE-sun-off-1]
    public void SwitchUnavailable_Unavailable_NoInstall()
    {
        sw.Available = false;
        for (var i = 0; i < 3; i++) s.Tick();
        Assert.Equal(SunOffStatus.Unavailable, s.Status);
        Assert.Equal(0, sw.Installs);
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [AC-sun-off-5] [EDGE-sun-off-2]
    public void InstallFails_LatchedUnavailable_SentinelCleared_NoRetry()
    {
        sw.InstallResult = false;
        s.Tick();
        Assert.Equal(SunOffStatus.Unavailable, s.Status);
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);
        Assert.Equal(1, Lines("Warning", "couldn't be hooked"));
        for (var i = 0; i < 3; i++) s.Tick();
        config.SunOffWhileShown = false; s.Tick();
        config.SunOffWhileShown = true; s.Tick();
        Assert.Equal(1, sw.Installs);
        Assert.Equal(SunOffStatus.Unavailable, s.Status);
        Assert.False(sw.Shared.Writing);
    }

    [Fact] // [AC-sun-off-5] [EDGE-sun-off-26]
    public void PlausibilityFail_LatchesUnavailable_NeverZeroes()
    {
        s.Tick();
        buf.SetColours(float.NaN, 0.5f, 0.5f, 0f, 0f, 0f);
        Assert.Equal(PostResult.Probed, buf.Post(sw.Shared));
        s.Tick();
        Assert.Equal(SunOffStatus.Unavailable, s.Status);
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);
        Assert.Equal(1, Lines("Warning", "don't look like colours"));
        var before = buf.Snapshot();
        for (var i = 0; i < 5; i++) { s.Tick(); buf.Post(sw.Shared); }
        config.SunOffWhileShown = false; s.Tick();
        config.SunOffWhileShown = true; s.Tick();
        buf.Post(sw.Shared);
        Assert.Equal(SunOffStatus.Unavailable, s.Status);
        Assert.Equal(before, buf.Snapshot());
    }

    [Fact] // [AC-sun-off-6] [EDGE-sun-off-4]
    public void SentinelAtStartup_CrashGuard_NoInstall_UntilOffOn()
    {
        sentinel = new FakeSentinel(setAtStartup: true);
        s = Make();
        Assert.Equal(1, Lines("Warning", "closed while the sun was off"));
        for (var i = 0; i < 3; i++) s.Tick();
        Assert.Equal(SunOffStatus.CrashGuard, s.Status);
        Assert.Equal(0, sw.Installs);
        s.Release();
        s.Teardown();
        Assert.True(sentinel.IsSet);   // not this session's flag: only off→on clears it

        sentinel = new FakeSentinel(setAtStartup: true);
        s = Make();
        s.Tick();
        config.SunOffWhileShown = false; s.Tick();
        config.SunOffWhileShown = true; s.Tick();
        Assert.Equal(1, sw.Installs);
        Assert.Equal(SunOffStatus.Starting, s.Status);
        Assert.Equal(["Clear", "Set"], sentinel.Calls);
    }

    [Fact] // [EDGE-sun-off-5]
    public void SentinelOrder_SetBeforeInstallAndWriting_ClearedAfter600AndOnStop()
    {
        sw.Order = sentinel.Calls;
        var sets = RecordSets();
        s.Tick();
        Assert.Equal(["Set", "Install"], sentinel.Calls);
        Assert.False(sets[0].Writing);                // set before the flag was raised
        Assert.True(sw.Shared.Writing);
        buf.SetColours(Colours);
        buf.Post(sw.Shared);
        for (var i = 2; i < 600; i++) s.Tick();       // approved at tick 2
        Assert.True(sentinel.IsSet);
        s.Tick();                                     // tick 600: 598 ticks after the first Passed
        s.Tick(); s.Tick();                           // tick 602: 600 after
        Assert.False(sentinel.IsSet);
        Assert.Equal(["Set", "Install", "Clear"], sentinel.Calls);

        lit = null; s.Tick();                         // Stop: nothing of ours left to clear
        lit = new("pano.jpg", new DomeView(0, false, 1, 1, 1));
        s.Tick();                                     // a new episode sets it again
        Assert.True(sentinel.IsSet);
        Assert.False(sets[^1].Writing);               // again before the flag
        s.Release();
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [EDGE-sun-off-5]
    public void SentinelWindowStartsAtFirstPassed()
    {
        sw.Order = sentinel.Calls;
        var sets = RecordSets();
        for (var i = 1; i < 600; i++) s.Tick();
        Assert.False(sets[0].Writing);                // tick 1: set before the flag
        Assert.True(sentinel.IsSet);
        s.Tick();                                     // tick 600, still Starting: the install window ends
        Assert.False(sentinel.IsSet);
        for (var i = 601; i < 700; i++) s.Tick();
        buf.SetColours(Colours);
        buf.Post(sw.Shared);                          // captured after tick 699
        s.Tick();                                     // tick 700: set again, then approved
        Assert.True(sentinel.IsSet);
        Assert.Equal(SunOffProbe.Passed, sw.Shared.Probe);
        Assert.Equal(["Set", "Install", "Clear", "Set"], sentinel.Calls);
        Assert.NotEqual(SunOffProbe.Passed, sets[1].Probe);   // tick 700: set before Approve
        for (var i = 701; i < 1300; i++) s.Tick();
        Assert.True(sentinel.IsSet);                  // tick 1299
        s.Tick();                                     // tick 1300
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [EDGE-sun-off-5]
    public void ApprovedBefore600_OneWindowFromPassed()
    {
        for (var i = 1; i < 10; i++) s.Tick();
        buf.SetColours(Colours);
        buf.Post(sw.Shared);
        for (var i = 10; i < 610; i++) s.Tick();      // approved at tick 10
        Assert.True(sentinel.IsSet);                  // tick 609
        s.Tick();                                     // tick 610
        Assert.False(sentinel.IsSet);
        Assert.Equal(1, sentinel.Sets);
    }

    [Theory] // [EDGE-sun-off-7]
    [InlineData("NoDome")]
    [InlineData("NotReady")]
    [InlineData("DroppedMidEpisode")]
    public void NoDomeOrNotReady_Waiting_NoWriting(string how)
    {
        if (how == "DroppedMidEpisode") TurnOn();
        if (how == "NotReady") game.Ready = false; else lit = null;
        s.Tick();
        Assert.Equal(SunOffStatus.Waiting, s.Status);
        Assert.False(sw.Shared.Writing);
    }

    [Fact] // [EDGE-sun-off-10]
    public void Unloading_DropsWriting_NoInstall_SentinelClearedIfOurs()
    {
        unloading = true;
        s.Tick();
        Assert.Equal(0, sw.Installs);
        unloading = false;
        TurnOn();
        unloading = true;
        s.Tick();
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);
        s.Teardown();
        Assert.Equal(1, sw.Installs);
    }

    [Fact] // [EDGE-sun-off-16]
    public void NightIndependent()
    {
        config.NightWhileShown = true;
        var env = new FakeEnvTime();
        var night = new NightWhileShown(env, game, () => lit != null, config, new FakeSentinel(), log, () => false);
        Plugin.TickAll(() => { }, null, night.Tick, s.Tick);
        buf.SetColours(Colours);
        buf.Post(sw.Shared);
        Plugin.TickAll(() => { }, null, night.Tick, s.Tick);
        Assert.Equal(NightStatus.On, night.Status);
        Assert.Equal(SunOffStatus.On, s.Status);
        Assert.All(env.Writes, w => Assert.Equal(NightTime.Seconds, w));
    }

    [Fact] // [EDGE-sun-off-20]
    public void ManyEpisodes_InstallOnce()
    {
        for (var i = 0; i < 5; i++)
        {
            s.Tick();
            Assert.True(sw.Shared.Writing);
            s.Release();
            Assert.False(sw.Shared.Writing);
        }
        Assert.Equal(1, sw.Installs);
    }

    [Theory] // [EDGE-sun-off-22]
    [InlineData(true)]
    [InlineData(false)]
    public void AtEpisodeTick600_LogsCountsDeltasAndProbeState(bool posts)
    {
        buf.SetColours(Colours);
        for (var i = 1; i <= 600; i++)
        {
            s.Tick();
            if (posts) buf.Post(sw.Shared);
        }
        var line = Assert.Single(log.Lines, l => l.Text.Contains("environment updates in 600 ticks")).Text;
        if (posts)
        {
            Assert.Contains("599 environment updates", line);   // the 600th call comes after tick 600's log
            Assert.Contains("599 on the game's own state", line);
            Assert.Contains("0x58", line);
            Assert.Contains("probe Passed", line);
        }
        else
        {
            Assert.Contains("0 environment updates", line);
            Assert.Contains("probe NotProbed", line);
        }
    }

    [Fact] // [EDGE-sun-off-25]
    public void PlausibilityPass_LogsSix_CaptureCallDoesNotZero()
    {
        s.Tick();
        buf.SetColours(Colours);
        var before = buf.Snapshot();
        Assert.Equal(PostResult.Probed, buf.Post(sw.Shared));
        Assert.Equal(before, buf.Snapshot());          // the capturing call doesn't zero
        s.Tick();
        Assert.Equal(1, Lines("Information", "sun (0.8, 0.7, 0.6), moon (0.1, 0.1, 0.2)"));
        Assert.Equal(PostResult.Written, buf.Post(sw.Shared));
    }

    [Fact] // [EDGE-sun-off-27]
    public void NoOwnStateCalls_StaysStarting()
    {
        for (var i = 0; i < 100; i++) s.Tick();
        Assert.Equal(SunOffStatus.Starting, s.Status);
        Assert.Equal(SunOffProbe.NotProbed, sw.Shared.Probe);
    }

    [Fact] // [EDGE-sun-off-27]
    public void CaptureBeforeStop_EvaluatedNextTick()
    {
        s.Tick();
        buf.SetColours(Colours);
        buf.Post(sw.Shared);
        s.Release();
        lit = null;
        s.Tick();
        Assert.Equal(SunOffProbe.Passed, sw.Shared.Probe);
        Assert.Equal(SunOffStatus.Waiting, s.Status);
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);                 // approved outside an episode: no zeroing window opened
        s.Teardown();
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [EDGE-sun-off-22]
    public unsafe void Diagnostics_NegativeDeltaInHex()
    {
        s.Tick();
        SunOffPostStep.Run(buf.Env - 0x10, buf.Env, sw.Shared);   // a state below the manager (never dereferenced)
        for (var i = 2; i <= 600; i++) s.Tick();
        Assert.Contains(log.Lines, l => l.Text.Contains("offsets [-0x10]"));
    }

    [Theory] // [EDGE-sun-off-10]
    [InlineData("Release")]
    [InlineData("Teardown")]
    public void Unloading_ReleaseAndTeardown_Drop_NoBackOnLine(string how)
    {
        TurnOn();
        unloading = true;
        if (how == "Release") s.Release(); else s.Teardown();
        Assert.False(sw.Shared.Writing);
        Assert.False(sentinel.IsSet);
        Assert.Equal(0, Lines("Information", "Sun back on."));
        s.Teardown();
        Assert.Equal(0, Lines("Information", "Sun back on."));
    }

    [Theory] // [EDGE-sun-off-28]
    [InlineData(120, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(600, false)]
    public void LowCallRate_Warns(int ownStateCalls, bool warns)
    {
        buf.SetColours(Colours);
        s.Tick();                                     // the episode's baseline is taken here
        for (var i = 0; i < ownStateCalls; i++) buf.Post(sw.Shared);
        for (var i = 2; i <= 600; i++) s.Tick();
        Assert.Contains(log.Lines, l => l.Text.Contains($"({ownStateCalls} on the game's own state"));
        Assert.Equal(warns ? 1 : 0, Lines("Warning", "updated its lighting only"));
    }

    [Fact] // [EDGE-sun-off-29]
    public void AllZeroSample_RearmsAndRetries()
    {
        buf.SetColours(0f, 0f, 0f, 0f, 0f, 0f);
        s.Tick();
        Assert.Equal(PostResult.Probed, buf.Post(sw.Shared));
        var before = buf.Snapshot();
        for (var i = 2; i <= 62; i++) s.Tick();       // seen at tick 2, re-armed 60 ticks later
        Assert.Equal(SunOffProbe.NotProbed, sw.Shared.Probe);
        Assert.True(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.Starting, s.Status);
        Assert.Equal(before, buf.Snapshot());
        buf.SetColours(Colours);
        Assert.Equal(PostResult.Probed, buf.Post(sw.Shared));
        s.Tick();
        Assert.Equal(SunOffStatus.On, s.Status);
        Assert.Equal(PostResult.Written, buf.Post(sw.Shared));
    }

    [Fact] // [EDGE-sun-off-32] [EDGE-sun-off-29]
    public void ZeroLightArea_RearmRateLimited_SentinelClearedAfter600Starting()
    {
        buf.SetColours(0f, 0f, 0f, 0f, 0f, 0f);
        var captures = 0;
        for (var i = 1; i <= 700; i++)
        {
            s.Tick();
            if (i == 599) Assert.True(sentinel.IsSet);
            if (i == 600) Assert.False(sentinel.IsSet);
            if (i >= 600) Assert.Equal(SunOffStatus.StartingSlow, s.Status);
            var result = buf.Post(sw.Shared);
            Assert.NotEqual(PostResult.Written, result);   // never zeroed
            if (result == PostResult.Probed) captures++;
        }
        Assert.InRange(captures, 2, (int)Math.Ceiling(700 / 60.0));
        Assert.Equal(1, Lines("Information", "all 0 here"));
        Assert.NotEqual(SunOffProbe.Passed, sw.Shared.Probe);

        lit = null; s.Tick();
        lit = new("pano.jpg", new DomeView(0, false, 1, 1, 1));
        for (var i = 0; i < 70; i++) { s.Tick(); buf.Post(sw.Shared); }
        Assert.Equal(2, Lines("Information", "all 0 here"));
    }

    [Fact] // [EDGE-sun-off-30]
    public void StartingPast600_StartingSlow_ThenOnWhenApproved()
    {
        for (var i = 1; i < 600; i++) s.Tick();
        Assert.Equal(SunOffStatus.Starting, s.Status);
        s.Tick();
        Assert.Equal(SunOffStatus.StartingSlow, s.Status);
        buf.SetColours(Colours);
        buf.Post(sw.Shared);
        s.Tick();
        Assert.Equal(SunOffStatus.On, s.Status);
    }

    [Theory]
    [InlineData(DeliveryMode.Local, false, true)]
    [InlineData(DeliveryMode.Local, true, false)]
    [InlineData(DeliveryMode.Cushion, false, false)]
    public void LocalDomeDrawn(DeliveryMode d, bool synced, bool expected)
    {
        Assert.Equal(expected, SunOff.LocalDomeDrawn(new LitBackdrop("x", default, synced), d));
        Assert.False(SunOff.LocalDomeDrawn(null, d));
    }
    [Fact] // [AC-per-backdrop-settings-8] [EDGE-per-backdrop-settings-21]
    public void SelectionChangeOffToOn_KeepsCrashGuard_UserToggleStillRetries()
    {
        config.SunOffWhileShown = false;
        sentinel = new FakeSentinel(setAtStartup: true);
        s = Make();
        s.Tick();
        BackdropSettingsTests.SwitchTo(config, "pano.jpg", e => e.SunOffWhileShown = true);
        s.Tick(); s.Tick();
        Assert.Equal(SunOffStatus.CrashGuard, s.Status);
        Assert.Equal(0, sw.Installs);
        config.SunOffWhileShown = false; s.Tick();
        config.SunOffWhileShown = true; s.Tick();
        Assert.Equal(1, sw.Installs);
    }
}
