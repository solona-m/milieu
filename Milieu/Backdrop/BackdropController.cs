using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.GameFiles;
using Milieu.Lighting;
using Milieu.Sync;

namespace Milieu.Backdrop;

public enum BackdropState { Hidden, Preparing, AwaitingRedirect, Shown, Suspended }

/// <summary>What the window shows about the backdrop.</summary>
public sealed record BackdropStatus(BackdropState State, float Progress, StatusMessage Message, IReadOnlyList<StatusMessage> Warnings);

/// <summary>
/// The backdrop's lifecycle: Hidden → Preparing → AwaitingRedirect → Shown ⇄ Suspended. Conversion runs on a
/// background task; everything else (Penumbra IPC, native spawn/move/remove) happens in <see cref="Tick"/> and the
/// event handlers, which the plugin calls on the framework thread only.
/// <para>
/// Two deliveries. Local (default): the all-collections "Milieu" temp mod plus a static VFX only the user sees. Synced
/// (experimental): the dome becomes an item's own VFX through a player-collection temp mod,
/// staged under Penumbra's mod directory so PSync pairs see it (<see cref="SyncedDelivery"/>, docs/adr/0006). All
/// branching reads <see cref="EffectiveDelivery"/>, never <c>config.Delivery</c> directly.
/// </para>
/// </summary>
public sealed class BackdropController : Gui.IGridController
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
    private readonly IBundledBackdrops? bundled;
    private readonly Action save;
    private readonly Func<bool> syncedAvailable;
    private readonly IPenumbraSync? sync;
    private readonly Func<string, SyncStaging> stagingFor;
    private readonly SyncedDelivery? synced;

    /// <summary>A synced job and the staged texture it writes (known once its texture key is), for cleanup when superseded.</summary>
    private sealed class StagingJob
    {
        public Task? Job;
        public volatile string? Output;
    }

    private readonly List<StagingJob> stagingJobs = [];
    private StagingJob? currentStaging;

    /// <summary>Test seam: the synced jobs not yet tidied up (a test waits for them before ticking).</summary>
    internal IReadOnlyList<Task> StagingJobTasks => stagingJobs.Where(j => j.Job != null).Select(j => j.Job!).ToList();

    private CancellationTokenSource? jobCts;
    private Task<PreparedTexture>? job;
    private volatile float progress;
    private PreparedTexture? texture;

    // Scene lighting (docs/adr/0009): the image and build values of the dome as drawn. The image path travels with the
    // texture: the job's request, then the texture when assigned, then the pending registration, then the shown dome.
    // Radius and CentreHeight are the values the dome was built with (local lights are planned from them, never live config).
    // Kind is the mesh (docs/adr/0015); CentreHeight is the dome centre's height above the feet: the camera height (0 for an
    // uncut open bottom); FloorY is the mesh's unit floor (the image floor's, or the open bottom's cut). A local spawn takes
    // its height from the build of the file it spawns, except a sphere (live height).
    private readonly record struct DomeBuild(bool Flip, float VerticalScale, float URepeat, float VRepeat, float? BakedYawRadians,
                                             float Radius, float CentreHeight, DomeMeshKind Kind, float? FloorY = null)
    {
        public bool OpenBottom => Kind == DomeMeshKind.OpenBottom;

        /// <summary>The model v below which an open bottom draws nothing (docs/adr/0015 amendment 2).</summary>
        public float CutV => OpenBottom ? DomeShape.OpenCutV(FloorY) : 0f;

        /// <summary>The half dome's wall, which bounds its VFX lights (docs/adr/0015); null for the sphere and the legacy floor.</summary>
        public DomeBounds? Bounds => Kind switch
        {
            DomeMeshKind.ImageFloor => new DomeBounds(Radius, VerticalScale),
            DomeMeshKind.OpenBottom => new DomeBounds(Radius, VerticalScale, CentreHeight),
            _ => null,
        };

        public DomeView View(float yaw) => new(yaw, Flip, VerticalScale, URepeat, VRepeat, OpenBottom, CutV);
    }

    private string? jobImagePath;
    private string? textureImagePath;
    private string? pendingImagePath;
    private DomeBuild? pendingBuild;
    private string? shownImagePath;
    private DomeBuild? shownBuild;
    private float spawnedYaw;

    private Dictionary<string, string>? pendingSet;
    private string? pendingAvfxGamePath;
    private int waitTicks;

    private Dictionary<string, string>? shownSet;
    private string? shownAvfxGamePath;
    private DomeShapeChoice? shownShape;
    private DomeShapeChoice? pendingShape;
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
                              Func<Func<Task<PreparedTexture>>, Task<PreparedTexture>>? run = null, Func<DateTime>? utcNow = null,
                              IBundledBackdrops? bundled = null, Action? save = null, Func<bool>? syncedAvailable = null,
                              IPenumbraSync? sync = null, IGameData? gameData = null, Func<string, SyncStaging>? stagingFor = null,
                              Func<string, CancellationToken, Task<LightRig?>>? loadRig = null,
                              bool fillsOnOwnEmitters = VfxLightLayout.FillsOnOwnEmitters,
                              Func<string, CancellationToken, Task<LightRig?>>? loadLocalRig = null,
                              bool halfDomeAvailable = true)
    {
        HalfDomeAvailable = halfDomeAvailable;
        this.loadRig = loadRig;
        this.loadLocalRig = loadLocalRig;
        lastSceneLighting = config.SceneLighting;
        this.fillsOnOwnEmitters = fillsOnOwnEmitters;
        this.bundled = bundled;
        this.save = save ?? (() => { });
        this.syncedAvailable = syncedAvailable ?? (() => false);
        this.sync = sync;
        this.stagingFor = stagingFor ?? (dir => new SyncStaging(dir));
        if (sync != null && gameData != null) synced = new SyncedDelivery(sync, gameData, log);
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

    /// <summary>The half-dome kill switch (<c>Plugin.HalfDomeAvailable</c>, docs/adr/0015): off builds the pre-0015 floor.</summary>
    public bool HalfDomeAvailable { get; }

    /// <summary>Raised at the end of <see cref="Hide"/> (so also on logout), <see cref="OnPenumbraDisposed"/> and <see cref="Teardown"/>.</summary>
    public event Action? Hidden;

    /// <summary>
    /// The dome as drawn, for scene lighting: the image of the texture actually shown (never a pending or failed request)
    /// and the build values and yaw of the drawn VFX; null when no dome is drawn (drawn = a live local VFX, or a committed
    /// synced registration). Framework thread only.
    /// </summary>
    public LitBackdrop? Lit
    {
        get
        {
            if (shownImagePath == null || shownBuild is not { } b) return null;
            if (synced?.Shown != null && b.BakedYawRadians is { } baked)
                return new LitBackdrop(shownImagePath, b.View(baked),
                                       Synced: true, VfxLightCount: SyncedLights?.Lights.Count ?? 0);
            if (handle is { IsDead: false } && b.BakedYawRadians == null)
                return new LitBackdrop(shownImagePath, b.View(spawnedYaw),
                                       Synced: false, VfxLightCount: LocalLights?.Lights.Count ?? 0);
            return null;
        }
    }

    /// <summary>A dome is drawn (a live local VFX or a committed synced registration). Framework thread only.</summary>
    public bool IsDrawn => Lit != null;

    /// <summary>The VFX lights the shown synced dome carries (docs/adr/0011); null when no synced dome is shown.</summary>
    internal VfxLightPlan? SyncedLights { get; private set; }

    /// <summary>The VFX lights the shown local dome carries (outside GPose; ADR 0011 local amendment); null when none is shown.</summary>
    internal VfxLightPlan? LocalLights { get; private set; }

    /// <summary>
    /// Local dome lights can be planned: the local loader is present (both kill switches on) and the early-removal latch
    /// isn't set. Read by the window on the draw thread (the latch is volatile, written on the framework thread only).
    /// </summary>
    public bool LocalLightsActive => loadLocalRig != null && !localLightsLatched;

    /// <summary>The delivery in force: the configured one when synced delivery is available, otherwise always Local.</summary>
    public DeliveryMode EffectiveDelivery => synced != null && syncedAvailable() ? config.Delivery : DeliveryMode.Local;

    private bool IsSynced => EffectiveDelivery != DeliveryMode.Local;

    /// <summary>The delivery something is currently live in (registered, pending or spawned), or null.</summary>
    private DeliveryMode? LiveDelivery =>
        synced is { HasRegistration: true } ? (synced.Shown ?? synced.Pending)!.Target.Mode
        : handle != null || shownSet != null || pendingSet != null ? DeliveryMode.Local
        : null;

    private bool SyncedLive => synced is { HasRegistration: true };

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

    public float Progress => progress;

    /// <summary>Shows a message without changing state (e.g. a missing file clicked in the grid).</summary>
    public void ReportStatus(StatusMessage m) => message = m;

    public static readonly TimeSpan ReconvertDelay = TimeSpan.FromMilliseconds(500);
    private DateTime? reconvertAt;

    private readonly Func<string, CancellationToken, Task<LightRig?>>? loadRig;
    private readonly Func<string, CancellationToken, Task<LightRig?>>? loadLocalRig;
    private readonly bool fillsOnOwnEmitters;

    // Local dome lights (ADR 0011 local amendment): what the pending and the shown local dome were planned for.
    private VfxLightPlan? pendingLocalLights;
    private bool pendingLocalGPose;
    private bool shownLocalGPose;
    /// <summary>The GPose state a local register last failed for: the level check doesn't retry it until an edge or a commit.</summary>
    private bool? failedLocalGPose;
    private volatile bool localLightsLatched;
    private bool? lastGPose;
    private bool lastSceneLighting;
    private int seenGeneration;
    private VfxLightPlan? pendingLights;
    private DateTime? lightRebuildAt;
    private bool lightRebuildHeld;

    /// <summary>
    /// Scene lighting, light strength or fill count changed: replan the shown synced dome's VFX lights 500 ms after the
    /// last change and rebuild if they differ. While a synced registration is pending, the request waits for it and then
    /// compares against what it committed. Held while in GPose (a rebuild redraws everyone, which can disturb posing) and
    /// fired 500 ms after GPose ends. Hidden or local: nothing (the setting is just saved).
    /// </summary>
    public void RequestLightRebuild()
    {
        lightRebuildAt = utcNow() + ReconvertDelay;
        lightRebuildHeld = false;
    }

    private void TickLightRebuild()
    {
        if (lightRebuildHeld)
        {
            if (game.IsGPosing) return;
            lightRebuildHeld = false;
            lightRebuildAt = utcNow() + ReconvertDelay;
            return;
        }
        if (lightRebuildAt is not { } at || utcNow() < at) return;
        lightRebuildAt = null;
        if (EffectiveDelivery == DeliveryMode.Local && !SyncedLive)
        {
            TickLocalLightCheck();
            return;
        }
        // A synced registration is pending (planned before this change): wait for it, then compare against what it committed.
        if (synced?.Pending != null && state == BackdropState.AwaitingRedirect) { lightRebuildAt = utcNow() + ReconvertDelay; return; }
        if (synced?.Shown == null || texture == null || state != BackdropState.Shown) return;   // nothing synced shown: dropped
        if (game.IsGPosing) { lightRebuildHeld = true; return; }
        var planned = PlanLights(texture, shownBuild);
        if (VfxLightPlan.SameLights(planned.Lights, SyncedLights?.Lights)) return;   // unchanged: no rebuild, no redraw
        ApplyRebuild();
    }

    /// <summary>
    /// Local: replan the shown dome's lights for the current GPose state and settings; rebuild only if they differ. No GPose
    /// hold (a local swap redraws nobody). A pending local registration is waited for; anything else not shown drops the
    /// check (the level check in <see cref="TickLocalLightEdges"/> re-arms it after a respawn).
    /// </summary>
    private void TickLocalLightCheck()
    {
        if (pendingSet != null && state == BackdropState.AwaitingRedirect) { lightRebuildAt = utcNow() + ReconvertDelay; return; }
        if (state != BackdropState.Shown || texture == null || LiveDelivery != DeliveryMode.Local || handle is not { IsDead: false }
            || shownBuild is not { } built)
            return;
        var gpose = game.IsGPosing;
        var planned = PlanLocalLights(texture, built, gpose);
        if (VfxLightPlan.SameLights(planned.Lights, LocalLights?.Lights)) { shownLocalGPose = gpose; return; }
        ApplyRebuild();
    }

    /// <summary>
    /// Local GPose and lighting edges (framework thread, every tick): a GPose edge schedules a light check (500 ms after the
    /// last edge); turning lighting off then on clears the early-removal latch; the level check re-arms a check when the
    /// shown dome was planned for the other GPose state (after a respawn), at most once per state until an edge or a commit.
    /// </summary>
    private void TickLocalLightEdges()
    {
        var gpose = game.IsGPosing;
        var lighting = config.SceneLighting;
        // A backdrop switch that turns lighting on isn't the user retrying (docs/adr/0014): only a toggle clears the latch.
        if (lighting && !lastSceneLighting && localLightsLatched && config.SelectionGeneration == seenGeneration)
        {
            localLightsLatched = false;
            log.Information("[Milieu] Scene lighting ticked again: the backdrop's own lights are back on.");
        }
        lastSceneLighting = lighting;
        seenGeneration = config.SelectionGeneration;
        var local = EffectiveDelivery == DeliveryMode.Local;
        if (lastGPose is { } was && was != gpose && local)
        {
            failedLocalGPose = null;
            RequestLightRebuild();
        }
        lastGPose = gpose;
        if (local && LiveDelivery == DeliveryMode.Local && state == BackdropState.Shown && handle is { IsDead: false }
            && shownLocalGPose != gpose && failedLocalGPose != gpose && lightRebuildAt == null)
            lightRebuildAt = utcNow() + ReconvertDelay;
    }

    /// <summary>
    /// The VFX lights for a synced dome of <paramref name="tex"/> built as <paramref name="build"/>: empty unless scene
    /// lighting is on, the synced-lights switch gave a rig loader, and the job loaded a rig.
    /// </summary>
    private VfxLightPlan PlanLights(PreparedTexture tex, DomeBuild? build)
    {
        if (!LightsWanted(tex, loadRig, build, out var rig, out var b)) return VfxLightPlan.Empty;
        var view = b.View(b.BakedYawRadians ?? 0f);
        return VfxLightPlanner.Plan(rig, view, config.Current.Radius, config.LightStrength, config.FillLightCount, fillsOnOwnEmitters,
                                    bounds: b.Bounds);
    }

    /// <summary>
    /// The VFX lights for a local dome of <paramref name="tex"/> built as <paramref name="build"/>: empty in GPose (native
    /// lights take over) or unless local lights are active, else as for synced (lighting on, a rig). Planned in the dome's
    /// local frame from the build's own radius and height.
    /// </summary>
    private VfxLightPlan PlanLocalLights(PreparedTexture tex, DomeBuild build, bool gpose)
    {
        if (gpose || !LocalLightsActive || !LightsWanted(tex, loadLocalRig, build, out var rig, out var b)) return VfxLightPlan.Empty;
        return VfxLightPlanner.PlanLocal(rig, b.Flip, b.VerticalScale, b.URepeat, b.VRepeat, b.Radius, b.CentreHeight,
                                         config.LightStrength, config.FillLightCount, fillsOnOwnEmitters, b.Bounds, b.OpenBottom, b.CutV);
    }

    /// <summary>The guard shared by synced and local planning: scene lighting on, a loader, a rig and a build.</summary>
    private bool LightsWanted(PreparedTexture tex, object? loader, DomeBuild? build, out LightRig rig, out DomeBuild b)
    {
        rig = null!;
        b = default;
        if (!config.SceneLighting || loader == null || tex.Rig is not { } r || build is not { } bb) return false;
        rig = r;
        b = bb;
        return true;
    }

    /// <summary>
    /// Exposure, tone map or texture size changed: re-run the conversion 500 ms after the last change, for a selected
    /// user image that is shown, suspended, converting or waiting for Penumbra (a running job is cancelled; latest
    /// wins). Hidden or bundled: nothing (the setting is just saved).
    /// </summary>
    public void RequestReconvert() => reconvertAt = utcNow() + ReconvertDelay;

    private void TickReconvert()
    {
        if (reconvertAt is not { } at || utcNow() < at) return;
        reconvertAt = null;
        if (config.ImagePath == null || BackdropPins.IsBundledId(config.ImagePath)) return;
        if (state is BackdropState.Shown or BackdropState.Suspended or BackdropState.Preparing or BackdropState.AwaitingRedirect) Show();
    }

    /// <summary>The reason Show is unavailable right now, in fixed priority order, or None.</summary>
    public MessageKey Blocker()
    {
        if (IsSynced)
        {
            // Synced delivery needs neither the static-VFX functions nor a Base collection (the player's collection loads it).
            return penumbra.State switch
            {
                PenumbraState.Missing => MessageKey.PenumbraMissing,
                PenumbraState.Incompatible => MessageKey.PenumbraIncompatible,
                PenumbraState.ModsDisabled => MessageKey.PenumbraModsDisabled,
                _ when sync!.ModDirectory == null => MessageKey.SyncNoModDirectory,
                _ => game.IsReady ? MessageKey.None : MessageKey.NotLoggedIn,
            };
        }
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
        if (state == BackdropState.Preparing && job != null && BackdropPins.TryParseId(config.ImagePath, out var pin))
            return StatusMessage.Of(MessageKey.LoadingBundled, ("name", pin.DisplayName));
        if (state == BackdropState.Preparing && job != null)
            return StatusMessage.Of(MessageKey.Converting, ("file", Path.GetFileName(config.ImagePath ?? "")), ("pct", (int)(progress * 100)));
        return message;
    }

    // ---------------------------------------------------------------- user actions

    /// <summary>Converts the configured image (or reuses the cache) and shows it. The latest request wins.</summary>
    public void Show()
    {
        reconvertAt = null;   // a reconvert queued for the previous selection is dropped (docs/adr/0014)
        if (Blocker() != MessageKey.None) return;
        if (string.IsNullOrEmpty(config.ImagePath))
        {
            message = StatusMessage.Of(MessageKey.NoImage);
            return;
        }
        if (BackdropPins.TryParseId(config.ImagePath, out var bundledPin) && bundled != null && !bundled.IsDownloaded(bundledPin.Stem))
        {
            message = StatusMessage.Of(MessageKey.BundledNotDownloaded, ("name", bundledPin.DisplayName));
            return;
        }

        // 1. A live delivery of the other kind comes down first: never both at once.
        if (LiveDelivery is { } live && live != EffectiveDelivery) TearDownLive();

        // A pending synced registration is superseded by this Show: take it out (the shown one, if any, is put back).
        if (synced?.Pending != null && synced.AbortPending()) ForgetShownSynced();

        // 2. First-upload gate: nothing personal leaves the machine before the user has been told, whatever the trigger.
        if (IsSynced && !BackdropPins.IsBundledId(config.ImagePath) && !config.SyncUploadNoticeShown)
        {
            CancelJob();
            // Another image shown through the item comes down too, so the state is Hidden and the next click shows.
            if (synced is { NeedsCleanup: true })
            {
                synced.RemoveAll(redraw: true);
                CleanupStaging(keepShown: false);
            }
            SyncedLights = null;
            message = StatusMessage.Of(MessageKey.SyncUploadNotice, ("file", Path.GetFileName(config.ImagePath)));
            config.SyncUploadNoticeShown = true;
            save();
            state = FallbackState();
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
        if (IsSynced)
        {
            var modDir = sync!.ModDirectory!;
            var staging = stagingFor(modDir);
            var holder = new StagingJob();
            var token = cts.Token;
            stagingJobs.Add(holder);
            currentStaging = holder;
            job = run(async () =>
            {
                var prepared = await preparer.PrepareTextureAsync(request, reporter, token).ConfigureAwait(false);
                holder.Output = staging.TexturePath(prepared.Key);
                PreparedTexture staged;
                try { staged = staging.StageAtex(prepared, token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Warning("[Milieu] Could not stage the synced backdrop: " + LogText.Describe(e, cache.Root, modDir));
                    throw new MilieuException(StatusMessage.Of(MessageKey.WriteFailed));
                }
                // The light rig for the dome's VFX lights: never fails the backdrop (no lights instead).
                if (loadRig == null) return staged;
                try { return staged with { Rig = await loadRig(request.ImagePath, token).ConfigureAwait(false) }; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.Warning("[Milieu] Could not load the light rig for the synced backdrop; showing it without lights: " + e.GetType().Name);
                    return staged;
                }
            });
            jobImagePath = request.ImagePath;
            holder.Job = job;
        }
        else
        {
            var token = cts.Token;
            var localLoader = loadLocalRig;
            job = localLoader == null
                ? run(() => preparer.PrepareTextureAsync(request, reporter, token))
                : run(async () =>
                {
                    var prepared = await preparer.PrepareTextureAsync(request, reporter, token).ConfigureAwait(false);
                    // The light rig for the dome's own lights, whatever the lighting toggle says (ticking it later needs no
                    // re-show). It never fails the backdrop: no lights instead.
                    try { return prepared with { Rig = await localLoader(request.ImagePath, token).ConfigureAwait(false) }; }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.Warning("[Milieu] Could not load the light rig for the backdrop; showing it without its own lights: " + e.GetType().Name);
                        return prepared;
                    }
                });
            jobImagePath = request.ImagePath;
            currentStaging = null;
        }
        outstanding.RemoveAll(t => t.IsCompleted);
        outstanding.Add(job);
        state = BackdropState.Preparing;
        log.Information($"[Milieu] Preparing {Path.GetFileName(request.ImagePath)}.");
    }

    /// <summary>
    /// "Visible to" changed (saved by the window). Hidden: nothing more. Otherwise Show again, which takes the old delivery
    /// down first and applies the first-upload gate.
    /// </summary>
    public void ApplyDelivery()
    {
        if (state == BackdropState.Hidden) return;
        Show();
    }

    /// <summary>Takes down whatever is live (local static VFX + all-collections mod, or the synced registration).</summary>
    private void TearDownLive()
    {
        CancelJob();
        if (LiveDelivery == DeliveryMode.Local)
        {
            RemoveDome();
            penumbra.Clear();
            shownSet = null; shownAvfxGamePath = null;
            pendingSet = null; pendingAvfxGamePath = null;
            LocalLights = null; pendingLocalLights = null;
        }
        else if (synced != null)
        {
            synced.RemoveAll(redraw: true);
            SyncedLights = null;
            CleanupStaging(keepShown: false);
        }
        state = FallbackState();
    }

    /// <summary>Hide, /milieu off: removes the dome, its temp mod or the synced mod (disabled and emptied), cancels a conversion.</summary>
    public void Hide()
    {
        reconvertAt = null;
        CancelJob();
        RemoveDome();
        if (shownSet != null || pendingSet != null) penumbra.Clear();
        shownSet = null; shownAvfxGamePath = null;
        pendingSet = null; pendingAvfxGamePath = null;
        if (synced is { NeedsCleanup: true }) synced.RemoveAll(redraw: true);
        SyncedLights = null;
        LocalLights = null; pendingLocalLights = null;
        CleanupStaging(keepShown: false);   // whenever the folder exists (e.g. a failed registration's atex); never creates it
        state = BackdropState.Hidden;
        message = StatusMessage.None;
        Hidden?.Invoke();
    }

    /// <summary>
    /// Yaw/height changed: respawn the dome at its new placement (no rebuild, no IPC). Moving the VFX object is not
    /// enough: its one particle is emitted at spawn and stays where it was born, so a moved emitter leaves the visible
    /// dome behind (seen in game, build 3). The new dome is spawned before the old one is removed, so it never flickers.
    /// Saved settings apply on the next spawn otherwise.
    /// </summary>
    public void ApplyPlacement()
    {
        // Synced: the game places the item's VFX, so height and yaw are baked into the file: one rebuild, one redraw.
        if (SyncedLive) { ApplyRebuild(); return; }
        if (state != BackdropState.Shown || handle is not { IsDead: false } old || shownAvfxGamePath == null) return;
        var spawned = SpawnDome(shownAvfxGamePath, shownBuild);
        if (spawned == null) return;
        spawner.Remove(old);
        handle = spawned;
    }

    /// <summary>
    /// Height changed. With ground projection the floor is baked into the mesh, so a new floor (or mesh) means a rebuild; if
    /// the shape is unchanged (projection off, or an open bottom) the same VFX is respawned. Exactly one swap.
    /// </summary>
    public void ApplyHeight()
    {
        if (SyncedLive) { ApplyRebuild(); return; }
        // A lit local dome has the height baked into its light offsets: rebuild. While a rebuild is pending, the VFX about
        // to appear is the pending one: compare against its lights and its floor.
        if ((pendingSet != null ? pendingLocalLights : LocalLights) is { Lights.Count: > 0 }) { ApplyRebuild(); return; }
        var current = pendingSet != null ? pendingShape : shownShape;
        var wanted = config.Current.Shape(HalfDomeAvailable);
        if (current is not { } c || c.Kind != wanted.Kind || c.FloorY != wanted.FloorY) ApplyRebuild();
        else ApplyPlacement();
    }

    /// <summary>Radius, vertical scale, repeats, flip, brightness, mode or ground projection changed: rebuild the VFX and swap it in.</summary>
    public void ApplyRebuild()
    {
        if (texture == null) return;
        // The texture was prepared for the other delivery (synced textures are staged copies): show again instead.
        if (LiveDelivery is { } live && live != EffectiveDelivery) { Show(); return; }
        if (state is BackdropState.Shown or BackdropState.AwaitingRedirect or BackdropState.Suspended) Register(texture);
    }

    /// <summary>Re-centres the dome on the player.</summary>
    public void Recenter()
    {
        if (SyncedLive || IsSynced) return;   // the item carries the dome
        if (game.PlayerPosition is { } p) centre = p;
        ApplyPlacement();
    }

    // ---------------------------------------------------------------- game and Penumbra events (framework thread)

    public void OnTerritoryChanged()
    {
        if (SyncedLive) return;   // the item's VFX comes and goes with the item; nothing to respawn
        if (handle != null) RemoveDome();
        if (state == BackdropState.Shown) state = BackdropState.Suspended;
        recenterOnRespawn = true;
        readyFrames = 0;
    }

    public void OnLogout()
    {
        if (state == BackdropState.Hidden && handle == null && synced is not { NeedsCleanup: true }) return;
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
        synced?.Forget();   // no IPC now; the synced mod outlives Penumbra, so its collections stay unsettled for cleanup
        SyncedLights = null;
        LocalLights = null; pendingLocalLights = null;
        state = BackdropState.Hidden;
        if (wasShowing) message = StatusMessage.Of(MessageKey.PenumbraGone);
        Hidden?.Invoke();
    }

    /// <summary>Penumbra (re)started: clear anything an earlier session left behind.</summary>
    public void OnPenumbraInitialized()
    {
        penumbra.Refresh();
        if (penumbra.State == PenumbraState.Available || penumbra.State == PenumbraState.ModsDisabled
            || penumbra.State == PenumbraState.NoBaseCollection)
        {
            penumbra.Clear();
            // Synced leftovers, also when the kill switch hides synced delivery (an earlier session may have left
            // them). Removal only: the staging folder is never created here.
            if (synced != null)
            {
                synced.Forget();
                synced.ClearEverywhere();
            }
            if (state == BackdropState.Hidden && sync?.ModDirectory is { } dir)
            {
                var staging = stagingFor(dir);
                if (staging.Exists) TryCleanup(staging, new HashSet<string>(), dir);
            }
        }
    }

    /// <summary>Called by the remove hook (any thread): the handle is already marked dead.</summary>
    public void OnGameRemoved(VfxHandle h) => log.Debug($"[Milieu] StaticVfxRemove fired for Milieu dome (game-side, ready={lastReady}).");

    // ---------------------------------------------------------------- per-frame

    public void Tick()
    {
        spawner.Tick();
        if (++ticks % PenumbraRefreshTicks == 0) penumbra.Refresh();
        while (posted.TryDequeue(out var action)) action();
        TickReconvert();
        TickLocalLightEdges();
        TickLightRebuild();

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
        TickStagingJobs();
        uiBlocker = Blocker();
    }

    private void CheckGameRemoval()
    {
        if (handle is not { IsDead: true } dead) return;
        handle = null;
        if (state is not (BackdropState.Shown or BackdropState.Suspended)) return;
        var early = game.IsReady && utcNow() - dead.SpawnedAtUtc < EarlyRemovalWindow;
        if (early)
        {
            earlyRemovals++;
            if (earlyRemovals > MaxEarlyRemovals)
            {
                penumbra.Clear();
                shownSet = null; shownAvfxGamePath = null;
                LocalLights = null; pendingLocalLights = null;
                state = BackdropState.Hidden;
                message = StatusMessage.Of(MessageKey.GameEndedVfx);
                log.Warning("[Milieu] The game kept ending the backdrop VFX right after it was shown; stopped re-showing it.");
                return;
            }
        }
        state = BackdropState.Suspended;
        readyFrames = 0;
        // A lit local dome the game ended early: the backdrop's own lights are latched off (until lighting is ticked off and
        // on) and the dome is rebuilt without them, rather than respawning the lit file.
        // Only while the config is local too: a local dome left up under a synced config must never be re-registered here
        // (that would skip Show's teardown and the first-upload gate); the counting above handles it as before.
        if (early && LocalLights is { Lights.Count: > 0 } && texture != null && LiveDelivery == DeliveryMode.Local && !SyncedLive
            && EffectiveDelivery == DeliveryMode.Local)
        {
            if (!localLightsLatched)
                log.Warning("[Milieu] The game ended the backdrop with its own lights right after it was shown; showing it without them until scene lighting is ticked off and on.");
            localLightsLatched = true;
            Register(texture);
        }
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
        textureImagePath = jobImagePath;   // only here: a cancelled or faulted job never rebinds the image
        warnings = texture.Warnings;
        if (currentStaging != null && ReferenceEquals(currentStaging.Job, done))
        {
            stagingJobs.Remove(currentStaging);   // consumed: its file is the one being registered
            currentStaging = null;
        }
        Register(texture);
    }

    /// <summary>The shown synced dome was taken down after a refusal: stop treating it as drawn (Lit, the shown build).</summary>
    private void ForgetShownSynced()
    {
        SyncedLights = null;
        if (handle is { IsDead: false }) return;   // a local dome is what's drawn
        shownImagePath = null;
        shownBuild = null;
    }

    private BackdropState FallbackState() =>
        handle is { IsDead: false } || synced?.Shown != null ? BackdropState.Shown
        : synced?.Pending != null ? BackdropState.AwaitingRedirect   // still registered: TickAwaiting resolves or aborts it
        : shownSet != null ? BackdropState.Suspended
        : BackdropState.Hidden;

    /// <summary>
    /// A synced job that was cancelled, faulted or superseded has completed: delete only its own staged texture, and only
    /// if that isn't the current job's, the pending one's or the shown one's file (same-key safe). Nothing else is touched.
    /// </summary>
    private void TickStagingJobs()
    {
        if (stagingJobs.Count == 0) return;
        foreach (var sj in stagingJobs.ToArray())
        {
            if (sj.Job is not { IsCompleted: true }) continue;
            if (ReferenceEquals(sj, currentStaging) && ReferenceEquals(sj.Job, job)) continue;   // still the current job
            stagingJobs.Remove(sj);
            if (ReferenceEquals(sj, currentStaging)) currentStaging = null;
            var output = sj.Output;
            if (output == null || IsProtectedStaged(output)) continue;
            CacheStore.TryDelete(output);
        }
    }

    private bool IsProtectedStaged(string path)
    {
        bool Same(string? other) => other != null && SamePath(other, path);
        if ((currentStaging != null && ReferenceEquals(currentStaging.Job, job) && Same(currentStaging.Output)) || Same(texture?.DiskPath))
            return true;
        foreach (var reg in new[] { synced?.Pending, synced?.Shown })
            if (reg != null)
                foreach (var disk in reg.Plan.Paths.Values)
                    if (Same(disk)) return true;
        return false;
    }

    /// <summary>Builds the VFX for the texture and the current settings, writes it, and registers both files.</summary>
    private void Register(PreparedTexture tex)
    {
        if (IsSynced) { RegisterSynced(tex); return; }
        byte[] avfx;
        var shape = config.Current.Shape(HalfDomeAvailable);
        var gpose = game.IsGPosing;
        var build = new DomeBuild(config.FlipHorizontal, config.Current.VerticalScale, config.HorizontalRepeat, config.VerticalRepeat, null,
                                  config.Current.Radius, shape.CentreHeight, shape.Kind, shape.FloorY);
        var lights = PlanLocalLights(tex, build, gpose);
        if (lights.OutOfBounds)
            log.Information("[Milieu] The backdrop's own lights would sit too far from the dome at this height; showing it without them.");
        byte[] BuildWith(VfxLightPlan l) => DomeAvfxBuilder.Build(new DomeAvfxParams(tex.GamePath, build.Radius, build.VerticalScale,
            build.URepeat, build.VRepeat, build.Flip, config.Brightness, shape.FloorY, Lights: l.Lights.Count > 0 ? l.Lights : null,
            Mesh: shape.Kind));
        try
        {
            try { avfx = BuildWith(lights); }
            catch (Exception e) when (lights.Lights.Count > 0 && e is ArgumentException or InvalidDataException)
            {
                // Defence in depth, unreachable with valid settings: the planner keeps positions within the builder's bound and
                // every other light value is planner-made and clamped. If it ever fires, the dome is shown without lights.
                log.Warning("[Milieu] Could not build the dome VFX with its own lights; building it without them: " + LogText.Describe(e, cache.Root));
                lights = VfxLightPlan.Empty with { Estimated = lights.Estimated };
                avfx = BuildWith(lights);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            log.Error("[Milieu] Could not build the dome VFX: " + LogText.Describe(e, cache.Root));
            message = StatusMessage.Of(MessageKey.WriteFailed);
            failedLocalGPose = gpose;
            state = FallbackState();
            return;
        }
        var key = CacheStore.AvfxKey(avfx);
        var avfxDisk = cache.AvfxDiskPath(key);
        try { if (!File.Exists(avfxDisk)) cache.WriteAtomic(avfxDisk, avfx); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            message = StatusMessage.Of(MessageKey.WriteFailed);
            failedLocalGPose = gpose;
            state = FallbackState();
            return;
        }

        penumbra.Refresh();
        if (Blocker() is not (MessageKey.None or MessageKey.NotLoggedIn))
        {
            failedLocalGPose = gpose;
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
            failedLocalGPose = gpose;
            state = FallbackState();
            return;
        }
        pendingSet = set;
        pendingAvfxGamePath = CacheStore.AvfxGamePath(key);
        pendingShape = shape;
        pendingImagePath = textureImagePath;
        pendingBuild = build;
        pendingLocalLights = lights;
        pendingLocalGPose = gpose;
        waitTicks = 0;
        state = BackdropState.AwaitingRedirect;
    }

    /// <summary>
    /// Synced: builds the VFX with height and yaw baked in and bound to its carrier, writes it to the staging folder, and
    /// registers the IMC VFX id plus both redirects in the player's collection.
    /// </summary>
    private void RegisterSynced(PreparedTexture tex)
    {
        var target = SyncTarget.For(EffectiveDelivery)!;
        var modDir = sync!.ModDirectory;
        if (modDir == null)
        {
            message = StatusMessage.Of(MessageKey.SyncNoModDirectory);
            state = FallbackState();
            return;
        }
        var staging = stagingFor(modDir);
        var shape = config.Current.Shape(HalfDomeAvailable);
        var bakedYaw = YawRadians();
        var build = new DomeBuild(config.FlipHorizontal, config.Current.VerticalScale, config.HorizontalRepeat, config.VerticalRepeat, bakedYaw,
                                  config.Current.Radius, shape.CentreHeight, shape.Kind, shape.FloorY);
        // Item VFX are anchored at world height 0, not at the item (seen in game, 2026-11-16): raise the dome to the player.
        var anchorY = game.PlayerPosition?.Y ?? 0f;
        var lights = PlanLights(tex, build);
        byte[] avfx;
        try
        {
            avfx = DomeAvfxBuilder.Build(new DomeAvfxParams(tex.GamePath, config.Current.Radius, build.VerticalScale,
                build.URepeat, build.VRepeat, build.Flip, config.Brightness, shape.FloorY,
                BakedHeight: shape.CentreHeight, BakedYawRadians: bakedYaw, AttachToOwner: true,
                EmitterBaseHeight: anchorY, Lights: lights.Lights, Mesh: shape.Kind));
            log.Information($"[Milieu] Synced dome anchor height {anchorY:0.00} (player world Y).");
            LogLights("Synced", lights, new Vector3(0, LightRigLayout.AnchorHeight, 0));
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            log.Error("[Milieu] Could not build the synced dome VFX: " + LogText.Describe(e, cache.Root, modDir));
            message = StatusMessage.Of(MessageKey.WriteFailed);
            state = FallbackState();
            return;
        }
        var avfxDisk = staging.Store.AvfxDiskPath(CacheStore.AvfxKey(avfx));
        try
        {
            staging.EnsureFolder();
            if (!File.Exists(avfxDisk)) staging.Store.WriteAtomic(avfxDisk, avfx);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning("[Milieu] Could not write the synced dome VFX: " + LogText.Describe(e, cache.Root, modDir));
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
        var (failure, failureMessage, shownLost) = synced!.Begin(tex, avfxDisk, target);
        if (failure != MessageKey.None)
        {
            message = failureMessage ?? StatusMessage.Of(failure);
            if (shownLost) ForgetShownSynced();
            state = FallbackState();
            if (shownLost && state == BackdropState.Hidden) Hidden?.Invoke();
            return;
        }
        pendingShape = shape;
        pendingImagePath = textureImagePath;
        pendingBuild = build;
        pendingLights = lights;
        waitTicks = 0;
        state = BackdropState.AwaitingRedirect;
    }

    /// <summary>
    /// One log line per dome VFX light: azimuth, elevation, Bri and distance from <paramref name="reference"/> (the anchor in
    /// the lights' frame), for in-game tuning.
    /// </summary>
    private void LogLights(string label, VfxLightPlan plan, Vector3 reference)
    {
        foreach (var l in plan.Lights)
        {
            var rel = l.Position - reference;
            var d = rel.Length();
            var az = MathF.Atan2(rel.X, rel.Z) * 180f / MathF.PI;
            var el = d > 0 ? MathF.Asin(Math.Clamp(rel.Y / d, -1f, 1f)) * 180f / MathF.PI : 0f;
            log.Information($"[Milieu] {label} {(l.Shadows ? "key" : "fill")} light: azimuth {az:0}°, elevation {el:0}°, Bri {l.Brightness:0.0}, distance {d:0.0}.");
        }
    }

    private void TickAwaitingSynced()
    {
        if (synced!.PendingResolved())
        {
            var target = synced.Pending!.Target;
            var syncWarnings = synced.Commit();
            // Replace, never append: the texture's warnings plus the sync warnings, once each.
            var list = new List<StatusMessage>();
            foreach (var w in (texture?.Warnings ?? []).Concat(syncWarnings))
                if (!list.Any(x => x.Key == w.Key)) list.Add(w);
            warnings = list;
            shownShape = pendingShape;
            shownImagePath = pendingImagePath;
            shownBuild = pendingBuild;
            SyncedLights = pendingLights;
            state = BackdropState.Shown;
            message = StatusMessage.Of(target.ShownMessage);
            CleanupStaging(keepShown: true);
            return;
        }
        if (++waitTicks < RedirectTimeoutTicks) return;
        log.Warning("[Milieu] Penumbra did not resolve the synced backdrop files in time.");
        var lost = synced.AbortPending();
        if (lost) ForgetShownSynced();
        message = StatusMessage.Of(MessageKey.SyncRedirectTimeout);
        state = FallbackState();
        if (lost && state == BackdropState.Hidden) Hidden?.Invoke();
    }

    private void TickAwaiting()
    {
        if (synced?.Pending != null) { TickAwaitingSynced(); return; }
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
            var spawned = SpawnDome(pendingAvfxGamePath, pendingBuild);
            if (spawned == null)
            {
                message = StatusMessage.Of(MessageKey.InteropUnavailable);
                failedLocalGPose = pendingLocalGPose;
                state = FallbackState();
                return;
            }
            if (old is { IsDead: false }) spawner.Remove(old);   // new one first, then the old one: no flicker
            handle = spawned;
            shownSet = pendingSet;
            shownAvfxGamePath = pendingAvfxGamePath;
            shownShape = pendingShape;
            shownImagePath = pendingImagePath;
            shownBuild = pendingBuild;
            LocalLights = pendingLocalLights;
            shownLocalGPose = pendingLocalGPose;
            failedLocalGPose = null;
            if (LocalLights is { Lights.Count: > 0 } lit && shownBuild is { } sb)
                LogLights("Local", lit, new Vector3(0, LightRigLayout.AnchorHeight - sb.CentreHeight, 0));
            pendingSet = null; pendingAvfxGamePath = null; pendingLocalLights = null;
            state = BackdropState.Shown;
            readyFrames = 0;
            CleanupCache();
            return;
        }
        if (++waitTicks < RedirectTimeoutTicks) return;
        message = StatusMessage.Of(MessageKey.RedirectTimeout);
        log.Warning("[Milieu] Penumbra did not resolve the backdrop files in time.");
        failedLocalGPose = pendingLocalGPose;
        pendingSet = null; pendingAvfxGamePath = null; pendingLocalLights = null;
        if (shownSet != null) penumbra.Set(shownSet);
        else penumbra.Clear();
        state = FallbackState();
    }

    private void TickShown()
    {
        if (SyncedLive)
        {
            // The item carries the dome: game readiness doesn't matter. Follow the player's collection if it changes.
            if (ticks % PenumbraRefreshTicks == 0 && synced!.CollectionMoved() && texture != null) Register(texture);
            return;
        }
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
        var spawned = SpawnDome(shownAvfxGamePath, shownBuild);
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

    /// <summary>
    /// Where the dome of <paramref name="build"/> is spawned: its built centre height (docs/adr/0015), so a failed rebuild
    /// after a shape change never respawns the old file at the new shape's height; a sphere follows the live height, since a
    /// height change respawns it without a rebuild; no recorded build falls back to the live shape's centre.
    /// </summary>
    private Vector3 SpawnPosition(DomeBuild? build) => centre + new Vector3(0, build switch
    {
        { Kind: DomeMeshKind.Sphere } => config.Current.HeightOffset,
        { } b => b.CentreHeight,
        null => config.Current.Shape(HalfDomeAvailable).CentreHeight,   // defensive: believed unreachable (every spawn path has a build)
    }, 0);

    private float YawRadians() => config.Current.YawDegrees * MathF.PI / 180f;

    /// <summary>Every local dome spawn goes through here, so the drawn yaw (<see cref="Lit"/>) is the one actually spawned.</summary>
    private VfxHandle? SpawnDome(string gamePath, DomeBuild? build)
    {
        var yaw = YawRadians();
        var spawned = spawner.Spawn(gamePath, SpawnPosition(build), yaw);
        if (spawned != null) spawnedYaw = yaw;
        return spawned;
    }

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

    /// <summary>Deletes Milieu's staged files except (optionally) the shown registration's. Only if the folder exists.</summary>
    private void CleanupStaging(bool keepShown)
    {
        if (sync?.ModDirectory is not { } dir) return;
        var staging = stagingFor(dir);
        if (!staging.Exists) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keepShown)
            foreach (var reg in new[] { synced?.Shown, synced?.Pending })
                if (reg != null)
                    foreach (var disk in reg.Plan.Paths.Values) keep.Add(Path.GetFileName(disk));
        if (currentStaging != null && ReferenceEquals(currentStaging.Job, job) && currentStaging.Output is { } running)
            keep.Add(Path.GetFileName(running));
        TryCleanup(staging, keep, dir);
    }

    private void TryCleanup(SyncStaging staging, HashSet<string> keep, string dir)
    {
        try { staging.Store.Cleanup(keep); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Debug("[Milieu] Staging cleanup skipped: " + LogText.Describe(e, dir));
        }
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
    /// Plugin unload: cancel the job (the caller waits for <see cref="RunningJob"/> up to 2 s), remove the dome, the local
    /// temp mod and the synced mod (disabled and emptied; after Penumbra went away, emptied on disk only). Framework thread only.
    /// </summary>
    public void Teardown()
    {
        CancelJob();
        RemoveDome();
        penumbra.Clear();
        shownSet = null; pendingSet = null;
        if (synced is { NeedsCleanup: true }) synced.RemoveAll(redraw: true);
        SyncedLights = null;
        LocalLights = null; pendingLocalLights = null;
        CleanupStaging(keepShown: false);   // whenever the folder exists (e.g. a failed registration's atex); never creates it
        state = BackdropState.Hidden;
        Hidden?.Invoke();
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
