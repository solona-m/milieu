using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.GameFiles;

namespace Milieu.Backdrop;

public enum BackdropState { Hidden, Preparing, AwaitingRedirect, Shown, Suspended }

/// <summary>What the window shows about the backdrop.</summary>
public sealed record BackdropStatus(BackdropState State, float Progress, StatusMessage Message, IReadOnlyList<StatusMessage> Warnings);

/// <summary>
/// The backdrop's lifecycle: Hidden → Preparing → AwaitingRedirect → Shown ⇄ Suspended. Conversion runs on a
/// background task; everything else (Penumbra IPC, native spawn/move/remove) happens in <see cref="Tick"/> and the
/// event handlers, which the plugin calls on the framework thread only.
/// </summary>
public sealed class BackdropController
{
    public const int RedirectTimeoutTicks = 120;
    public const int RespawnDelayFrames = 30;
    public const int PenumbraRefreshTicks = 60;
    public static readonly TimeSpan EarlyRemovalWindow = TimeSpan.FromSeconds(10);
    public const int MaxEarlyRemovals = 2;

    private readonly IVfxSpawner spawner;
    private readonly IPenumbraRedirects penumbra;
    private readonly IGameState game;
    private readonly IBackdropPreparer preparer;
    private readonly CacheStore cache;
    private readonly Configuration config;
    private readonly ILogSink log;
    private readonly Func<Func<Task<PreparedTexture>>, Task<PreparedTexture>> run;
    private readonly Func<DateTime> utcNow;

    private CancellationTokenSource? jobCts;
    private Task<PreparedTexture>? job;
    private volatile float progress;
    private PreparedTexture? texture;

    private Dictionary<string, string>? pendingSet;
    private string? pendingAvfxGamePath;
    private int waitTicks;

    private Dictionary<string, string>? shownSet;
    private string? shownAvfxGamePath;
    private VfxHandle? handle;
    private Vector3 centre;
    private bool recenterOnRespawn;
    private int readyFrames;
    private int earlyRemovals;
    private int ticks;

    private readonly ConcurrentQueue<Action> posted = new();
    private readonly List<Task> outstanding = [];
    private volatile bool lastReady;
    private volatile MessageKey uiBlocker = MessageKey.NotLoggedIn;

    private BackdropState state = BackdropState.Hidden;
    private StatusMessage message = StatusMessage.None;
    private IReadOnlyList<StatusMessage> warnings = [];

