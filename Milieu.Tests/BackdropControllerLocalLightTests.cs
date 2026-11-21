using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Gui;
using Milieu.Lighting;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>The "Only me" dome's own VFX lights outside GPose, through the controller (ADR 0011 local amendment).</summary>
public sealed class BackdropControllerLocalLightTests : IDisposable
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
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private LightRig? rig = Rig();
    private bool rigThrows;
    private int localLoads;
    private BackdropController c = null!;

    private const string RingPath = "chara/accessory/a0028/vfx/eff/va0001.avfx";

    public BackdropControllerLocalLightTests()
    {
        cache = new CacheStore(dir.File("cache"));
        preparer = new KeyedPreparer(cache);
        pen.ModDirectory = dir.File("mods");
        Directory.CreateDirectory(pen.ModDirectory);
        data.Files[SyncTarget.MadBirdRing.ImcPath] = SyncFixtures.RingImc(3);
        data.Files[SyncTarget.Cushion.ImcPath] = SyncFixtures.CushionImc(2);
        config.ImagePath = dir.File("pano.hdr");
        config.SyncUploadNoticeShown = true;
        config.Delivery = DeliveryMode.Local;
        spawner.Clock = () => now;   // "early" removal measured on the test clock
        Create();
    }

    public void Dispose() => dir.Dispose();

    private void Create(bool localLoader = true, bool fills = false, bool halfDome = true) =>
        c = new BackdropController(spawner, pen, game, preparer, cache, config, log, run: f => f(), utcNow: () => now,
                                   syncedAvailable: () => true, sync: pen, gameData: data,
                                   loadRig: (_, _) => Task.FromResult(rig), fillsOnOwnEmitters: fills,
                                   loadLocalRig: localLoader ? LoadLocal : null, halfDomeAvailable: halfDome);

    private Task<LightRig?> LoadLocal(string path, CancellationToken ct)
    {
        localLoads++;
        if (rigThrows) throw new IOException("rig store unreadable");
        return Task.FromResult(rig);
    }

    private static LightRig Rig(bool key = true) =>
        new(LightExtractor.Version, key ? new RigLight(0.5f, 0.25f, new Vector3(3, 2, 1)) : null,
            [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
             new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
            new Vector3(10, 10, 10));

    private void Show()
    {
        c.Show();
        c.Tick();
        c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
    }

    private KeyValuePair<string, string> AvfxEntry() => pen.Sets[^1].First(kv => kv.Key.EndsWith(".avfx"));
    private byte[] AvfxBytes() => File.ReadAllBytes(AvfxEntry().Value);
    private int Efcts() => AvfxChunkReader.Parse(AvfxBytes()).All("Efct").Count();
    private int SetCalls => pen.Calls.Count(x => x == "Set");

    private void Advance(int ms) { now += TimeSpan.FromMilliseconds(ms); c.Tick(); }

    /// <summary>Flips GPose and ticks once (the edge), then lets the 500 ms light check run and the swap commit.</summary>
    private void GPose(bool on, bool settle = true)
    {
        game.IsGPosing = on;
        c.Tick();
        if (!settle) return;
        Advance(600);
        c.Tick();
    }

    // The shape the controller builds (docs/adr/0015): its centre height shifts local lights, and a half dome bounds them.
    private DomeShapeChoice Shape => config.Current.Shape(true);

    private DomeBounds? Bounds => Shape.Kind is DomeMeshKind.ImageFloor or DomeMeshKind.OpenBottom
        ? new DomeBounds(config.Current.Radius, config.Current.VerticalScale) : null;

    private VfxLightPlan Expected() =>
        VfxLightPlanner.PlanLocal(rig!, config.FlipHorizontal, config.Current.VerticalScale, config.HorizontalRepeat, config.VerticalRepeat,
                                  config.Current.Radius, Shape.CentreHeight, config.LightStrength, config.FillLightCount,
                                  bounds: Bounds, lowerHalfHidden: Shape.Kind == DomeMeshKind.OpenBottom);

    private byte[] BuildFor(IReadOnlyList<DomeLight>? lights)
    {
        var tex = pen.Sets[^1].First(kv => !kv.Key.EndsWith(".avfx")).Key;
        return DomeAvfxBuilder.Build(new DomeAvfxParams(tex, config.Current.Radius, config.Current.VerticalScale, config.HorizontalRepeat,
            config.VerticalRepeat, config.FlipHorizontal, config.Brightness, Shape.FloorY, Lights: lights, Mesh: Shape.Kind));
    }

    // ---------------------------------------------------------------- AC

    [Fact] // [AC-local-dome-lights-1]
    public void LocalOutsideGPose_DomeCarriesPlannedKey()
    {
        Show();
        var expected = Expected();
        Assert.Single(expected.Lights);
        Assert.Equal(BuildFor(expected.Lights), AvfxBytes());
        Assert.Equal(1, Efcts());
        Assert.Equal(1, c.Lit!.VfxLightCount);
        Assert.False(c.Lit.Synced);
        Assert.Equal(expected.Lights, c.LocalLights!.Lights);
        Assert.Equal(1, localLoads);
        // The log measures from the anchor in the lights' frame: the same numbers as the unshifted plan from the anchor.
        var unshifted = expected.Lights[0].Position + new Vector3(0, config.Current.HeightOffset, 0) - new Vector3(0, LightRigLayout.AnchorHeight, 0);
        var el = MathF.Asin(unshifted.Y / unshifted.Length()) * 180f / MathF.PI;
        Assert.Contains(log.Lines, l => l.Text.Contains("Local key light") && l.Text.Contains($"elevation {el:0}°"));
    }

    [Fact] // [AC-local-dome-lights-2]
    public void EnterGPose_After500ms_RebuildsUnlit_NewSpawnedBeforeOldRemoved()
    {
        Show();
        var sets = SetCalls;
        spawner.Order = [];
        game.IsGPosing = true;
        c.Tick();
        Advance(400);
        Assert.Equal(sets, SetCalls);
        Advance(200);
        Assert.Equal(sets + 1, SetCalls);
        c.Tick();
        Assert.Equal(["Spawn", "Remove"], spawner.Order);
        Assert.Equal(0, Efcts());
        Assert.Equal(0, c.Lit!.VfxLightCount);
        Assert.Equal(BuildFor(null), AvfxBytes());   // [AC-local-dome-lights-5] in GPose: today's bytes
    }

    [Fact] // [AC-local-dome-lights-2] [EDGE-local-dome-lights-17]
    public void NeverBothOnAnyTick()
    {
        var lights = new FakeLightSpawner();
        var s = new SceneLighting(lights, game, () => c.Lit, config, (_, _) => Task.FromResult(rig), new FakeSentinel(), log, () => false,
                                  () => c.EffectiveDelivery, () => true, f => f());
        Show();
        var sawNative = false;
        for (var i = 0; i < 240; i++)
        {
            if (i == 20) game.IsGPosing = true;
            if (i == 140) game.IsGPosing = false;
            now += TimeSpan.FromMilliseconds(16);
            c.Tick();
            s.Tick();
            Assert.False(s.LiveCount > 0 && c.Lit!.VfxLightCount > 0, $"tick {i}: native and dome lights together");
            sawNative |= s.LiveCount > 0;
        }
        Assert.True(sawNative);
        Assert.Equal(1, c.Lit!.VfxLightCount);   // lit again after leaving GPose
        Assert.Equal(0, s.LiveCount);
    }

    [Fact] // [AC-local-dome-lights-3] [EDGE-local-dome-lights-26]
    public void ShowInGPose_Unlit_ThenLitAfterExit()
    {
        game.IsGPosing = true;
        Show();
        Assert.Equal(0, Efcts());
        GPose(false);
        Assert.Equal(1, Efcts());
        Assert.Equal(1, c.Lit!.VfxLightCount);
    }

    [Fact] // [AC-local-dome-lights-3]
    public void LeaveGPose_After500ms_RebuildsLit()
    {
        Show();
        GPose(true);
        Assert.Equal(0, Efcts());
        var sets = SetCalls;
        game.IsGPosing = false;
        c.Tick();
        Advance(400);
        Assert.Equal(sets, SetCalls);
        Advance(200);
        c.Tick();
        Assert.Equal(sets + 1, SetCalls);
        Assert.Equal(1, Efcts());
    }

    [Fact] // [AC-local-dome-lights-4]
    public void StrengthChangeOutsideGPose_RebuildsAfter500ms_OnlyIfDifferent()
    {
        Show();
        var bri = c.LocalLights!.Lights[0].Brightness;
        config.LightStrength = 2f;
        c.RequestLightRebuild();
        Advance(600);
        c.Tick();
        Assert.Equal(2 * bri, c.LocalLights!.Lights[0].Brightness, 3);
        var sets = SetCalls;
        c.RequestLightRebuild();
        Advance(600);
        c.Tick();
        Assert.Equal(sets, SetCalls);
    }

    [Fact] // [AC-local-dome-lights-4] [EDGE-local-dome-lights-22]
    public void UntickOutsideGPose_RebuiltUnlitAfter500ms_ThenRelit()
    {
        Show();
        LightingViewModel.LightChange(config, cfg => cfg.SceneLighting = false, () => { }, c.RequestLightRebuild)();
        Advance(400);
        Assert.Equal(1, Efcts());   // the lights linger up to 500 ms
        Advance(200);
        c.Tick();
        Assert.Equal(0, Efcts());
        LightingViewModel.LightChange(config, cfg => cfg.SceneLighting = true, () => { }, c.RequestLightRebuild)();
        Advance(600);
        c.Tick();
        Assert.Equal(1, Efcts());
    }

    [Theory] // [AC-local-dome-lights-5] [EDGE-local-dome-lights-6] [EDGE-local-dome-lights-14]
    [InlineData("off")]
    [InlineData("zero")]
    [InlineData("norig")]
    [InlineData("noloader")]
    [InlineData("gpose")]
    [InlineData("nothingdrawn")]
    public void LightsNotWanted_AvfxByteIdenticalToNoLightsBuild(string why)
    {
        Arrange(why);
        Show();
        Assert.Equal(BuildFor(null), AvfxBytes());
        Assert.Equal(0, c.Lit!.VfxLightCount);
    }

    private void Arrange(string why)
    {
        switch (why)
        {
            case "off": config.SceneLighting = false; break;
            case "zero": config.LightStrength = 0f; break;
            case "norig": rig = null; break;
            case "noloader": Create(localLoader: false); break;
            case "gpose": game.IsGPosing = true; break;
            case "nothingdrawn": config.VerticalRepeat = 4f; config.HorizontalRepeat = 0.5f; rig = Rig() with { Key = new RigLight(0.9f, 0.99f, new Vector3(3, 2, 1)) }; break;
        }
    }

    [Theory] // [AC-local-dome-lights-5] [EDGE-local-dome-lights-5] [EDGE-local-dome-lights-6]
    [InlineData("off")]
    [InlineData("zero")]
    [InlineData("norig")]
    [InlineData("noloader")]
    public void LightsNotWanted_GPoseEnterExit_NoSetNoSpawn(string why)
    {
        Arrange(why);
        Show();
        var (sets, spawns) = (SetCalls, spawner.Spawns.Count);
        GPose(true);
        GPose(false);
        Assert.Equal((sets, spawns), (SetCalls, spawner.Spawns.Count));
    }

    [Fact] // [AC-local-dome-lights-8]
    public void SyncedShow_LocalLoaderPresent_NotCalled_SyncedAvfxUnchanged()
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        Show();
        Assert.Equal(0, localLoads);
        Assert.Null(c.LocalLights);
        var lit = File.ReadAllBytes(pen.SyncSets[^1].Paths[RingPath]);
        c.Hide();
        Create(localLoader: false);
        Show();
        Assert.Equal(lit, File.ReadAllBytes(pen.SyncSets[^1].Paths[RingPath]));
    }

    // ---------------------------------------------------------------- edges

    [Fact] // [EDGE-local-dome-lights-1]
    public void GPoseChangeWhilePending_WaitsThenComparesCommitted()
    {
        pen.ResolveWorks = false;
        c.Show();
        c.Tick();
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        game.IsGPosing = true;
        c.Tick();
        Advance(600);   // the check waits for the pending registration
        Assert.Equal(1, SetCalls);
        pen.ResolveWorks = true;
        c.Tick();       // committed: planned outside GPose, so lit
        Assert.Equal(1, c.Lit!.VfxLightCount);
        Advance(600);
        c.Tick();
        Assert.Equal(2, SetCalls);
        Assert.Equal(0, c.Lit!.VfxLightCount);
    }

    [Fact] // [EDGE-local-dome-lights-2]
    public void GPoseFlapWithin500ms_NoRebuild()
    {
        Show();
        var sets = SetCalls;
        game.IsGPosing = true;
        c.Tick();
        Advance(200);
        game.IsGPosing = false;
        c.Tick();
        for (var i = 0; i < 10; i++) Advance(200);
        Assert.Equal(sets, SetCalls);
    }

    [Fact] // [EDGE-local-dome-lights-2]
    public void GPoseEnterExitEnter_OneRebuild500msAfterLastEdge()
    {
        Show();
        var sets = SetCalls;
        game.IsGPosing = true; c.Tick();
        Advance(200);
        game.IsGPosing = false; c.Tick();
        Advance(200);
        game.IsGPosing = true; c.Tick();
        Advance(400);
        Assert.Equal(sets, SetCalls);
        Advance(200);
        for (var i = 0; i < 10; i++) Advance(200);
        Assert.Equal(sets + 1, SetCalls);
        Assert.Equal(0, c.Lit!.VfxLightCount);
    }

    [Fact] // [EDGE-local-dome-lights-3]
    public void TerritoryChangeDuringPendingSwitch_RespawnThenLevelCheckRebuilds()
    {
        Show();
        game.IsGPosing = true;
        c.Tick();
        c.OnTerritoryChanged();
        Advance(600);   // not shown: the check is dropped
        Assert.Equal(BackdropState.Suspended, c.State);
        for (var i = 0; i < BackdropController.RespawnDelayFrames; i++) c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(1, c.Lit!.VfxLightCount);   // the shown (lit) file respawned
        Advance(600);
        c.Tick();
        Assert.Equal(0, c.Lit!.VfxLightCount);   // the level check caught up
    }

    [Theory] // [EDGE-local-dome-lights-4]
    [InlineData(true)]
    [InlineData(false)]
    public void HideOrPenumbraGoneWithPendingCheck_NoRebuild_LocalLightsNull(bool hide)
    {
        Show();
        game.IsGPosing = true;
        c.Tick();
        if (hide) c.Hide(); else c.OnPenumbraDisposed();
        var sets = SetCalls;
        for (var i = 0; i < 10; i++) Advance(200);
        Assert.Equal(sets, SetCalls);
        Assert.Null(c.LocalLights);
        Assert.Null(c.Lit);
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-7]
    public void LocalRigLoadThrows_DomeShownWithoutLights_Warning()
    {
        rigThrows = true;
        Show();
        Assert.Equal(0, Efcts());
        Assert.Contains(log.Lines, l => l.Text.Contains("Could not load the light rig"));
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-8]
    public void HugeHeightOffset_DomeShownWithoutLights_GPoseEnterExit_NoSet()
    {
        config.Current.HeightOffset = 2000f;
        Show();
        Assert.Equal(0, Efcts());
        Assert.Single(log.Lines, l => l.Text.Contains("too far from the dome"));
        var sets = SetCalls;
        GPose(true);
        GPose(false);
        Assert.Equal(sets, SetCalls);
    }

    [Theory] // [EDGE-local-dome-lights-9]
    [InlineData(true)]
    [InlineData(false)]
    public void HeightChangeOnDome_SameFloor_RebuildsOnlyWhenLit(bool lit)
    {
        config.Current.GroundProjection = false;
        config.SceneLighting = lit;
        Show();
        var (sets, spawns) = (SetCalls, spawner.Spawns.Count);
        config.Current.HeightOffset = 2.5f;
        c.ApplyHeight();
        Assert.Equal(lit ? sets + 1 : sets, SetCalls);
        if (!lit) Assert.Equal(spawns + 1, spawner.Spawns.Count);   // respawned only
        c.Tick();
        if (lit) Assert.Equal(Expected().Lights, c.LocalLights!.Lights);   // the new height is in the offsets
    }

    [Fact] // [EDGE-local-dome-lights-9]
    public void HeightChangeWhileLitRebuildPending_UsesPendingLights()
    {
        config.Current.GroundProjection = false;
        Show();                                   // shown lit
        game.IsGPosing = true;
        c.Tick();
        pen.ResolveWorks = false;
        Advance(600);                             // an unlit rebuild is pending
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        var sets = SetCalls;
        config.Current.HeightOffset = 2.5f;
        c.ApplyHeight();                          // compares against the pending (unlit) dome: no rebuild
        Assert.Equal(sets, SetCalls);

        // The reverse: shown unlit, a lit rebuild pending, then a height change rebuilds.
        pen.ResolveWorks = true;
        c.Tick();
        Assert.Equal(0, c.Lit!.VfxLightCount);
        game.IsGPosing = false;
        c.Tick();
        pen.ResolveWorks = false;
        Advance(600);
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        sets = SetCalls;
        config.Current.HeightOffset = 3f;
        c.ApplyHeight();
        Assert.Equal(sets + 1, SetCalls);
    }

    [Fact] // [EDGE-local-dome-lights-10]
    public void YawChangeOrRecenterOnLitDome_RespawnsSameFile_NoSet()
    {
        Show();
        var (sets, path) = (SetCalls, spawner.Spawns[^1].Path);
        config.Current.YawDegrees = 90;
        c.ApplyPlacement();
        c.Recenter();
        Assert.Equal(sets, SetCalls);
        Assert.All(spawner.Spawns.Skip(1), s => Assert.Equal(path, s.Path));
        Assert.Equal(3, spawner.Spawns.Count);
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-11]
    public void GameEndsLitDomeEarly_LatchesAndRebuildsUnlit_WarningLogged()
    {
        Show();
        spawner.GameFrees(spawner.Spawns[^1].Handle);
        c.Tick();
        c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(0, Efcts());
        Assert.False(c.LocalLightsActive);
        Assert.Contains(log.Lines, l => l.Text.Contains("ended the backdrop with its own lights"));
    }

    [Fact] // [EDGE-local-dome-lights-11]
    public void GameEndsUnlitDomeEarly_CountingUnchanged()
    {
        config.SceneLighting = false;
        Show();
        var sets = SetCalls;
        spawner.GameFrees(spawner.Spawns[^1].Handle);
        c.Tick();
        Assert.Equal(BackdropState.Suspended, c.State);
        Assert.Equal(sets, SetCalls);
        Assert.True(c.LocalLightsActive);
    }

    [Fact] // [EDGE-local-dome-lights-12]
    public void DeliverySwitchWhileLit_LocalLightsCleared_SyncedFromSyncedLoaderOnly()
    {
        Show();
        Assert.Equal(1, localLoads);
        config.Delivery = DeliveryMode.MadBirdRing;
        c.ApplyDelivery();
        c.Tick();
        c.Tick();
        Assert.Null(c.LocalLights);
        Assert.Equal(1, localLoads);
        Assert.True(c.Lit!.Synced);
    }

    [Fact] // [EDGE-local-dome-lights-13]
    public void LocalDomeUnderSyncedConfig_NoGPoseSwap()
    {
        Show();
        config.Delivery = DeliveryMode.MadBirdRing;   // saved but not applied: the local dome stays up
        var sets = SetCalls;
        GPose(true);
        Assert.Equal(sets, SetCalls);
    }

    [Fact] // [EDGE-local-dome-lights-15]
    public void NoLoader_LocalLightsActiveFalse()
    {
        Create(localLoader: false);
        Assert.False(c.LocalLightsActive);
        Show();
        Assert.Equal(0, Efcts());
    }

    [Fact] // [EDGE-local-dome-lights-18]
    public void LocalSwap_IsDrawnEveryTick_NoHiddenEvent()
    {
        Show();
        var hidden = 0;
        c.Hidden += () => hidden++;
        game.IsGPosing = true;
        for (var i = 0; i < 60; i++)
        {
            now += TimeSpan.FromMilliseconds(16);
            c.Tick();
            Assert.True(c.IsDrawn);
            Assert.False(c.Lit!.Synced);
        }
        Assert.Equal(0, hidden);
        Assert.Equal(0, c.Lit!.VfxLightCount);
    }

    [Fact] // [EDGE-local-dome-lights-19]
    public void EstimatedRig_LightsMatchPlanLocal_EstimatedFlag()
    {
        rig = SceneLightingTests.RigE;
        Show();
        Assert.True(c.LocalLights!.Estimated);
        Assert.Equal(Expected().Lights, c.LocalLights.Lights);
    }

    [Fact] // [EDGE-local-dome-lights-19]
    public void NoKeyRig_StandInKeyShipped()
    {
        rig = Rig(key: false);
        Show();
        Assert.True(c.LocalLights!.HasKey);
        Assert.True(Assert.Single(c.LocalLights.Lights).Shadows);
    }

    [Fact] // [EDGE-local-dome-lights-20]
    public void LightChangeInGPose_NoReload()
    {
        game.IsGPosing = true;
        Show();
        var sets = SetCalls;
        config.LightStrength = 2f;
        c.RequestLightRebuild();
        Advance(600);
        c.Tick();
        Assert.Equal(sets, SetCalls);
    }

    [Fact] // [EDGE-local-dome-lights-21]
    public void FillCountChangeWithFillsOff_NoRebuild()
    {
        Show();
        var sets = SetCalls;
        config.FillLightCount = 3;
        c.RequestLightRebuild();
        Advance(600);
        c.Tick();
        Assert.Equal(sets, SetCalls);
    }

    [Fact] // [EDGE-local-dome-lights-23]
    public void SwapsKeepOnlyShownAndPendingAvfxInCache()
    {
        Show();
        GPose(true);
        GPose(false);
        GPose(true);
        Assert.Single(Directory.GetFiles(cache.Root, "*.avfx", SearchOption.AllDirectories));
    }

    [Fact] // [EDGE-local-dome-lights-24]
    public void SunOffUnticked_DomeStillLit()
    {
        config.SunOffWhileShown = false;
        Show();
        Assert.Equal(1, Efcts());
    }

    [Fact] // [EDGE-local-dome-lights-25]
    public void PlayerMoves_NoRebuildNoRespawn()
    {
        Show();
        var (sets, spawns) = (SetCalls, spawner.Spawns.Count);
        game.PlayerPosition = new Vector3(50, 20, 80);
        for (var i = 0; i < 20; i++) Advance(200);
        Assert.Equal((sets, spawns), (SetCalls, spawner.Spawns.Count));
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-27]
    public void SwapSetRefusedInGPose_OneAttemptPerEdge()
    {
        Show();
        pen.SetSucceeds = false;
        var sets = SetCalls;
        game.IsGPosing = true;
        c.Tick();
        for (var i = 0; i < 100; i++) Advance(200);   // 20 s
        Assert.Equal(sets + 1, SetCalls);
        Assert.True(c.IsDrawn);                       // the old dome stays drawn
        game.IsGPosing = false; c.Tick();             // an edge: the shown (lit) dome already fits, nothing to do
        game.IsGPosing = true; c.Tick();              // the next edge allows one more attempt
        for (var i = 0; i < 100; i++) Advance(200);
        Assert.Equal(sets + 2, SetCalls);
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-27]
    public void SwapResolveTimeout_OneAttemptPerEdge()
    {
        Show();
        var sets = SetCalls;
        pen.ResolveWorks = false;
        game.IsGPosing = true;
        c.Tick();
        for (var i = 0; i < 200; i++) Advance(100);   // 20 s: one attempt, then the timeout (120 ticks)
        Assert.Equal(sets + 2, SetCalls);   // the one attempt, plus putting the shown set back after the timeout
        for (var i = 0; i < 200; i++) Advance(100);   // no further attempt for this GPose state
        Assert.Equal(sets + 2, SetCalls);
        Assert.Contains(log.Lines, l => l.Text.Contains("did not resolve"));
        Assert.True(c.IsDrawn);
    }

    [Fact] // [EDGE-local-dome-lights-28]
    public void ShownWithLightingOff_ThenTicked_LightsWithoutReShow()
    {
        config.SceneLighting = false;
        Show();
        Assert.Equal(0, Efcts());
        var prepares = preparer.Calls;
        LightingViewModel.LightChange(config, cfg => cfg.SceneLighting = true, () => { }, c.RequestLightRebuild)();
        Advance(600);
        c.Tick();
        Assert.Equal(prepares, preparer.Calls);
        Assert.Equal(1, Efcts());
    }

    [Fact] // [EDGE-local-dome-lights-28]
    public void LocalJob_LoadsRigWhenLoaderPresent_EvenWithLightingOff()
    {
        config.SceneLighting = false;
        Show();
        Assert.Equal(1, localLoads);
    }

    [Fact] // [EDGE-local-dome-lights-29]
    public void LatchClearedOnLightingOffOn_LightsReturn()
    {
        Show();
        spawner.GameFrees(spawner.Spawns[^1].Handle);
        c.Tick();
        c.Tick();
        Assert.False(c.LocalLightsActive);
        config.SceneLighting = false;
        c.Tick();
        config.SceneLighting = true;
        c.Tick();
        Assert.True(c.LocalLightsActive);
        c.RequestLightRebuild();
        Advance(600);
        c.Tick();
        Assert.Equal(1, Efcts());
    }

    [Fact] // [EDGE-local-dome-lights-29]
    public void Latched_NoGPoseSwap_LocalLightsActiveFalse()
    {
        Show();
        spawner.GameFrees(spawner.Spawns[^1].Handle);
        c.Tick();
        c.Tick();
        var sets = SetCalls;
        GPose(true);
        GPose(false);
        Assert.Equal(sets, SetCalls);
        Assert.False(c.LocalLightsActive);
    }
    [Fact] // [AC-local-dome-lights-4]
    public void ToggleAndFillChanges_GoThroughLightChange()
    {
        Create(fills: true);
        Show();
        Assert.Equal(3, Efcts());   // key + 2 fills (default fill count 2)
        var sets = SetCalls;
        LightingViewModel.LightChange(config, cfg => cfg.FillLightCount = 1, () => { }, c.RequestLightRebuild)();
        Advance(600);
        c.Tick();
        Assert.Equal(sets + 1, SetCalls);
        Assert.Equal(2, Efcts());
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-27]
    public void SwapBlockedByPenumbra_OneAttemptPerEdge()
    {
        Show();
        pen.State = PenumbraState.ModsDisabled;
        var (sets, refreshes) = (SetCalls, pen.Refreshes);
        game.IsGPosing = true;
        c.Tick();
        for (var i = 0; i < 100; i++) Advance(200);   // 20 s
        Assert.Equal(sets, SetCalls);
        Assert.InRange(pen.Refreshes - refreshes, 1, 3);   // one attempt (plus at most two periodic refreshes)
        Assert.True(c.IsDrawn);
    }

    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-27]
    public void SwapSpawnFails_OneAttemptPerEdge()
    {
        Show();
        spawner.ReturnNull = true;
        var sets = SetCalls;
        game.IsGPosing = true;
        c.Tick();
        for (var i = 0; i < 100; i++) Advance(200);
        Assert.Equal(sets + 1, SetCalls);
        Assert.Equal(1, spawner.Calls.Count(x => x == "SpawnRefused"));
        Assert.True(c.IsDrawn);
    }

    [Fact] // [EDGE-local-dome-lights-11]
    public void GameEndsLitDomeEarly_ThenUnlitTwice_CountingHides()
    {
        spawner.Clock = () => now;
        Show();
        spawner.GameFrees(spawner.Spawns[^1].Handle);   // lit, early: latched, rebuilt unlit
        c.Tick();
        c.Tick();
        Assert.Equal(0, Efcts());
        spawner.GameFrees(spawner.Spawns[^1].Handle);   // unlit, early: the second early removal
        c.Tick();
        for (var i = 0; i < BackdropController.RespawnDelayFrames; i++) c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
        spawner.GameFrees(spawner.Spawns[^1].Handle);   // the third: the guard hides it
        c.Tick();
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(MessageKey.GameEndedVfx, c.Status.Message.Key);
        Assert.Null(c.LocalLights);
    }
    [Fact] // [AC-local-dome-lights-6] [EDGE-local-dome-lights-27]
    public void SwapWriteFails_OneAttemptPerEdge()
    {
        Show();
        // A folder where the unlit swap's .avfx goes: File.Exists is false and the atomic move throws.
        Directory.CreateDirectory(cache.AvfxDiskPath(CacheStore.AvfxKey(BuildFor(null))));
        var sets = SetCalls;
        game.IsGPosing = true;
        c.Tick();
        Advance(600);                                      // the one attempt: the write fails
        Assert.Equal(MessageKey.WriteFailed, c.Status.Message.Key);
        c.ReportStatus(StatusMessage.None);                // any further attempt would set it again
        for (var i = 0; i < 100; i++) Advance(200);
        Assert.Equal(MessageKey.None, c.Status.Message.Key);
        Assert.Equal(sets, SetCalls);
        Assert.True(c.IsDrawn);
        Assert.Equal(1, c.Lit!.VfxLightCount);
    }
    [Fact] // [EDGE-local-dome-lights-11] [EDGE-local-dome-lights-13]
    public void GameEndsLitDomeEarly_UnderSyncedConfig_NoSyncedRegistration()
    {
        config.SyncUploadNoticeShown = false;
        Show();
        config.Delivery = DeliveryMode.MadBirdRing;   // saved but not applied: the local dome stays up
        spawner.GameFrees(spawner.Spawns[^1].Handle);
        c.Tick();
        c.Tick();
        Assert.Empty(pen.SyncSets);
        Assert.True(c.LocalLightsActive);             // not latched: the plain counting handled it
        Assert.Equal(BackdropState.Suspended, c.State);
    }
    // ---------------------------------------------------------------- per-backdrop settings (docs/adr/0014)

    [Fact] // [EDGE-per-backdrop-settings-24] [AC-per-backdrop-settings-5]
    public void BackdropSwitch_UnlitAToLitB_RegistersOnceWithBsLights()
    {
        config.UserImages = [config.ImagePath!, dir.File("b.hdr")];
        config.SceneLighting = false;
        config.StoreSelected();
        Show();
        Assert.Equal(0, Efcts());
        var sets = SetCalls;
        BackdropSettingsTests.SwitchTo(config, dir.File("b.hdr"), e => { e.SceneLighting = true; e.LightStrength = 1.5f; });
        Show();
        Assert.Equal(sets + 1, SetCalls);
        Assert.Equal(Expected().Lights, c.LocalLights!.Lights);
        for (var i = 0; i < 10; i++) Advance(200);   // 2 s
        Assert.Equal(sets + 1, SetCalls);
    }

    [Fact] // [AC-per-backdrop-settings-5] [EDGE-per-backdrop-settings-24] [EDGE-per-backdrop-settings-15]
    public void BackdropSwitch_OneRegistrationWithNewBackdropsLights_NoRebuild()
    {
        config.UserImages = [config.ImagePath!, dir.File("b.hdr")];
        config.StoreSelected();
        Show();
        config.LightStrength = 2f;
        c.RequestLightRebuild();                       // queued for A
        var sets = SetCalls;
        BackdropSettingsTests.SwitchTo(config, dir.File("b.hdr"), e => e.LightStrength = 0.5f);
        Show();
        for (var i = 0; i < 10; i++) Advance(200);
        Assert.Equal(sets + 1, SetCalls);              // the queued check compares against B's committed lights
        Assert.Equal(Expected().Lights, c.LocalLights!.Lights);
    }

    [Fact] // [AC-per-backdrop-settings-8] [EDGE-per-backdrop-settings-21]
    public void BackdropSwitch_DoesNotClearLocalLightsLatch_UserToggleStillDoes()
    {
        config.UserImages = [config.ImagePath!, dir.File("b.hdr")];
        Show();
        spawner.GameFrees(spawner.Spawns[^1].Handle);
        c.Tick(); c.Tick();
        Assert.False(c.LocalLightsActive);
        config.SceneLighting = false;
        config.StoreSelected();
        c.Tick();
        BackdropSettingsTests.SwitchTo(config, dir.File("b.hdr"), e => e.SceneLighting = true);   // off→on by a switch
        c.Tick();
        Assert.False(c.LocalLightsActive);
        config.SceneLighting = false; c.Tick();
        config.SceneLighting = true; c.Tick();
        Assert.True(c.LocalLightsActive);
    }

    // ---------------------------------------------------------------- half dome (docs/adr/0015), through the controller

    private static readonly Vector3 LightAnchor = new(0, LightRigLayout.AnchorHeight, 0);

    /// <summary>Asserts each light (in the feet frame) lies on its ray within 0.9 × the R-10 half dome's wall.</summary>
    private static void InsideWall(IReadOnlyList<DomeLight> lights, float centre)
    {
        foreach (var l in lights)
        {
            var rel = l.Position + new Vector3(0, centre, 0) - LightAnchor;
            var exit = DomeShape.ExitDistance(LightAnchor, Vector3.Normalize(rel), 10f, 1f);
            Assert.True(rel.Length() <= 0.9f * exit + 1e-3f, $"light at {rel.Length()} beyond 0.9 × wall {exit}");
        }
    }

    [Theory] // [AC-half-dome-5] [EDGE-half-dome-14]
    [InlineData(false)]
    [InlineData(true)]
    public void RoomHalfDome_LocalLightsBoundedAndShiftedByCentre(bool open)
    {
        config.Mode = DomeMode.Room;
        config.Room.Bottom = open ? DomeBottom.Open : DomeBottom.ImageFloor;
        Show();
        var lights = c.LocalLights!.Lights;
        Assert.Single(lights);
        Assert.Equal(Expected().Lights, lights);
        var centre = open ? 0f : 1.6f;
        InsideWall(lights, centre);
        // The key (45° up) sits closer than the old sphere rule (0.9 × R = 9): the half dome's wall is nearer.
        var keyDistance = (lights[0].Position + new Vector3(0, centre, 0) - LightAnchor).Length();
        Assert.InRange(keyDistance, 8f, 8.6f);
        Assert.Equal(open, c.Lit!.View.LowerHalfHidden);
    }

    [Fact] // [EDGE-half-dome-18]
    public void RoomOpenBottom_KeyBelowHorizonNotLit()
    {
        rig = new LightRig(LightExtractor.Version, new RigLight(0.5f, 0.75f, new Vector3(3, 2, 1)),
                           [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f))], new Vector3(10, 10, 10));
        config.Mode = DomeMode.Room;
        config.Room.Bottom = DomeBottom.Open;
        Show();
        Assert.False(c.LocalLights!.HasKey);   // the sun is in the undrawn lower half
        config.Room.Bottom = DomeBottom.ImageFloor;
        c.ApplyRebuild();
        c.Tick();
        Assert.True(c.LocalLights!.HasKey);    // drawn on the image floor
    }

    [Theory] // [AC-half-dome-5] [AC-half-dome-6] [EDGE-half-dome-14]
    [InlineData(false)]
    [InlineData(true)]
    public void RoomHalfDome_SyncedLightsBounded(bool open)
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        config.Mode = DomeMode.Room;
        config.Room.Bottom = open ? DomeBottom.Open : DomeBottom.ImageFloor;
        Show();
        var expected = VfxLightPlanner.Plan(rig!, new DomeView(0, config.FlipHorizontal, 1, config.HorizontalRepeat, config.VerticalRepeat, open),
                                            10f, config.LightStrength, config.FillLightCount, false, bounds: new DomeBounds(10f, 1f));
        Assert.Equal(expected.Lights, c.SyncedLights!.Lights);
        InsideWall(c.SyncedLights.Lights, 0f);   // synced lights are already in the feet frame (emitter-relative)
        Assert.Equal(open, c.Lit!.View.LowerHalfHidden);
    }

    [Fact] // [AC-half-dome-4] [EDGE-half-dome-19]
    public void KillSwitchOff_SyncedBuildsLegacyFloor()
    {
        Create(halfDome: false);
        config.Delivery = DeliveryMode.MadBirdRing;
        config.Mode = DomeMode.Room;
        config.Room.Bottom = DomeBottom.Open;
        Show();
        var vdrw = AvfxChunkReader.Parse(File.ReadAllBytes(pen.SyncSets[^1].Paths[RingPath])).Need("Modl").Need("VDrw").Data;
        Assert.Equal(SphereMesh.BuildLegacyFloor(0.16f).VertexBytes(), vdrw);
        Assert.False(c.Lit!.View.LowerHalfHidden);
    }
}
