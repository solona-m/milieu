using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Lighting;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>Synced VFX lights through the controller (docs/adr/0011).</summary>
public sealed class BackdropControllerSyncLightTests : IDisposable
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
    private readonly string modDir;
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private LightRig? rig = Rig();
    private bool rigThrows;
    private int rigLoads;
    private BackdropController c = null!;

    private const string RingPath = "chara/accessory/a0028/vfx/eff/va0001.avfx";

    public BackdropControllerSyncLightTests()
    {
        cache = new CacheStore(dir.File("cache"));
        preparer = new KeyedPreparer(cache);
        modDir = dir.File("mods");
        Directory.CreateDirectory(modDir);
        pen.ModDirectory = modDir;
        data.Files[SyncTarget.MadBirdRing.ImcPath] = SyncFixtures.RingImc(3);
        data.Files[SyncTarget.Cushion.ImcPath] = SyncFixtures.CushionImc(2);
        config.ImagePath = dir.File("pano.hdr");
        config.SyncUploadNoticeShown = true;
        config.Delivery = DeliveryMode.MadBirdRing;
        Create();
    }

    private void Create(bool withLoader = true, bool fills = false) =>
        c = new BackdropController(spawner, pen, game, preparer, cache, config, log, run: f => f(), utcNow: () => now,
                                   syncedAvailable: () => true, sync: pen, gameData: data,
                                   loadRig: withLoader ? LoadRig : null, fillsOnOwnEmitters: fills);

    private Task<LightRig?> LoadRig(string path, CancellationToken ct)
    {
        rigLoads++;
        if (rigThrows) throw new IOException("rig store unreadable");
        return Task.FromResult(rig);
    }

    public void Dispose() => dir.Dispose();

    private static LightRig Rig(bool key = true) =>
        new(LightExtractor.Version, key ? new RigLight(0.5f, 0.25f, new Vector3(3, 2, 1)) : null,
            [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
             new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
            new Vector3(10, 10, 10));

    private void ShowSynced()
    {
        c.Show();
        c.Tick();
        c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
    }

    private ChunkNode Dome() => AvfxChunkReader.Parse(File.ReadAllBytes(pen.SyncSets[^1].Paths[RingPath]));

    private void Advance(int ms) { now += TimeSpan.FromMilliseconds(ms); c.Tick(); }

    [Fact] // [AC-synced-lighting-1]
    public void SyncedShow_StagedDomeCarriesShadowKey()
    {
        ShowSynced();
        var root = Dome();
        var e = Assert.Single(root.All("Efct"));
        Assert.Equal(0, e.Need("EfVT").AsInt());
        Assert.Equal(1, e.Need("Data").Need("bSdw").AsInt());
        Assert.Equal(0, Assert.Single(root.All("Emit")).Need("EfNo").AsInt());   // fills off by default: key only
        Assert.Equal(1, c.SyncedLights!.Lights.Count);
        Assert.Contains(log.Lines, l => l.Text.Contains("Synced key light"));
    }

    [Fact] // [AC-synced-lighting-1]
    public void FillsEnabled_AddsParticleLessFillEmitters()
    {
        Create(fills: true);
        ShowSynced();
        var root = Dome();
        var emits = root.All("Emit").ToArray();
        Assert.Equal(3, emits.Length);   // key + 2 fills (default fill count 2)
        Assert.Equal(0, emits[1].Need("PrCn").AsInt());
        Assert.All(root.All("Efct").Skip(1), f => Assert.Equal(0, f.Need("Data").Need("bSdw").AsInt()));
    }

    [Fact] // [AC-synced-lighting-7]
    public void SyncedBuild_RaisesEmitters_EffectorPosIndependentOfPlayerY()
    {
        game.PlayerPosition = new Vector3(0, 0, 0);
        ShowSynced();
        var low = Dome();
        c.Hide();
        game.PlayerPosition = new Vector3(0, 18.25f, 0);
        ShowSynced();
        var high = Dome();
        Assert.Equal(18.25f, high.Need("Emit").Need("Pos").Need("Y").CurveValue());
        Assert.Equal(low.Need("Efct").Need("Data").Need("Pos").Data, high.Need("Efct").Need("Data").Need("Pos").Data);
    }

    [Fact] // [AC-synced-lighting-2]
    public void StrengthChange_RebuildsOnce_After500ms()
    {
        ShowSynced();
        var sets = pen.SyncSets.Count;
        config.LightStrength = 1.5f;
        c.RequestLightRebuild();
        Advance(200);
        config.LightStrength = 2f;
        c.RequestLightRebuild();
        Advance(400);
        Assert.Equal(sets, pen.SyncSets.Count);   // still debouncing
        Advance(200);
        c.Tick();
        Assert.Equal(sets + 1, pen.SyncSets.Count);
        c.Tick();
        Assert.Equal(VfxLightLayout.KeyBrightness * 2, Dome().Need("Efct").Need("Data").Need("Col").Need("Bri").CurveValue(), 3);
    }

    [Fact] // [AC-synced-lighting-2]
    public void FillCountChange_Rebuilds_WhenFillsEnabled()
    {
        Create(fills: true);
        ShowSynced();
        var sets = pen.SyncSets.Count;
        config.FillLightCount = 1;
        c.RequestLightRebuild();
        Advance(600);
        Assert.Equal(sets + 1, pen.SyncSets.Count);
    }

    [Fact] // [AC-synced-lighting-2] [EDGE-synced-lighting-3]
    public void LightingOff_RebuildHasNoEffector()
    {
        ShowSynced();
        config.SceneLighting = false;
        c.RequestLightRebuild();
        Advance(600);
        c.Tick();
        Assert.Empty(Dome().All("Efct"));
        Assert.Empty(c.SyncedLights!.Lights);
    }

    [Fact] // [EDGE-synced-lighting-41]
    public void UnchangedPlan_NoRebuildNoRedraw()
    {
        ShowSynced();
        var (sets, redraws) = (pen.SyncSets.Count, pen.Redraws);
        config.FillLightCount = 3;   // fills are off by default: the planned lights don't change
        c.RequestLightRebuild();
        Advance(600);
        Assert.Equal((sets, redraws), (pen.SyncSets.Count, pen.Redraws));
    }

    [Fact] // [EDGE-synced-lighting-5]
    public void LightRequest_WhileHiddenOrLocal_NoRebuild()
    {
        c.RequestLightRebuild();
        Advance(600);
        Assert.Empty(pen.SyncSets);
        config.Delivery = DeliveryMode.Local;
        c.Show(); c.Tick(); c.Tick();
        config.LightStrength = 2;
        c.RequestLightRebuild();
        Advance(600);
        Assert.Empty(pen.SyncSets);
    }

    [Fact] // [EDGE-synced-lighting-38]
    public void LightRequestInGPose_DeferredUntilExit_ThenDebounced()
    {
        ShowSynced();
        var sets = pen.SyncSets.Count;
        game.IsGPosing = true;
        config.LightStrength = 2;
        c.RequestLightRebuild();
        Advance(600);
        Advance(5000);
        Assert.Equal(sets, pen.SyncSets.Count);   // held in GPose
        game.IsGPosing = false;
        c.Tick();
        Assert.Equal(sets, pen.SyncSets.Count);   // 500 ms after GPose ends
        Advance(600);
        Assert.Equal(sets + 1, pen.SyncSets.Count);
    }

    [Fact] // [EDGE-synced-lighting-38]
    public void LightRequestInGPose_DeferredThenLocal_Dropped()
    {
        ShowSynced();
        game.IsGPosing = true;
        config.LightStrength = 2;
        c.RequestLightRebuild();
        Advance(600);
        c.Hide();
        var sets = pen.SyncSets.Count;
        game.IsGPosing = false;
        c.Tick();
        Advance(600);
        Assert.Equal(sets, pen.SyncSets.Count);
    }

    [Fact] // [EDGE-synced-lighting-49]
    public void NonLightRebuildInGPose_AppliesCurrentLights_DeferredRequestThenNoOps()
    {
        ShowSynced();
        game.IsGPosing = true;
        config.LightStrength = 2;
        c.RequestLightRebuild();
        Advance(600);
        config.Current.YawDegrees = 30;   // a non-light rebuild runs in GPose and plans with the current light settings
        c.ApplyPlacement();
        c.Tick(); c.Tick();
        var sets = pen.SyncSets.Count;
        game.IsGPosing = false;
        c.Tick();
        Advance(600);
        Assert.Equal(sets, pen.SyncSets.Count);   // the deferred light request finds the same lights
    }

    [Fact] // [EDGE-synced-lighting-6]
    public void LightRequest_WhilePending_Supersedes()
    {
        ShowSynced();
        pen.ResolvePlayerWorks = false;
        config.Current.Radius = 9000;
        c.ApplyRebuild();
        c.Tick();
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        config.LightStrength = 2;
        c.RequestLightRebuild();
        Advance(600);
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);   // not Shown: the request is dropped, the pending one stands
    }

    [Fact] // [EDGE-synced-lighting-1]
    public void RigLoadThrows_ShownWithoutLights_Logged()
    {
        rigThrows = true;
        ShowSynced();
        Assert.Empty(Dome().All("Efct"));
        Assert.Contains(log.Lines, l => l.Text.Contains("light rig"));
    }

    [Fact] // [EDGE-synced-lighting-1] [EDGE-synced-lighting-40]
    public void NoLoadRig_NoLights_NullRig_NoLights()
    {
        Create(withLoader: false);
        ShowSynced();
        Assert.Empty(Dome().All("Efct"));
        Assert.Equal(0, rigLoads);
        c.Hide();
        Create();
        rig = null;
        ShowSynced();
        Assert.Empty(Dome().All("Efct"));
        Assert.Equal(0, c.Lit!.VfxLightCount);   // [EDGE-synced-lighting-47]
    }

    [Fact] // [AC-synced-lighting-3]
    public void LocalShow_WithRig_DomeHasNoEffector()
    {
        config.Delivery = DeliveryMode.Local;
        c.Show(); c.Tick(); c.Tick();
        Assert.Equal(0, rigLoads);   // the local job loads no rig for VFX lights
        var avfx = AvfxChunkReader.Parse(File.ReadAllBytes(pen.Sets[^1].First(kv => kv.Key.EndsWith(".avfx")).Value));
        Assert.Empty(avfx.All("Efct"));
    }

    [Fact] // [AC-synced-lighting-4]
    public void Lit_SyncedOnlyForCommittedSyncedRegistration()
    {
        ShowSynced();
        Assert.True(c.Lit!.Synced);
        Assert.Equal(1, c.Lit.VfxLightCount);
        Assert.True(c.IsDrawn);
        c.Hide();
        Assert.Null(c.Lit);
        Assert.False(c.IsDrawn);
        Assert.Null(c.SyncedLights);
        config.Delivery = DeliveryMode.Local;
        c.Show(); c.Tick(); c.Tick();
        Assert.False(c.Lit!.Synced);
        Assert.Equal(0, c.Lit.VfxLightCount);
    }

    [Fact] // [EDGE-synced-lighting-35]
    public void SyncedBlocker_LocalDomeStays_LitNotSynced()
    {
        config.Delivery = DeliveryMode.Local;
        c.Show(); c.Tick(); c.Tick();
        pen.ModDirectory = null;                      // a synced blocker: the local dome stays up
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show(); c.Tick(); c.Tick();
        Assert.NotNull(c.Lit);
        Assert.False(c.Lit!.Synced);
    }

    [Fact] // [EDGE-synced-lighting-46]
    public void FailedRebuild_LitIsDrawnSyncedLightsUnchanged()
    {
        ShowSynced();
        var lights = c.SyncedLights;
        pen.SetSyncedResults.Enqueue(9);   // the rebuild is refused; the shown plan is put back
        config.LightStrength = 2;
        c.RequestLightRebuild();
        Advance(600);
        Assert.True(c.IsDrawn);
        Assert.Same(lights, c.SyncedLights);
    }
}
