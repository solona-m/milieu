using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Sync;

namespace Milieu.Lighting;

/// <summary>What the "Scene lighting" panel says (texts in <see cref="Gui.LightingViewModel"/>).</summary>
public enum LightingStatus
{
    Off,
    Unavailable,
    CrashGuard,
    NeedsHdr,
    WaitingForBackdrop,
    WaitingForGPose,
    Analysing,
    CreateFailed,
    ReadFailed,
    StrengthZero,
    OnWithKey,
    OnNoKey,
    OnKeyNotDrawn,
}

/// <summary>The lighting state as of the last tick. <see cref="FileName"/> is the image's file name only, never a path.</summary>
public sealed record LightingStatusSnapshot(LightingStatus Status, string FileName = "", int Fills = 0)
{
    public static readonly LightingStatusSnapshot Initial = new(LightingStatus.Off);
}

/// <summary>
/// Scene lighting's lifecycle (docs/adr/0009), ticked on the framework thread right after the backdrop controller.
/// Lights exist only while <see cref="ShouldLight"/> holds (option on, spawner available, no latch, an HDR image drawn on
/// the dome, ready and in GPose with a local player, the framework not unloading) and only after
/// <see cref="LightRigLayout.GposeSettleTicks"/> consecutive ticks of it; the counter resets on any tick it doesn't hold,
/// and the lights are removed in that same tick. The rig is loaded off the framework thread (one job, the last path kept
/// in memory); the plan is applied by slot (create the missing, update the changed, destroy the rest).
/// <para>
/// Failures latch until the user turns the option off and on (an image change also clears ReadFailed): Unavailable (no
/// light function), CreateFailed (the game didn't create a light, or one went dead), ReadFailed (the rig job failed),
/// CrashGuard (the crash sentinel was found set at startup). The sentinel is set before each create batch and cleared
/// after <see cref="LightRigLayout.StableTicks"/> with lights alive, on the off→on toggle, and when every light was
/// destroyed. While the framework unloads (game exit) no native call is made: handles are dropped on purpose.
/// </para>
/// </summary>
public sealed class SceneLighting
{
    private enum Latch { None, CreateFailed, ReadFailed, CrashGuard }

    private readonly ILightSpawner spawner;
    private readonly IGameState game;
    private readonly Func<LitBackdrop?> lit;
    private readonly Configuration config;
    private readonly Func<string, CancellationToken, Task<LightRig?>> loadRig;
    private readonly ILightingSentinel sentinel;
    private readonly ILogSink log;
    private readonly Func<bool> isUnloading;
    private readonly Func<DeliveryMode> delivery;
    private readonly Func<Func<Task<LightRig?>>, Task<LightRig?>> run;

    private readonly Dictionary<string, (LightHandle Handle, LightParams P)> lights = [];
    private Latch latch;
    private string? readFailedPath;
    private bool wasEnabled;
    private int settle;
    private int stable;
    private bool sentinelOwned;   // set by this instance and not yet cleared
    private Vector3? anchor;

    private string? rigPath;
    private LightRig? rig;
    private string? jobPath;
    private Task<LightRig?>? job;
    private CancellationTokenSource? jobCts;
    private readonly List<Task> outstanding = [];

    private volatile LightingStatusSnapshot status = LightingStatusSnapshot.Initial;

    public SceneLighting(ILightSpawner spawner, IGameState game, Func<LitBackdrop?> lit, Configuration config,
                         Func<string, CancellationToken, Task<LightRig?>> loadRig, ILightingSentinel sentinel, ILogSink log,
                         Func<bool> isUnloading, Func<DeliveryMode> delivery, Func<Func<Task<LightRig?>>, Task<LightRig?>>? run = null)
    {
        this.spawner = spawner;
        this.game = game;
        this.lit = lit;
        this.config = config;
        this.loadRig = loadRig;
        this.sentinel = sentinel;
        this.log = log;
        this.isUnloading = isUnloading;
        this.delivery = delivery;
        this.run = run ?? (f => Task.Run(f));
        wasEnabled = config.SceneLighting;
        if (sentinel.WasSetAtStartup)
        {
            latch = Latch.CrashGuard;
            log.Warning("[Milieu] The game closed while scene lights were being set up last session; scene lighting is off until it is unticked and ticked again.");
        }
    }

    /// <summary>The status as of the last tick (read by the window off the framework thread).</summary>
    public LightingStatusSnapshot Status => status;

    /// <summary>Lights currently held (test seam).</summary>
    internal int LiveCount => lights.Count;

    internal static bool IsHdrImage(string path) =>
        !BackdropPins.IsBundledId(path) && ImageLoader.KindOf(path)?.IsHdr() == true;

    private static string FileOf(string path) => BackdropPins.IsBundledId(path) ? path : Path.GetFileName(path);

