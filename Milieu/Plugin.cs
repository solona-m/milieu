using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public const int BuildNumber = 16;

    private const string CommandName = "/milieu";

    private readonly WindowSystem windows = new("Milieu");
    private readonly MainWindow mainWindow;
    private readonly Configuration config;
    private readonly ILogSink log;
    private readonly NativeStringPool strings = new();
    private readonly VfxInterop vfx;
    private readonly PenumbraIpc penumbra;
    private readonly BackdropController controller;
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
        controller = new BackdropController(vfx, penumbra, new GameState(ClientState, Condition, ObjectTable),
                                            new BackdropPreparer(cache, new Bc7(), downloads, thumbStore), cache, config, log,
                                            bundled: downloads, save: Save, syncedAvailable: SyncedAvailable,
                                            sync: penumbra, gameData: new GameData(DataManager));
        var userImages = config.UserImages.ToArray();
        Task.Run(() =>
        {
            // Startup tidy-up off the framework thread: v1 JPEGs and unpinned textures, orphaned thumbnails.
            try { downloads.CleanupStale(); thumbStore.Cleanup(userImages); }
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
        mainWindow = new MainWindow(controller, config, Save, grid, new ThumbnailCache(TextureProvider, thumbStore), SyncedAvailable);
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
    /// Synced delivery (experimental, never tested in game yet) is offered on testing and dev builds only: on stable, PSync
    /// pairs would receive untested VFX bytes without having opted in (docs/adr/0006). One line to enable it for stable.
    /// </summary>
    private static bool SyncedAvailable() => PluginInterface.IsTesting || PluginInterface.IsDev;

    private void OnUpdate(IFramework _) => controller.Tick();

    private void OnTerritoryChanged(uint _) => controller.OnTerritoryChanged();

    private void OnLogout(int type, int code) => controller.OnLogout();

    // Penumbra events fire on Penumbra's thread: hop to the framework thread before touching the VFX or the IPC.
    private void OnPenumbraInitialized() => Framework.RunOnFrameworkThread(controller.OnPenumbraInitialized);

    private void OnPenumbraDisposed() => Framework.RunOnFrameworkThread(controller.OnPenumbraDisposed);

    public void Dispose()
    {
        ShutdownSequence.Run(
            cancelJobs: controller.CancelJobForDispose,
            disposeDownloads: downloads.Dispose,
            runTeardown: wait =>
            {
                if (Framework.IsInFrameworkUpdateThread) { controller.Teardown(); return true; }
                return Framework.RunOnFrameworkThread(controller.Teardown).Wait(wait);
            },
            unhook: Unhook,
            freeStrings: strings.FreeAll,
            leakStrings: strings.Leak,
            log: log);
    }

    private void Unhook()
    {
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