    public BackdropController(IVfxSpawner spawner, IPenumbraRedirects penumbra, IGameState game, IBackdropPreparer preparer,
                              CacheStore cache, Configuration config, ILogSink log,
                              Func<Func<Task<PreparedTexture>>, Task<PreparedTexture>>? run = null, Func<DateTime>? utcNow = null)
    {
        this.spawner = spawner;
        this.penumbra = penumbra;
        this.game = game;
        this.preparer = preparer;
        this.cache = cache;
        this.config = config;
        this.log = log;
        this.run = run ?? (f => Task.Run(f));
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public BackdropStatus Status => new(state, progress, CurrentMessage(), warnings);

    /// <summary>
    /// The blocker as of the last tick, for the window (ImGui draws off the framework thread, so it must not read game
    /// memory itself).
    /// </summary>
    public MessageKey UiBlocker => uiBlocker;

    /// <summary>
    /// Queues a user action (from the window or a command) to run at the start of the next framework tick, where native
    /// calls and IPC are allowed.
    /// </summary>
    public void Post(Action action) => posted.Enqueue(action);

    public BackdropState State => state;

    /// <summary>The reason Show is unavailable right now, in fixed priority order, or None.</summary>
    public MessageKey Blocker()
    {
        if (!spawner.Available) return MessageKey.InteropUnavailable;
        return penumbra.State switch
        {
            PenumbraState.Missing => MessageKey.PenumbraMissing,
            PenumbraState.Incompatible => MessageKey.PenumbraIncompatible,
            PenumbraState.ModsDisabled => MessageKey.PenumbraModsDisabled,
            PenumbraState.NoBaseCollection => MessageKey.PenumbraNoBaseCollection,
            _ => game.IsReady ? MessageKey.None : MessageKey.NotLoggedIn,
        };
    }

    private StatusMessage CurrentMessage()
    {
        if (state == BackdropState.Preparing && job != null)
            return StatusMessage.Of(MessageKey.Converting, ("file", Path.GetFileName(config.ImagePath ?? "")), ("pct", (int)(progress * 100)));
        return message;
    }

    // ---------------------------------------------------------------- user actions

    /// <summary>Converts the configured image (or reuses the cache) and shows it. The latest request wins.</summary>
    public void Show()
    {
        if (Blocker() != MessageKey.None) return;
        if (string.IsNullOrEmpty(config.ImagePath))
        {
            message = StatusMessage.Of(MessageKey.NoImage);
            return;
        }
        CancelJob();
        earlyRemovals = 0;
        message = StatusMessage.None;
        warnings = [];
        progress = 0;
        var request = BackdropRequest.From(config);
        var cts = new CancellationTokenSource();
        jobCts = cts;
        var reporter = new Imaging.Bc7.SyncProgress(p => progress = p);
        job = run(() => preparer.PrepareTextureAsync(request, reporter, cts.Token));
        outstanding.RemoveAll(t => t.IsCompleted);
        outstanding.Add(job);
        state = BackdropState.Preparing;
        log.Information($"[Milieu] Preparing {Path.GetFileName(request.ImagePath)}.");
    }

    /// <summary>Hide, /milieu off: removes the dome and the temp mod, cancels a conversion.</summary>
    public void Hide()
    {
        CancelJob();
        RemoveDome();
        if (shownSet != null || pendingSet != null) penumbra.Clear();
        shownSet = null; shownAvfxGamePath = null;
        pendingSet = null; pendingAvfxGamePath = null;
        state = BackdropState.Hidden;
        message = StatusMessage.None;
    }

    /// <summary>Yaw/height changed: move the live dome (no rebuild, no IPC). Saved settings apply on the next spawn otherwise.</summary>
    public void ApplyLive()
    {
        if (state == BackdropState.Shown && handle is { IsDead: false } h) spawner.Move(h, SpawnPosition(), YawRadians());
    }

    /// <summary>Radius, vertical scale, repeats, flip or brightness changed: rebuild the VFX and swap it in.</summary>
    public void ApplyRebuild()
    {
        if (texture == null) return;
        if (state is BackdropState.Shown or BackdropState.AwaitingRedirect or BackdropState.Suspended) Register(texture);
    }

    /// <summary>Re-centres the dome on the player.</summary>
    public void Recenter()
    {
        if (game.PlayerPosition is { } p) centre = p;
        ApplyLive();
    }

    // ---------------------------------------------------------------- game and Penumbra events (framework thread)

    public void OnTerritoryChanged()
    {
        if (handle != null) RemoveDome();
        if (state == BackdropState.Shown) state = BackdropState.Suspended;
        recenterOnRespawn = true;
        readyFrames = 0;
    }

    public void OnLogout()
    {
        if (state == BackdropState.Hidden && handle == null) return;
        Hide();
        log.Information("[Milieu] Logged out; backdrop hidden.");
    }

    /// <summary>Penumbra was unloaded: remove the dome (its files can no longer load) and stay hidden.</summary>
    public void OnPenumbraDisposed()
    {
        var wasShowing = state != BackdropState.Hidden || handle != null;
        CancelJob();
        RemoveDome();
        shownSet = null; shownAvfxGamePath = null; pendingSet = null; pendingAvfxGamePath = null;
        state = BackdropState.Hidden;
        if (wasShowing) message = StatusMessage.Of(MessageKey.PenumbraGone);
    }

    /// <summary>Penumbra (re)started: clear anything an earlier session left behind.</summary>
    public void OnPenumbraInitialized()
    {
        penumbra.Refresh();
        if (penumbra.State == PenumbraState.Available || penumbra.State == PenumbraState.ModsDisabled
            || penumbra.State == PenumbraState.NoBaseCollection)
            penumbra.Clear();
    }

    /// <summary>Called by the remove hook (any thread): the handle is already marked dead.</summary>
    public void OnGameRemoved(VfxHandle h) => log.Debug($"[Milieu] StaticVfxRemove fired for Milieu dome (game-side, ready={lastReady}).");

    // ---------------------------------------------------------------- per-frame

    public void Tick()
    {
        spawner.Tick();
        if (++ticks % PenumbraRefreshTicks == 0) penumbra.Refresh();
        while (posted.TryDequeue(out var action)) action();

        CheckGameRemoval();
        var ready = game.IsReady;
        lastReady = ready;
        if (handle is { IsDead: false } live)
        {
            // A dome that survived the early window resets the early-removal guard.
            if (utcNow() - live.SpawnedAtUtc >= EarlyRemovalWindow) earlyRemovals = 0;
            // Not ready (zone change, cutscene, logout) in ANY state: the dome comes down now, even mid-conversion.
            if (!ready && state is not (BackdropState.Shown or BackdropState.Suspended))
            {
                if (game.BetweenAreas) recenterOnRespawn = true;
                RemoveDome();
                readyFrames = 0;
            }
        }
        switch (state)
        {
            case BackdropState.Preparing: TickPreparing(); break;
            case BackdropState.AwaitingRedirect: TickAwaiting(); break;
            case BackdropState.Shown:
            case BackdropState.Suspended: TickShown(); break;
        }
        uiBlocker = Blocker();
    }

    private void CheckGameRemoval()
    {
        if (handle is not { IsDead: true } dead) return;
        handle = null;
        if (state is not (BackdropState.Shown or BackdropState.Suspended)) return;
        if (game.IsReady && utcNow() - dead.SpawnedAtUtc < EarlyRemovalWindow)
        {
            earlyRemovals++;
            if (earlyRemovals > MaxEarlyRemovals)
            {
                penumbra.Clear();
                shownSet = null; shownAvfxGamePath = null;
                state = BackdropState.Hidden;
                message = StatusMessage.Of(MessageKey.GameEndedVfx);
                log.Warning("[Milieu] The game kept ending the backdrop VFX right after it was shown; stopped re-showing it.");
                return;
            }
        }
        state = BackdropState.Suspended;
        readyFrames = 0;
    }

    private void TickPreparing()
    {
        if (job is not { IsCompleted: true } done) return;
        job = null;
        jobCts?.Dispose();
        jobCts = null;
        if (done.IsCanceled) { state = FallbackState(); return; }
        if (done.IsFaulted)
        {
            var ex = done.Exception?.GetBaseException();
            message = ex is MilieuException me ? me.Status : StatusMessage.Of(MessageKey.DecodeFailed, ("file", Path.GetFileName(config.ImagePath ?? "")));
            if (ex is not MilieuException && ex != null) log.Warning("[Milieu] Conversion failed: " + LogText.Describe(ex, config.ImagePath, cache.Root));
            state = FallbackState();
            return;
        }
        texture = done.Result;
        warnings = texture.Warnings;
        Register(texture);
    }

    private BackdropState FallbackState() => handle is { IsDead: false } ? BackdropState.Shown : shownSet != null ? BackdropState.Suspended : BackdropState.Hidden;

    /// <summary>Builds the VFX for the texture and the current settings, writes it, and registers both files.</summary>
    private void Register(PreparedTexture tex)
    {
        byte[] avfx;
        try
        {
            avfx = DomeAvfxBuilder.Build(new DomeAvfxParams(tex.GamePath, config.Current.Radius, config.Current.VerticalScale,
                config.HorizontalRepeat, config.VerticalRepeat, config.FlipHorizontal, config.Brightness));
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            log.Error("[Milieu] Could not build the dome VFX: " + LogText.Describe(e, cache.Root));
            message = StatusMessage.Of(MessageKey.WriteFailed);
            state = FallbackState();
            return;
        }
        var key = CacheStore.AvfxKey(avfx);
        var avfxDisk = cache.AvfxDiskPath(key);
        try { if (!File.Exists(avfxDisk)) cache.WriteAtomic(avfxDisk, avfx); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            message = StatusMessage.Of(MessageKey.WriteFailed);
            state = FallbackState();
            return;
        }

        penumbra.Refresh();
        if (Blocker() is not (MessageKey.None or MessageKey.NotLoggedIn))
        {
            state = FallbackState();
            return;
        }
        var set = new Dictionary<string, string>
        {
            [tex.GamePath] = tex.DiskPath,
            [CacheStore.AvfxGamePath(key)] = avfxDisk,
        };
        if (!penumbra.Set(set))
        {
            message = StatusMessage.Of(MessageKey.RedirectTimeout);
            state = FallbackState();
            return;
        }
        pendingSet = set;
        pendingAvfxGamePath = CacheStore.AvfxGamePath(key);
        waitTicks = 0;
        state = BackdropState.AwaitingRedirect;
    }

    private void TickAwaiting()
    {
        if (pendingSet == null || pendingAvfxGamePath == null) { state = FallbackState(); return; }
        if (Resolved(pendingSet))
        {
            if (!game.IsReady) return;   // spawn as soon as the game is ready
            var old = handle;
            if ((old == null || old.IsDead) && (shownSet == null || recenterOnRespawn))
            {
                centre = game.PlayerPosition ?? centre;
                recenterOnRespawn = false;
            }
            var spawned = spawner.Spawn(pendingAvfxGamePath, SpawnPosition(), YawRadians());
            if (spawned == null)
            {
                message = StatusMessage.Of(MessageKey.InteropUnavailable);
                state = FallbackState();
                return;
            }
            if (old is { IsDead: false }) spawner.Remove(old);   // new one first, then the old one: no flicker
            handle = spawned;
            shownSet = pendingSet;
            shownAvfxGamePath = pendingAvfxGamePath;
            pendingSet = null; pendingAvfxGamePath = null;
            state = BackdropState.Shown;
            readyFrames = 0;
            CleanupCache();
            return;
        }
        if (++waitTicks < RedirectTimeoutTicks) return;
        message = StatusMessage.Of(MessageKey.RedirectTimeout);
        log.Warning("[Milieu] Penumbra did not resolve the backdrop files in time.");
        pendingSet = null; pendingAvfxGamePath = null;
        if (shownSet != null) penumbra.Set(shownSet);
        else penumbra.Clear();
        state = FallbackState();
    }

    private void TickShown()
    {
        if (!game.IsReady)
        {
            if (handle != null) RemoveDome();
            if (game.BetweenAreas) recenterOnRespawn = true;
            state = BackdropState.Suspended;
            readyFrames = 0;
            return;
        }
        if (state != BackdropState.Suspended) return;
        if (++readyFrames < RespawnDelayFrames || shownAvfxGamePath == null) return;
        if (recenterOnRespawn && game.PlayerPosition is { } p) centre = p;
        recenterOnRespawn = false;
        var spawned = spawner.Spawn(shownAvfxGamePath, SpawnPosition(), YawRadians());
        if (spawned == null) { readyFrames = 0; return; }
        handle = spawned;
        state = BackdropState.Shown;
    }

    private bool Resolved(IReadOnlyDictionary<string, string> set)
    {
        foreach (var (gamePath, disk) in set)
            if (!SamePath(penumbra.Resolve(gamePath), disk)) return false;
        return true;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private Vector3 SpawnPosition() => centre + new Vector3(0, config.Current.HeightOffset, 0);

    private float YawRadians() => config.Current.YawDegrees * MathF.PI / 180f;

    private void RemoveDome()
    {
        var h = handle;
        if (h == null) return;
        if (h.IsDead || spawner.Remove(h)) handle = null;   // a refused removal keeps the handle, so it is never orphaned
    }

    private void CancelJob()
    {
        try { jobCts?.Cancel(); } catch (ObjectDisposedException) { }
        job = null;
        jobCts = null;
    }

    private void CleanupCache()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in new[] { shownSet, pendingSet })
            if (set != null) foreach (var disk in set.Values) keep.Add(Path.GetFileName(disk));
        try { cache.Cleanup(keep); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug("[Milieu] Cache cleanup skipped: " + LogText.Describe(e, cache.Root)); }
    }

    /// <summary>
    /// Plugin unload: cancel the job (the caller waits for <see cref="RunningJob"/> up to 2 s), remove the dome and the
    /// temp mod. Framework thread only.
    /// </summary>
    public void Teardown()
    {
        CancelJob();
        RemoveDome();
        penumbra.Clear();
        shownSet = null; pendingSet = null;
        state = BackdropState.Hidden;
    }

    /// <summary>Cancels the conversion and returns its task, so Dispose can wait (bounded) for it to stop.</summary>
    public Task? CancelJobForDispose()
    {
        CancelJob();
        outstanding.RemoveAll(t => t.IsCompleted);
        return outstanding.Count == 0 ? null : Task.WhenAll(outstanding);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a cancelled job; true if it finished.</summary>
    public static bool WaitForJob(Task? running, TimeSpan timeout)
    {
        if (running == null) return true;
        try { return running.Wait(timeout); }
        catch (AggregateException) { return true; }
    }
}
