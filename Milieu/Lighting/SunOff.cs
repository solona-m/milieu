using System;
using System.Globalization;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Interop;
using Milieu.Sync;

namespace Milieu.Lighting;

/// <summary>The environment-update hook behind a seam (docs/adr/0013).</summary>
public interface ISunSwitch
{
    /// <summary>The signature matched exactly once and EnvManager's address resolved (this session).</summary>
    bool Available { get; }

    /// <summary>Framework thread: creates and enables the hook once (idempotent); false on failure.</summary>
    bool Install();

    /// <summary>The state the detour reads (the fake uses a real one).</summary>
    SunOffShared Shared { get; }
}

/// <summary>What the "Game sun" block says (texts in Gui.SunOffViewModel).</summary>
public enum SunOffStatus { Off, NotOnlyMe, Waiting, Starting, StartingSlow, On, Unavailable, CrashGuard }

/// <summary>
/// "Turn off the game's sun" (docs/adr/0013), ticked on the framework thread after night. While the option is on, the
/// backdrop is shown to "Only me" (local delivery), its dome is drawn and the game is ready, the environment-update detour
/// zeroes the game's sun and moon light colour in EnvManager's own state after every update. Only the user sees it.
/// <list type="bullet">
/// <item>Episode start: the sentinel is set, the hook is installed (once per session), the flag is raised. Before the first
/// zeroing of the session the detour captures the six target floats; the next tick checks them (Pass approves, an all-zero
/// sample re-arms at most once per <see cref="SunOffLayout.RearmIntervalTicks"/>, Fail latches Unavailable).</item>
/// <item>Restore = stop writing: the game rewrites the state on its next update.</item>
/// <item>The sentinel covers two windows: the install plus 600 starting ticks, and the first 600 ticks of zeroing.
/// Found at startup it latches CrashGuard until off→on.</item>
/// </list>
/// </summary>
public sealed class SunOff
{
    public const int StableTicks = 600;

    private readonly ISunSwitch sw;
    private readonly IGameState game;
    private readonly Func<LitBackdrop?> lit;
    private readonly Func<DeliveryMode> delivery;
    private readonly Configuration config;
    private readonly ILightingSentinel sentinel;
    private readonly ILogSink log;
    private readonly Func<bool> isUnloading;

    private bool episode;
    private bool installAttempted;
    private bool unavailable;
    private bool crashGuard;
    private bool wasEnabled;
    private int seenGeneration;
    private bool tornDown;
    private bool sentinelOurs;
    private bool inconclusiveLogged;
    private long sessionTick;
    private long? capturedSeenTick;
    private int episodeTicks;
    private long? passedAt;
    private SunOffCounts baseline;

    public SunOff(ISunSwitch sw, IGameState game, Func<LitBackdrop?> lit, Func<DeliveryMode> delivery, Configuration config,
                  ILightingSentinel sentinel, ILogSink log, Func<bool> isUnloading)
    {
        this.sw = sw;
        this.game = game;
        this.lit = lit;
        this.delivery = delivery;
        this.config = config;
        this.sentinel = sentinel;
        this.log = log;
        this.isUnloading = isUnloading;
        wasEnabled = config.SunOffWhileShown;
        seenGeneration = config.SelectionGeneration;
        if (sentinel.WasSetAtStartup)
        {
            crashGuard = true;
            log.Warning("[Milieu] The game closed while the sun was off last time; the sun stays on until the option is ticked again.");
        }
    }

    public SunOffStatus Status { get; private set; } = SunOffStatus.Off;

    /// <summary>Only me, and a local dome drawn.</summary>
    public static bool LocalDomeDrawn(LitBackdrop? lit, DeliveryMode d) => d == DeliveryMode.Local && lit is { Synced: false };

    public void Tick()
    {
        if (tornDown) return;
        if (isUnloading()) { Drop(); return; }
        sessionTick++;
        var on = config.SunOffWhileShown;
        if (on && !wasEnabled && config.SelectionGeneration == seenGeneration)
        {
            // Off → on: the explicit retry (an Unavailable latch stays: the game build hasn't changed).
            crashGuard = false;
            sentinel.Clear();
            sentinelOurs = false;
        }
        wasEnabled = on;
        seenGeneration = config.SelectionGeneration;   // a backdrop switch that ticks the flag isn't a retry (docs/adr/0014)

        EvaluateProbe();

        if (!sw.Available || unavailable) { Stop(); Status = SunOffStatus.Unavailable; return; }
        if (crashGuard) { Stop(); Status = SunOffStatus.CrashGuard; return; }
        if (!on) { Stop(); Status = SunOffStatus.Off; return; }
        if (delivery() != DeliveryMode.Local) { Stop(); Status = SunOffStatus.NotOnlyMe; return; }
        if (!LocalDomeDrawn(lit(), DeliveryMode.Local) || !game.IsReady) { Stop(); Status = SunOffStatus.Waiting; return; }

        if (!episode)
        {
            SetSentinel();
            if (!installAttempted)
            {
                installAttempted = true;
                if (!sw.Install())
                {
                    ClearSentinel();
                    unavailable = true;
                    Status = SunOffStatus.Unavailable;
                    log.Warning("[Milieu] Sun off unavailable: the game's lighting update couldn't be hooked.");
                    return;
                }
            }
            episode = true;
            episodeTicks = 0;
            passedAt = null;
            inconclusiveLogged = false;
            baseline = sw.Shared.Counts;
            sw.Shared.SetWriting(true);
            log.Information("[Milieu] Sun off.");
        }

        if (++episodeTicks == StableTicks) LogDiagnostics();

        if (sw.Shared.Probe == SunOffProbe.Passed)
        {
            passedAt ??= sessionTick;
            if (sessionTick - passedAt == StableTicks) ClearSentinel();   // the zeroing window
            Status = SunOffStatus.On;
        }
        else
        {
            if (episodeTicks == StableTicks) ClearSentinel();            // the install window: the hook has survived
            Status = episodeTicks >= StableTicks ? SunOffStatus.StartingSlow : SunOffStatus.Starting;
        }
    }

