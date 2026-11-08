using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class BackdropControllerTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeSpawner spawner = new();
    private readonly FakePenumbra penumbra = new();
    private readonly FakeGame game = new();
    private readonly FakeLog log = new();
    private readonly CacheStore cache;
    private readonly FakePreparer preparer;
    private readonly Configuration config = new();
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly BackdropController c;

    public BackdropControllerTests()
    {
        cache = new CacheStore(dir.File("cache"));
        preparer = new FakePreparer(cache);
        config.ImagePath = dir.File("pano.jpg");
        spawner.Clock = () => now;
        c = new BackdropController(spawner, penumbra, game, preparer, cache, config, log, run: f => f(), utcNow: () => now);
    }

    public void Dispose() => dir.Dispose();

    private void Ticks(int n) { for (var i = 0; i < n; i++) c.Tick(); }

    /// <summary>Show, let the job complete, register, resolve and spawn.</summary>
    private VfxHandle ShowAndSpawn()
    {
        c.Show();
        c.Tick();   // job done → registered
        c.Tick();   // resolved → spawned
        Assert.Equal(BackdropState.Shown, c.State);
        return spawner.Spawns[^1].Handle;
    }

    [Fact] // [AC-hdri-backdrop-1]
    public void Show_HappyPath_RegistersBothFilesThenSpawnsAfterRedirectResolves()
    {
        penumbra.ResolveWorks = false;
        c.Show();
        Assert.Equal(1, preparer.Calls);
        c.Tick();
        var set = Assert.Single(penumbra.Sets);
        Assert.Equal(2, set.Count);
        Assert.Contains(set, kv => kv.Key.EndsWith("/sky.atex") && kv.Value == cache.TextureDiskPath("00112233445566ff"));
        var avfx = set.Single(kv => kv.Key.EndsWith("/dome.avfx"));
        Assert.Matches("^vfx/milieu/[0-9a-f]{16}/dome\\.avfx$", avfx.Key);
        Assert.True(File.Exists(avfx.Value));
        AvfxChunkReader.Parse(File.ReadAllBytes(avfx.Value));
        Ticks(5);
        Assert.Empty(spawner.Spawns);                       // not before Penumbra resolves both
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);

        penumbra.ResolveWorks = true;
        c.Tick();
        var spawn = Assert.Single(spawner.Spawns);
        Assert.Equal(avfx.Key, spawn.Path);
        Assert.Equal(game.PlayerPosition!.Value + new Vector3(0, config.Sky.HeightOffset, 0), spawn.Pos);
        Assert.Equal(BackdropState.Shown, c.State);
        var vm = new Gui.BackdropViewModel(c.Status, c.Blocker(), config);
        Assert.Equal("Apply", vm.ShowButtonLabel);
        Assert.True(vm.CanHide);
    }

    [Fact] // [AC-hdri-backdrop-2]
    public void Mode_SelectsPresetForAvfxAndSpawnPosition()
    {
        config.Mode = DomeMode.Room;
        ShowAndSpawn();
        var avfx = AvfxChunkReader.Parse(File.ReadAllBytes(penumbra.Current!.Values.Single(v => v.EndsWith(".avfx"))));
        Assert.Equal(10f, avfx.Need("Ptcl").Need("Scl").Need("X").CurveValue());
        Assert.Equal(game.PlayerPosition!.Value.Y + 1.6f, spawner.Spawns[^1].Pos.Y, 4);

        config.Room.Radius = 25;
        Assert.Equal(20000f, config.Sky.Radius);
        config.Mode = DomeMode.Sky;
        c.ApplyRebuild();
        c.Tick();
        var sky = AvfxChunkReader.Parse(File.ReadAllBytes(penumbra.Current!.Values.Single(v => v.EndsWith(".avfx"))));
        Assert.Equal(20000f, sky.Need("Ptcl").Need("Scl").Need("X").CurveValue());
    }

    [Fact] // [AC-hdri-backdrop-3]
    public void ApplyPlacement_SpawnsNewBeforeRemovingOld_SameAvfx()
    {
        var h = ShowAndSpawn();
        var sets = penumbra.Sets.Count;
        var avfx = spawner.Spawns[0].Path;
        config.Sky.YawDegrees = 90;
        config.Sky.HeightOffset = 5;
        spawner.Calls.Clear();
        c.ApplyPlacement();
        // Respawned at the new placement (new first, then the old removed); never just moved.
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
        Assert.Empty(spawner.Moves);
        var spawn = spawner.Spawns[^1];
        Assert.Equal(avfx, spawn.Path);
        Assert.Equal(MathF.PI / 2, spawn.Yaw, 4);
        Assert.Equal(game.PlayerPosition!.Value.Y + 5, spawn.Pos.Y, 4);
        Assert.True(h.IsDead);
        Assert.Equal(sets, penumbra.Sets.Count);   // no new files, no IPC
        Assert.Equal(1, preparer.Calls);
    }

    [Fact] // [AC-hdri-backdrop-3] [EDGE-hdri-backdrop-23]
    public void RadiusChange_RebuildsAvfxAndSwapsWithoutReconverting()
    {
        var old = ShowAndSpawn();
        var oldSet = penumbra.Current!;
        config.Sky.Radius = 5000;
        c.ApplyRebuild();
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        var newSet = penumbra.Current!;
        Assert.Equal(oldSet.Keys.Single(k => k.EndsWith(".atex")), newSet.Keys.Single(k => k.EndsWith(".atex")));
        Assert.NotEqual(oldSet.Keys.Single(k => k.EndsWith(".avfx")), newSet.Keys.Single(k => k.EndsWith(".avfx")));
        c.Tick();
        Assert.Equal(new[] { "Spawn", "Spawn", "Remove" }, spawner.Calls);   // new one spawned before the old one goes
        Assert.Same(old, spawner.Removes.Single());
        Assert.Equal(1, preparer.Calls);
    }

    [Fact] // [AC-hdri-backdrop-6]
    public void Conversion_RunsOffTick_ProgressReported()
    {
        preparer.Gate = new TaskCompletionSource<PreparedTexture>();
        var asyncController = new BackdropController(spawner, penumbra, game, preparer, cache, config, log);
        asyncController.Show();
        Assert.True(SpinWait.SpinUntil(() => preparer.Calls == 1, TimeSpan.FromSeconds(5)));
        Assert.NotEqual(Environment.CurrentManagedThreadId, preparer.Threads.Single());   // on a worker, not the caller
        asyncController.Tick();
        asyncController.Tick();
        Assert.Equal(1, preparer.Calls);                     // Tick never calls the preparer itself
        preparer.LastProgress!.Report(0.4f);
        Assert.Equal("Converting pano.jpg… 40%", asyncController.Status.Message.Text);
        preparer.Gate.SetResult(FakePreparer.Make(cache, config.ImagePath!));
        asyncController.Tick();
        Assert.Equal(BackdropState.AwaitingRedirect, asyncController.State);
    }

    [Fact] // [AC-hdri-backdrop-7]
    public void Hide_RemovesVfxThenTempMod()
    {
        var h = ShowAndSpawn();
        spawner.Calls.Clear(); penumbra.Calls.Clear();
        c.Hide();
        Assert.Same(h, spawner.Removes.Single());
        Assert.Equal("Clear", penumbra.Calls.Last());
        Assert.Equal(BackdropState.Hidden, c.State);
        var vm = new Gui.BackdropViewModel(c.Status, c.Blocker(), config);
        Assert.True(vm.CanShow);
        Assert.False(vm.CanHide);
    }

    [Fact] // [AC-hdri-backdrop-7]
    public void Teardown_RemovesEverything()
    {
        var h = ShowAndSpawn();
        c.Teardown();
        Assert.True(h.IsDead);
        Assert.Null(penumbra.Current);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [AC-hdri-backdrop-7] [EDGE-hdri-backdrop-40]
    public void Startup_ClearsLeftoverTempMod()
    {
        c.OnPenumbraInitialized();
        Assert.Equal(1, penumbra.Clears);
        Assert.Empty(spawner.Spawns);
    }

    [Fact] // [EDGE-hdri-backdrop-40]
    public void PenumbraInitialized_ClearsLeftoverOnce()
    {
        c.OnPenumbraInitialized();
        Assert.Equal(1, penumbra.Clears);
        penumbra.State = PenumbraState.Missing;
        c.OnPenumbraInitialized();
        Assert.Equal(1, penumbra.Clears);
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-20]
    public void TerritoryChange_RemovesThenRespawnsRecentered()
    {
        var h = ShowAndSpawn();
        game.BetweenAreas = true;
        c.Tick();
        Assert.True(h.IsDead);
        Assert.Equal(BackdropState.Suspended, c.State);
        game.BetweenAreas = false;
        game.PlayerPosition = new Vector3(500, 0, -500);
        Ticks(BackdropController.RespawnDelayFrames);
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(new Vector3(500, config.Sky.HeightOffset, -500), spawner.Spawns[^1].Pos);
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-21]
    public void Logout_RemovesAndStaysHidden()
    {
        var h = ShowAndSpawn();
        game.IsLoggedIn = false;
        c.OnLogout();
        Assert.True(h.IsDead);
        Assert.Null(penumbra.Current);
        Assert.Equal(BackdropState.Hidden, c.State);
        game.IsLoggedIn = true;
        Ticks(100);
        Assert.Single(spawner.Spawns);
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-33]
    public void GPose_KeepsDome()
    {
        var h = ShowAndSpawn();
        // GPose does not affect readiness (FakeGame mirrors GameState: only login, player, areas and cutscenes count).
        Ticks(100);
        Assert.False(h.IsDead);
        Assert.Single(spawner.Spawns);
        Assert.Empty(spawner.Removes);
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-34]
    public void Cutscene_SuspendsAndRespawnsSameCentre()
    {
        ShowAndSpawn();
        var first = spawner.Spawns[^1].Pos;
        game.InCutscene = true;
        c.Tick();
        Assert.Equal(BackdropState.Suspended, c.State);
        Assert.Single(spawner.Removes);
        game.PlayerPosition = new Vector3(1, 2, 3);
        game.InCutscene = false;
        Ticks(BackdropController.RespawnDelayFrames);
        Assert.Equal(first, spawner.Spawns[^1].Pos);
    }

    [Fact] // [AC-hdri-backdrop-9] [EDGE-hdri-backdrop-4]
    public void InteropUnavailable_NeverCallsNative()
    {
        spawner.Available = false;
        c.Show();
        Ticks(10);
        Assert.Equal(0, spawner.NativeCalls);
        Assert.Equal(0, preparer.Calls);
        Assert.Equal(MessageKey.InteropUnavailable, c.Blocker());
    }

    [Fact] // [AC-hdri-backdrop-10]
    public void Construction_DoesNotAutoShow()
    {
        Ticks(200);
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Empty(penumbra.Sets);
        Assert.Empty(spawner.Spawns);
    }

    [Fact] // [EDGE-hdri-backdrop-1]
    public void PenumbraBecomesAvailable_EnablesShow()
    {
        penumbra.State = PenumbraState.Missing;
        Assert.False(new Gui.BackdropViewModel(c.Status, c.Blocker(), config).CanShow);
        penumbra.State = PenumbraState.Available;
        c.OnPenumbraInitialized();
        Assert.True(new Gui.BackdropViewModel(c.Status, c.Blocker(), config).CanShow);
    }

    [Fact] // [EDGE-hdri-backdrop-3]
    public void PenumbraDisposed_RemovesOnFrameworkThread_ThenHides()
    {
        var h = ShowAndSpawn();
        c.OnPenumbraDisposed();
        Assert.Same(h, spawner.Removes.Single());
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(MessageKey.PenumbraGone, c.Status.Message.Key);
        Ticks(50);
        Assert.Single(spawner.Spawns);
    }

    [Fact] // [EDGE-hdri-backdrop-5]
    public void Show_WhenNotReady_IsNoOp()
    {
        game.Ready = false;
        c.Show();
        Assert.Equal(0, preparer.Calls);
        Assert.Equal(MessageKey.NotLoggedIn, c.Blocker());
    }

    [Fact] // [EDGE-hdri-backdrop-15]
    public void SecondShow_CancelsFirst_LatestWins()
    {
        preparer.Gate = new TaskCompletionSource<PreparedTexture>();
        c.Show();
        var firstToken = preparer.LastToken;
        var firstGate = preparer.Gate;
        preparer.Gate = null;
        config.ImagePath = dir.File("second.jpg");
        c.Show();
        Assert.True(firstToken.IsCancellationRequested);
        firstGate.SetResult(FakePreparer.Make(cache, "x", "aaaaaaaaaaaaaaaa"));
        Ticks(3);
        Assert.Single(spawner.Spawns);
        Assert.DoesNotContain(penumbra.Current!.Keys, k => k.Contains("aaaaaaaaaaaaaaaa"));
    }

    [Fact] // [EDGE-hdri-backdrop-16]
    public void TeardownDuringPrepare_NoSpawn()
    {
        preparer.Gate = new TaskCompletionSource<PreparedTexture>();
        c.Show();
        var token = preparer.LastToken;
        c.Teardown();
        Assert.True(token.IsCancellationRequested);
        preparer.Gate.SetResult(FakePreparer.Make(cache, "x"));
        Ticks(5);
        Assert.Empty(spawner.Spawns);
        Assert.Empty(penumbra.Sets);
    }

    [Fact] // [EDGE-hdri-backdrop-17]
    public void WriteFailed_KeepsOldDome()
    {
        var h = ShowAndSpawn();
        preparer.Throw = new MilieuException(StatusMessage.Of(MessageKey.WriteFailed));
        c.Show();
        c.Tick();
        Assert.Equal(MessageKey.WriteFailed, c.Status.Message.Key);
        Assert.False(h.IsDead);
        Assert.Equal(BackdropState.Shown, c.State);
    }

    [Fact] // [EDGE-hdri-backdrop-18]
    public void RedirectTimeout_RestoresShownSet()
    {
        ShowAndSpawn();
        var shown = penumbra.Current!;
        config.Sky.Radius = 4000;
        penumbra.ResolveWorks = false;
        c.ApplyRebuild();
        Ticks(BackdropController.RedirectTimeoutTicks);
        Assert.Equal(MessageKey.RedirectTimeout, c.Status.Message.Key);
        Assert.Equal(shown, penumbra.Current);
        Assert.Single(spawner.Spawns);

        // No dome yet: a timeout clears instead.
        c.Hide();
        c.Show();
        Ticks(BackdropController.RedirectTimeoutTicks + 1);
        Assert.Null(penumbra.Current);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-hdri-backdrop-19]
    public void GameFreedVfx_NeverTouchedAgain_Respawns()
    {
        var h = ShowAndSpawn();
        now += TimeSpan.FromMinutes(1);
        spawner.GameFrees(h);
        c.ApplyPlacement();
        c.Tick();
        Assert.Empty(spawner.Moves);
        Assert.Empty(spawner.Removes);
        Ticks(BackdropController.RespawnDelayFrames);
        Assert.Equal(2, spawner.Spawns.Count);
        c.Hide();
        Assert.DoesNotContain(h, spawner.Removes);
    }

    [Fact] // [EDGE-hdri-backdrop-19]
    public void MarkDead_IsThreadSafe()
    {
        for (var round = 0; round < 50; round++)
        {
            var h = new VfxHandle(1, "x", DateTime.UtcNow);
            var winners = 0;
            Parallel.For(0, 16, _ => { if (h.MarkDead()) Interlocked.Increment(ref winners); });
            Assert.Equal(1, winners);
            Assert.True(h.IsDead);
        }
    }

    [Fact] // [EDGE-hdri-backdrop-22]
    public void LiveChange_WhileSuspended_NoNativeCall()
    {
        ShowAndSpawn();
        game.InCutscene = true;
        c.Tick();
        var calls = spawner.NativeCalls;
        config.Sky.YawDegrees = 45;
        c.ApplyPlacement();
        Assert.Equal(calls, spawner.NativeCalls);
    }

    [Fact] // [EDGE-hdri-backdrop-24]
    public void SettingsChange_WhileHidden_OnlySaves()
    {
        config.Sky.Radius = 300;
        c.ApplyRebuild();
        c.ApplyPlacement();
        Ticks(5);
        Assert.Empty(penumbra.Sets);
        Assert.Equal(0, spawner.NativeCalls);
    }

    [Fact] // [EDGE-hdri-backdrop-28]
    public void Off_WhileHiddenOrPreparing()
    {
        c.Hide();
        Assert.Equal(0, penumbra.Clears);
        Assert.Equal(0, spawner.NativeCalls);
        preparer.Gate = new TaskCompletionSource<PreparedTexture>();
        c.Show();
        c.Hide();
        Assert.True(preparer.LastToken.IsCancellationRequested);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-hdri-backdrop-29]
    public void TeardownTimeout_DoesNotHang_LeaksStrings()
    {
        var never = new TaskCompletionSource().Task;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(BackdropController.WaitForJob(never, TimeSpan.FromMilliseconds(50)));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(BackdropController.WaitForJob(null, TimeSpan.Zero));
    }

    [Fact] // [EDGE-hdri-backdrop-31]
    public void Logs_NeverContainFullPath()
    {
        config.ImagePath = Path.Combine(dir.Path, "secret", "dir", "pano.jpg");
        preparer.Throw = new IOException($"Could not find {config.ImagePath}");
        c.Show();
        c.Tick();
        preparer.Throw = null;
        ShowAndSpawn();
        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, l => Assert.DoesNotContain("secret", l.Text));
        Assert.DoesNotContain("secret", c.Status.Message.Text);
    }

    [Fact] // [EDGE-hdri-backdrop-32]
    public void RepeatedEarlyGameRemoval_StopsWithGameEndedVfx()
    {
        for (var i = 0; i < 3; i++)
        {
            var h = i == 0 ? ShowAndSpawn() : spawner.Spawns[^1].Handle;
            now += TimeSpan.FromSeconds(1);
            spawner.GameFrees(h);
            c.Tick();
            if (i < 2) Ticks(BackdropController.RespawnDelayFrames);
        }
        Assert.Equal(BackdropState.Hidden, c.State);
        Assert.Equal(MessageKey.GameEndedVfx, c.Status.Message.Key);
        Assert.Null(penumbra.Current);
        var spawns = spawner.Spawns.Count;
        Ticks(100);
        Assert.Equal(spawns, spawner.Spawns.Count);
        Assert.Single(log.Lines, l => l.Level == "Warning" && l.Text.Contains("kept ending"));
    }

    [Fact] // [EDGE-hdri-backdrop-35]
    public void ReadinessFlicker_NoRespawnBefore30Frames()
    {
        ShowAndSpawn();
        game.BetweenAreas = true;
        c.Tick();
        game.BetweenAreas = false;
        Ticks(BackdropController.RespawnDelayFrames - 5);
        game.BetweenAreas = true;
        c.Tick();
        game.BetweenAreas = false;
        Ticks(BackdropController.RespawnDelayFrames - 1);
        Assert.Single(spawner.Spawns);
        c.Tick();
        Assert.Equal(2, spawner.Spawns.Count);
    }

    [Fact] // [EDGE-hdri-backdrop-36]
    public void TerritoryChangedEvent_RemovesImmediately()
    {
        var h = ShowAndSpawn();
        c.OnTerritoryChanged();
        Assert.True(h.IsDead);
        Assert.Equal(BackdropState.Suspended, c.State);
    }

    [Fact] // [EDGE-hdri-backdrop-36]
    public void LogoutEvent_RemovesImmediately()
    {
        var h = ShowAndSpawn();
        c.OnLogout();
        Assert.True(h.IsDead);
        Assert.Equal(BackdropState.Hidden, c.State);
    }

    [Fact] // [EDGE-hdri-backdrop-37]
    public void DisposeDuringEncode_WaitsForJob_NoFileWritten()
    {
        preparer.Gate = new TaskCompletionSource<PreparedTexture>();
        c.Show();
        var running = c.CancelJobForDispose();
        Assert.NotNull(running);
        Assert.True(preparer.LastToken.IsCancellationRequested);
        preparer.Gate.SetCanceled();
        Assert.True(BackdropController.WaitForJob(running, TimeSpan.FromSeconds(2)));
        c.Teardown();
        Assert.False(Directory.Exists(cache.Root) && Directory.EnumerateFiles(cache.Root, "dome_*").Any());
    }

    [Fact] // [EDGE-hdri-backdrop-38]
    public void ModsDisabled_BlocksShow()
    {
        penumbra.State = PenumbraState.ModsDisabled;
        c.Show();
        Assert.Equal(0, preparer.Calls);
        Assert.Equal(MessageKey.PenumbraModsDisabled, c.Blocker());
    }

    [Fact] // [EDGE-hdri-backdrop-39]
    public void NoBaseCollection_BlocksShow()
    {
        penumbra.State = PenumbraState.NoBaseCollection;
        c.Show();
        Assert.Equal(0, preparer.Calls);
        Assert.Equal(MessageKey.PenumbraNoBaseCollection, c.Blocker());
    }

    [Fact] // [EDGE-hdri-backdrop-42]
    public void ShowDuringAwaitingRedirect_Supersedes_TimeoutRestoresShownSet()
    {
        ShowAndSpawn();
        var shown = penumbra.Current!;
        penumbra.ResolveWorks = false;
        config.Sky.Radius = 3000;
        c.ApplyRebuild();
        Assert.Equal(BackdropState.AwaitingRedirect, c.State);
        config.Sky.Radius = 2000;
        c.ApplyRebuild();                         // supersedes the pending set
        Assert.Equal(3, penumbra.Sets.Count);
        Ticks(BackdropController.RedirectTimeoutTicks);
        Assert.Equal(shown, penumbra.Current);    // the set actually on screen, not the superseded one
        Assert.Equal(BackdropState.Shown, c.State);
    }


    [Fact] // [EDGE-hdri-backdrop-32]
    public void EarlyRemovalGuard_ResetsWhenADomeSurvives10s()
    {
        // Two early removals…
        for (var i = 0; i < 2; i++)
        {
            var h = i == 0 ? ShowAndSpawn() : spawner.Spawns[^1].Handle;
            now += TimeSpan.FromSeconds(1);
            spawner.GameFrees(h);
            c.Tick();
            Ticks(BackdropController.RespawnDelayFrames);
        }
        // …then a dome that survives the window resets the count…
        now += BackdropController.EarlyRemovalWindow + TimeSpan.FromSeconds(1);
        c.Tick();
        // …so one more early removal still respawns.
        var survivor = spawner.Spawns[^1].Handle;
        now += TimeSpan.FromSeconds(1);
        spawner.GameFrees(survivor);
        c.Tick();
        Ticks(BackdropController.RespawnDelayFrames);
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(4, spawner.Spawns.Count);
    }

    [Fact] // [AC-hdri-backdrop-8] [EDGE-hdri-backdrop-34]
    public void Cutscene_WhilePreparingAnotherImage_RemovesTheShownDome()
    {
        var a = ShowAndSpawn();
        preparer.Gate = new TaskCompletionSource<PreparedTexture>();
        config.ImagePath = dir.File("b.jpg");
        c.Show();
        Assert.Equal(BackdropState.Preparing, c.State);
        game.InCutscene = true;
        c.Tick();
        Assert.True(a.IsDead);
        Assert.Same(a, spawner.Removes.Single());
        game.InCutscene = false;
        preparer.Gate.SetResult(FakePreparer.Make(cache, "b", "bbbbbbbbbbbbbbbb"));
        Ticks(3);
        Assert.Equal(BackdropState.Shown, c.State);
        Assert.Equal(a.SpawnedAtUtc, a.SpawnedAtUtc);
        Assert.Equal(spawner.Spawns[0].Pos, spawner.Spawns[^1].Pos);   // same centre after a cutscene
    }

    [Fact] // [EDGE-hdri-backdrop-41]
    public void RefusedRemove_KeepsTheHandle()
    {
        var h = ShowAndSpawn();
        spawner.Refuse = true;
        c.Hide();
        Assert.False(h.IsDead);
        spawner.Refuse = false;
        c.Teardown();
        Assert.True(h.IsDead);   // still found and removed later
    }

    [Fact] // [AC-hdri-backdrop-1]
    public void PostedActions_RunOnTheNextTick()
    {
        c.Post(c.Show);
        Assert.Equal(0, preparer.Calls);
        c.Tick();
        Assert.Equal(1, preparer.Calls);
        game.Ready = false;
        c.Tick();
        Assert.Equal(MessageKey.NotLoggedIn, c.UiBlocker);
        game.Ready = true;
        c.Tick();
        Assert.Equal(MessageKey.None, c.UiBlocker);
    }

    [Fact] // [EDGE-hdri-backdrop-37]
    public void CancelJobForDispose_CoversEveryOutstandingJob()
    {
        var first = new TaskCompletionSource<PreparedTexture>();
        preparer.Gate = first;
        c.Show();
        var second = new TaskCompletionSource<PreparedTexture>();
        preparer.Gate = second;
        c.Show();
        var all = c.CancelJobForDispose()!;
        second.SetCanceled();
        Assert.False(BackdropController.WaitForJob(all, TimeSpan.FromMilliseconds(50)));   // the first is still running
        first.SetCanceled();
        Assert.True(BackdropController.WaitForJob(all, TimeSpan.FromSeconds(2)));
    }


    private float SpawnedFloorY()
    {
        var avfx = AvfxChunkReader.Parse(File.ReadAllBytes(penumbra.Current!.Values.Single(v => v.EndsWith(".avfx"))));
        var vdrw = avfx.Need("Modl").Need("VDrw").Data;
        var min = float.MaxValue;
        for (var o = 0; o < vdrw.Length; o += 36) min = Math.Min(min, (float)BitConverter.ToHalf(vdrw, o + 2));
        return -min;
    }

    [Fact] // [AC-hdri-backdrop-15]
    public void RoomDefaults_BuildWithFloorY_HeightOverRadius()
    {
        config.Mode = DomeMode.Room;
        ShowAndSpawn();
        Assert.Equal(0.16f, SpawnedFloorY(), 2);
        Assert.Equal(game.PlayerPosition!.Value.Y + 1.6f, spawner.Spawns[^1].Pos.Y, 4);
    }

    [Fact] // [EDGE-hdri-backdrop-82]
    public void FloorYNull_WhenHeightNotBelowRadiusOrNonPositive()
    {
        var d = DomeSettings.RoomDefaults();
        Assert.Equal(0.16f, d.FloorY()!.Value, 4);
        d.HeightOffset = 0; Assert.Null(d.FloorY());
        d.HeightOffset = -1; Assert.Null(d.FloorY());
        d.HeightOffset = 10; Assert.Null(d.FloorY());     // = radius × vertical scale
        d.HeightOffset = 1.6f; d.GroundProjection = false; Assert.Null(d.FloorY());
        Assert.Null(DomeSettings.SkyDefaults().FloorY());  // sky: projection off by default
    }

    [Fact] // [AC-hdri-backdrop-3] [EDGE-hdri-backdrop-84]
    public void HeightRelease_ProjectionOn_FloorChanged_OneRebuildNoPlacement()
    {
        config.Mode = DomeMode.Room;
        ShowAndSpawn();
        var oldAvfx = spawner.Spawns[^1].Path;
        spawner.Calls.Clear();
        config.Room.HeightOffset = 2.0f;
        c.ApplyHeight();
        c.Tick();
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
        Assert.NotEqual(oldAvfx, spawner.Spawns[^1].Path);
        Assert.Equal(0.20f, SpawnedFloorY(), 2);
        Assert.Equal(game.PlayerPosition!.Value.Y + 2.0f, spawner.Spawns[^1].Pos.Y, 4);
    }

    [Fact] // [AC-hdri-backdrop-3] [EDGE-hdri-backdrop-84]
    public void HeightRelease_ProjectionOn_FloorUnchanged_DoesPlacement()
    {
        config.Mode = DomeMode.Room;
        config.Room.HeightOffset = 12;   // ≥ radius × vertical scale: no floor
        ShowAndSpawn();
        var sets = penumbra.Sets.Count;
        var avfx = spawner.Spawns[^1].Path;
        spawner.Calls.Clear();
        config.Room.HeightOffset = 15;
        c.ApplyHeight();
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
        Assert.Equal(avfx, spawner.Spawns[^1].Path);
        Assert.Equal(sets, penumbra.Sets.Count);
        Assert.Equal(game.PlayerPosition!.Value.Y + 15, spawner.Spawns[^1].Pos.Y, 4);
    }

    [Fact] // [AC-hdri-backdrop-3] [EDGE-hdri-backdrop-84]
    public void HeightRelease_ProjectionOff_DoesPlacement()
    {
        ShowAndSpawn();
        var avfx = spawner.Spawns[^1].Path;
        var sets = penumbra.Sets.Count;
        spawner.Calls.Clear();
        config.Sky.HeightOffset = 800;
        c.ApplyHeight();
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
        Assert.Equal(avfx, spawner.Spawns[^1].Path);
        Assert.Equal(sets, penumbra.Sets.Count);
    }

    [Fact] // [AC-hdri-backdrop-3]
    public void Recenter_RespawnsAtPlayer_NewFirst()
    {
        ShowAndSpawn();
        game.PlayerPosition = new Vector3(7, 8, 9);
        spawner.Calls.Clear();
        c.Recenter();
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
        Assert.Equal(new Vector3(7, 8 + config.Sky.HeightOffset, 9), spawner.Spawns[^1].Pos);
    }

    [Fact] // [EDGE-hdri-backdrop-94]
    public void ModeSwitch_And_ProjectionToggle_OneRebuildOnly()
    {
        ShowAndSpawn();
        spawner.Calls.Clear();
        config.Mode = DomeMode.Room;
        c.ApplyRebuild();
        c.Tick();
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
        Assert.Equal(game.PlayerPosition!.Value.Y + 1.6f, spawner.Spawns[^1].Pos.Y, 4);
        spawner.Calls.Clear();
        config.Room.GroundProjection = false;
        c.ApplyRebuild();
        c.Tick();
        Assert.Equal(new[] { "Spawn", "Remove" }, spawner.Calls);
    }
}