    public void Tick()
    {
        // (0) Game exit: no native calls at all.
        if (isUnloading()) return;

        // (a) The user's off→on: every latch and the sentinel are cleared (the retry).
        var enabled = config.SceneLighting;
        if (enabled && !wasEnabled)
        {
            latch = Latch.None;
            readFailedPath = null;
            sentinel.Clear();
            sentinelOwned = false;
        }
        wasEnabled = enabled;

        // (b) Everything that must hold for lights; otherwise none, now.
        var current = lit();
        if (latch == Latch.ReadFailed && current != null && !string.Equals(current.ImagePath, readFailedPath, StringComparison.OrdinalIgnoreCase))
        {
            latch = Latch.None;   // a different image: try again
            readFailedPath = null;
        }
        var blocked = Blocked(enabled, current);
        if (blocked != null)
        {
            settle = 0;
            CancelRigJob();
            RemoveAll();
            status = blocked;
            return;
        }
        var shown = current!;
        var position = game.PlayerPosition!.Value;

        // (c) The rig for the drawn image: from memory, or one job off the framework thread.
        if (!string.Equals(rigPath, shown.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            if (!TickRigJob(shown.ImagePath))
            {
                // The previous image's lights stay until the new rig is in: they keep their per-frame refresh meanwhile.
                RefreshAndCount();
                return;
            }
        }
        if (rig == null)
        {
            settle = 0;
            RemoveAll();
            status = new LightingStatusSnapshot(LightingStatus.NeedsHdr, FileOf(shown.ImagePath));
            return;
        }

        // (d) GPose settle delay.
        if (settle < LightRigLayout.GposeSettleTicks)
        {
            settle++;
            status = new LightingStatusSnapshot(LightingStatus.WaitingForGPose, FileOf(shown.ImagePath));
            return;
        }

        // (e) Plan and apply by slot.
        var target = position + new Vector3(0, LightRigLayout.AnchorHeight, 0);
        if (anchor is not { } a || Vector3.Distance(a, target) > LightRigLayout.AnchorMoveThreshold) anchor = target;
        var plan = LightRigPlanner.Plan(rig, shown.View, anchor.Value, config.LightStrength, config.FillLightCount);
        if (!Apply(plan)) return;

        // (f), (g)
        if (!RefreshAndCount()) return;

        status = new LightingStatusSnapshot(
            !(config.LightStrength > 0) ? LightingStatus.StrengthZero
            : !plan.RigHasKey ? LightingStatus.OnNoKey
            : !plan.KeyDrawn ? LightingStatus.OnKeyNotDrawn
            : LightingStatus.OnWithKey,
            FileOf(shown.ImagePath), plan.FillCount);
    }

    /// <summary>
    /// (f) The per-frame refresh of every live light (a dead one fails and removes them all), then (g) the stable count:
    /// lights that stayed up <see cref="LightRigLayout.StableTicks"/> clear the sentinel. False if it failed (latched).
    /// </summary>
    private bool RefreshAndCount()
    {
        foreach (var (_, (handle, _)) in lights.ToArray())
            if (!spawner.Refresh(handle))
            {
                FailAndRemove("[Milieu] A scene light is no longer valid; scene lighting is off until it is unticked and ticked again.");
                return false;
            }
        if (lights.Count > 0)
        {
            if (++stable == LightRigLayout.StableTicks && sentinelOwned)
            {
                sentinel.Clear();
                sentinelOwned = false;
            }
        }
        else stable = 0;
        return true;
    }

    /// <summary>Why there must be no lights right now (the status to show), or null when <c>ShouldLight</c> holds.</summary>
    private LightingStatusSnapshot? Blocked(bool enabled, LitBackdrop? current)
    {
        if (!spawner.Available) return new(LightingStatus.Unavailable);   // first: the panel disables its controls
        if (!enabled) return new(LightingStatus.Off);
        switch (latch)
        {
            case Latch.CrashGuard: return new(LightingStatus.CrashGuard);
            case Latch.CreateFailed: return new(LightingStatus.CreateFailed);
            case Latch.ReadFailed: return new(LightingStatus.ReadFailed, FileOf(readFailedPath ?? ""));
        }
        if (current == null) return new(LightingStatus.WaitingForBackdrop);
        if (!IsHdrImage(current.ImagePath)) return new(LightingStatus.NeedsHdr, FileOf(current.ImagePath));
        if (!game.IsReady || !game.IsGPosing || game.PlayerPosition == null) return new(LightingStatus.WaitingForGPose, FileOf(current.ImagePath));
        if (isUnloading()) return new(LightingStatus.Off);
        return null;
    }

    /// <summary>Starts or keeps the one rig job for <paramref name="path"/>; true once its result is in memory.</summary>
    private bool TickRigJob(string path)
    {
        if (job != null && !string.Equals(jobPath, path, StringComparison.OrdinalIgnoreCase)) CancelRigJob();   // a stale job
        if (job == null)
        {
            var cts = new CancellationTokenSource();
            jobCts = cts;
            jobPath = path;
            var token = cts.Token;
            job = run(() => loadRig(path, token));
            outstanding.RemoveAll(t => t.IsCompleted);
            outstanding.Add(job);
        }
        if (!job.IsCompleted)
        {
            status = new LightingStatusSnapshot(LightingStatus.Analysing, FileOf(path));
            return false;
        }

        var done = job;
        job = null;
        jobCts?.Dispose();
        jobCts = null;
        jobPath = null;
        if (done.IsCanceled)
        {
            status = new LightingStatusSnapshot(LightingStatus.Analysing, FileOf(path));
            return false;   // started again next tick
        }
        if (done.IsFaulted)
        {
            var ex = done.Exception?.GetBaseException();
            if (ex is not MilieuException && ex != null) log.Warning("[Milieu] Reading the light failed: " + LogText.Describe(ex, path));
            latch = Latch.ReadFailed;
            readFailedPath = path;
            settle = 0;
            RemoveAll();
            status = new LightingStatusSnapshot(LightingStatus.ReadFailed, FileOf(path));
            return false;
        }
        rigPath = path;
        rig = done.Result;
        return true;
    }

    /// <summary>Creates the missing slots, updates the changed ones, destroys the unplanned ones. False if it failed (latched).</summary>
    private bool Apply(PlanResult plan)
    {
        var created = false;
        foreach (var (slot, p) in plan.Lights)
        {
            if (lights.TryGetValue(slot, out var held))
            {
                if (held.P == p) continue;
                if (!spawner.Update(held.Handle, p))
                {
                    FailAndRemove("[Milieu] A scene light is no longer valid; scene lighting is off until it is unticked and ticked again.");
                    return false;
                }
                lights[slot] = (held.Handle, p);
                continue;
            }
            if (lights.Count == 0 && !created)
            {
                sentinel.Set();   // before every create batch: found at startup, it means this attempt crashed the game
                sentinelOwned = true;
                stable = 0;
            }
            var handle = spawner.Create(p);
            if (handle == null)
            {
                FailAndRemove("[Milieu] The game didn't create a scene light; scene lighting is off until it is unticked and ticked again.");
                return false;
            }
            lights[slot] = (handle, p);
            created = true;
        }
        var planned = plan.Lights.Select(l => l.Slot).ToHashSet();
        foreach (var slot in lights.Keys.Where(k => !planned.Contains(k)).ToArray())
        {
            var (handle, _) = lights[slot];
            if (handle.IsDead || spawner.Destroy(handle)) lights.Remove(slot);   // a refused destroy is retried next tick
        }
        if (lights.Count == 0 && sentinelOwned)
        {
            // Nothing left alive (strength 0, nothing planned or drawn): this attempt is over.
            sentinel.Clear();
            sentinelOwned = false;
        }
        if (created && plan.Lights.FirstOrDefault(l => l.Slot == LightRigPlanner.KeySlot) is { P: { } key } && anchor is { } a && rig?.Key is { } k)
        {
            var d = Vector3.Normalize(key.Position - a);
            var az = MathF.Atan2(d.Z, d.X) * 180 / MathF.PI;
            var el = MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) * 180 / MathF.PI;
            log.Information($"[Milieu] Scene lights on ({lights.Count}, {delivery()} delivery): key at azimuth {az:0.#}°, elevation {el:0.#}° (image s {k.S:0.###}, t {k.T:0.###}).");
        }
        else if (created) log.Information($"[Milieu] Scene lights on ({lights.Count}, {delivery()} delivery): no key light.");
        return true;
    }

