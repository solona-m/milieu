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
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hooks { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;

    /// <summary>Hand-maintained; bump it for every build meant for the game.</summary>
    public const int BuildNumber = 23;

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
                                            sync: penumbra, gameData: new GameData(DataManager));
        lighting = CreateLighting(LightingAvailable(), () => new LightInterop(SigScanner, Framework, log), gameState, () => controller.Lit,
                                  config, preparer.PrepareLightsAsync,
                                  () => new FileLightingSentinel(Path.Combine(configDir, FileLightingSentinel.FileName), log), log,
                                  () => Framework.IsFrameworkUnloading, () => controller.EffectiveDelivery);
        if (lighting != null) controller.Hidden += lighting.RemoveAll;   // the None tile and /milieu off remove the lights within the call
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
                                    () => lighting?.Status ?? LightingStatusSnapshot.Initial, lighting != null);
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

    /// <summary>The VFX light experiment's mode from "vfxlight &lt;0-5&gt;", or null when the args are anything else.</summary>
    public static int? ParseVfxLight(string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("vfxlight", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mode)
               && mode is >= 0 and <= 5 ? mode : null;
    }

    /// <summary>The args start with "vfxlight" (so a bad mode gets a usage line instead of toggling the window).</summary>
    public static bool IsVfxLightCommand(string args) =>
        args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] && first.Equals("vfxlight", StringComparison.OrdinalIgnoreCase);

    private void OnCommand(string command, string args)
    {
        // Experiment (testing builds; remove before the next stable merge): "/milieu vfxlight <0-5>".
        switch (ParseVfxLight(args))
        {
            case int mode:
                GameFiles.DomeAvfxBuilder.TestLightMode = mode;
                ChatGui.Print($"[Milieu] Test VFX light {mode} ({mode switch { 1 => "point, set up like the venue", 2 => "point, long range + bright", 3 => "directional, as the game's", 4 => "directional, red + tilted", 5 => "directional, red + tilted, on the dome emitter", _ => "off" }}). Pick None, then your scene again.");
                return;
            case null when IsVfxLightCommand(args):
                ChatGui.Print("[Milieu] Usage: /milieu vfxlight 0-5 (0 off, 1 point like the venue, 2 point long range + bright, 3 directional as the game's, 4 directional red + tilted, 5 the same on the dome emitter).");
                return;
        }
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

    /// <summary>Test seam: builds scene lighting, or nothing at all (the factories are never called) when the kill switch is off.</summary>
    internal static SceneLighting? CreateLighting(bool available, Func<ILightSpawner> spawnerFactory, Backdrop.IGameState game,
                                                  Func<LitBackdrop?> lit, Configuration config,
                                                  Func<string, CancellationToken, Task<LightRig?>> loadRig,
                                                  Func<ILightingSentinel> sentinelFactory, ILogSink log, Func<bool> isUnloading,
                                                  Func<DeliveryMode> delivery)
    {
        if (!available) return null;
        return new SceneLighting(spawnerFactory(), game, lit, config, loadRig, sentinelFactory(), log, isUnloading, delivery);
    }

    /// <summary>Every running job (conversion, light rig) as one task to wait on, or null when none runs.</summary>
    internal static Task? CombineJobs(params Task?[] jobs)
    {
        var running = jobs.Where(j => j != null).Select(j => j!).ToArray();
        return running.Length == 0 ? null : Task.WhenAll(running);
    }

    // Lighting ticks after the controller, so a dome that went away this tick takes its lights with it in the same tick.
    private void OnUpdate(IFramework _)
    {
        controller.Tick();
        lighting?.Tick();
    }

    private void OnTerritoryChanged(uint _)
    {
        controller.OnTerritoryChanged();
        lighting?.RemoveAll();
    }

    private void OnLogout(int type, int code)
    {
        controller.OnLogout();
        lighting?.RemoveAll();
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

    /// <summary>The lights go before the dome (and no native light call is made while the game is exiting).</summary>
    private void TeardownAll()
    {
        lighting?.Teardown();
        controller.Teardown();
    }

    private void Unhook()
    {
        if (lighting != null) controller.Hidden -= lighting.RemoveAll;
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
