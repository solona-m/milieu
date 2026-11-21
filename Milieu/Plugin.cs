using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Gui;
using Milieu.Imaging;
using Milieu.Interop;
using Milieu.Lighting;
using Milieu.Sync;

namespace Milieu;

public enum CommandAction { ToggleWindow, Hide }

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hooks { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;

    /// <summary>Hand-maintained; bump it for every build meant for the game.</summary>
    public const int BuildNumber = 56;

    private const string CommandName = "/milieu";

    private readonly WindowSystem windows = new("Milieu");
    private readonly MainWindow mainWindow;
    private readonly Configuration config;
    private readonly ILogSink log;
    private readonly NativeStringPool strings = new();
    private readonly VfxInterop vfx;
    private readonly PenumbraIpc penumbra;
    private readonly BackdropController controller;
    private readonly SceneLighting? lighting;
    private readonly NightWhileShown? night;
    private readonly SunOff? sunOff;
    private readonly IDisposable? sunOffInterop;
    private readonly DefaultBackdropsDownloadService downloads;
    private readonly SceneGridViewModel grid;

    public Plugin()
    {
        log = new DalamudLog(Log);
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var configDir = PluginInterface.GetPluginConfigDirectory();
        config.Migrate(configDir);
        config.Clamp(SyncedAvailable());
        var cache = new CacheStore(Path.Combine(configDir, "cache"));
        var thumbStore = new ThumbStore(Path.Combine(configDir, "cache", "thumbs"));
        try { cache.Cleanup(new HashSet<string>()); }   // nothing is shown at startup, so nothing needs keeping
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug("[Milieu] Startup cache cleanup skipped."); }

        vfx = new VfxInterop(SigScanner, Hooks, Framework, log, strings);
        penumbra = new PenumbraIpc(PluginInterface, log, configDir);
        downloads = new DefaultBackdropsDownloadService(log, configDir);
        var gameState = new GameState(ClientState, Condition, ObjectTable);
        var preparer = new BackdropPreparer(cache, CreateBlockEncoder(FastConversionAvailable()), downloads, thumbStore, log, storeRigs: LightingAvailable());
        controller = new BackdropController(vfx, penumbra, gameState, preparer, cache, config, log,
                                            bundled: downloads, save: Save, syncedAvailable: SyncedAvailable,
                                            sync: penumbra, gameData: new GameData(DataManager),
                                            loadRig: SyncedRigLoader(LightingAvailable(), SyncedLightsAvailable(), preparer.PrepareLightsAsync),
                                            loadLocalRig: LocalRigLoader(LightingAvailable(), LocalLightsAvailable(), preparer.PrepareLightsAsync));
        lighting = CreateLighting(LightingAvailable(), () => new LightInterop(SigScanner, Framework, log), gameState, () => controller.Lit,
                                  config, preparer.PrepareLightsAsync,
                                  () => new FileLightingSentinel(Path.Combine(configDir, FileLightingSentinel.FileName), log), log,
                                  () => Framework.IsFrameworkUnloading, () => controller.EffectiveDelivery,
                                  () => LightingAvailable() && SyncedLightsAvailable());
        night = CreateNight(NightAvailable(), () => new EnvTimeInterop(Framework, log), gameState, () => controller.IsDrawn, config,
                            () => new FileLightingSentinel(Path.Combine(configDir, FileLightingSentinel.NightFileName), log, "Night sentinel"), log,
                            () => Framework.IsFrameworkUnloading);
        sunOff = CreateSunOff(SunOffAvailable(), () => new SunOffInterop(SigScanner, Hooks, Framework, log), gameState, () => controller.Lit,
                              () => controller.EffectiveDelivery, config,
                              () => new FileLightingSentinel(Path.Combine(configDir, FileLightingSentinel.SunOffFileName), log, "Sun-off sentinel"),
                              log, () => Framework.IsFrameworkUnloading, out var sunSwitch);
        sunOffInterop = sunSwitch as IDisposable;
        Wire(controller, lighting, night, sunOff);   // the None tile and /milieu off remove the lights, restore the time and the sun within the call
        var userImages = config.UserImages.ToArray();
        Task.Run(() =>
        {
            // Startup tidy-up off the framework thread: v1 JPEGs and unpinned textures, orphaned thumbnails, old light rigs.
            try
            {
                downloads.CleanupStale();
                thumbStore.Cleanup(userImages);
                if (LightingAvailable()) new LightRigStore(cache).TrimRigs(LightRigStore.DefaultKeep);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug("[Milieu] Startup cleanup skipped."); }
        });

        vfx.GameRemoved += controller.OnGameRemoved;
        penumbra.Initialized += OnPenumbraInitialized;
        penumbra.Disposed += OnPenumbraDisposed;
        Framework.Update += OnUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        ClientState.Logout += OnLogout;
        Framework.RunOnFrameworkThread(controller.OnPenumbraInitialized);   // clear a temp mod an earlier session left

        grid = new SceneGridViewModel(config, Save, controller.Post, controller, downloads, File.Exists);
        mainWindow = new MainWindow(controller, config, Save, grid, new ThumbnailCache(TextureProvider, thumbStore),
                                    () => controller.LocalLightsActive, SyncedAvailable,
                                    () => lighting?.Status ?? LightingStatusSnapshot.Initial, lighting != null,
                                    () => night?.Status ?? NightStatus.Off, night != null,
                                    () => sunOff?.Status ?? SunOffStatus.Off, sunOff != null);
        windows.AddWindow(mainWindow);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Milieu window. '/milieu off' hides the backdrop.",
        });
        // Scene lighting runs only in GPose, so the window has to stay usable there.
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.OpenSettings;
    }

    /// <summary>"off" hides the backdrop; anything else toggles the window.</summary>
    public static CommandAction ParseCommand(string args) =>
        string.Equals(args.Trim(), "off", StringComparison.OrdinalIgnoreCase) ? CommandAction.Hide : CommandAction.ToggleWindow;

    private void OnCommand(string command, string args)
    {
        if (ParseCommand(args) == CommandAction.Hide) grid.SelectNone();
        else mainWindow.Toggle();
    }

    private void Save() => SaveConfig(config, c => PluginInterface.SavePluginConfig(c));

    /// <summary>
    /// Test seam: every save stores the working copy into the selected backdrop's entry first (docs/adr/0014), under the
    /// config's lock, so serialization never sees the store mid-write.
    /// </summary>
    internal static void SaveConfig(Configuration c, Action<Configuration> persist)
    {
        lock (c.Gate)
        {
            c.StoreSelected();
            persist(c);
        }
    }

    /// <summary>
    /// Synced delivery is offered on every build (the owner enabled it for stable on 2026-11-09, docs/adr/0006). This is the
    /// kill switch: return false to hide the "Visible to" options again and load a saved synced choice as Only me.
    /// </summary>
    private static bool SyncedAvailable() => true;

    /// <summary>
    /// Scene lighting's kill switch (docs/adr/0009): return false and nothing lighting-related runs: SceneLighting isn't
    /// constructed, no light function is resolved or called, conversions extract and store no light rig, the rig cache
    /// isn't trimmed, and the "Scene lighting" block is hidden.
    /// </summary>
    private static bool LightingAvailable() => true;

    /// <summary>
    /// Synced VFX lights' kill switch (docs/adr/0011): return false and synced domes carry no lights (no rig is loaded for
    /// them, no effector is written), and native GPose lights run under synced delivery as before. Off as well whenever
    /// scene lighting itself is off.
    /// </summary>
    private static bool SyncedLightsAvailable() => true;

    /// <summary>
    /// Local dome lights' kill switch (docs/adr/0011, local amendment): return false and "Only me" domes carry no lights (no
    /// rig is loaded for them, no effector is written; the panel texts are today's), and the scene keeps only ambient light
    /// outside GPose with the sun off. Off as well whenever scene lighting itself is off. Release gate: flip this to false
    /// before tagging if the in-game positive control (ADR 0011 local amendment) fails or local dome lights regress.
    /// </summary>
    private static bool LocalLightsAvailable() => true;

    /// <summary>
    /// Night while shown's kill switch (docs/adr/0012): return false and nothing night-related is constructed, resolved or
    /// drawn (no time-of-day read or write, no "Time of day" block).
    /// </summary>
    private static bool NightAvailable() => true;

    /// <summary>
    /// Sun off's kill switch (docs/adr/0013): return false and nothing sun-related is scanned, hooked, constructed or drawn.
    /// Release gate: no testing or stable tag ships with this true (and the option on by default) until ADR 0013's
    /// UNVERIFIED items 1, 2, 3 and 5 have passed in game; otherwise flip this to false, or the default to off, before tagging.
    /// </summary>
    private static bool SunOffAvailable() => true;

    /// <summary>
    /// Fast conversion's kill switch (docs/adr/0015): return false and user images are BC7-encoded by the reference encoder
    /// (BCnEncoder, about ten times slower) as before, instead of the in-repo <see cref="OpaqueBc7"/>. Release gate: flip
    /// this to false before tagging if the in-game visual check of converted user images fails.
    /// </summary>
    private static bool FastConversionAvailable() => true;

    /// <summary>The block encoder for user images: the in-repo encoder, or the reference encoder with the kill switch off.</summary>
    internal static IBlockEncoder CreateBlockEncoder(bool fast) => fast ? new OpaqueBc7() : new Bc7();

    /// <summary>
    /// Test seam: builds sun off and its switch, or nothing at all (the factories are never called) when the kill switch is
    /// off. <paramref name="created"/> is the switch, disposed with the hooks.
    /// </summary>
    internal static SunOff? CreateSunOff(bool available, Func<ISunSwitch> switchFactory, Backdrop.IGameState game, Func<LitBackdrop?> lit,
                                         Func<DeliveryMode> delivery, Configuration config, Func<ILightingSentinel> sentinelFactory,
                                         ILogSink log, Func<bool> isUnloading, out ISunSwitch? created)
    {
        created = null;
        if (!available) return null;
        created = switchFactory();
        return new SunOff(created, game, lit, delivery, config, sentinelFactory(), log, isUnloading);
    }

    /// <summary>Test seam: builds night while shown, or nothing at all (the factories are never called) when the kill switch is off.</summary>
    internal static NightWhileShown? CreateNight(bool available, Func<IEnvTime> envFactory, Backdrop.IGameState game, Func<bool> drawn,
                                                 Configuration config, Func<ILightingSentinel> sentinelFactory, ILogSink log,
                                                 Func<bool> isUnloading)
    {
        if (!available) return null;
        return new NightWhileShown(envFactory(), game, drawn, config, sentinelFactory(), log, isUnloading);
    }

    /// <summary>Test seam: the controller's rig loader for synced VFX lights, or null when either kill switch is off.</summary>
    internal static Func<string, CancellationToken, Task<LightRig?>>? SyncedRigLoader(bool lightingAvailable, bool syncedLightsAvailable,
                                                                                       Func<string, CancellationToken, Task<LightRig?>> load) =>
        lightingAvailable && syncedLightsAvailable ? load : null;

    /// <summary>Test seam: the controller's rig loader for local dome lights, or null when either kill switch is off.</summary>
    internal static Func<string, CancellationToken, Task<LightRig?>>? LocalRigLoader(bool lightingAvailable, bool localLightsAvailable,
                                                                                      Func<string, CancellationToken, Task<LightRig?>> load) =>
        lightingAvailable && localLightsAvailable ? load : null;

    /// <summary>Test seam: builds scene lighting, or nothing at all (the factories are never called) when the kill switch is off.</summary>
    internal static SceneLighting? CreateLighting(bool available, Func<ILightSpawner> spawnerFactory, Backdrop.IGameState game,
                                                  Func<LitBackdrop?> lit, Configuration config,
                                                  Func<string, CancellationToken, Task<LightRig?>> loadRig,
                                                  Func<ILightingSentinel> sentinelFactory, ILogSink log, Func<bool> isUnloading,
                                                  Func<DeliveryMode> delivery, Func<bool> syncedLightsAvailable)
    {
        if (!available) return null;
        return new SceneLighting(spawnerFactory(), game, lit, config, loadRig, sentinelFactory(), log, isUnloading, delivery,
                                 syncedLightsAvailable);
    }

    /// <summary>Every running job (conversion, light rig) as one task to wait on, or null when none runs.</summary>
    internal static Task? CombineJobs(params Task?[] jobs)
    {
        var running = jobs.Where(j => j != null).Select(j => j!).ToArray();
        return running.Length == 0 ? null : Task.WhenAll(running);
    }

    private void OnUpdate(IFramework _) => TickAll(controller.Tick, lighting == null ? null : lighting.Tick, night == null ? null : night.Tick,
                                                   sunOff == null ? null : sunOff.Tick);

    private void OnTerritoryChanged(uint _) => OnZoneOrLogout(controller.OnTerritoryChanged, lighting, night, zone: true, sunOff);

    private void OnLogout(int type, int code) => OnZoneOrLogout(controller.OnLogout, lighting, night, zone: false, sunOff);

    /// <summary>
    /// Test seam, the tick order: controller, then lighting, then night, then sun off, so a dome that went away this tick
    /// takes its lights, the night and the sun off with it in the same tick.
    /// </summary>
    internal static void TickAll(Action controllerTick, Action? lightingTick, Action? nightTick, Action? sunOffTick = null)
    {
        controllerTick();
        lightingTick?.Invoke();
        nightTick?.Invoke();
        sunOffTick?.Invoke();
    }

    /// <summary>
    /// Test seam: a zone change or logout. The night is forgotten <em>first</em> (nothing written now; restored on the first
    /// ready tick), because the controller's logout hides the backdrop and its Hidden event would otherwise restore the time
    /// during the logout. Then the sun comes back (writes stop), then the controller's own handling, then the lights.
    /// </summary>
    internal static void OnZoneOrLogout(Action controllerEvent, SceneLighting? lighting, NightWhileShown? night, bool zone,
                                        SunOff? sunOff = null)
    {
        if (zone) night?.OnTerritoryChanged();
        else night?.Forget();
        sunOff?.Release();
        controllerEvent();
        lighting?.RemoveAll();
    }

    /// <summary>Test seam: the Hidden subscriptions (lights removed and the time restored within the Hide call).</summary>
    internal static void Wire(BackdropController controller, SceneLighting? lighting, NightWhileShown? night, SunOff? sunOff = null)
    {
        if (lighting != null) controller.Hidden += lighting.RemoveAll;
        if (night != null) controller.Hidden += night.Release;
        if (sunOff != null) controller.Hidden += sunOff.Release;
    }

    internal static void Unwire(BackdropController controller, SceneLighting? lighting, NightWhileShown? night, SunOff? sunOff = null)
    {
        if (lighting != null) controller.Hidden -= lighting.RemoveAll;
        if (night != null) controller.Hidden -= night.Release;
        if (sunOff != null) controller.Hidden -= sunOff.Release;
    }

    // Penumbra events fire on Penumbra's thread: hop to the framework thread before touching the VFX or the IPC.
    private void OnPenumbraInitialized() => Framework.RunOnFrameworkThread(controller.OnPenumbraInitialized);

    private void OnPenumbraDisposed() => Framework.RunOnFrameworkThread(controller.OnPenumbraDisposed);

    public void Dispose()
    {
        ShutdownSequence.Run(
            cancelJobs: () => CombineJobs(controller.CancelJobForDispose(), lighting?.CancelJobForDispose()),
            disposeDownloads: downloads.Dispose,
            runTeardown: wait =>
            {
                if (Framework.IsInFrameworkUpdateThread) { TeardownAll(); return true; }
                return Framework.RunOnFrameworkThread(TeardownAll).Wait(wait);
            },
            unhook: Unhook,
            freeStrings: strings.FreeAll,
            leakStrings: strings.Leak,
            log: log);
    }

    private void TeardownAll() => TeardownOrder(lighting == null ? null : lighting.Teardown, night == null ? null : night.Teardown,
                                                controller.Teardown, sunOff == null ? null : sunOff.Teardown);

    /// <summary>
    /// Test seam: the lights, then the night, then sun off, go before the dome (no native call is made while the game is
    /// exiting). The sun-off hook itself is disposed later, in Unhook (its flag is closed first).
    /// </summary>
    internal static void TeardownOrder(Action? lightingTeardown, Action? nightTeardown, Action controllerTeardown, Action? sunOffTeardown = null)
    {
        lightingTeardown?.Invoke();
        nightTeardown?.Invoke();
        sunOffTeardown?.Invoke();
        controllerTeardown();
    }

    private void Unhook()
    {
        Unwire(controller, lighting, night, sunOff);
        sunOffInterop?.Dispose();   // closes the flag, then disposes the hook
        vfx.GameRemoved -= controller.OnGameRemoved;
        vfx.Dispose();
        penumbra.Initialized -= OnPenumbraInitialized;
        penumbra.Disposed -= OnPenumbraDisposed;
        penumbra.Dispose();
        Framework.Update -= OnUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        ClientState.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.OpenSettings;
        CommandManager.RemoveHandler(CommandName);
        windows.RemoveAllWindows();
        mainWindow.Dispose();
    }

    private sealed class DalamudLog(IPluginLog log) : ILogSink
    {
        public void Debug(string message) => log.Debug(message);
        public void Information(string message) => log.Information(message);
        public void Warning(string message) => log.Warning(message);
        public void Error(string message) => log.Error(message);
    }
}
