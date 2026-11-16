using System;
using Milieu.Backdrop;

namespace Milieu.Lighting;

/// <summary>
/// The game's local time of day (docs/adr/0012), behind a seam. Framework thread only. Reads return null while the game
/// object isn't there yet (no latch); <see cref="Available"/> is false when the address never resolved (this session).
/// </summary>
public interface IEnvTime
{
    bool Available { get; }

    /// <summary>EnvManager.DayTimeSeconds, the time of day the scene is lit for.</summary>
    float? ReadDayTimeSeconds();

    /// <summary>The game clock's seconds into the Eorzean day (the override when one is set), in [0, 86400).</summary>
    float? ReadEorzeaDaySeconds();

    bool WriteDayTimeSeconds(float seconds);
}

/// <summary>Night's numbers and wrap-aware helpers.</summary>
public static class NightTime
{
    /// <summary>01:00 Eorzean time.</summary>
    public const float Seconds = 3600f;
    public const float DayLength = 86400f;

    /// <summary>The first read must be within this of the game clock (ET seconds), or this zone's time isn't the clock's.</summary>
    public const float CrossCheckTolerance = 300f;

    /// <summary>Eorzean seconds per real second.</summary>
    public const float EtPerRealSecond = 20.57f;
    public const float OwnershipMargin = 2f;
    public const float OwnershipCap = 600f;

    /// <summary>Distance between two times of day, around the clock.</summary>
    public static float Distance(float a, float b)
    {
        var d = MathF.Abs(a - b) % DayLength;
        return MathF.Min(d, DayLength - d);
    }

    /// <summary>
    /// How far the field may have moved from Milieu's last write and still be Milieu's: tight right after a write (another
    /// plugin's nearby value isn't claimed), following a field the game advances between frames, capped for long gaps.
    /// </summary>
    public static float OwnershipTolerance(TimeSpan sinceLastWrite) =>
        MathF.Min(OwnershipMargin + EtPerRealSecond * (float)Math.Max(0, sinceLastWrite.TotalSeconds) * 1.5f, OwnershipCap);

    public static bool Plausible(float? v) => v is { } x && float.IsFinite(x) && x >= 0 && x < DayLength;
}

/// <summary>What the "Time of day" panel says (texts in Gui.NightViewModel).</summary>
public enum NightStatus { Off, Waiting, On, NotInThisZone, Unavailable, CrashGuard }

/// <summary>
/// "Night while the backdrop is shown" (docs/adr/0012), ticked on the framework thread after scene lighting. While the
/// option is on, a dome is drawn and the game is ready, the local time of day is held at 01:00 so the game's sun doesn't
/// compete with the backdrop's lights. Only the user sees it.
/// <list type="bullet">
/// <item>Episode start: the first read must be plausible (else latched Unavailable until off→on) and within
/// <see cref="NightTime.CrossCheckTolerance"/> of the game clock (else "not in this zone": no write, checked again after
/// the next territory change). Then the sentinel is set and night is written every tick.</item>
/// <item>Restore (option off, no dome drawn, Hidden, teardown): if the field still reads as Milieu's night (scaled
/// tolerance), the value derived from the current game clock is written back; otherwise nothing (something else changed
/// it). Forget (territory change, logout): nothing is written now; the first ready tick afterwards restores the same way
/// before anything else. While the game exits: no read or write at all.</item>
/// <item>A sentinel set before the first write and found at startup latches CrashGuard until off→on.</item>
/// </list>
/// </summary>
public sealed class NightWhileShown
{
    public const int StableTicks = 600;

    private readonly IEnvTime env;
    private readonly IGameState game;
    private readonly Func<bool> drawn;
    private readonly Configuration config;
    private readonly ILightingSentinel sentinel;
    private readonly ILogSink log;
    private readonly Func<bool> isUnloading;
    private readonly Func<DateTime> utcNow;

    private bool episode;
    private DateTime? lastWrite;
    private bool restorePending;
    private bool unavailable;
    private bool crashGuard;
    private bool notInThisZone;
    private bool wasEnabled;
    private bool tornDown;
    private int stable;

    public NightWhileShown(IEnvTime env, IGameState game, Func<bool> drawn, Configuration config, ILightingSentinel sentinel,
                           ILogSink log, Func<bool> isUnloading, Func<DateTime>? utcNow = null)
    {
        this.env = env;
        this.game = game;
        this.drawn = drawn;
        this.config = config;
        this.sentinel = sentinel;
        this.log = log;
        this.isUnloading = isUnloading;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        wasEnabled = config.NightWhileShown;
        if (sentinel.WasSetAtStartup)
        {
            crashGuard = true;
            log.Warning("[Milieu] The game closed while night was on last time; night is off until it is ticked again.");
        }
    }

