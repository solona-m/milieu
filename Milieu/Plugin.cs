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
    public const int BuildNumber = 42;

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
        var preparer = new BackdropPreparer(cache, new Bc7(), downloads, thumbStore, log, storeRigs: LightingAvailable());
        controller = new BackdropController(vfx, penumbra, gameState, preparer, cache, config, log,
                                            bundled: downloads, save: Save, syncedAvailable: SyncedAvailable,
                                            sync: penumbra, gameData: new GameData(DataManager),
                                            loadRig: SyncedRigLoader(LightingAvailable(), SyncedLightsAvailable(), preparer.PrepareLightsAsync));
        lighting = CreateLighting(LightingAvailable(), () => new LightInterop(SigScanner, Framework, log), gameState, () => controller.Lit,
                                  config, preparer.PrepareLightsAsync,
                                  () => new FileLightingSentinel(Path.Combine(configDir, FileLightingSentinel.FileName), log), log,
                                  () => Framework.IsFrameworkUnloading, () => controller.EffectiveDelivery,
                                  () => LightingAvailable() && SyncedLightsAvailable());
        night = CreateNight(NightAvailable(), () => new EnvTimeInterop(Framework, log), gameState, () => controller.IsDrawn, config,
                            () => new FileLightingSentinel(Path.Combine(configDir, FileLightingSentinel.NightFileName), log, "Night sentinel"), log,
                            () => Framework.IsFrameworkUnloading);
        Wire(controller, lighting, night);   // the None tile and /milieu off remove the lights and restore the time within the call
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
        mainWindow = new MainWindow(controller, config, Save, grid, new ThumbnailCache(TextureProvider, thumbStore), SyncedAvailable,
                                    () => lighting?.Status ?? LightingStatusSnapshot.Initial, lighting != null,
                                    () => night?.Status ?? NightStatus.Off, night != null);
        windows.AddWindow(mainWindow);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Milieu window. '/milieu off' hides the backdrop.",
        });
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

    private void Save() => PluginInterface.SavePluginConfig(config);

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
    /// Night while shown's kill switch (docs/adr/0012): return false and nothing night-related is constructed, resolved or
    /// drawn (no time-of-day read or write, no "Time of day" block).
    /// </summary>
    private static bool NightAvailable() => true;

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

    private void OnUpdate(IFramework _) => TickAll(controller.Tick, lighting == null ? null : lighting.Tick, night == null ? null : night.Tick);

    private void OnTerritoryChanged(uint _) => OnZoneOrLogout(controller.OnTerritoryChanged, lighting, night, zone: true);

    private void OnLogout(int type, int code) => OnZoneOrLogout(controller.OnLogout, lighting, night, zone: false);

    /// <summary>
    /// Test seam, the tick order: controller, then lighting, then night, so a dome that went away this tick takes its lights
    /// and the night with it in the same tick.
    /// </summary>
    internal static void TickAll(Action controllerTick, Action? lightingTick, Action? nightTick)
    {
        controllerTick();
        lightingTick?.Invoke();
        nightTick?.Invoke();
    }

    /// <summary>
    /// Test seam: a zone change or logout. The night is forgotten <em>first</em> (nothing written now; restored on the first
    /// ready tick), because the controller's logout hides the backdrop and its Hidden event would otherwise restore the time
    /// during the logout. Then the controller's own handling, then the lights.
    /// </summary>
    internal static void OnZoneOrLogout(Action controllerEvent, SceneLighting? lighting, NightWhileShown? night, bool zone)
    {
        if (zone) night?.OnTerritoryChanged();
        else night?.Forget();
        controllerEvent();
        lighting?.RemoveAll();
    }

    /// <summary>Test seam: the Hidden subscriptions (lights removed and the time restored within the Hide call).</summary>
    internal static void Wire(BackdropController controller, SceneLighting? lighting, NightWhileShown? night)
    {
        if (lighting != null) controller.Hidden += lighting.RemoveAll;
        if (night != null) controller.Hidden += night.Release;
    }

    internal static void Unwire(BackdropController controller, SceneLighting? lighting, NightWhileShown? night)
    {
        if (lighting != null) controller.Hidden -= lighting.RemoveAll;
        if (night != null) controller.Hidden -= night.Release;
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

    private void TeardownAll() => TeardownOrder(lighting == null ? null : lighting.Teardown, night == null ? null : night.Teardown, controller.Teardown);

    /// <summary>Test seam: the lights, then the night, go before the dome (no native call is made while the game is exiting).</summary>
    internal static void TeardownOrder(Action? lightingTeardown, Action? nightTeardown, Action controllerTeardown)
    {
        lightingTeardown?.Invoke();
        nightTeardown?.Invoke();
        controllerTeardown();
    }

    private void Unhook()
    {
        Unwire(controller, lighting, night);
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