    /// <summary>Hide, None, /milieu off, Penumbra disposed, a zone change, logout: the writes stop within the call.</summary>
    public void Release()
    {
        if (tornDown) return;
        if (isUnloading()) { Drop(); return; }
        Stop();
    }

    /// <summary>Plugin unload, on the framework thread: the writes stop, then inert.</summary>
    public void Teardown()
    {
        if (tornDown) return;
        if (isUnloading()) Drop();
        else Stop();
        tornDown = true;
    }

    /// <summary>A sample the detour captured: logged and checked (any tick, even outside an episode).</summary>
    private void EvaluateProbe()
    {
        var shared = sw.Shared;
        if (shared.Probe != SunOffProbe.Captured) { capturedSeenTick = null; return; }
        Span<float> six = stackalloc float[SunOffLayout.SampleFloats];
        if (!shared.TryReadSample(six)) return;
        switch (SunOffPlausibility.Check(six))
        {
            case PlausibilityVerdict.Pass:
                log.Information($"[Milieu] Sun off: the game's sun and moon colours are {Format(six)}.");
                if (episode) SetSentinel();   // the zeroing window
                shared.Approve();
                capturedSeenTick = null;
                break;
            case PlausibilityVerdict.Inconclusive:
                capturedSeenTick ??= sessionTick;
                if (!inconclusiveLogged)
                {
                    inconclusiveLogged = true;
                    log.Information("[Milieu] Sun off: the game's sun and moon colours are all 0 here; checking again later.");
                }
                if (sessionTick - capturedSeenTick >= SunOffLayout.RearmIntervalTicks && shared.Rearm()) capturedSeenTick = null;
                break;
            default:
                shared.Reject();
                unavailable = true;
                ClearSentinel();
                capturedSeenTick = null;
                log.Warning($"[Milieu] Sun off unavailable: the game's lighting values don't look like colours ({Format(six)}).");
                break;
        }
    }

    private void LogDiagnostics()
    {
        var c = sw.Shared.Counts.Minus(baseline);
        var deltas = string.Join(", ", sw.Shared.Deltas().Select(d => (d < 0 ? "-0x" : "0x") + Math.Abs(d).ToString("X", CultureInfo.InvariantCulture)));
        log.Information($"[Milieu] Sun off: {c.Calls} environment updates in {StableTicks} ticks ({c.AtState} on the game's own state, " +
                        $"{c.AtGPoseState} on the GPose state, {c.Other} other, {c.NoManager} without the manager); " +
                        $"offsets [{deltas}]; probe {sw.Shared.Probe}.");
        if (c.AtState < SunOffLayout.MinOwnStateCallsPerWindow)
            log.Warning($"[Milieu] Sun off: the game updated its lighting only {c.AtState} times in {StableTicks} frames; " +
                        "the sun may take a moment to come back when the backdrop is hidden.");
    }

    private void Stop()
    {
        if (!episode) return;
        episode = false;
        sw.Shared.SetWriting(false);
        ClearSentinel();
        log.Information("[Milieu] Sun back on.");
    }

    /// <summary>The game is exiting: managed state only.</summary>
    private void Drop()
    {
        episode = false;
        sw.Shared.SetWriting(false);
        ClearSentinel();
    }

    private void SetSentinel()
    {
        if (sentinelOurs) return;
        sentinel.Set();
        sentinelOurs = true;
    }

    /// <summary>Clears only a flag this session set (one found at startup is cleared by off→on alone).</summary>
    private void ClearSentinel()
    {
        if (!sentinelOurs) return;
        sentinel.Clear();
        sentinelOurs = false;
    }

    private static string Format(ReadOnlySpan<float> v)
    {
        var parts = new string[v.Length];
        for (var i = 0; i < v.Length; i++) parts[i] = v[i].ToString("0.###", CultureInfo.InvariantCulture);
        return "sun (" + string.Join(", ", parts.Take(3)) + "), moon (" + string.Join(", ", parts.Skip(3)) + ")";
    }
}