    public NightStatus Status { get; private set; } = NightStatus.Off;

    public void Tick()
    {
        if (tornDown || isUnloading()) return;
        var on = config.NightWhileShown;
        if (on && !wasEnabled)
        {
            // Off → on: the explicit retry.
            unavailable = false;
            crashGuard = false;
            notInThisZone = false;
            sentinel.Clear();
        }
        wasEnabled = on;

        if (restorePending && game.IsReady)
        {
            restorePending = false;
            WriteBackIfOurs("after a zone change or logout");
        }

        if (crashGuard) { Status = NightStatus.CrashGuard; EndEpisode(); return; }
        if (unavailable) { Status = NightStatus.Unavailable; return; }
        if (!on) { EndEpisode(); Status = NightStatus.Off; return; }
        if (!env.Available) { Latch("the game's time of day can't be found"); return; }
        if (!drawn() || !game.IsReady) { EndEpisode(); Status = NightStatus.Waiting; return; }
        if (notInThisZone) { Status = NightStatus.NotInThisZone; return; }

        if (!episode)
        {
            var read = env.ReadDayTimeSeconds();
            var clock = env.ReadEorzeaDaySeconds();
            if (read == null || clock == null) { Status = NightStatus.Waiting; return; }   // not created yet: no latch
            if (!NightTime.Plausible(read) || !NightTime.Plausible(clock)) { Latch($"implausible time of day ({read}, {clock})"); return; }
            if (NightTime.Distance(read.Value, clock.Value) > NightTime.CrossCheckTolerance)
            {
                notInThisZone = true;
                Status = NightStatus.NotInThisZone;
                log.Information("[Milieu] Night: this area's time of day isn't the game clock's; not changing it here.");
                return;
            }
            sentinel.Set();
            episode = true;
            stable = 0;
            log.Information("[Milieu] Night on.");
        }
        if (!env.WriteDayTimeSeconds(NightTime.Seconds))
        {
            EndEpisode();
            Latch("writing the time of day failed");
            return;
        }
        lastWrite = utcNow();
        if (stable < StableTicks && ++stable == StableTicks) sentinel.Clear();
        Status = NightStatus.On;
    }

    /// <summary>Hide, None, /milieu off, Penumbra disposed: the time comes back within the call (if it is still ours).</summary>
    public void Release()
    {
        if (tornDown) return;
        if (isUnloading()) { Drop(); return; }
        EndEpisode();
    }

    /// <summary>Logout: nothing is written now; the first ready tick afterwards restores if the field is still ours.</summary>
    public void Forget()
    {
        if (tornDown) return;
        if (episode) restorePending = true;
        episode = false;
        sentinel.Clear();
    }

    /// <summary>A territory change: Forget, and the new zone gets a fresh cross-check.</summary>
    public void OnTerritoryChanged()
    {
        Forget();
        notInThisZone = false;
    }

    /// <summary>Plugin unload, on the framework thread: restore if ours (no env access while the game exits), then inert.</summary>
    public void Teardown()
    {
        if (tornDown) return;
        if (isUnloading()) Drop();
        else
        {
            EndEpisode();
            if (restorePending && game.IsReady) WriteBackIfOurs("on unload");
        }
        restorePending = false;
        tornDown = true;
    }

    private void EndEpisode()
    {
        if (!episode) return;
        episode = false;
        WriteBackIfOurs("restored");
        sentinel.Clear();
    }

    private void Drop()
    {
        episode = false;
        restorePending = false;
        sentinel.Clear();
    }

    /// <summary>Writes the game clock's time back when the field still reads as Milieu's night.</summary>
    private void WriteBackIfOurs(string when)
    {
        if (lastWrite is not { } at) return;
        var read = env.ReadDayTimeSeconds();
        var clock = env.ReadEorzeaDaySeconds();
        lastWrite = null;
        if (read is not { } r || !NightTime.Plausible(r) || clock is not { } c || !NightTime.Plausible(c)) return;
        if (NightTime.Distance(r, NightTime.Seconds) > NightTime.OwnershipTolerance(utcNow() - at))
        {
            log.Information("[Milieu] Night: the time of day was changed by something else; leaving it.");
            return;
        }
        env.WriteDayTimeSeconds(c);
        log.Information($"[Milieu] Night off ({when}).");
    }

    private void Latch(string reason)
    {
        unavailable = true;
        Status = NightStatus.Unavailable;
        log.Warning($"[Milieu] Night unavailable: {reason}. Untick and tick it again to retry.");
    }
}
