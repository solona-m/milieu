using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Imaging;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>A preparer whose texture key, warnings and gating each test controls.</summary>
public sealed class KeyedPreparer(CacheStore cache) : IBackdropPreparer
{
    public int Calls;
    public string Key = "00112233445566ff";
    public IReadOnlyList<StatusMessage> Warnings = [];
    public readonly Queue<TaskCompletionSource<PreparedTexture>> Gates = new();
    public readonly List<CancellationToken> Tokens = [];

    public Task<PreparedTexture> PrepareTextureAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct)
    {
        Calls++;
        Tokens.Add(ct);
        if (Gates.Count > 0) return Gates.Dequeue().Task;
        return Task.FromResult(Make(Key));
    }

    public PreparedTexture Make(string key, int length = 64)
    {
        Directory.CreateDirectory(cache.Root);
        var disk = cache.TextureDiskPath(key);
        File.WriteAllBytes(disk, Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray());
        return new PreparedTexture(key, disk, CacheStore.TextureGamePath(key), 1024, 512, ImageKind.Jpeg, Warnings);
    }
}

public sealed class BackdropControllerSyncTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly List<string> order = [];
    private readonly FakeSpawner spawner = new();
    private readonly FakePenumbra pen = new();
    private readonly FakeGame game = new();
    private readonly FakeLog log = new();
    private readonly FakeGameData data = new();
    private readonly CacheStore cache;
    private readonly KeyedPreparer preparer;
    private readonly Configuration config = new();
    private readonly string modDir;
    private readonly string userImage;
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private bool available = true;
    private int saves;
    private Func<string, SyncStaging>? stagingSeam;
    private readonly BackdropController c;

    public BackdropControllerSyncTests()
    {
        cache = new CacheStore(dir.File("cache"));
        preparer = new KeyedPreparer(cache);
        modDir = dir.File("mods");
        Directory.CreateDirectory(modDir);
        pen.ModDirectory = modDir;
        pen.Order = order;
        spawner.Order = order;
        spawner.Clock = () => now;
        data.Files[SyncTarget.MadBirdRing.ImcPath] = SyncFixtures.RingImc(3);
        data.Files[SyncTarget.Cushion.ImcPath] = SyncFixtures.CushionImc(2);
        userImage = dir.File("pano.jpg");
        config.ImagePath = userImage;
        config.SyncUploadNoticeShown = true;   // most tests are past the one-time notice
        c = new BackdropController(spawner, pen, game, preparer, cache, config, log, run: f => f(), utcNow: () => now,
                                   save: () => saves++, syncedAvailable: () => available, sync: pen, gameData: data,
                                   stagingFor: d => stagingSeam?.Invoke(d) ?? new SyncStaging(d));
    }

    public void Dispose() => dir.Dispose();

    private string Staging => Path.Combine(modDir, SyncStaging.FolderName);

    private string[] StagedFiles() => Directory.Exists(Staging) ? Directory.GetFiles(Staging).Select(f => Path.GetFileName(f)).Order().ToArray() : [];

    private void Ticks(int n) { for (var i = 0; i < n; i++) c.Tick(); }

    /// <summary>Gated jobs continue on the thread pool once released: wait for every synced job before ticking.</summary>
    private void WaitJobs(int stillGated = 0) =>
        Assert.True(SpinWait.SpinUntil(() => c.StagingJobTasks.Count(t => !t.IsCompleted) <= stillGated, 5000), "synced jobs finished");

    private void ShowSynced(DeliveryMode mode = DeliveryMode.MadBirdRing)
    {
        config.Delivery = mode;
        c.Show();
        c.Tick();   // job done → staged, registered
        c.Tick();   // resolved → committed
        Assert.Equal(BackdropState.Shown, c.State);
    }

    private void ShowLocal()
    {
        config.Delivery = DeliveryMode.Local;
        c.Show();
        c.Tick();
        c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.NotEmpty(spawner.Spawns);
    }

    private static ChunkNode Avfx(string path) => AvfxChunkReader.Parse(File.ReadAllBytes(path));

    // ---------------------------------------------------------------- AC

    [Fact] // [AC-synced-delivery-2]
    public void LocalMode_UsesAllCollectionsModAndSpawner_NeverSync()
    {
        ShowLocal();
        Assert.Single(pen.Sets);
        Assert.Single(spawner.Spawns);
        Assert.DoesNotContain(pen.SyncCalls, x => x is "SetSynced" or "ClearSynced" or "Redraw" or "Meta");
        Assert.False(Directory.Exists(Staging));
    }

    [Fact] // [AC-synced-delivery-3]
    public void Cushion_Show_StagesRegistersInPlayerCollectionRedrawsNoSpawn()
    {
        ShowSynced(DeliveryMode.Cushion);
        var staged = StagedFiles();
        Assert.Contains("sky_00112233445566ff.atex", staged);
        var dome = Assert.Single(staged, f => f.StartsWith("dome_"));
        var set = Assert.Single(pen.SyncSets);
        Assert.Equal(pen.Player!.Value.Id, set.Collection);
        Assert.Equal(Path.Combine(Staging, dome), set.Paths["chara/monster/m8058/obj/body/b0001/vfx/eff/vm0200.avfx"]);
        Assert.Equal(Path.Combine(Staging, "sky_00112233445566ff.atex"), set.Paths["vfx/milieu/00112233445566ff/sky.atex"]);
        Assert.Equal(1, pen.Redraws);
        Assert.Empty(spawner.Spawns);
        Assert.Equal(MessageKey.SyncShownCushion, c.Status.Message.Key);
        var itpr = Avfx(Path.Combine(Staging, dome)).Need("Emit").Need("ItPr");
        Assert.Equal((1, 0, 0, 1), (itpr.Need("PICd").AsInt(), itpr.Need("ICbS").AsInt(), itpr.Need("ICbR").AsInt(), itpr.Need("ICbP").AsInt()));
        Assert.Empty(pen.Sets);   // no all-collections mod
    }

    [Fact] // [AC-synced-delivery-4]
    public void Ring_Show_RegistersA0028RightRing()
    {
        ShowSynced(DeliveryMode.MadBirdRing);
        Assert.Contains("chara/accessory/a0028/vfx/eff/va0200.avfx", pen.SyncSets.Single().Paths.Keys);
        Assert.Equal(MessageKey.SyncShownRing, c.Status.Message.Key);
    }

    [Fact] // [AC-synced-delivery-6]
    public void SyncedShown_YawChange_RebuildsBakedOnceRedrawsOnce()
    {
        ShowSynced();
        config.Current.YawDegrees = 90;
        c.ApplyPlacement();
        c.Tick();
        Assert.Equal(2, pen.SyncSets.Count);
        Assert.Equal(2, pen.Redraws);
        var dome = pen.SyncSets[^1].Paths["chara/accessory/a0028/vfx/eff/va0200.avfx"];
        Assert.Equal(MathF.PI / 2, Avfx(dome).Need("Ptcl").Need("Rot").Need("Y").CurveValue(), 4);
        Assert.Empty(spawner.Spawns);
    }

    [Fact] // [AC-synced-delivery-7]
    public void SyncedShown_Hide_ClearsRedrawsCleans()
    {
        ShowSynced();
        c.Hide();
        Assert.Equal(pen.Player!.Value.Id, pen.SyncClears.Single());
        Assert.Equal(2, pen.Redraws);
        Assert.DoesNotContain(StagedFiles(), f => f.StartsWith("sky_") || f.StartsWith("dome_"));
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [AC-synced-delivery-7] [EDGE-synced-delivery-16]
    public void Startup_ClearsSyncedEverywhereAndStaging()
    {
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, "sky_x.atex"), [1]);
        File.WriteAllBytes(Path.Combine(Staging, "notes.txt"), [1]);
        c.OnPenumbraInitialized();
        Assert.Single(pen.ClearEverywhereCalls);
        Assert.Equal(["notes.txt"], StagedFiles());
    }

    [Fact] // [AC-synced-delivery-8]
    public void SwitchLocalToRing_WhileShown_RemovesStaticAndAllCollectionsModFirst()
    {
        ShowLocal();
        order.Clear();
        config.Delivery = DeliveryMode.MadBirdRing;
        c.ApplyDelivery();
        c.Tick();
        Assert.True(order.IndexOf("Remove") >= 0 && order.IndexOf("Remove") < order.IndexOf("Clear"));
        Assert.True(order.IndexOf("Clear") < order.IndexOf("SetSynced"));
    }

    [Fact] // [AC-synced-delivery-8]
    public void SwitchRingToLocal_WhileShown_ClearsSyncedAndRedraws()
    {
        ShowSynced();
        order.Clear();
        config.Delivery = DeliveryMode.Local;
        c.ApplyDelivery();
        Ticks(2);
        Assert.Equal(["ClearSynced", "Redraw", "Set", "Spawn"], order.ToArray());
    }

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-32]
    public void SwitchToSynced_UserImage_NoticeBeforeUpload()
    {
        config.SyncUploadNoticeShown = false;
        ShowLocal();
        var calls = preparer.Calls;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.ApplyDelivery();
        Assert.Single(spawner.Removes);
        Assert.Equal(1, pen.Clears);
        Assert.Equal(calls, preparer.Calls);
        Assert.Empty(pen.SyncSets);
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        Assert.Contains("pano.jpg", c.Status.Message.Text);
        Assert.True(config.SyncUploadNoticeShown);
        Assert.Equal(1, saves);
        c.Show();
        c.Tick();
        Assert.Single(pen.SyncSets);
    }

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-32] [EDGE-synced-delivery-34]
    public void SwitchWhileHidden_ThenUserImageClick_NoticeFirst()
    {
        config.SyncUploadNoticeShown = false;
        config.Delivery = DeliveryMode.Cushion;
        c.ApplyDelivery();
        Assert.False(config.SyncUploadNoticeShown);
        Assert.Empty(pen.SyncCalls);
        c.Show();
        Assert.Equal(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        Assert.Empty(pen.SyncSets);
        Assert.Equal(0, preparer.Calls);
        c.Show();
        c.Tick();
        Assert.Single(pen.SyncSets);
    }

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-34]
    public void BundledShown_Switch_ThenUserImageClick_NoticeFirst()
    {
        config.SyncUploadNoticeShown = false;
        config.ImagePath = "bundled:moon";
        ShowLocal();
        config.Delivery = DeliveryMode.MadBirdRing;
        c.ApplyDelivery();
        Ticks(2);
        Assert.Single(pen.SyncSets);                       // the bundled scene registered synced at once
        Assert.False(config.SyncUploadNoticeShown);
        Assert.NotEqual(MessageKey.SyncUploadNotice, c.Status.Message.Key);

        config.ImagePath = userImage;
        c.Show();
        Assert.Equal(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        c.Tick();
        Assert.Single(pen.SyncSets);
        preparer.Key = "aaaaaaaaaaaaaaaa";
        c.Show();
        c.Tick();
        Assert.Equal(2, pen.SyncSets.Count);
    }

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-32]
    public void ReconvertWhileShown_SyncedUserImage_NoticeFirst()
    {
        config.SyncUploadNoticeShown = false;
        ShowLocal();
        var calls = preparer.Calls;
        config.Delivery = DeliveryMode.MadBirdRing;     // no ApplyDelivery
        c.RequestReconvert();
        now += TimeSpan.FromMilliseconds(600);
        c.Tick();
        Assert.Single(spawner.Removes);
        Assert.Equal(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        Assert.Equal(calls, preparer.Calls);
        Assert.Empty(pen.SyncSets);
    }

    [Fact] // [AC-synced-delivery-10]
    public void SwitchToSynced_BundledScene_Immediate()
    {
        config.SyncUploadNoticeShown = false;
        config.ImagePath = "bundled:moon";
        ShowLocal();
        var calls = preparer.Calls;
        config.Delivery = DeliveryMode.Cushion;
        c.ApplyDelivery();
        Assert.Equal(calls + 1, preparer.Calls);
        Assert.False(config.SyncUploadNoticeShown);
        Assert.Equal(0, saves);
    }

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-28]
    public void SharedCollection_Warning()
    {
        pen.Player = new PlayerCollectionInfo(Guid.NewGuid(), false);
        ShowSynced();
        Assert.Contains(c.Status.Warnings, w => w.Key == MessageKey.SyncSharedCollection);
    }

    // ---------------------------------------------------------------- EDGE

    [Fact] // [EDGE-synced-delivery-1]
    public void NoModDirectory_BlocksShow()
    {
        pen.ModDirectory = null;
        config.Delivery = DeliveryMode.MadBirdRing;
        Assert.Equal(MessageKey.SyncNoModDirectory, c.Blocker());
        c.Show();
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(0, preparer.Calls);
        Assert.Empty(pen.SyncSets);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact] // [EDGE-synced-delivery-2]
    public void StagingFailure_WriteFailed_NothingRegistered()
    {
        stagingSeam = d => new SyncStaging(d) { OpenSource = _ => throw new IOException("disk full at " + d) };
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        Assert.Equal(MessageKey.WriteFailed, c.Status.Message.Key);
        Assert.Empty(pen.SyncSets);
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Empty(Directory.EnumerateFiles(Staging, "*.tmp"));
    }

    [Fact] // [EDGE-synced-delivery-3]
    public void NoPlayerCollection_Message_NothingRegistered()
    {
        pen.Player = null;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        Assert.Equal(MessageKey.SyncNoPlayerCollection, c.Status.Message.Key);
        Assert.Empty(pen.SyncSets);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-synced-delivery-4]
    public void AddRefused_SyncRefusedWithCode_FallsBack()
    {
        pen.SetSyncedResult = 11;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        Assert.Equal(MessageKey.SyncRefused, c.Status.Message.Key);
        Assert.Contains("11", c.Status.Message.Text);
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Empty(pen.Synced);
    }

    [Fact] // [EDGE-synced-delivery-5]
    public void ResolveTimeout_ClearsAndRestoresPrevious()
    {
        // No previous registration: cleared, timeout message, hidden.
        pen.ResolvePlayerWorks = false;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        Ticks(BackdropController.RedirectTimeoutTicks);
        Assert.Equal(MessageKey.SyncRedirectTimeout, c.Status.Message.Key);
        Assert.Single(pen.SyncClears);
        Assert.Equal(BackdropState.Hidden, c.State);

        // With a shown registration: the previous one is put back and the state stays Shown.
        pen.ResolvePlayerWorks = true;
        c.Show();
        Ticks(2);
        var previous = pen.SyncSets[^1].Paths;
        pen.ResolvePlayerWorks = false;
        config.Current.Radius = 9000;
        c.ApplyRebuild();
        Ticks(BackdropController.RedirectTimeoutTicks + 1);
        Assert.Equal(previous, pen.SyncSets[^1].Paths);
        Assert.Equal(BackdropState.Shown, c.State);
    }

    [Fact] // [EDGE-synced-delivery-8]
    public void VanillaImcMissing_GameDataUnreadable()
    {
        data.Files.Remove(SyncTarget.MadBirdRing.ImcPath);
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        Assert.Equal(MessageKey.SyncGameDataUnreadable, c.Status.Message.Key);
        Assert.Contains("Mad Bird Ring", c.Status.Message.Text);
        Assert.Empty(pen.SyncSets);
    }

    [Fact] // [EDGE-synced-delivery-10]
    public void CollectionChange_MovesModOnce()
    {
        var a = pen.Player!.Value.Id;
        ShowSynced();
        var b = Guid.NewGuid();
        pen.Player = new PlayerCollectionInfo(b, true);
        Ticks(BackdropController.PenumbraRefreshTicks + 2);
        Assert.Equal(a, pen.SyncClears.Single());
        Assert.Equal(b, pen.SyncSets[^1].Collection);
        Assert.Equal(2, pen.Redraws);
        Assert.Equal(2, pen.MetaReads);
        Ticks(BackdropController.PenumbraRefreshTicks * 2);
        Assert.Equal(2, pen.SyncSets.Count);
    }

    [Fact] // [EDGE-synced-delivery-11]
    public void SwitchWhileConverting_CancelsAndUsesNewDelivery()
    {
        config.Delivery = DeliveryMode.Local;
        var gate = new TaskCompletionSource<PreparedTexture>();
        preparer.Gates.Enqueue(gate);
        c.Show();
        Assert.Equal(BackdropState.Preparing, c.State);
        config.Delivery = DeliveryMode.MadBirdRing;
        c.ApplyDelivery();
        Assert.True(preparer.Tokens[0].IsCancellationRequested);
        Ticks(2);
        gate.SetResult(preparer.Make("eeeeeeeeeeeeeeee"));
        WaitJobs();
        c.Tick();
        Assert.Single(pen.SyncSets);
        Assert.Empty(spawner.Spawns);
        Assert.Empty(pen.Sets);
    }

    [Fact] // [EDGE-synced-delivery-11] [EDGE-synced-delivery-34]
    public void SwitchWhileHidden_OnlySaves()
    {
        config.SyncUploadNoticeShown = false;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.ApplyDelivery();
        Assert.Equal(0, preparer.Calls);
        Assert.Empty(pen.SyncCalls);
        Assert.False(config.SyncUploadNoticeShown);
        Assert.Equal(MessageKey.None, c.Status.Message.Key);
    }

    [Fact] // [EDGE-synced-delivery-12]
    public void SyncedShown_ZoneChangeAndNotReady_NoSpawnerNoRemoval()
    {
        ShowSynced();
        c.OnTerritoryChanged();
        game.Ready = false;
        Ticks(40);
        game.Ready = true;
        Ticks(40);
        Assert.Equal(0, spawner.NativeCalls);
        Assert.Empty(pen.SyncClears);
        Assert.Equal(BackdropState.Shown, c.State);
    }

    [Fact] // [EDGE-synced-delivery-13]
    public void Logout_RemovesSynced()
    {
        ShowSynced();
        c.OnLogout();
        Assert.Single(pen.SyncClears);
        Assert.DoesNotContain(StagedFiles(), f => f.StartsWith("sky_") || f.StartsWith("dome_"));
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-synced-delivery-14]
    public void PenumbraDisposed_ForgetsWithoutIpc_ThenInitClears()
    {
        var player = pen.Player!.Value.Id;
        ShowSynced();
        var calls = pen.SyncCalls.Count;
        c.OnPenumbraDisposed();
        Assert.Equal(calls, pen.SyncCalls.Count);
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(MessageKey.PenumbraGone, c.Status.Message.Key);
        c.OnPenumbraInitialized();
        Assert.Contains(player, pen.ClearEverywhereCalls.Single());
        Assert.DoesNotContain(StagedFiles(), f => f.StartsWith("sky_") || f.StartsWith("dome_"));
    }

    [Fact] // [EDGE-synced-delivery-15]
    public void Teardown_ClearsRedrawsCleans()
    {
        ShowSynced();
        c.Teardown();
        Assert.Single(pen.SyncClears);
        Assert.Equal(2, pen.Redraws);
        Assert.DoesNotContain(StagedFiles(), f => f.StartsWith("sky_") || f.StartsWith("dome_"));
    }

    [Fact] // [EDGE-synced-delivery-19]
    public void SyncedShown_HeightChange_RebuildOnce()
    {
        ShowSynced();
        config.Current.HeightOffset = 3f;
        c.ApplyHeight();
        c.Tick();
        Assert.Equal(2, pen.SyncSets.Count);
        Assert.Equal(2, pen.Redraws);
        var dome = pen.SyncSets[^1].Paths["chara/accessory/a0028/vfx/eff/va0200.avfx"];
        Assert.Equal(3f, Avfx(dome).Need("Ptcl").Need("Pos").Need("Y").CurveValue());
        Assert.Empty(spawner.Spawns);
        c.Recenter();
        Assert.Equal(2, pen.SyncSets.Count);
    }

    [Fact] // [EDGE-synced-delivery-20]
    public void Blocker_ByMode()
    {
        spawner.Available = false;
        pen.State = PenumbraState.NoBaseCollection;
        config.Delivery = DeliveryMode.MadBirdRing;
        Assert.Equal(MessageKey.None, c.Blocker());
        config.Delivery = DeliveryMode.Local;
        Assert.Equal(MessageKey.InteropUnavailable, c.Blocker());
        spawner.Available = true;
        Assert.Equal(MessageKey.PenumbraNoBaseCollection, c.Blocker());
    }

    [Fact] // [EDGE-synced-delivery-22]
    public void SupersededJob_FinishesAfterCurrentMoveBeforeRegister_CurrentFileSurvives()
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        var gateA = new TaskCompletionSource<PreparedTexture>();
        var gateB = new TaskCompletionSource<PreparedTexture>();
        preparer.Gates.Enqueue(gateA);
        preparer.Gates.Enqueue(gateB);
        c.Show();   // A
        c.Show();   // B supersedes A
        gateB.SetResult(preparer.Make("bbbbbbbbbbbbbbbb"));   // B's atex moved into place
        WaitJobs(stillGated: 1);
        var aStaged = Path.Combine(Staging, "sky_aaaaaaaaaaaaaaaa.atex");
        File.WriteAllBytes(aStaged, [1, 2, 3]);              // what A's copy left behind
        gateA.SetResult(preparer.Make("aaaaaaaaaaaaaaaa"));   // A completes (cancelled) before B registers
        WaitJobs();
        c.Tick();
        Assert.False(File.Exists(aStaged));
        var bStaged = Path.Combine(Staging, "sky_bbbbbbbbbbbbbbbb.atex");
        Assert.True(File.Exists(bStaged));
        Assert.Equal(bStaged, pen.SyncSets.Single().Paths["vfx/milieu/bbbbbbbbbbbbbbbb/sky.atex"]);
    }

    [Fact] // [EDGE-synced-delivery-22]
    public void SupersededJob_SameKey_DeletesNothing()
    {
        config.Delivery = DeliveryMode.MadBirdRing;
        var gateA = new TaskCompletionSource<PreparedTexture>();
        var gateB = new TaskCompletionSource<PreparedTexture>();
        preparer.Gates.Enqueue(gateA);
        preparer.Gates.Enqueue(gateB);
        c.Show();
        c.Show();
        gateB.SetResult(preparer.Make("cccccccccccccccc"));
        WaitJobs(stillGated: 1);
        gateA.SetResult(preparer.Make("cccccccccccccccc"));
        WaitJobs();
        c.Tick();
        Ticks(2);
        var staged = Path.Combine(Staging, "sky_cccccccccccccccc.atex");
        Assert.True(File.Exists(staged));
        Assert.Single(pen.SyncSets);
        Assert.Equal(BackdropState.Shown, c.State);
    }

    [Fact] // [EDGE-synced-delivery-22]
    public void CancelledJob_DoesNotTouchShownFiles()
    {
        ShowSynced();
        var shownAtex = Path.Combine(Staging, "sky_00112233445566ff.atex");
        var gateX = new TaskCompletionSource<PreparedTexture>();
        var gateY = new TaskCompletionSource<PreparedTexture>();
        preparer.Gates.Enqueue(gateX);
        preparer.Gates.Enqueue(gateY);
        c.Show();   // X: same key as the shown texture
        c.Show();   // Y supersedes X
        gateX.SetResult(preparer.Make("00112233445566ff"));
        WaitJobs(stillGated: 1);
        c.Tick();
        Assert.True(File.Exists(shownAtex));
        gateY.SetResult(preparer.Make("dddddddddddddddd"));
        WaitJobs();
        Ticks(2);
        Assert.True(File.Exists(Path.Combine(Staging, "sky_dddddddddddddddd.atex")));
    }

    [Fact] // [EDGE-synced-delivery-23]
    public void Logs_NeverContainModDirectory()
    {
        stagingSeam = d => new SyncStaging(d) { OpenSource = p => throw new IOException("can't read " + p + " in " + d) };
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        stagingSeam = null;
        pen.SetSyncedResult = 5;
        c.Show();
        c.Tick();
        pen.SetSyncedResult = 0;
        ShowSynced();
        c.Hide();
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Text.Contains(modDir, StringComparison.OrdinalIgnoreCase)
                                              || l.Text.Contains(modDir.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(modDir, c.Status.Message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // [EDGE-synced-delivery-24]
    public void BundledScene_StagedAsIs_NoUploadNotice()
    {
        config.SyncUploadNoticeShown = false;
        config.ImagePath = "bundled:moon";
        ShowSynced();
        var staged = Path.Combine(Staging, "sky_00112233445566ff.atex");
        Assert.Equal(File.ReadAllBytes(cache.TextureDiskPath("00112233445566ff")), File.ReadAllBytes(staged));
        Assert.NotEqual(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        Assert.False(config.SyncUploadNoticeShown);
    }

    [Fact] // [EDGE-synced-delivery-32]
    public void UserImage_AfterNoticeSeen_ShowsImmediately()
    {
        ShowSynced();
        Assert.Single(pen.SyncSets);
        Assert.Equal(0, saves);
    }

    [Fact] // [EDGE-synced-delivery-32]
    public void UploadNotice_NeverInLocalMode()
    {
        config.SyncUploadNoticeShown = false;
        ShowLocal();
        Assert.False(config.SyncUploadNoticeShown);
        Assert.NotEqual(MessageKey.SyncUploadNotice, c.Status.Message.Key);
    }

    [Fact] // [EDGE-synced-delivery-33]
    public void TwoRebuilds_SharedCollection_ExactlyOneWarning()
    {
        preparer.Warnings = [StatusMessage.Of(MessageKey.NoWrap, ("file", "pano.jpg"))];
        pen.Player = new PlayerCollectionInfo(Guid.NewGuid(), false);
        ShowSynced();
        config.Current.Radius = 9000;
        c.ApplyRebuild(); c.Tick();
        config.Current.Radius = 8000;
        c.ApplyRebuild(); c.Tick();
        Assert.Equal([MessageKey.NoWrap, MessageKey.SyncSharedCollection], c.Status.Warnings.Select(w => w.Key).ToArray());
    }

    [Fact] // [EDGE-synced-delivery-35]
    public void Unavailable_SyncedConfig_UsesLocalPath_NoSyncCalls()
    {
        available = false;
        config.Delivery = DeliveryMode.MadBirdRing;
        Assert.Equal(DeliveryMode.Local, c.EffectiveDelivery);
        c.Show();
        Ticks(2);
        Assert.Single(pen.Sets);
        Assert.Single(spawner.Spawns);
        c.ApplyRebuild(); Ticks(2);
        c.Hide();
        Assert.Empty(pen.RegistrationCalls);
        spawner.Available = false;
        Assert.Equal(MessageKey.InteropUnavailable, c.Blocker());
    }

    [Fact] // [EDGE-synced-delivery-35]
    public void Available_SyncedConfig_TakesSyncedPath()
    {
        available = true;
        config.Delivery = DeliveryMode.MadBirdRing;
        Assert.Equal(DeliveryMode.MadBirdRing, c.EffectiveDelivery);
        ShowSynced();
        Assert.Single(pen.SyncSets);
    }

    [Fact] // [EDGE-synced-delivery-35]
    public void Unavailable_StartupCleanup_RemovesOnly_NeverCreatesFolder()
    {
        available = false;
        c.OnPenumbraInitialized();
        Assert.Single(pen.ClearEverywhereCalls);
        Assert.False(Directory.Exists(Staging));
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, "sky_x.atex"), [1]);
        c.OnPenumbraInitialized();
        Assert.Empty(StagedFiles());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
        Assert.Empty(pen.RegistrationCalls);
    }

    // ---------------------------------------------------------------- review fixes

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-32]
    public void GridClick_AfterNoticeOverSyncedBundled_NextClickShows()
    {
        config.SyncUploadNoticeShown = false;
        config.UserImages = [userImage];
        config.ImagePath = "bundled:moon";
        ShowSynced();                                          // a bundled scene shown through the ring
        var grid = new Milieu.Gui.SceneGridViewModel(config, () => saves++, c.Post, c, new FakeGridDownloads { Unpublished = false },
                                                     p => p == userImage);
        var tile = grid.Tiles.Single(t => t.Id == userImage);
        grid.Click(tile);
        c.Tick();                                              // the posted click runs: notice
        Assert.Equal(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        Assert.Equal(BackdropState.Hidden, c.State);           // the bundled dome came down too
        Assert.Single(pen.SyncClears);
        Assert.Equal(2, pen.Redraws);
        Assert.Single(pen.SyncSets);                           // nothing of the user image registered
        grid.Click(grid.Tiles.Single(t => t.Id == userImage)); // the next click is not swallowed
        Ticks(2);
        Assert.Equal(2, pen.SyncSets.Count);
        Assert.Equal(BackdropState.Shown, c.State);
    }

    [Fact] // [AC-synced-delivery-10] [EDGE-synced-delivery-32]
    public void Reconvert_AfterNotice_DoesNotUpload()
    {
        config.SyncUploadNoticeShown = false;
        config.ImagePath = "bundled:moon";
        ShowSynced();
        config.ImagePath = userImage;
        c.Show();
        Assert.Equal(MessageKey.SyncUploadNotice, c.Status.Message.Key);
        var sets = pen.SyncSets.Count;
        c.RequestReconvert();
        now += TimeSpan.FromSeconds(1);
        Ticks(3);
        Assert.Equal(sets, pen.SyncSets.Count);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-synced-delivery-5]
    public void Show_WithPendingOnly_AbortsItFirst()
    {
        pen.ResolvePlayerWorks = false;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        config.SyncUploadNoticeShown = false;                 // the next Show hits the notice gate
        c.Show();
        Assert.Single(pen.SyncClears);
        Assert.Empty(pen.Synced);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-synced-delivery-13]
    public void Logout_WithPendingOnly_RemovesIt()
    {
        pen.ResolvePlayerWorks = false;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        c.OnLogout();
        Assert.Single(pen.SyncClears);
        Assert.Empty(pen.Synced);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-synced-delivery-4] [EDGE-synced-delivery-5]
    public void FailedRebuildWithPendingOnly_StaysAwaiting_ThenTimesOutClean()
    {
        pen.ResolvePlayerWorks = false;
        config.Delivery = DeliveryMode.MadBirdRing;
        c.Show();
        c.Tick();
        pen.SetSyncedResult = 11;
        config.Current.Radius = 9000;
        c.ApplyRebuild();                                      // refused: the earlier pending one is still registered
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        Ticks(BackdropController.RedirectTimeoutTicks + 1);
        Assert.Empty(pen.Synced);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-synced-delivery-2] [EDGE-synced-delivery-16]
    public void Hide_CleansStagingWheneverItExists_NeverCreatesIt()
    {
        config.Delivery = DeliveryMode.Local;
        c.Hide();
        Assert.False(Directory.Exists(Staging));
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, "sky_left.atex"), [1]);
        File.WriteAllBytes(Path.Combine(Staging, "notes.txt"), [1]);
        c.Hide();
        Assert.Equal(["notes.txt"], StagedFiles());
        Assert.Empty(pen.RegistrationCalls);
    }
}