    private void FailAndRemove(string message)
    {
        log.Warning(message);
        latch = Latch.CreateFailed;
        settle = 0;
        RemoveAll();
        status = new LightingStatusSnapshot(LightingStatus.CreateFailed);
    }

    /// <summary>
    /// Destroys every light (wired to the controller's Hidden event, zone changes and logout). A refused destroy keeps its
    /// handle for the next tick; the sentinel is cleared only once every light was destroyed. While the framework unloads
    /// the handles are dropped without native calls (a game exit is not a crash, so the sentinel is cleared).
    /// </summary>
    public void RemoveAll()
    {
        if (isUnloading())
        {
            if (lights.Count > 0) log.Debug("[Milieu] Game exiting: scene lights left to the game.");
            lights.Clear();
            if (sentinelOwned) { sentinel.Clear(); sentinelOwned = false; }
            stable = 0;
            return;
        }
        if (lights.Count == 0) return;
        foreach (var slot in lights.Keys.ToArray())
        {
            var (handle, _) = lights[slot];
            if (handle.IsDead || spawner.Destroy(handle)) lights.Remove(slot);
        }
        stable = 0;
        if (lights.Count == 0 && sentinelOwned)
        {
            sentinel.Clear();
            sentinelOwned = false;
        }
    }

    /// <summary>Plugin unload, on the framework thread: cancel the rig job and remove every light.</summary>
    public void Teardown()
    {
        CancelRigJob();
        RemoveAll();
    }

    private void CancelRigJob()
    {
        try { jobCts?.Cancel(); } catch (ObjectDisposedException) { }
        jobCts = null;
        job = null;
        jobPath = null;
    }

    /// <summary>Cancels the rig job and returns it (and any earlier one still running), so Dispose can wait a bounded time.</summary>
    public Task? CancelJobForDispose()
    {
        CancelRigJob();
        outstanding.RemoveAll(t => t.IsCompleted);
        return outstanding.Count == 0 ? null : Task.WhenAll(outstanding);
    }
}
