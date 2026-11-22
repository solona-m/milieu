using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Gui;
using Milieu.Imaging;
using Milieu.Lighting;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// Switching backdrops end to end (docs/adr/0014): a real BackdropController and SceneGridViewModel over one Configuration,
/// with scene lighting, night and sun off ticked in plugin order.
/// </summary>
public sealed class PerBackdropSwitchTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeSpawner spawner = new();
    private readonly FakePenumbra pen = new();
    private readonly FakeGame game = new();
    private readonly FakeLog log = new();
    private readonly FakeGameData data = new();
    private readonly CacheStore cache;
    private readonly KeyedPreparer preparer;
    private readonly Configuration config = new();
    private readonly FakeGridDownloads downloads = new();
    private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeEnvTime env = new();
    private readonly FakeSunSwitch sw = new();
    private readonly FakeLightSpawner lights = new();
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private BackdropController c = null!;
    private SceneGridViewModel grid = null!;
    private NightWhileShown night = null!;
    private SunOff sunOff = null!;
    private SceneLighting lighting = null!;
    private readonly string a, b;

    private const string RingPath = "chara/accessory/a0028/vfx/eff/va0001.avfx";

    private static LightRig Rig() =>
        new(LightExtractor.Version, new RigLight(0.5f, 0.25f, new Vector3(3, 2, 1)),
            [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
             new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
            new Vector3(10, 10, 10));

    public PerBackdropSwitchTests()
    {
        cache = new CacheStore(dir.File("cache"));
        preparer = new KeyedPreparer(cache);
        pen.ModDirectory = dir.File("mods");
        Directory.CreateDirectory(pen.ModDirectory);
        data.Files[SyncTarget.MadBirdRing.ImcPath] = SyncFixtures.RingImc(3);
        data.Files[SyncTarget.Cushion.ImcPath] = SyncFixtures.CushionImc(2);
        a = dir.File("a.hdr");
        b = dir.File("b.hdr");
        files.Add(a);
        files.Add(b);
        config.UserImages = [a, b];
        config.SyncUploadNoticeShown = true;
        spawner.Clock = () => now;
        Create();
        // A: room, lighting off, night on, sun off on, exposure -2. B: sky, lighting on, night off, sun off off, exposure +1.
        Seed(a, s => { s.Mode = DomeMode.Room; s.Room.Radius = 8; s.Room.YawDegrees = 30; s.HorizontalRepeat = 1; s.FlipHorizontal = true;
                       s.Brightness = 0.7f; s.ExposureEv = -2; s.ToneMap = ToneMapOperator.Reinhard; s.SceneLighting = false;
                       s.NightWhileShown = true; s.SunOffWhileShown = true; });
        Seed(b, s => { s.Mode = DomeMode.Sky; s.Sky.Radius = 5000; s.Sky.HeightOffset = 3; s.Sky.YawDegrees = -45; s.HorizontalRepeat = 2;
                       s.FlipHorizontal = false; s.Brightness = 1.2f; s.ExposureEv = 1; s.ToneMap = ToneMapOperator.Aces; s.SceneLighting = true;
                       s.LightStrength = 1.5f; s.NightWhileShown = false; s.SunOffWhileShown = false; });
    }

    public void Dispose() => dir.Dispose();

    private void Seed(string id, Action<BackdropSettings> set)
    {
        var e = new BackdropSettings();
        set(e);
        config.Backdrops[id] = e;
    }

    private void Create()
    {
        c = new BackdropController(spawner, pen, game, preparer, cache, config, log, run: f => f(), utcNow: () => now,
                                   syncedAvailable: () => true, sync: pen, gameData: data,
                                   loadRig: (_, _) => Task.FromResult<LightRig?>(Rig()), loadLocalRig: (_, _) => Task.FromResult<LightRig?>(Rig()));
        grid = new SceneGridViewModel(config, () => Plugin.SaveConfig(config, _ => { }), c.Post, c, downloads, files.Contains);
        night = new NightWhileShown(env, game, () => c.IsDrawn, config, new FakeSentinel(), log, () => false, () => now);
        sunOff = new SunOff(sw, game, () => c.Lit, () => c.EffectiveDelivery, config, new FakeSentinel(), log, () => false);
        lighting = new SceneLighting(lights, game, () => c.Lit, config, (_, _) => Task.FromResult<LightRig?>(Rig()), new FakeSentinel(), log,
                                     () => false, () => c.EffectiveDelivery, () => true, f => f());
        Plugin.Wire(c, lighting, night, sunOff);
    }

    private void Tick(int n = 1, int ms = 16)
    {
        for (var i = 0; i < n; i++)
        {
            now += TimeSpan.FromMilliseconds(ms);
            Plugin.TickAll(c.Tick, lighting.Tick, night.Tick, sunOff.Tick);
        }
    }

    private Tile T(string id) => grid.Tiles.Single(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private void Click(string id) { grid.Click(T(id)); Tick(3); }

    private ChunkNode LocalDome() => AvfxChunkReader.Parse(File.ReadAllBytes(pen.Sets[^1].First(kv => kv.Key.EndsWith(".avfx")).Value));

    private int SetCalls => pen.Calls.Count(x => x == "Set");

    // ---------------------------------------------------------------- AC

    [Fact] // [AC-per-backdrop-settings-1]
    public void ClickTile_ControllerBuildsWithThatBackdropsSettings()
    {
        Click(b);
        Click(a);
        Assert.Equal(BackdropState.Shown, c.State);
        var req = preparer.Requests[^1];
        Assert.Equal((a, -2f, ToneMapOperator.Reinhard), (req.ImagePath, req.ExposureEv, req.ToneMap));
        var spawn = spawner.Spawns[^1];
        Assert.Equal(30f * MathF.PI / 180f, spawn.Yaw, 4);
        Assert.Equal(game.PlayerPosition!.Value.Y + config.Backdrops[a].Room.Shape(true).CentreHeight, spawn.Pos.Y, 3);
        var e = config.Backdrops[a];
        var tex = pen.Sets[^1].First(kv => !kv.Key.EndsWith(".avfx")).Key;
        var expected = DomeAvfxBuilder.Build(new DomeAvfxParams(tex, e.Room.Radius, e.Room.VerticalScale, e.HorizontalRepeat, e.VerticalRepeat,
                                                                e.FlipHorizontal, e.Brightness, e.Room.Shape(true).FloorY, Mesh: e.Room.Shape(true).Kind));
        Assert.Equal(expected, File.ReadAllBytes(pen.Sets[^1].First(kv => kv.Key.EndsWith(".avfx")).Value));   // A's lighting is off: no lights
    }

    [Fact] // [AC-per-backdrop-settings-1]
    public void Switch_NightAndSunOffAndNativeLightsFollowSelection()
    {
        game.IsGPosing = true;
        Click(a);
        Tick(LightRigLayout.GposeSettleTicks + 5);
        Assert.Equal(NightStatus.On, night.Status);
        Assert.True(sw.Shared.Writing);
        Assert.Equal(0, lighting.LiveCount);              // A: scene lighting off
        Click(b);
        Tick(LightRigLayout.GposeSettleTicks + 5);
        Assert.Equal(NightStatus.Off, night.Status);
        Assert.False(sw.Shared.Writing);
        Assert.True(lighting.LiveCount > 0);              // B: scene lighting on (native, in GPose)
    }

    [Fact] // [AC-per-backdrop-settings-2]
    public void EditThenSwitchAwayAndBack_EditKept_OthersUntouched()
    {
        Click(a);
        var bBefore = Newtonsoft.Json.JsonConvert.SerializeObject(config.Backdrops[b]);
        config.Brightness = 1.9f;                          // a panel edit ends in save()
        Plugin.SaveConfig(config, _ => { });
        Click(b);
        Assert.Equal(1.2f, config.Brightness);
        Click(a);
        Assert.Equal(1.9f, config.Brightness);
        Assert.Equal(bBefore, Newtonsoft.Json.JsonConvert.SerializeObject(config.Backdrops[b]));
    }

    [Fact] // [AC-per-backdrop-settings-5]
    public void Switch_OnePrepareOneSetOneSpawn_NothingFollowsFor2s()
    {
        Click(a);
        var (prepares, sets, spawns, removes) = (preparer.Calls, SetCalls, spawner.Spawns.Count, spawner.Removes.Count);
        Click(b);
        Assert.Equal((prepares + 1, sets + 1, spawns + 1, removes + 1), (preparer.Calls, SetCalls, spawner.Spawns.Count, spawner.Removes.Count));
        Tick(125);   // 2 s
        Assert.Equal((prepares + 1, sets + 1, spawns + 1, removes + 1), (preparer.Calls, SetCalls, spawner.Spawns.Count, spawner.Removes.Count));
        Assert.Single(LocalDome().All("Efct"));             // B carries its own light
    }

    [Fact] // [AC-per-backdrop-settings-5] [EDGE-per-backdrop-settings-18]
    public void Switch_Synced_OneRegistrationOneRedraw_DeliveryAndNoticeUnchanged()
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        Click(a);
        var redraws = pen.Redraws;
        Click(b);
        Assert.Equal(redraws + 1, pen.Redraws);
        Tick(125);
        Assert.Equal(redraws + 1, pen.Redraws);
        Assert.Equal((DeliveryMode.MadBirdRing, true), (config.Delivery, config.SyncUploadNoticeShown));
    }

    [Theory] // [AC-per-backdrop-settings-11] [EDGE-per-backdrop-settings-17]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchToUndownloadedOrMissing_HidesDome_NightAndSunRestored_LightsRemoved(bool missing)
    {
        game.IsGPosing = true;
        Seed(BackdropPins.IdOf("forest"), s => s.SceneLighting = true);
        Click(b);                                          // lit in GPose, night off
        Tick(LightRigLayout.GposeSettleTicks + 5);
        Assert.True(lighting.LiveCount > 0);
        var aEntry = Newtonsoft.Json.JsonConvert.SerializeObject(config.Backdrops[b]);
        if (missing) files.Remove(a);
        grid.Click(T(missing ? a : BackdropPins.IdOf("forest")));
        Tick();
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.False(c.IsDrawn);
        Assert.Equal(0, lighting.LiveCount);
        Assert.False(sw.Shared.Writing);
        Assert.Equal(aEntry, Newtonsoft.Json.JsonConvert.SerializeObject(config.Backdrops[b]));
        if (missing) Assert.Equal(MessageKey.FileMissing, c.Status.Message.Key);
    }

    [Fact] // [AC-per-backdrop-settings-11]
    public void UndownloadedSceneShowsWhenDownloadCompletes()
    {
        var forest = BackdropPins.IdOf("forest");
        Click(a);
        grid.Click(T(forest));
        Tick();
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(new[] { "forest" }, downloads.Requests);
        downloads.Downloaded.Add("forest");
        grid.OnDraw();
        Tick(3);
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(forest, preparer.Requests[^1].ImagePath);
    }

    [Fact] // [AC-per-backdrop-settings-11]
    public void Synced_ClickUndownloaded_HideThenShow_TwoRedraws()
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        Click(a);
        var redraws = pen.Redraws;
        grid.Click(T(BackdropPins.IdOf("forest")));
        Tick();
        Assert.Equal(redraws + 1, pen.Redraws);            // the hide
        downloads.Downloaded.Add("forest");
        grid.OnDraw();
        Tick(3);
        Assert.Equal(redraws + 2, pen.Redraws);            // the show
    }

    // ---------------------------------------------------------------- edges

    [Fact] // [EDGE-per-backdrop-settings-14]
    public void PendingReconvertForA_ThenSwitch_OnePrepareWithBsExposure()
    {
        Click(a);
        var prepares = preparer.Calls;
        config.ExposureEv = 3;
        c.RequestReconvert();                              // queued for A, 500 ms
        grid.Click(T(b));
        Tick(60);                                          // well past 500 ms
        Assert.Equal(prepares + 1, preparer.Calls);
        Assert.Equal((b, 1f), (preparer.Requests[^1].ImagePath, preparer.Requests[^1].ExposureEv));
    }

    [Fact] // [EDGE-per-backdrop-settings-28]
    public void ExposureEditOnA_ThenClickMissingB_NoShowNoConversionError()
    {
        Click(a);
        var prepares = preparer.Calls;
        config.ExposureEv = 3;
        c.RequestReconvert();
        files.Remove(b);
        grid.Click(T(b));
        Tick(62);                                          // 1 s
        Assert.Equal(prepares, preparer.Calls);
        Assert.Equal(MessageKey.FileMissing, c.Status.Message.Key);
    }

    [Fact] // [EDGE-per-backdrop-settings-16]
    public void SwitchWhilePreparingA_CancelsA_OneJobForB()
    {
        var held = new TaskCompletionSource<PreparedTexture>();
        preparer.Gates.Enqueue(held);
        grid.Click(T(a));
        Tick();
        Assert.Equal(BackdropState.Preparing, c.State);
        grid.Click(T(b));
        Tick(3);
        Assert.Equal(2, preparer.Calls);
        Assert.True(preparer.Tokens[0].IsCancellationRequested);
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(b, preparer.Requests[^1].ImagePath);
    }

    [Theory] // [EDGE-per-backdrop-settings-17]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SwitchToUndownloadedOrMissing_ThenGPoseOrTerritory_NoRegisterNoSpawnOfA(bool missing, bool territory)
    {
        Click(a);
        if (missing) files.Remove(b);
        grid.Click(T(missing ? b : BackdropPins.IdOf("forest")));
        Tick();
        var (sets, spawns, prepares) = (SetCalls, spawner.Spawns.Count, preparer.Calls);
        if (territory) { c.OnTerritoryChanged(); Tick(60); }
        else { game.IsGPosing = true; Tick(40); game.IsGPosing = false; Tick(40); }
        c.RequestLightRebuild();
        c.ApplyPlacement();
        Tick(40);
        Assert.Equal((sets, spawns, prepares), (SetCalls, spawner.Spawns.Count, preparer.Calls));
    }

    [Fact] // [EDGE-per-backdrop-settings-20]
    public void KillSwitchesOff_FieldsStillStored()
    {
        var plain = new BackdropController(spawner, pen, game, preparer, cache, config, log, run: f => f(), utcNow: () => now);
        var g = new SceneGridViewModel(config, () => Plugin.SaveConfig(config, _ => { }), plain.Post, plain, downloads, files.Contains);
        g.Click(g.Tiles.Single(t => t.Id == a));
        plain.Tick();
        config.NightWhileShown = false;
        config.SunOffWhileShown = false;
        config.LightStrength = 0.2f;
        Plugin.SaveConfig(config, _ => { });
        Assert.Equal((false, false, 0.2f), (config.Backdrops[a].NightWhileShown, config.Backdrops[a].SunOffWhileShown, config.Backdrops[a].LightStrength));
    }

    [Fact] // [EDGE-per-backdrop-settings-23]
    public void Switch_LogsNoKeyOrFullPath()
    {
        Click(a);
        Click(b);
        Assert.DoesNotContain(log.Lines, l => l.Text.Contains(dir.File("")) || l.Text.Contains(a) || l.Text.Contains(b));
    }
    [Fact] // [EDGE-per-backdrop-settings-15]
    public void PendingLightRebuild_Synced_HeldInGPose_NoRebuildAfterSwitch()
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        game.IsGPosing = true;
        Click(b);                                          // synced, lit
        config.LightStrength = 0.4f;                       // an edit on B in GPose: the rebuild is held
        c.RequestLightRebuild();
        Tick(40);
        var (redraws, syncSets) = (pen.Redraws, pen.SyncSets.Count);
        Click(a);                                          // A commits (one registration, one redraw)
        Assert.Equal(redraws + 1, pen.Redraws);
        game.IsGPosing = false;                            // the held rebuild fires and compares against A's committed lights
        Tick(125);
        Assert.Equal(redraws + 1, pen.Redraws);
        Assert.Equal(syncSets + 1, pen.SyncSets.Count);
    }

    [Fact] // [EDGE-half-dome-15]
    public void Switch_UsesEntryBottom()
    {
        config.Backdrops[a].Room.Bottom = DomeBottom.ImageFloor;
        config.Backdrops[b].Mode = DomeMode.Room;
        config.Backdrops[b].Room.Bottom = DomeBottom.Open;
        config.Backdrops[b].Room.HeightOffset = 0f;   // the uncut open bottom at the feet
        Click(a);
        Assert.Equal(BackdropState.Shown, c.State);
        int Vertices() => AvfxChunkReader.Parse(File.ReadAllBytes(pen.Sets[^1].First(kv => kv.Key.EndsWith(".avfx")).Value))
            .Need("Modl").Need("VDrw").Data.Length / 36;
        Assert.NotEqual(2425, Vertices());
        var sets = pen.Sets.Count;
        Click(b);
        Assert.Equal(sets + 1, pen.Sets.Count);   // one build for the switch
        Assert.Equal(2425, Vertices());
        Assert.Equal(game.PlayerPosition!.Value.Y, spawner.Spawns[^1].Pos.Y, 3);
        Assert.Equal(DomeBottom.ImageFloor, config.Backdrops[a].Room.Bottom);
    }
}
